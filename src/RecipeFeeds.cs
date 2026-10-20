using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PclRepair
{
    [DataContract]
    public sealed class SignedRecipeFeed
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public string algorithm="RSA-PSS-SHA256";
        [DataMember(Order=2)] public string payload;
        [DataMember(Order=3)] public string signature;
    }
    [DataContract]
    public sealed class RecipeCatalog
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public long sequence;
        [DataMember(Order=2)] public string issued_utc;
        [DataMember(Order=3)] public string expires_utc;
        [DataMember(Order=4)] public string min_tool_version;
        [DataMember(Order=5)] public List<RecipeProfile> profiles=new List<RecipeProfile>();
    }
    [DataContract]
    public sealed class RecipeSource
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public string timestamp_utc;
        [DataMember(Order=2)] public bool enabled=true;
        [DataMember(Order=3)] public string url;
        [DataMember(Order=4)] public string public_key_pem;
    }
    public static class RecipeFeedWire
    {
        public const int MaxBytes=8*1024*1024;
        public static RSA PublicKey(string pem)
        {
            // Trust is provisioned locally once. A feed can never supply a new key,
            // executable helper, assembly, script, or a URL from which to load one.
            if(pem==null||pem.Length>16384||pem.Contains("PRIVATE",StringComparison.Ordinal)
                ||!pem.Trim().StartsWith("-----BEGIN PUBLIC KEY-----",StringComparison.Ordinal)
                ||!pem.Trim().EndsWith("-----END PUBLIC KEY-----",StringComparison.Ordinal))
                throw new Unsupported("A public RSA SubjectPublicKeyInfo PEM is required.");
            var rsa=RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
                if(rsa.KeySize<2048||rsa.KeySize>4096) throw new Unsupported("Unsupported RSA public key size.");
                return rsa;
            }
            catch {rsa.Dispose();throw;}
        }
        public static bool IsFresh(RecipeCatalog catalog)
        {
            return ParseTime(catalog.expires_utc)>DateTimeOffset.UtcNow;
        }
        static DateTimeOffset ParseTime(string text)
        {
            DateTimeOffset value;
            if(text==null||!text.EndsWith("Z",StringComparison.Ordinal)
                ||!DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out value))
                throw new Unsupported("Recipe catalog timestamps must be UTC.");
            return value;
        }
        public static RecipeCatalog Verify(byte[] bytes,string publicKeyPem,bool requireFresh=true)
        {
            if(bytes==null||bytes.Length==0||bytes.Length>MaxBytes) throw new Unsupported("Recipe feed exceeds its size limit.");
            var envelope=Json.Decode<SignedRecipeFeed>(bytes);
            if(envelope==null||envelope.schema_version!=1||envelope.algorithm!="RSA-PSS-SHA256"
                ||envelope.payload==null||envelope.signature==null||envelope.signature.Length>1024)
                throw new Unsupported("Unsupported signed recipe feed.");
            byte[] payload=Convert.FromBase64String(envelope.payload),signature=Convert.FromBase64String(envelope.signature);
            // Verify the exact bytes before decoding any recipe or symbolic IL.
            using(var key=PublicKey(publicKeyPem))
                if(!key.VerifyData(payload,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))
                    throw new Unsupported("Recipe publisher signature is invalid.");
            var catalog=Json.Decode<RecipeCatalog>(payload);
            ValidateCatalog(catalog,requireFresh);
            return catalog;
        }
        public static void ValidateCatalog(RecipeCatalog catalog,bool requireFresh=true)
        {
            Version minimum;
            if(catalog==null||catalog.schema_version!=1||catalog.sequence<1
                ||!Version.TryParse(catalog.min_tool_version,out minimum)||minimum.Revision<0
                ||minimum>Version.Parse(Program.Version)||catalog.profiles==null
                ||catalog.profiles.Count<1||catalog.profiles.Count>16)
                throw new Unsupported("Recipe catalog needs a different helper or schema.");
            var issued=ParseTime(catalog.issued_utc);
            var expires=ParseTime(catalog.expires_utc);
            if(issued>DateTimeOffset.UtcNow.AddMinutes(5)||expires<=issued||(requireFresh&&!IsFresh(catalog)))
                throw new Unsupported("Recipe catalog is expired or not yet valid.");
            foreach(var profile in catalog.profiles) Repair.ValidateProfile(profile);
        }
        public static string PayloadHash(byte[] bytes)
        {
            return Hash.Bytes(Convert.FromBase64String(Json.Decode<SignedRecipeFeed>(bytes).payload));
        }
    }
    public sealed class RecipeFeeds
    {
        readonly string directory;
        readonly Journal journal;
        readonly HttpMessageHandler handler;
        readonly bool useBundledSource;
        public RecipeFeeds(string directory,Journal journal=null,HttpMessageHandler handler=null,bool useBundledSource=false)
        {
            this.directory=Path.GetFullPath(directory);this.journal=journal;this.handler=handler;this.useBundledSource=useBundledSource;
        }
        public string SourcePath { get {return Path.Combine(directory,"recipe-source.json");} }
        static Uri ValidateUrl(string url)
        {
            Uri uri;
            if(url==null||url.Length>2048||!Uri.TryCreate(url,UriKind.Absolute,out uri)
                ||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo!=""||uri.Fragment!=""||uri.Query!="")
                throw new Unsupported("Use a public static HTTPS feed URL without credentials, query or fragment.");
            return uri;
        }
        static byte[] ReadBounded(string path,int limit)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
            {
                if(file.Length<1||file.Length>limit) throw new Unsupported("Recipe file exceeds its size limit.");
                using(var output=new MemoryStream())
                {
                    byte[] buffer=new byte[8192];int count;
                    while((count=file.Read(buffer,0,buffer.Length))>0)
                    {
                        if(output.Length+count>limit) throw new Unsupported("Recipe file exceeds its size limit.");
                        output.Write(buffer,0,count);
                    }
                    return output.ToArray();
                }
            }
        }
        static void AtomicWrite(string path,byte[] bytes)
        {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                if(File.Exists(path)) File.Replace(temp,path,null);else File.Move(temp,path);
            }
            finally {if(File.Exists(temp)) File.Delete(temp);}
        }
        public void Configure(string url,string publicKeyPem)
        {
            var uri=ValidateUrl(url);
            string normalized;
            using(var key=RecipeFeedWire.PublicKey(publicKeyPem)) normalized=key.ExportSubjectPublicKeyInfoPem();
            Directory.CreateDirectory(directory);
            var source=new RecipeSource{timestamp_utc=DateTime.UtcNow.ToString("o"),url=uri.AbsoluteUri,public_key_pem=normalized};
            using(var gate=SourceGate()) AtomicWrite(SourcePath,Json.Encode(source));
            journal?.Write("Info","recipe_source_configured","Recipe source and public key configured",null);
        }
        public void Disable()
        {
            if(!File.Exists(SourcePath)&&!useBundledSource) return;
            using(var gate=SourceGate())
            {
                // Serialize read-modify-write against source-set so a concurrent
                // disable cannot restore an older URL/key over a newly bound source.
                var source=LoadSource();source.enabled=false;source.timestamp_utc=DateTime.UtcNow.ToString("o");
                AtomicWrite(SourcePath,Json.Encode(source));
            }
            journal?.Write("Info","recipe_source_disabled","Remote recipe updates disabled",null);
        }
        RecipeSource LoadSource()
        {
            byte[] bytes;
            if(File.Exists(SourcePath)) bytes=ReadBounded(SourcePath,32768);
            else
            {
                if(!useBundledSource) throw new Unsupported("No recipe source is configured.");
                using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("recipe-source.json"))
                using(var buffer=new MemoryStream())
                {
                    if(stream==null) throw new Unsupported("No bundled recipe source is available.");
                    stream.CopyTo(buffer);bytes=buffer.ToArray();
                }
            }
            var source=Json.Decode<RecipeSource>(bytes);
            if(source==null||source.schema_version!=1) throw new Unsupported("Invalid recipe source configuration.");
            ValidateUrl(source.url);
            using(var key=RecipeFeedWire.PublicKey(source.public_key_pem)) { }
            return source;
        }
        FileStream SourceGate()
        {
            Directory.CreateDirectory(directory);
            for(int i=0;i<50;i++)
            {
                try {return new FileStream(Path.Combine(directory,"recipe-source.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None);}
                catch(IOException) {Thread.Sleep(100);}
            }
            throw new IOException("Recipe source configuration is busy.");
        }
        public string Status()
        {
            if(!File.Exists(SourcePath)&&!useBundledSource) return "NOT CONFIGURED";
            var source=LoadSource();
            // Deliberately omit URLs and filesystem paths from logs and status.
            using(var key=RecipeFeedWire.PublicKey(source.public_key_pem))
                return (source.enabled?"ENABLED":"DISABLED")+" KEY_SHA256="+Hash.Bytes(key.ExportSubjectPublicKeyInfo());
        }
        string CacheDirectory(RecipeSource source)
        {
            return Path.Combine(directory,"recipe-cache",Hash.Bytes(Encoding.UTF8.GetBytes(source.url+"\n"+source.public_key_pem)));
        }
        static RecipeProfile Matching(byte[] input,RecipeCatalog catalog)
        {
            foreach(var profile in catalog.profiles)
            {
                try {Repair.Inspect(input,profile);return profile;}
                catch(Unsupported) { }
            }
            return null;
        }
        byte[] Download(RecipeSource source)
        {
            // No redirects/cookies/credentials: a static feed never gets to change
            // the trust destination. The same deadline also bounds response-body reads.
            using(var client=handler==null
                ?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false})
                :new HttpClient(handler,false))
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                client.Timeout=Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-Local-Download-Repair/"+Program.Version);
                using(var response=client.GetAsync(ValidateUrl(source.url),HttpCompletionOption.ResponseHeadersRead,timeout.Token).GetAwaiter().GetResult())
                {
                    if(response.StatusCode!=System.Net.HttpStatusCode.OK) throw new Unsupported("Recipe feed did not return HTTP 200.");
                    if(response.Content.Headers.ContentLength>RecipeFeedWire.MaxBytes) throw new Unsupported("Recipe feed exceeds its size limit.");
                    using(var stream=response.Content.ReadAsStreamAsync(timeout.Token).GetAwaiter().GetResult())
                    using(var output=new MemoryStream())
                    {
                        byte[] buffer=new byte[8192];int count;
                        while((count=stream.ReadAsync(buffer.AsMemory(),timeout.Token).AsTask().GetAwaiter().GetResult())>0)
                        {
                            if(output.Length+count>RecipeFeedWire.MaxBytes) throw new Unsupported("Recipe feed exceeds its size limit.");
                            output.Write(buffer,0,count);
                        }
                        return output.ToArray();
                    }
                }
            }
        }
        public RecipeProfile Select(byte[] input,bool allowNetwork=true)
        {
            var embedded=Repair.LoadProfile();
            try {Repair.Inspect(input,embedded);return embedded;}
            catch(Unsupported) { }
            if(!File.Exists(SourcePath)&&!useBundledSource)
                throw new Unsupported("No compatible recipe. Configure a recipe source and its publisher public key once.");
            var source=LoadSource();
            if(!source.enabled) throw new Unsupported("No compatible built-in recipe; remote recipe updates are disabled.");
            string cache=CacheDirectory(source),path=Path.Combine(cache,"feed.json");
            // Cache files are authenticated on every use. A corrupt old cache is not
            // silently discarded: that would erase its anti-rollback floor.
            if(File.Exists(path))
            {
                var current=RecipeFeedWire.Verify(ReadBounded(path,RecipeFeedWire.MaxBytes),source.public_key_pem,false);
                var match=RecipeFeedWire.IsFresh(current)?Matching(input,current):null;
                if(match!=null) return match;
            }
            if(!allowNetwork) throw new Unsupported("No compatible local recipe. Use apply/run to check the configured feed.");
            Directory.CreateDirectory(cache);
            FileStream gate=null;
            // Serialize refresh across helper processes, then reread the cache. The
            // previous complete envelope remains usable if a download/write fails.
            for(int i=0;i<200&&gate==null;i++)
            {
                try {gate=new FileStream(Path.Combine(cache,"feed.lock"),FileMode.OpenOrCreate,FileAccess.Write,FileShare.None);}
                catch(IOException) {Thread.Sleep(100);}
            }
            if(gate==null) throw new IOException("Another recipe refresh is still in progress.");
            using(gate)
            {
                byte[] previous=File.Exists(path)?ReadBounded(path,RecipeFeedWire.MaxBytes):null;
                var old=previous==null?null:RecipeFeedWire.Verify(previous,source.public_key_pem,false);
                var reused=old!=null&&RecipeFeedWire.IsFresh(old)?Matching(input,old):null;
                if(reused!=null) return reused;
                byte[] downloaded;
                try {downloaded=Download(source);}
                catch(Exception ex)
                {
                    journal?.Write("Error","recipe_fetch_failed","Recipe feed could not be fetched",ex.GetType().Name);
                    throw new Unsupported("Recipe feed is unavailable. Launcher was not modified; retry by opening PCL later.");
                }
                var next=RecipeFeedWire.Verify(downloaded,source.public_key_pem);
                if(old!=null&&(next.sequence<old.sequence||(next.sequence==old.sequence
                    &&RecipeFeedWire.PayloadHash(downloaded)!=RecipeFeedWire.PayloadHash(previous))))
                    throw new Unsupported("Recipe catalog rollback or sequence reuse was refused.");
                AtomicWrite(path,downloaded);
                journal?.Write("Info","recipe_catalog_verified","Signed recipe catalog verified and cached",next.sequence.ToString(CultureInfo.InvariantCulture));
                var selected=Matching(input,next);
                if(selected==null) throw new Unsupported("Publisher has not released a compatible recipe. Launcher was not modified; retry by opening PCL later.");
                return selected;
            }
        }
        public Inspection Apply(string target)
        {
            target=Repair.ValidatePath(target);
            byte[] input=File.ReadAllBytes(target);
            return Repair.Apply(target,Select(input),Hash.Bytes(input));
        }
    }
}
