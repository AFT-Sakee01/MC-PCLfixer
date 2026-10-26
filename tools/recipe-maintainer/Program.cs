using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PclRepair;

[DataContract]
sealed class MaintenanceEvidence
{
    [DataMember(Order=0)] public int schema_version=1;
    [DataMember(Order=1)] public string timestamp_utc;
    [DataMember(Order=2)] public string target_sha256;
    [DataMember(Order=3)] public string profile_sha256;
    [DataMember(Order=4)] public int checks_passed;
    [DataMember(Order=5)] public string launcher_version;
    [DataMember(Order=6)] public string patched_sha256;
    [DataMember(Order=7)] public string mode;
    [DataMember(Order=8)] public List<string> check_names;
}

static class RecipeMaintainer
{
    static string ProjectRoot()
    {
        // The maintenance tool is a source-workspace command. Restrict every
        // generated file to this workspace's Work tree, including self-tests.
        for(string current=Path.GetFullPath(Environment.CurrentDirectory);current!=null;current=Path.GetDirectoryName(current))
            if(File.Exists(Path.Combine(current,"AGENTS.md"))&&File.Exists(Path.Combine(current,"src","PclRepair.csproj")))
                return current;
        throw new Unsupported("Run the maintainer from its source workspace.");
    }

    static int Main(string[] args)
    {
        Journal journal=null;
        string operation=args.Length>0&&(args[0]=="prepare"||args[0]=="catalog"||args[0]=="self-test")?args[0]:"invalid";
        try
        {
            journal=new Journal(CheckedPath(Path.Combine(ProjectRoot(),"Work","maintainer-logs"),null,true));
            journal.Write("Info","maintenance_start","Recipe maintenance started",operation);
            if(args.Length==5&&args[0]=="prepare")
            {
                byte[] input=ReadInput(args[1],".exe",128*1024*1024,true);
                var catalog=ReadVerifiedCatalog(args[2],args[3]);
                var evidence=Prepare(input,catalog,args[4]);
                if(Hash.File(CheckedPath(args[1],".exe",true))!=evidence.target_sha256)
                    throw new IOException("Official input changed during maintenance.");
                Console.WriteLine("PREPARED VERSION="+evidence.launcher_version+" MODE="+evidence.mode
                    +" CHECKS="+evidence.checks_passed+" TARGET_SHA256="+evidence.target_sha256);
            }
            else if(args.Length==4&&args[0]=="catalog")
            {
                var catalog=ReadVerifiedCatalog(args[1],args[2]);
                WriteNew(CheckedPath(args[3],".json",true),Json.Encode(catalog));
                Console.WriteLine("VERIFIED CATALOG SEQUENCE="+catalog.sequence.ToString(CultureInfo.InvariantCulture));
            }
            else if(args.Length==3&&args[0]=="self-test")
                SelfTest(args[1],args[2]);
            else
            {
                Console.WriteLine("prepare <official.exe> <verified-current-feed.json> <public.pem> <new-output-dir>");
                Console.WriteLine("catalog <signed-feed.json> <public.pem> <new-output.json>");
                Console.WriteLine("self-test <official-2.99.exe> <new-test-dir>");
                journal.Write("Warning","maintenance_usage","Recipe maintenance arguments refused",operation);
                return 2;
            }
            journal.Write("Info","maintenance_completed","Recipe maintenance completed",operation);
            return 0;
        }
        catch(Exception ex)
        {
            // Raw IL, initializer constants, local paths and arbitrary input text
            // never enter the scheduler's failure logs or notification payload.
            journal?.Write("Error","maintenance_refused","Recipe maintenance refused",ex.GetType().Name);
            Console.Error.WriteLine("Recipe maintenance refused: "+ex.GetType().Name);
            return 2;
        }
        finally {journal?.Dispose();}
    }

    static string CheckedPath(string path,string extension,bool requireWork)
    {
        string full=Path.GetFullPath(path);
        if(extension!=null&&!String.Equals(Path.GetExtension(full),extension,StringComparison.OrdinalIgnoreCase))
            throw new Unsupported("Unexpected maintenance file extension.");
        if(requireWork)
        {
            string work=Path.Combine(ProjectRoot(),"Work")+Path.DirectorySeparatorChar;
            var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            if(!full.StartsWith(work,comparison)) throw new Unsupported("Maintenance output and launcher inputs must be under Work.");
        }
        for(string current=full;current!=null;current=Path.GetDirectoryName(current))
        {
            string name=Path.GetFileName(current);
            if(name.Equals("Backups",StringComparison.OrdinalIgnoreCase)||name.Equals(".pcl-repair",StringComparison.OrdinalIgnoreCase)
                ||name.Equals("PCL2DLA.zip",StringComparison.OrdinalIgnoreCase))
                throw new Unsupported("Protected maintenance path.");
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new Unsupported("Reparse maintenance path is not supported.");
        }
        return full;
    }

