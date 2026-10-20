using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Mono.Cecil;

namespace PclRepair
{
    [DataContract]
    public sealed class RecipeProfile
    {
        [DataMember(Name="download",Order=0)] public Recipe Download;
        [DataMember(Name="update",Order=1)] public UpdateRecipe Update;
    }
    [DataContract]
    public sealed class Receipt
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public string timestamp_utc;
        [DataMember(Order=2)] public string tool_version=Program.Version;
        [DataMember(Order=3)] public string original_sha256;
        [DataMember(Order=4)] public string patched_sha256;
        [DataMember(Order=5)] public string launcher_version;
    }
    public sealed class Inspection
    {
        public string State;
        public string Version;
        public string Sha256;
        public int Methods;
        public bool FullUpdates;
    }
    public static class Repair
    {
        // A remotely distributed profile may adapt reviewed bodies and fingerprints,
        // but cannot expand the helper's authority to other launcher methods.
        static readonly HashSet<string> AllowedMethods=new HashSet<string>(StringComparer.Ordinal) {
            "System.Void PCL.ModModpack::ModpackInstall()",
            "PCL.ModLoader/LoaderCombo`1<System.String> PCL.ModModpack::ModpackInstall(System.String,System.String,System.String)",
            "System.Void PCL.PageDownloadInstall::LoaderInit()",
            "System.Void PCL.PageDownloadModify::LoadInstanceFinished(PCL.ModLoader/LoaderTask`2<PCL.ModMinecraft/McInstance,System.Object>)",
            "System.Void PCL.PageDownloadResourceDetail::InitializeStackResourceVersions(System.Windows.Controls.StackPanel)"
        };
        public static void ValidateRecipe(Recipe recipe)
        {
            if(recipe==null||recipe.schema_version!=1||!Hash.IsSha256(recipe.donor_sha256)
                ||!Hash.IsSha256(recipe.restricted_sha256)||recipe.methods==null
                ||recipe.methods.Count!=AllowedMethods.Count||recipe.guards==null||recipe.guards.Count>10000)
                throw new Unsupported("Invalid download recipe.");
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in recipe.methods)
            {
                if(item==null||item.name==null||!AllowedMethods.Contains(item.name)||!names.Add(item.name)
                    ||!Hash.IsSha256(item.before_hash)||!Hash.IsSha256(item.after_hash)||item.before_hash==item.after_hash)
                    throw new Unsupported("Invalid or unauthorized recipe method.");
                Bodies.Validate(item.body);
                if(Hash.Bytes(Json.Encode(item.body))!=item.after_hash)
                    throw new Unsupported("Recipe body does not match its reviewed output fingerprint.");
            }
            var guards=new HashSet<string>(StringComparer.Ordinal);
            foreach(var guard in recipe.guards)
                if(guard==null||String.IsNullOrWhiteSpace(guard.name)||guard.name.Length>16384
                    ||AllowedMethods.Contains(guard.name)||!guards.Add(guard.name)||!Hash.IsSha256(guard.hash))
                    throw new Unsupported("Invalid or duplicate dependency guard.");
        }
        public static void ValidateProfile(RecipeProfile profile)
        {
            if(profile==null) throw new Unsupported("Repair profile is missing.");
            ValidateRecipe(profile.Download);
            UpdateCompatibility.Validate(profile.Update);
        }
        public static RecipeProfile LoadProfile()
        {
            return new RecipeProfile { Download=LoadRecipe(),Update=UpdateCompatibility.LoadDefault() };
        }
        public static Recipe LoadRecipe()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("download-recipe.json"))
            using(var buffer=new MemoryStream())
            {
                if(stream==null) throw new Unsupported("Embedded repair recipe is missing.");
                stream.CopyTo(buffer);
                var recipe=Json.Decode<Recipe>(buffer.ToArray());
                ValidateRecipe(recipe);
                return recipe;
            }
        }
        public static Inspection Inspect(byte[] bytes,Recipe recipe)
        {
            return Inspect(bytes,new RecipeProfile {Download=recipe,Update=UpdateCompatibility.LoadDefault()});
        }
        public static Inspection Inspect(byte[] bytes,RecipeProfile profile)
        {
            ValidateProfile(profile);
            var recipe=profile.Download;
            using(var module=Modules.Read(bytes))
            {
                if(module.Assembly==null || module.Assembly.Name.Name!="Plain Craft Launcher 2")
                    throw new Unsupported("Target is not Plain Craft Launcher 2.");
                if(module.Assembly.Name.HasPublicKey || (module.Attributes & ModuleAttributes.StrongNameSigned)!=0)
                    throw new Unsupported("Signed assembly requires a separately reviewed recipe.");
                var symbols=new Symbols(module);
                int before=0,after=0;
                foreach(var item in recipe.methods)
                {
                    MethodDefinition method;
                    if(!symbols.Definitions.TryGetValue(item.name,out method)) throw new Unsupported("Required method is missing: "+item.name);
                    string actual=Bodies.Fingerprint(method);
                    if(actual==item.before_hash) before++;
                    else if(actual==item.after_hash) after++;
                    else throw new Unsupported("Method has changed; target was not modified: "+item.name);
                }
                foreach(var guard in recipe.guards)
                {
                    MethodDefinition method;
                    if(!symbols.Definitions.TryGetValue(guard.name,out method) || Bodies.Fingerprint(method)!=guard.hash)
                        throw new Unsupported("A required dependency has changed: "+guard.name);
                }
                // An external profile cannot silently omit the guards generated for
                // direct local IL dependencies. External framework references still
                // resolve through the launcher's existing metadata, never new code.
                var guarded=new HashSet<string>(recipe.guards.Select(g=>g.name),StringComparer.Ordinal);
                foreach(var reference in recipe.methods.SelectMany(m=>m.body.instructions).Where(o=>o.kind=="method").Select(o=>o.value).Distinct())
                {
                    MethodDefinition dependency;
                    if(symbols.Definitions.TryGetValue(reference,out dependency)&&dependency.HasBody
                        &&!AllowedMethods.Contains(reference)&&!guarded.Contains(reference))
                        throw new Unsupported("A required dependency guard is missing: "+reference);
                }
                if(before!=recipe.methods.Count && after!=recipe.methods.Count)
                    throw new Unsupported("Partially modified launcher. Restore its original backup first.");
                return new Inspection{State=before>0?"restricted":"repaired",Version=module.Assembly.Name.Version.ToString(),Sha256=Hash.Bytes(bytes),Methods=recipe.methods.Count,
                    FullUpdates=UpdateCompatibility.IsPrepared(module,profile.Update)};
            }
        }
        static Dictionary<string,string> BodyHashes(ModuleDefinition module)
        {
            return module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToDictionary(m=>m.FullName,m=>Bodies.Fingerprint(m));
        }
        public static byte[] Transform(byte[] bytes,Recipe recipe)
        {
            return Transform(bytes,new RecipeProfile {Download=recipe,Update=UpdateCompatibility.LoadDefault()});
        }
        public static byte[] Transform(byte[] bytes,RecipeProfile profile)
        {
            var initial=Inspect(bytes,profile);
            var recipe=profile.Download;
            if(initial.State=="repaired"&&initial.FullUpdates) return bytes;
            Dictionary<string,string> originalBodies;
            Dictionary<string,string> originalResources;
            byte[] output;
            using(var module=Modules.Read(bytes))
            {
                originalBodies=BodyHashes(module);
                originalResources=module.Resources.OfType<EmbeddedResource>().ToDictionary(r=>r.Name,r=>Hash.Bytes(r.GetResourceData()));
                var symbols=new Symbols(module);
                foreach(var item in recipe.methods)
                    Bodies.Replace(symbols.Definitions[item.name],item.body,symbols);
                UpdateCompatibility.Apply(module,profile.Update);
                using(var stream=new MemoryStream()) { module.Write(stream); output=stream.ToArray(); }
            }
            var completed=Inspect(output,profile);
            if(completed.State!="repaired"||!completed.FullUpdates) throw new Unsupported("Post-write validation failed.");
            // Cecil rewrites metadata tokens. Compare symbolic IL, not raw byte positions,
            // and require all other method bodies and embedded resources to remain identical.
            using(var module=Modules.Read(output))
            {
                var resulting=BodyHashes(module);
                var allowed=new HashSet<string>(recipe.methods.Select(m=>m.name));
                allowed.Add(UpdateCompatibility.MethodName);
                if(resulting.Count!=originalBodies.Count || originalBodies.Any(p=>!resulting.ContainsKey(p.Key) || (!allowed.Contains(p.Key)&&resulting[p.Key]!=p.Value)))
                    throw new Unsupported("Unexpected change outside the download methods and reviewed update marker.");
                var resources=module.Resources.OfType<EmbeddedResource>().ToDictionary(r=>r.Name,r=>Hash.Bytes(r.GetResourceData()));
                if(resources.Count!=originalResources.Count || originalResources.Any(p=>!resources.ContainsKey(p.Key)||resources[p.Key]!=p.Value))
                    throw new Unsupported("Unexpected embedded resource change.");
            }
            return output;
        }
        internal static string ValidatePath(string target)
        {
            string full=Path.GetFullPath(target);
            if(!String.Equals(Path.GetExtension(full),".exe",StringComparison.OrdinalIgnoreCase)) throw new Unsupported("Select a launcher .exe, not an archive.");
            if(full.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(p=>p.Equals("Backups",StringComparison.OrdinalIgnoreCase)||p.Equals(".pcl-repair",StringComparison.OrdinalIgnoreCase)))
                throw new Unsupported("Backup paths are protected.");
            // Refuse link targets: a test copy must not resolve to the user's sole original.
            for(var part=new FileInfo(full) as FileSystemInfo;part!=null;)
            {
                if((part.Attributes&FileAttributes.ReparsePoint)!=0) throw new Unsupported("Symbolic links/reparse points are not patch targets.");
                var file=part as FileInfo;
                part=file!=null?file.Directory:((DirectoryInfo)part).Parent;
            }
            if(new FileInfo(full).Length>128L*1024*1024) throw new Unsupported("Launcher is larger than the reviewed input limit.");
            return full;
        }
        static void WriteNew(string path,byte[] bytes)
        {
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(bytes,0,bytes.Length);output.Flush(true);}
        }
        static void AtomicReplace(string target,byte[] bytes,string expected)
        {
            string temporary=target+".repair-"+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                WriteNew(temporary,bytes);
                if(Hash.File(target)!=expected) throw new IOException("Target changed while preparing repair. No replacement performed.");
                // File.Replace keeps replacement on the same filesystem. A running Windows
                // launcher denies replacement; we never terminate it or fall back to delete.
                File.Replace(temporary,target,null);
            }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        public static Inspection Apply(string target,Recipe recipe)
        {
            return Apply(target,new RecipeProfile {Download=recipe,Update=UpdateCompatibility.LoadDefault()});
        }
        public static Inspection Apply(string target,RecipeProfile profile,string expectedSha256=null)
        {
            ValidateProfile(profile);
            if(expectedSha256!=null&&!Hash.IsSha256(expectedSha256))
                throw new Unsupported("Invalid expected launcher fingerprint.");
            target=ValidatePath(target);
            string state=Path.Combine(Path.GetDirectoryName(target),".pcl-repair");
            Directory.CreateDirectory(state);
            // An exclusive open serializes independent helper invocations for this folder.
            // The lock is released by the OS after a crash; no stale PID lock is required.
            using(var locked=new FileStream(Path.Combine(state,"operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                byte[] original=File.ReadAllBytes(target);
                // Selecting a remote profile can take time. Bind the eventual write
                // to that exact input under the operation lock, even if a concurrent
                // replacement would also happen to match the selected method recipe.
                if(expectedSha256!=null&&Hash.Bytes(original)!=expectedSha256)
                    throw new IOException("Launcher changed while selecting a repair profile. No replacement performed.");
                var info=Inspect(original,profile);
                if(info.State=="repaired"&&info.FullUpdates) return info;
                byte[] patched=Transform(original,profile);
                string backupDir=Path.Combine(state,"backups");
                Directory.CreateDirectory(backupDir);
                string backup=Path.Combine(backupDir,info.Sha256+".exe");
                if(!File.Exists(backup)) WriteNew(backup,original);
                if(Hash.File(backup)!=info.Sha256) throw new IOException("Existing backup failed SHA-256 verification.");
                string restoreHash=info.Sha256;
                // Upgrading a 1.0.0.0 repaired copy adds only the full-update marker.
                // Retain the verified receipt lineage so restore still returns its
                // original official executable, not an intermediate patched build.
                string parentReceipt=Path.Combine(state,info.Sha256+".json");
                if(info.State=="repaired"&&File.Exists(parentReceipt))
                {
                    var parent=Json.Decode<Receipt>(File.ReadAllBytes(parentReceipt));
                    if(parent.schema_version!=1||parent.patched_sha256!=info.Sha256||!AutoRepair.IsHash(parent.original_sha256))
                        throw new IOException("Previous repair receipt is invalid.");
                    if(Hash.File(Path.Combine(backupDir,parent.original_sha256+".exe"))!=parent.original_sha256)
                        throw new IOException("Previous repair backup is damaged.");
                    restoreHash=parent.original_sha256;
                }
                var receipt=new Receipt {timestamp_utc=DateTime.UtcNow.ToString("o"),original_sha256=restoreHash,patched_sha256=Hash.Bytes(patched),launcher_version=info.Version};
                string receiptPath=Path.Combine(state,receipt.patched_sha256+".json");
                if(!File.Exists(receiptPath)) WriteNew(receiptPath,Json.Encode(receipt));
                else
                {
                    var prior=Json.Decode<Receipt>(File.ReadAllBytes(receiptPath));
                    if(prior.original_sha256!=receipt.original_sha256 || prior.patched_sha256!=receipt.patched_sha256)
                        throw new IOException("Repair receipt conflicts with current input.");
                }
                AtomicReplace(target,patched,info.Sha256);
                return Inspect(File.ReadAllBytes(target),profile);
            }
        }
        public static string Restore(string target)
        {
            target=ValidatePath(target);
            string state=Path.Combine(Path.GetDirectoryName(target),".pcl-repair");
            using(var locked=new FileStream(Path.Combine(state,"operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                string current=Hash.File(target);
                var receipt=Json.Decode<Receipt>(File.ReadAllBytes(Path.Combine(state,current+".json")));
                // Receipt data is untrusted: constrain the backup name to an actual SHA-256.
                if(receipt.schema_version!=1 || receipt.patched_sha256!=current || receipt.original_sha256==null
                    || receipt.original_sha256.Length!=64 || receipt.original_sha256.Any(c=>!"0123456789abcdef".Contains(c)))
                    throw new IOException("Invalid restore receipt.");
                byte[] original=File.ReadAllBytes(Path.Combine(state,"backups",receipt.original_sha256+".exe"));
                if(Hash.Bytes(original)!=receipt.original_sha256) throw new IOException("Backup is damaged; restore refused.");
                AtomicReplace(target,original,current);
                return receipt.original_sha256;
            }
        }
    }
}
