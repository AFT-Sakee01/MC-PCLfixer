using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace PclRepair
{
    [DataContract]
    public sealed class AutoState
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public string timestamp_utc;
        [DataMember(Order=2)] public string tool_version=Program.Version;
        [DataMember(Order=3)] public string target_name;
        [DataMember(Order=4)] public string verified_sha256;
        [DataMember(Order=5)] public string bootstrap_sha256;
        [DataMember(Order=6)] public string original_config_sha256;
        [DataMember(Order=7)] public string installed_config_sha256;
    }

    public static class AutoRepair
    {
        public const string AssemblyName="PCL.Repair.Bootstrap";
        public const string ManagerType="PclRepair.Bootstrap.Manager";
        public const string HelperName="PCL-Download-Repair.exe";
        public static string StatePath(string target) { return target+".pcl-repair.json"; }
        public static bool IsHash(string value) { return value!=null&&value.Length==64&&value.All(c=>"0123456789abcdef".Contains(c)); }
        static byte[] Bootstrap()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("repair-bootstrap.dll"))
            using(var buffer=new MemoryStream())
            {
                if(stream==null) throw new IOException("Bootstrap component is missing from this build.");
                stream.CopyTo(buffer);return buffer.ToArray();
            }
        }
        static AutoState ReadState(string target)
        {
            var state=Json.Decode<AutoState>(File.ReadAllBytes(StatePath(target)));
            if(state.schema_version!=1||!String.Equals(state.target_name,Path.GetFileName(target),StringComparison.OrdinalIgnoreCase)||!IsHash(state.verified_sha256)
                ||!IsHash(state.bootstrap_sha256)||!IsHash(state.installed_config_sha256)
                ||(state.original_config_sha256!=null&&!IsHash(state.original_config_sha256)))
                throw new IOException("Automatic repair state is invalid. No configuration was changed.");
            return state;
        }
        static XDocument ReadConfig(byte[] bytes)
        {
            using(var input=new MemoryStream(bytes))
            using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024}))
            {
                var doc=XDocument.Load(reader,LoadOptions.PreserveWhitespace);
                if(doc.Root==null||doc.Root.Name!="configuration"||doc.Root.Elements("runtime").Count()>1)
                    throw new IOException("Unsupported launcher configuration.");
                return doc;
            }
        }
        static byte[] EncodeConfig(XDocument doc)
        {
            using(var stream=new MemoryStream()) {doc.Save(stream,SaveOptions.DisableFormatting);return stream.ToArray();}
        }
        static bool OwnsConfig(XDocument doc,bool rejectOther=true)
        {
            var runtime=doc.Root.Element("runtime");
            if(runtime==null) return false;
            var assembly=runtime.Elements("appDomainManagerAssembly").ToList();
            var type=runtime.Elements("appDomainManagerType").ToList();
            if(assembly.Count==0&&type.Count==0) return false;
            if(assembly.Count!=1||type.Count!=1||(string)assembly[0].Attribute("value")!=AssemblyName
                ||(string)type[0].Attribute("value")!=ManagerType)
            {
                if(!rejectOther) return false;
                throw new IOException("A different CLR domain manager is configured. Refusing to replace it.");
            }
            return true;
        }
        static FileStream Gate(string target)
        {
            string dir=Path.Combine(Path.GetDirectoryName(target),".pcl-repair");
            Directory.CreateDirectory(dir);
            return new FileStream(Path.Combine(dir,"auto.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        }
        static void WriteAtomic(string path,byte[] bytes,string expected)
        {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {output.Write(bytes,0,bytes.Length);output.Flush(true);}
                // The gate serializes this tool, while this hash also detects edits by
                // the launcher or user. Never silently overwrite their configuration.
                if((File.Exists(path)?Hash.File(path):null)!=expected) throw new IOException("File changed during automatic repair setup.");
                if(expected==null) File.Move(temp,path);else File.Replace(temp,path,null);
            }
            finally {if(File.Exists(temp)) File.Delete(temp);}
        }
        public static void Enable(string target,Inspection verified)
        {
            target=Repair.ValidatePath(target);
            if(verified.State!="repaired"||Hash.File(target)!=verified.Sha256) throw new IOException("Verify the repaired launcher before enabling automatic repair.");
            if(!File.Exists(Path.Combine(Path.GetDirectoryName(target),HelperName)))
                throw new IOException("Place PCL-Download-Repair.exe beside the launcher before enabling automatic repair.");
            using(var gate=Gate(target)) EnableLocked(target,verified);
        }
        static void EnableLocked(string target,Inspection verified)
        {
            string config=target+".config",statePath=StatePath(target);
            byte[] original=File.Exists(config)?File.ReadAllBytes(config):null;
            string configHash=original==null?null:Hash.Bytes(original);
            var doc=original==null?new XDocument(new XElement("configuration")):ReadConfig(original);
            bool owns=OwnsConfig(doc);
            var previous=File.Exists(statePath)?ReadState(target):null;
            if(owns&&previous==null) throw new IOException("Automatic repair configuration exists without its state. Disable it before reinstalling.");
            byte[] bootstrap=Bootstrap();
            string dll=Path.Combine(Path.GetDirectoryName(target),AssemblyName+".dll");
            string existingDll=File.Exists(dll)?Hash.File(dll):null;
            string dllHash=Hash.Bytes(bootstrap);
            if(existingDll!=null&&existingDll!=dllHash&&(previous==null||existingDll!=previous.bootstrap_sha256))
                throw new IOException("An unrecognized bootstrap DLL already exists.");
            string originalHash=owns?previous.original_config_sha256:configHash;
            if(original!=null&&!owns)
            {
                string backupDir=Path.Combine(Path.GetDirectoryName(target),".pcl-repair","config-backups");
                Directory.CreateDirectory(backupDir);
                string backup=Path.Combine(backupDir,configHash+".config");
                if(!File.Exists(backup)) WriteAtomic(backup,original,null);
                if(Hash.File(backup)!=configHash) throw new IOException("Configuration backup failed verification.");
            }
            var runtime=doc.Root.Element("runtime");
            if(runtime==null) {runtime=new XElement("runtime");doc.Root.Add(runtime);}
            if(!owns)
            {
                runtime.Add(new XElement("appDomainManagerAssembly",new XAttribute("value",AssemblyName)));
                runtime.Add(new XElement("appDomainManagerType",new XAttribute("value",ManagerType)));
            }
            byte[] updatedConfig=owns?original:EncodeConfig(doc);
            var state=new AutoState{timestamp_utc=DateTime.UtcNow.ToString("o"),target_name=Path.GetFileName(target),
                verified_sha256=verified.Sha256,bootstrap_sha256=dllHash,original_config_sha256=originalHash,
                // Preserve the first installed configuration baseline. Refreshing it
                // after a user edit would make Disable mistake that edit for ours
                // and restore the older backup over the user's new settings.
                installed_config_sha256=owns?previous.installed_config_sha256:Hash.Bytes(updatedConfig)};
            // Install in dependency order. A crash before the final config write leaves
            // the old launch path usable; a missing optional state produces one notice.
            if(existingDll!=dllHash) WriteAtomic(dll,bootstrap,existingDll);
            WriteAtomic(statePath,Json.Encode(state),File.Exists(statePath)?Hash.File(statePath):null);
            if(configHash!=Hash.Bytes(updatedConfig)) WriteAtomic(config,updatedConfig,configHash);
        }
        public static void Disable(string target)
        {
            target=Repair.ValidatePath(target);
            using(var gate=Gate(target))
            {
                string config=target+".config",statePath=StatePath(target);
                if(!File.Exists(config))
                {
                    if(File.Exists(statePath)) File.Delete(statePath);
                    return;
                }
                byte[] original=File.ReadAllBytes(config);
                string hash=Hash.Bytes(original);
                var doc=ReadConfig(original);
                if(!OwnsConfig(doc,false))
                {
                    if(File.Exists(statePath)) File.Delete(statePath);
                    return;
                }
                AutoState previous=null;
                try { if(File.Exists(statePath)) previous=ReadState(target); }
                catch(Exception) { /* A damaged optional state must not block removing our own CLR entries. */ }
                if(previous!=null&&hash==previous.installed_config_sha256)
                {
                    if(previous.original_config_sha256==null) File.Delete(config);
                    else
                    {
                        byte[] backup=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(target),".pcl-repair","config-backups",previous.original_config_sha256+".config"));
                        if(Hash.Bytes(backup)!=previous.original_config_sha256) throw new IOException("Configuration backup is damaged.");
                        WriteAtomic(config,backup,hash);
                    }
                }
                else
                {
                    // Preserve edits made since installation, removing only our two
                    // CLR entries. Other settings and other applications are untouched.
                    var runtime=doc.Root.Element("runtime");
                    runtime.Elements("appDomainManagerAssembly").Remove();
                    runtime.Elements("appDomainManagerType").Remove();
                    WriteAtomic(config,EncodeConfig(doc),hash);
                }
                if(File.Exists(statePath)) File.Delete(statePath);
                // The DLL may serve another EXE in this directory, so leave it inert.
            }
        }
        public static Process StartLauncher(string target,string[] args)
        {
            var start=new ProcessStartInfo(Path.GetFullPath(target)){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(target))};
            foreach(var argument in args) start.ArgumentList.Add(argument);
            return Process.Start(start);
        }
        static bool ParentHasExited(int parentId)
        {
            try { using(var process=Process.GetProcessById(parentId)) return process.HasExited; }
            catch(ArgumentException) {return true;}
            catch(System.ComponentModel.Win32Exception) {return false;}
            catch(InvalidOperationException) {return false;}
        }
        public static bool Resume(string target,int parentId,long parentStartTicks,string expectedHash,string[] args,Recipe recipe,Journal journal)
        {
            return Resume(target,parentId,parentStartTicks,expectedHash,args,
                bytes=>new RecipeProfile{Download=recipe,Update=UpdateCompatibility.LoadDefault()},journal);
        }
        public static bool Resume(string target,int parentId,long parentStartTicks,string expectedHash,string[] args,Func<byte[],RecipeProfile> selectProfile,Journal journal)
        {
            target=Repair.ValidatePath(target);
            if(!IsHash(expectedHash)||parentId<=0||parentStartTicks<=0) throw new Unsupported("Invalid bootstrap handoff.");
            var state=ReadState(target);
            if(!OwnsConfig(ReadConfig(File.ReadAllBytes(target+".config")))) throw new Unsupported("Automatic repair is not enabled.");
            try
            {
                using(var parent=Process.GetProcessById(parentId))
                {
                    // Do not kill processes. Start time detects PID reuse; the bootstrap
                    // exits itself before main, leaving all user windows and games alone.
                    if(parent.StartTime.ToUniversalTime().Ticks!=parentStartTicks) throw new IOException("The handoff process ID was reused.");
                    if(!parent.WaitForExit(30000)) throw new IOException("The launcher bootstrap did not exit in time.");
                }
            }
            catch(ArgumentException) { /* The short-lived bootstrap has already exited. */ }
            catch(InvalidOperationException) when(ParentHasExited(parentId)) { }
            catch(System.ComponentModel.Win32Exception) when(ParentHasExited(parentId)) { }
            FileStream gate=null;
            // An owner can wait for a recipe lock (20s), fetch (15s), and validate.
            // Give simultaneous launcher starts time to observe its committed state.
            for(int i=0;i<600&&gate==null;i++)
            {
                try {gate=Gate(target);} catch(IOException) {Thread.Sleep(100);}
            }
            if(gate==null) throw new IOException("Another automatic repair is still in progress.");
            using(gate)
            {
                string actual=Hash.File(target);
                state=ReadState(target);
                // Two simultaneous starts of the updated EXE can hand off twice.
                // Once the first helper commits and restarts, the second does nothing.
                if(actual==state.verified_sha256&&actual!=expectedHash) return false;
                if(actual!=expectedHash) throw new IOException("Launcher changed again during update handoff.");
                // Select only after the bootstrap exits and this handoff holds the
                // local gate. The selector may fetch an authenticated new recipe.
                var repaired=Repair.Apply(target,selectProfile(File.ReadAllBytes(target)),expectedHash);
                EnableLocked(target,repaired);
                journal.Write("Info","auto_repaired","Updated launcher repaired before restart",repaired.Sha256);
                using(var started=StartLauncher(target,args)) { }
                return true;
            }
        }
    }
}