    static byte[] ReadInput(string path,string extension,int limit,bool requireWork=false)
    {
        path=CheckedPath(path,extension,requireWork);
        using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(input.Length<1||input.Length>limit) throw new Unsupported("Maintenance input size is outside limits.");
            byte[] bytes=new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            return bytes;
        }
    }

    static void WriteNew(string path,byte[] bytes)
    {
        // CreateNew refuses overwriting an existing artifact. Evidence is written
        // last, so a failed/interrupted preparation cannot look ready to publish.
        if(!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException();
        using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {output.Write(bytes);output.Flush(true);}
    }

    static string NewDirectory(string path)
    {
        path=CheckedPath(path,null,true);
        if(File.Exists(path)||Directory.Exists(path)) throw new IOException("Use a new isolated maintenance directory.");
        if(!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException();
        Directory.CreateDirectory(path);
        // The exclusive marker also arbitrates the unlikely same-path create
        // race; no process can share or reuse another preparation's directory.
        WriteNew(Path.Combine(path,".maintainer-owner"),Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
        return path;
    }

    static string PinnedKey()
    {
        using(var stream=typeof(Repair).Assembly.GetManifestResourceStream("recipe-source.json"))
        using(var buffer=new MemoryStream())
        {
            if(stream==null) throw new Unsupported("Pinned recipe source is missing.");
            stream.CopyTo(buffer);
            var source=Json.Decode<RecipeSource>(buffer.ToArray());
            if(source==null||source.schema_version!=1) throw new Unsupported("Pinned recipe source is invalid.");
            return source.public_key_pem;
        }
    }

    static RecipeCatalog ReadVerifiedCatalog(string feed,string publicKey)
    {
        string pem=new UTF8Encoding(false,true).GetString(ReadInput(publicKey,".pem",16384));
        using(var supplied=RecipeFeedWire.PublicKey(pem))
        using(var pinned=RecipeFeedWire.PublicKey(PinnedKey()))
            if(!supplied.ExportSubjectPublicKeyInfo().SequenceEqual(pinned.ExportSubjectPublicKeyInfo()))
                throw new Unsupported("Maintenance publisher key differs from the helper's pinned key.");
        // An expired authentic catalog still carries its monotonic sequence and
        // reviewed profiles. Only a separately issued fresh catalog is published.
        return RecipeFeedWire.Verify(ReadInput(feed,".json",RecipeFeedWire.MaxBytes),pem,false);
    }

    static BodyRecipe WithoutNops(MethodDefinition method)
    {
        // An IL prefix and its following instruction form one operation. A NOP
        // between them breaks that instruction sequence rather than merely adding
        // an empty step; removing it cannot prove the input's semantic validity.
        // For a run of NOPs after a prefix, its first NOP triggers this check.
        for(int i=1;i<method.Body.Instructions.Count;i++)
            if(method.Body.Instructions[i].OpCode.Code==Code.Nop
                &&method.Body.Instructions[i-1].OpCode.OpCodeType==OpCodeType.Prefix)
                throw new Unsupported("NOP interrupts an IL prefix instruction sequence.");
        var body=Bodies.Capture(method);
        Bodies.Validate(body);
        int count=body.instructions.Count;
        var keep=Enumerable.Range(0,count).Where(i=>body.instructions[i].code!="nop").ToList();
        if(keep.Count==count||keep.Count==0) throw new Unsupported("No provable NOP-only adaptation.");
        var positions=keep.Select((old,index)=>new {old,index}).ToDictionary(p=>p.old,p=>p.index);
        var nextRetained=new int[count];
        int next=-1;
        for(int i=count-1;i>=0;i--)
        {
            int position;
            if(positions.TryGetValue(i,out position)) next=position;
            nextRetained[i]=next;
        }
        Func<int,bool,int> remap=(index,end)=>{
            if(index==-1&&end) return -1;
            if(index<0||index>=count) throw new Unsupported("Invalid canonical IL boundary.");
            // Branches and region starts target the next retained instruction.
            // Only a region end may become end-of-body after trailing NOP removal.
            // Precompute that successor so many branches into a long NOP run
            // cannot turn this bounded input check into a quadratic-time walk.
            if(nextRetained[index]<0) {if(end)return -1;throw new Unsupported("A branch or region start targets trailing NOPs.");}
            return nextRetained[index];
        };
        var instructions=keep.Select(i=>body.instructions[i]).ToList();
        foreach(var op in instructions)
        {
            if(op.kind=="branch") op.value=remap(Int32.Parse(op.value,CultureInfo.InvariantCulture),false).ToString(CultureInfo.InvariantCulture);
            else if(op.kind=="switch"&&op.value.Length>0)
                op.value=String.Join(",",op.value.Split(',').Select(n=>remap(Int32.Parse(n,CultureInfo.InvariantCulture),false).ToString(CultureInfo.InvariantCulture)));
        }
        foreach(var h in body.handlers)
        {
            h.try_start=remap(h.try_start,false);h.try_end=remap(h.try_end,true);
            h.handler_start=remap(h.handler_start,false);h.handler_end=remap(h.handler_end,true);
            if(h.filter_start>=0)h.filter_start=remap(h.filter_start,false);
        }
        body.instructions=instructions;
        Bodies.Validate(body);
        return body;
    }

    static string EquivalentHash(MethodDefinition method,string reviewed)
    {
        string actual=Bodies.Fingerprint(method);
        if(actual==reviewed) return actual;
        if(Hash.Bytes(Json.Encode(WithoutNops(method)))!=reviewed)
            throw new Unsupported("Method contains changes beyond provable NOP insertion.");
        return actual;
    }

    static RecipeProfile Adapt(byte[] input,RecipeProfile reviewed)
    {
        var candidate=Json.Decode<RecipeProfile>(Json.Encode(reviewed));
        using(var module=Modules.Read(input))
        {
            var symbols=new Symbols(module);
            foreach(var item in candidate.Download.methods)
            {
                MethodDefinition method;
                if(!symbols.Definitions.TryGetValue(item.name,out method)) throw new Unsupported("A reviewed method is missing.");
                item.before_hash=EquivalentHash(method,item.before_hash);
            }
            // A changed dependency is never learned from the new executable.
            // Its canonical entire body must match the signed historical hash.
            foreach(var guard in candidate.Download.guards)
            {
                MethodDefinition method;
                if(!symbols.Definitions.TryGetValue(guard.name,out method)) throw new Unsupported("A reviewed dependency is missing.");
                guard.hash=EquivalentHash(method,guard.hash);
            }
            MethodDefinition initializer;
            if(!symbols.Definitions.TryGetValue(UpdateCompatibility.MethodName,out initializer))
                throw new Unsupported("Reviewed update initializer is missing.");
            string before=EquivalentHash(initializer,candidate.Update.before_hash);
            if(before!=candidate.Update.before_hash)
            {
                var stores=initializer.Body.Instructions.Where(i=>i.OpCode.Code==Code.Stsfld
                    &&i.Operand is FieldReference field&&field.FullName==UpdateCompatibility.FieldName).ToList();
                if(stores.Count!=1||stores[0].Previous==null||stores[0].Previous.OpCode.Code!=Code.Ldstr
                    ||(string)stores[0].Previous.Operand!=candidate.Update.original_value)
                    throw new Unsupported("Reviewed update marker assignment changed.");
                var literal=stores[0].Previous;
                candidate.Update.before_hash=before;
                literal.Operand=candidate.Update.replacement_value;
                candidate.Update.after_hash=Bodies.Fingerprint(initializer);
                literal.Operand=candidate.Update.original_value;
            }
        }
        candidate.Download.restricted_sha256=Hash.Bytes(input);
        Repair.ValidateProfile(candidate);
        Repair.Inspect(input,candidate);
        return candidate;
    }

    static RecipeProfile Select(byte[] input,RecipeCatalog catalog,out string mode)
    {
        // Embedded profiles have been reviewed with this maintainer release.
        // The authenticated catalog still supplies sequence/expiry provenance;
        // this fallback bootstraps a newly reviewed profile's first publication.
        var reviewed=catalog.profiles.Concat(Repair.LoadProfiles()).ToList();
        foreach(var profile in reviewed)
        {
            try {Repair.Inspect(input,profile);mode="reviewed_exact";return profile;}
            catch(Unsupported) { }
        }
        foreach(var profile in reviewed)
        {
            try {var adapted=Adapt(input,profile);mode="proven_nop_insertion";return adapted;}
            catch(Unsupported) { }
        }
        throw new Unsupported("No reviewed profile or provable NOP-only adaptation matches.");
    }

    static byte[] Mutate(byte[] input,Action<ModuleDefinition> mutation)
    {
        using(var module=Modules.Read(input))
        using(var output=new MemoryStream())
        {mutation(module);module.Write(output);return output.ToArray();}
    }

    static byte[] ChangedGuard(byte[] input,RecipeProfile profile)
    {
        if(profile.Download.guards.Count==0) throw new Unsupported("Maintenance requires a dependency rejection test.");
        return Mutate(input,module=>{
            var method=new Symbols(module).Definitions[profile.Download.guards[0].name];
            method.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Ldc_I4_0));
            method.Body.Instructions.Insert(1,Instruction.Create(OpCodes.Pop));
        });
    }

    static bool Refused(Action action)
    {
        try {action();return false;}
        catch(Unsupported) {return true;}
    }

    static MaintenanceEvidence Prepare(byte[] input,RecipeCatalog catalog,string output)
    {
        string mode;
        var profile=Select(input,catalog,out mode);
        var checks=new List<string>();
        Action<bool,string> check=(value,name)=>{
            if(!value) throw new InvalidOperationException("Maintenance validation failed.");
            checks.Add(name);
        };
        Repair.ValidateProfile(profile);
        check(true,"profile_schema_and_fixed_scope");
        var initial=Repair.Inspect(input,profile);
        check(initial.State=="restricted","official_input_is_restricted");
        byte[] patched=Repair.Transform(input,profile);
        var verified=Repair.Inspect(patched,profile);
        check(verified.State=="repaired"&&verified.FullUpdates,"transform_scope_resources_and_output");
        check(Repair.Transform(patched,profile).SequenceEqual(patched),"idempotent_transform");
        check(Refused(()=>Repair.Inspect(ChangedGuard(input,profile),profile)),"changed_dependency_refused");
        string directory=NewDirectory(output);
        string target=Path.Combine(directory,"roundtrip.exe");
        WriteNew(target,input);
        var applied=Repair.Apply(target,profile,initial.Sha256);
        check(applied.Sha256==verified.Sha256&&File.ReadAllBytes(target).SequenceEqual(patched),"apply_matches_validated_transform");
        string backup=Path.Combine(directory,".pcl-repair","backups",initial.Sha256+".exe");
        check(File.ReadAllBytes(backup).SequenceEqual(input),"backup_matches_exact_input");
        check(Repair.Restore(target)==initial.Sha256&&File.ReadAllBytes(target).SequenceEqual(input),"restore_matches_exact_input");
        byte[] profileBytes=Json.Encode(profile);
        Repair.ValidateProfile(Json.Decode<RecipeProfile>(profileBytes));
        check(true,"profile_serialization_roundtrip");
        check(Hash.Bytes(input)==initial.Sha256,"in_memory_input_unchanged");
        var evidence=new MaintenanceEvidence {
            timestamp_utc=DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture),
            target_sha256=initial.Sha256,profile_sha256=Hash.Bytes(profileBytes),
            checks_passed=checks.Count,launcher_version=initial.Version,
            patched_sha256=verified.Sha256,mode=mode,check_names=checks
        };
        WriteNew(Path.Combine(directory,"patched-test.exe"),patched);
        WriteNew(Path.Combine(directory,"profile.json"),profileBytes);
        WriteNew(Path.Combine(directory,"evidence.json"),Json.Encode(evidence));
        return evidence;
    }

    static void SelfTest(string official,string output)
    {
        byte[] input=ReadInput(official,".exe",128*1024*1024,true);
        string root=ProjectRoot();
        var catalog=ReadVerifiedCatalog(Path.Combine(root,"feed","stable.json"),Path.Combine(root,"feed","publisher-public.pem"));
        string directory=NewDirectory(output);
        int passed=0;
        Action<bool,string> check=(value,name)=>{
            if(!value) throw new InvalidOperationException("Maintainer self-test failed.");
            passed++;Console.WriteLine("PASS "+name);
        };
        var known=Prepare(input,catalog,Path.Combine(directory,"known"));
        check(known.mode=="reviewed_exact"&&known.checks_passed==10,"known signed profile passes complete isolated roundtrip");
        string selectedMode;
        var profile=Select(input,catalog,out selectedMode);
        byte[] versionOnly=Mutate(input,module=>module.Assembly.Name.Version=new Version(2,99,3,0));
        var next=Prepare(versionOnly,catalog,Path.Combine(directory,"new-version"));
        check(next.mode=="reviewed_exact"&&next.launcher_version=="2.99.3.0","new assembly version reuses reviewed methods without rebasing guards");
        check(next.target_sha256!=known.target_sha256,"simulated new version has a distinct input hash");
        byte[] nopOnly=Mutate(input,module=>{
            var symbols=new Symbols(module);
            var method=profile.Download.methods.Select(m=>symbols.Definitions[m.name]).First(m=>m.Body.Instructions.All(i=>i.OpCode.Code!=Code.Nop));
            method.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop));
            var guard=profile.Download.guards.Select(g=>symbols.Definitions[g.name]).First(m=>m.Body.Instructions.All(i=>i.OpCode.Code!=Code.Nop));
            guard.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop));
            var initializer=symbols.Definitions[UpdateCompatibility.MethodName];
            if(initializer.Body.Instructions.All(i=>i.OpCode.Code!=Code.Nop))
                initializer.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop));
        });
        var adapted=Prepare(nopOnly,catalog,Path.Combine(directory,"nop-only"));
        check(adapted.mode=="proven_nop_insertion"&&adapted.checks_passed==10,"provable NOP-only changes adapt with dependency and update-marker checks");
        using(var prefixModule=ModuleDefinition.CreateModule("PrefixBoundaryFixture",ModuleKind.Dll))
        {
            var type=new TypeDefinition("Fixture","PrefixReader",TypeAttributes.Public|TypeAttributes.Class,prefixModule.TypeSystem.Object);
            prefixModule.Types.Add(type);
            var method=new MethodDefinition("Read",MethodAttributes.Public|MethodAttributes.Static,prefixModule.TypeSystem.Int32);
            type.Methods.Add(method);
            method.Parameters.Add(new ParameterDefinition("value",ParameterAttributes.None,new ByReferenceType(prefixModule.TypeSystem.Int32)));
            var prefix=Instruction.Create(OpCodes.Volatile);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(prefix);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldind_I4));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            string reviewed=Bodies.Fingerprint(method);
            method.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop));
            check(EquivalentHash(method,reviewed)==Bodies.Fingerprint(method),"NOP before an intact volatile-prefixed read remains equivalent");
            int boundary=method.Body.Instructions.IndexOf(prefix)+1;
            method.Body.Instructions.Insert(boundary,Instruction.Create(OpCodes.Nop));
            method.Body.Instructions.Insert(boundary+1,Instruction.Create(OpCodes.Nop));
            check(Refused(()=>EquivalentHash(method,reviewed)),"NOP run between IL prefix and operand instruction is refused");
        }
        check(Refused(()=>{string mode;Select(ChangedGuard(input,profile),catalog,out mode);}),"semantic or unproved dependency changes refused");
        byte[] unknown=Mutate(input,module=>{
            var method=new Symbols(module).Definitions[profile.Download.methods[0].name];
            method.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Ldc_I4_0));
            method.Body.Instructions.Insert(1,Instruction.Create(OpCodes.Pop));
        });
        check(Refused(()=>{string mode;Select(unknown,catalog,out mode);}),"unknown target method change refused");
        var envelope=Json.Decode<SignedRecipeFeed>(ReadInput(Path.Combine(root,"feed","stable.json"),".json",RecipeFeedWire.MaxBytes));
        byte[] signature=Convert.FromBase64String(envelope.signature);signature[0]^=1;
        envelope.signature=Convert.ToBase64String(signature);
        check(Refused(()=>RecipeFeedWire.Verify(Json.Encode(envelope),PinnedKey(),false)),"invalid publisher signature refused");
        using(var otherKey=RSA.Create(2048))
        {
            string otherPublic=Path.Combine(directory,"untrusted-public.pem");
            WriteNew(otherPublic,Encoding.ASCII.GetBytes(otherKey.ExportSubjectPublicKeyInfoPem()));
            check(Refused(()=>ReadVerifiedCatalog(Path.Combine(root,"feed","stable.json"),otherPublic)),
                "different public key cannot rebind the maintenance trust pin");
        }
        check(Refused(()=>CheckedPath(Path.Combine(root,"Backups","protected.exe"),".exe",true)),"protected backup path refused before read or mutation");
        check(Refused(()=>CheckedPath(Path.Combine(root,"outside-work.json"),".json",true)),"generation outside Work refused");
        check(Hash.File(CheckedPath(official,".exe",true))==Hash.Bytes(input),"official fixture remains byte-identical");
        Console.WriteLine("ALL "+passed+" MAINTAINER CHECKS PASSED; 3 preparations each passed 10 isolated checks.");
    }
}
