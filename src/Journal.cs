using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace PclRepair
{
    [DataContract]
    public sealed class LogLine
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public string timestamp_utc;
        [DataMember(Order=2)] public string level;
        [DataMember(Order=3)] public int process_id;
        [DataMember(Order=4)] public int thread_id;
        [DataMember(Order=5)] public string module="repair";
        [DataMember(Order=6)] public string @event;
        [DataMember(Order=7)] public string message;
        [DataMember(Order=8)] public string correlation_id;
        [DataMember(Order=9)] public LogData data;
    }
    [DataContract]
    public sealed class LogData
    {
        [DataMember(Order=0)] public string detail;
    }
    [DataContract]
    public sealed class DedupEntry
    {
        [DataMember(Order=0)] public string fingerprint;
        [DataMember(Order=1)] public DateTime first_utc;
        [DataMember(Order=2)] public DateTime last_utc;
        [DataMember(Order=3)] public int count;
        [DataMember(Order=4)] public int reported;
    }
    [DataContract]
    public sealed class DedupState
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public List<DedupEntry> entries=new List<DedupEntry>();
    }
    public sealed class Journal : IDisposable
    {
        public const int RotateBytes=5*1024*1024;
        public const int MaxBytes=6*1024*1024;
        readonly string directory;
        readonly string correlation=Guid.NewGuid().ToString("N");
        public Journal(string path) { directory=path; }
        LogLine Line(string level,string evt,string message,string data)
        {
            return new LogLine {timestamp_utc=DateTime.UtcNow.ToString("o"),level=level,process_id=Process.GetCurrentProcess().Id,
                thread_id=Thread.CurrentThread.ManagedThreadId,@event=evt,message=message,correlation_id=correlation,data=new LogData{detail=data}};
        }
        static byte[] Encode(LogLine line)
        {
            return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Json.Encode(line))+"\n");
        }
        void Append(string name,LogLine line)
        {
            byte[] bytes=Encode(line);
            if(bytes.Length>MaxBytes) return;
            string path=Path.Combine(directory,name+".jsonl");
            if(File.Exists(path) && new FileInfo(path).Length+bytes.Length>RotateBytes)
            {
                string moved=Path.Combine(directory,name+"-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+"-"+Guid.NewGuid().ToString("N")+".jsonl");
                try
                {
                    File.Move(path,moved);
                    using(var input=File.OpenRead(moved))
                    using(var output=File.Create(moved+".gz"))
                    using(var gzip=new GZipStream(output,CompressionMode.Compress)) input.CopyTo(gzip);
                    File.Delete(moved);
                    foreach(var old in Directory.GetFiles(directory,name+"-*.jsonl.gz").OrderByDescending(f=>f,StringComparer.Ordinal).Skip(3))
                        File.Delete(old);
                }
                catch(IOException)
                {
                    // Never append past the hard limit when compression/rotation fails.
                    // The fallback is bounded too; logging failure must not change repair behavior.
                    path=Path.Combine(directory,name+"-fallback.jsonl");
                    Console.Error.WriteLine("Log rotation failed; using bounded fallback.");
                }
            }
            if(File.Exists(path) && new FileInfo(path).Length+bytes.Length>MaxBytes) return;
            using(var output=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read))
                output.Write(bytes,0,bytes.Length);
        }
        void Locked(Action action)
        {
            // Exclusive OS file sharing serializes writes from separate helper processes.
            // Bounded retries keep an unavailable log directory from blocking application work.
            try
            {
                Directory.CreateDirectory(directory);
                for(int attempt=0;attempt<5;attempt++)
                {
                    FileStream gate;
                    try { gate=new FileStream(Path.Combine(directory,"journal.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
                    catch(IOException) { Thread.Sleep(30);continue; }
                    using(gate) action();
                    return;
                }
            }
            catch(Exception) { /* No recursive logging and no effect on the repair operation. */ }
        }
        DedupState Load()
        {
            string path=Path.Combine(directory,"error-dedup-state.json");
            try { return File.Exists(path)?Json.Decode<DedupState>(File.ReadAllBytes(path)):new DedupState(); }
            catch(Exception) { return new DedupState(); }
        }
        void Save(DedupState state)
        {
            state.entries=state.entries.Where(e=>DateTime.UtcNow-e.last_utc<TimeSpan.FromHours(24))
                .OrderByDescending(e=>e.last_utc).Take(1000).ToList();
            string path=Path.Combine(directory,"error-dedup-state.json"), temporary=path+"."+correlation+".tmp";
            File.WriteAllBytes(temporary,Json.Encode(state));
            if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
        }
        void Summary(DedupEntry entry)
        {
            int suppressed=entry.count-entry.reported;
            if(suppressed<=0) return;
            var line=Line("Error","error_dedup_summary","Repeated errors suppressed",entry.fingerprint+" count="+suppressed);
            Append("full",line);Append("error",line);
            entry.reported=entry.count;
        }
        public void Write(string level,string evt,string message,string data)
        {
            // Call sites pass fixed messages, hashes and exception type names only.
            // No paths, account information, arbitrary exception text or credentials are logged.
            Locked(()=>{
                var state=Load();
                bool error=level=="Error"||level=="Fatal";
                bool bypass=level=="Fatal"||evt=="unhandled"||evt=="data_integrity_failure";
                if(error&&!bypass)
                {
                    string fingerprint=Hash.Bytes(Encoding.UTF8.GetBytes(evt+"\n"+data));
                    var entry=state.entries.FirstOrDefault(e=>e.fingerprint==fingerprint);
                    var now=DateTime.UtcNow;
                    if(entry!=null&&now-entry.first_utc<TimeSpan.FromMinutes(10))
                    {
                        entry.last_utc=now;entry.count++;
                        if(entry.count-entry.reported>=100) Summary(entry);
                        Save(state);return;
                    }
                    if(entry!=null) {Summary(entry);state.entries.Remove(entry);}
                    state.entries.Add(new DedupEntry{fingerprint=fingerprint,first_utc=now,last_utc=now,count=0,reported=0});
                }
                var line=Line(level,evt,message,data);
                Append("full",line);
                if(error) Append("error",line);
                else if(!File.Exists(Path.Combine(directory,"error.jsonl"))) File.WriteAllBytes(Path.Combine(directory,"error.jsonl"),new byte[0]);
                Save(state);
            });
        }
        public void Dispose()
        {
            Locked(()=>{
                var state=Load();
                foreach(var entry in state.entries) Summary(entry);
                Append("full",Line("Info","exit","Repair tool finished",null));
                Save(state);
            });
        }
    }
}
