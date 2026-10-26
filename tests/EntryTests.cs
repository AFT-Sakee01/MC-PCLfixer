using Mono.Cecil;
using Mono.Cecil.Cil;
using PclRepair;

static class EntryTests
{
    public static int Run(string fixture,string root)
    {
        int count=0;
        void Check(bool ok,string name) {if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
        void Refuses(Action action,string name) {bool refused=false;try{action();}catch(Unsupported){refused=true;}Check(refused,name);}
        var input=File.ReadAllBytes(fixture);
        var hash=Hash.Bytes(input);
        var profile=Repair.LoadProfiles().Single(p=>p.Download.schema_version==2);
        var recipe=profile.Download;
        Check(Repair.Inspect(input,profile).Version=="2.99.0.1"&&recipe.methods.Count==12,"official 2.99.0.1 selects twelve-method profile");
        Refuses(()=>Repair.Inspect(input,Repair.LoadProfile()),"old five-method profile rejects hidden-entry release");
        var patched=Repair.Transform(input,profile);
        Check(Repair.Inspect(patched,profile).State=="repaired"&&Repair.Inspect(patched,profile).FullUpdates,"new download entries and full-package update marker restored");
        Check(Repair.Transform(patched,profile).SequenceEqual(patched),"new profile idempotent");
        using(var before=Modules.Read(input))
        using(var after=Modules.Read(patched))
        {
            var bs=new Symbols(before);var ps=new Symbols(after);
            var allowed=recipe.methods.Select(m=>m.name).Append(UpdateCompatibility.MethodName).ToHashSet();
            Check(bs.Definitions.Values.Where(m=>m.HasBody&&!allowed.Contains(m.FullName)).All(m=>Bodies.Fingerprint(m)==Bodies.Fingerprint(ps.Definitions[m.FullName])),"all methods outside reviewed scope unchanged including authentication and remote-settings blacklist");
            Check(bs.Definitions.Values.Where(m=>m.Name=="ServerSub").All(m=>Bodies.Fingerprint(m)==Bodies.Fingerprint(ps.Definitions[m.FullName])),"ServerSub settings protection preserved exactly");
            Check(before.Resources.OfType<EmbeddedResource>().All(r=>r.GetResourceData().SequenceEqual(after.Resources.OfType<EmbeddedResource>().Single(a=>a.Name==r.Name).GetResourceData())),"embedded UI resources unchanged");
        }
        byte[] Mutate(Action<ModuleDefinition> edit) {using var m=Modules.Read(input);using var s=new MemoryStream();edit(m);m.Write(s);return s.ToArray();}
        var partial=Mutate(m=>{var s=new Symbols(m);Bodies.Replace(s.Definitions[recipe.methods[0].name],recipe.methods[0].body,s);});
        Refuses(()=>Repair.Transform(partial,profile),"partial schema-two third-party repair refused");
        var unknown=Mutate(m=>new Symbols(m).Definitions[recipe.methods.Last().name].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop)));
        Refuses(()=>Repair.Transform(unknown,profile),"changed hidden-entry body refused");
        var changedGuard=Mutate(m=>new Symbols(m).Definitions[recipe.guards[0].name].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop)));
        Refuses(()=>Repair.Transform(changedGuard,profile),"new profile changed dependency refused");
        var invalid=Json.Decode<RecipeProfile>(Json.Encode(profile));invalid.Download.methods.RemoveAt(0);
        Refuses(()=>Repair.ValidateProfile(invalid),"schema-two missing method refused");
        invalid=Json.Decode<RecipeProfile>(Json.Encode(profile));invalid.Download.methods[0].name="System.Void PCL.ModSecret::ServerSub()";
        Refuses(()=>Repair.ValidateProfile(invalid),"signed profile cannot expand fixed allowlist");
        string dir=Path.Combine(root,"entries");Directory.CreateDirectory(dir);string target=Path.Combine(dir,"PCL.exe");File.WriteAllBytes(target,input);
        Check(Repair.Apply(target,profile).State=="repaired","new fixture atomic apply");
        Check(File.ReadAllBytes(Path.Combine(dir,".pcl-repair","backups",hash+".exe")).SequenceEqual(input),"new fixture exact backup");
        Check(Repair.Restore(target)==hash&&File.ReadAllBytes(target).SequenceEqual(input),"new fixture exact restore");
        Check(Hash.File(fixture)==hash,"new official fixture untouched");
        return count;
    }
}
