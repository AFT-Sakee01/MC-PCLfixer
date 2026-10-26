using System.Text.Json;
using System.Xml.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PclRepair;

static class Suite
{
    static int count;
    static void Check(bool value,string name) {if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
    static void Refuses(Action action,string name)
    {
        bool refused=false;
        try {action();} catch(Exception) {refused=true;}
        Check(refused,name);
    }
    static byte[] Mutate(byte[] input,Action<ModuleDefinition> mutate)
    {
        using(var module=Modules.Read(input))
        using(var result=new MemoryStream()) {mutate(module);module.Write(result);return result.ToArray();}
    }
    static int Main(string[] args)
    {
        string fixture=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        byte[] original=File.ReadAllBytes(fixture);
        string originalHash=Hash.Bytes(original);
        Recipe recipe=Repair.LoadRecipe();
        Check(Repair.Inspect(original,recipe).State=="restricted","official restricted input");
        Check(!Repair.Inspect(original,recipe).FullUpdates,"official delta-update marker detected");
        byte[] patched=Repair.Transform(original,recipe);
        Check(Repair.Inspect(patched,recipe).State=="repaired","five repaired methods and dependency guards");
        Check(Repair.Inspect(patched,recipe).FullUpdates,"patched launcher selects official full-package updates");
        byte[] legacy=Mutate(original,m=>{var s=new Symbols(m);foreach(var item in recipe.methods) Bodies.Replace(s.Definitions[item.name],item.body,s);});
        Check(Repair.Inspect(legacy,recipe).State=="repaired"&&!Repair.Inspect(legacy,recipe).FullUpdates,"legacy five-method repair recognized");
        Check(Repair.Inspect(Repair.Transform(legacy,recipe),recipe).FullUpdates,"legacy patch upgraded for complete-package updates");
        byte[] unknownMarker=Mutate(original,m=>{
            var method=new Symbols(m).Definitions[UpdateCompatibility.MethodName];
            var store=method.Body.Instructions.Single(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.FullName=="System.String PCL.ModSecret::UpdateMark");
            store.Previous.Operand="UnknownThirdPartyMarker";
        });
        Refuses(()=>Repair.Transform(unknownMarker,recipe),"unknown update marker refused");
        Check(Repair.Transform(patched,recipe).SequenceEqual(patched),"idempotent repair");
        Check(Hash.File(fixture)==originalHash,"input fixture untouched");
        byte[] future=Mutate(original,m=>m.Assembly.Name.Version=new Version(2,99,1,0));
        Check(Repair.Inspect(Repair.Transform(future,recipe),recipe).Version=="2.99.1.0","compatible update with new version and token layout");
        byte[] changed=Mutate(original,m=>new Symbols(m).Definitions[recipe.methods[0].name].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop)));
        Refuses(()=>Repair.Transform(changed,recipe),"changed restricted method refused");
        byte[] changedDependency=Mutate(original,m=>new Symbols(m).Definitions[recipe.guards[0].name].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop)));
        Refuses(()=>Repair.Transform(changedDependency,recipe),"changed dependency refused");
        byte[] partial=Mutate(original,m=>{var s=new Symbols(m);Bodies.Replace(s.Definitions[recipe.methods[0].name],recipe.methods[0].body,s);});
        Refuses(()=>Repair.Transform(partial,recipe),"partial third-party modification refused");
        Refuses(()=>Repair.Inspect(new byte[]{1,2,3},recipe),"invalid PE refused");
        string target=Path.Combine(root,"PCL.exe");
        File.WriteAllBytes(target,original);
        string ini=Path.Combine(root,"Setup.ini");File.WriteAllText(ini,"SystemSystemUpdate:3\n");
        Repair.Apply(target,recipe);
        string patchedHash=Hash.File(target);
        Check(File.ReadAllBytes(Path.Combine(root,".pcl-repair","backups",originalHash+".exe")).SequenceEqual(original),"exact backup created");
        Check(Repair.Apply(target,recipe).Sha256==patchedHash,"repeat apply does not modify file");
        Check(Repair.Restore(target)==originalHash&&Hash.File(target)==originalHash,"restore exact original");
        File.WriteAllBytes(target,future);
        Check(Repair.Apply(target,recipe).State=="repaired","reapply after compatible upstream replacement");
        Check(Repair.Restore(target)==Hash.Bytes(future),"restore corresponding updated original");
        Check(File.ReadAllText(ini)=="SystemSystemUpdate:3\n","setup ini untouched");
        File.WriteAllBytes(target,original);
        using(var locked=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read))
            Refuses(()=>Repair.Apply(target,recipe),"running or locked target refused");
        Check(Hash.File(target)==originalHash,"locked target left intact");
        Repair.Apply(target,recipe);
        string backup=Path.Combine(root,".pcl-repair","backups",originalHash+".exe");
        File.WriteAllBytes(backup,new byte[]{1,2,3});
        Refuses(()=>Repair.Restore(target),"corrupt backup refused");
        Check(Hash.File(target)==patchedHash,"failed restore leaves target intact");
        File.WriteAllBytes(backup,original);
        Refuses(()=>Repair.Apply(backup,recipe),"backup directory protected");
        string archive=Path.Combine(root,"PCL2DLA.zip");File.WriteAllBytes(archive,new byte[]{80,75,3,4});
        Refuses(()=>Repair.Apply(archive,recipe),"zip cannot be patched");
        Check(File.ReadAllBytes(archive).SequenceEqual(new byte[]{80,75,3,4}),"zip remains byte-identical");
        // Exercise configuration merging independently of launching the UI. The dummy
        // helper is never executed; real CLR/EXE handoff is covered by the process tests.
        File.WriteAllText(Path.Combine(root,AutoRepair.HelperName),"test-only placeholder");
        var verified=Repair.Inspect(File.ReadAllBytes(target),recipe);
        byte[] config=System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><configuration><runtime><gcServer enabled=\"false\"/></runtime><appSettings><add key=\"kept\" value=\"yes\"/></appSettings></configuration>");
        File.WriteAllBytes(target+".config",config);
        AutoRepair.Enable(target,verified);
        Check(XDocument.Load(target+".config").Root!.Element("runtime")!.Element("appDomainManagerType")!.Attribute("value")!.Value==AutoRepair.ManagerType,"local CLR bootstrap configured");
        Check(File.Exists(Path.Combine(root,AutoRepair.AssemblyName+".dll")),"bootstrap DLL installed before use");
        Check(JsonDocument.Parse(File.ReadAllBytes(AutoRepair.StatePath(target))).RootElement.GetProperty("verified_sha256").GetString()==patchedHash,"bootstrap state binds verified executable hash");
        AutoRepair.Enable(target,verified);
        AutoRepair.Disable(target);
        Check(File.ReadAllBytes(target+".config").SequenceEqual(config),"unchanged original config restored byte-for-byte");
        Check(!File.Exists(AutoRepair.StatePath(target)),"disable removes active state");
        AutoRepair.Enable(target,verified);
        var edited=XDocument.Load(target+".config");edited.Root!.Element("appSettings")!.Add(new XElement("add",new XAttribute("key","user_added"),new XAttribute("value","preserve")));
        edited.Save(target+".config");
        AutoRepair.Enable(target,verified);
        AutoRepair.Disable(target);
        var preserved=XDocument.Load(target+".config");
        Check(preserved.Root!.Element("appSettings")!.Elements("add").Any(e=>(string?)e.Attribute("key")=="user_added"),"disable preserves user config edits even after re-enable");
        Check(preserved.Root!.Element("runtime")!.Element("appDomainManagerAssembly")==null,"disable removes only own CLR entries");
        File.Delete(target+".config");
        AutoRepair.Enable(target,verified);
        using(var j=new Journal(Path.Combine(root,"auto-test-logs")))
            Check(!AutoRepair.Resume(target,Int32.MaxValue,1,originalHash,Array.Empty<string>(),recipe,j),"second handoff does not restart an already repaired launcher");
        AutoRepair.Disable(target);
        Check(!File.Exists(target+".config"),"newly created config removed on disable");
        File.WriteAllText(target+".config","<configuration><runtime><appDomainManagerAssembly value=\"Other\"/><appDomainManagerType value=\"Other.Manager\"/></runtime></configuration>");
        string foreignHash=Hash.File(target+".config");
        Refuses(()=>AutoRepair.Enable(target,verified),"foreign CLR bootstrap refused");
        Check(Hash.File(target+".config")==foreignHash,"foreign config untouched");
        File.Delete(target+".config");
        AutoRepair.Enable(target,verified);
        File.WriteAllText(AutoRepair.StatePath(target),"broken");
        AutoRepair.Disable(target);
        Check(XDocument.Load(target+".config").Root!.Element("runtime")!.Element("appDomainManagerType")==null,"damaged state still permits disabling own hook");
        File.Delete(target+".config");File.WriteAllText(AutoRepair.StatePath(target),"broken");
        AutoRepair.Disable(target);
        Check(!File.Exists(AutoRepair.StatePath(target)),"disable cleans damaged state after manual config removal");
        AutoRepair.Enable(target,verified);
        File.Delete(AutoRepair.StatePath(target));
        AutoRepair.Disable(target);
        Check(XDocument.Load(target+".config").Root!.Element("runtime")!.Element("appDomainManagerType")==null,"missing state still permits removing the CLR hook");
        string upgrade=Path.Combine(root,"legacy");Directory.CreateDirectory(Path.Combine(upgrade,".pcl-repair","backups"));
        string legacyTarget=Path.Combine(upgrade,"PCL.exe");
        File.WriteAllBytes(legacyTarget,legacy);
        File.WriteAllBytes(Path.Combine(upgrade,".pcl-repair","backups",originalHash+".exe"),original);
        File.WriteAllBytes(Path.Combine(upgrade,".pcl-repair",Hash.Bytes(legacy)+".json"),Json.Encode(new Receipt{original_sha256=originalHash,patched_sha256=Hash.Bytes(legacy),launcher_version="2.99.0.0"}));
        Repair.Apply(legacyTarget,recipe);
        Check(Repair.Restore(legacyTarget)==originalHash,"legacy upgrade preserves original restore lineage");
        string log=Path.Combine(root,"logs-test");
        using(var journal=new Journal(log))
        {
            journal.Write("Error","test_error","Fixed diagnostic","IOException");
            journal.Write("Error","test_error","Fixed diagnostic","IOException");
            journal.Write("Fatal","test_fatal","Fixed fatal","IOException");
            journal.Write("Fatal","test_fatal","Fixed fatal","IOException");
        }
        using(var journal=new Journal(log)) journal.Write("Error","test_error","Fixed diagnostic","IOException");
        var lines=File.ReadAllLines(Path.Combine(log,"error.jsonl")).Select(s=>JsonDocument.Parse(s)).ToList();
        Check(lines.Count(d=>d.RootElement.GetProperty("event").GetString()=="test_error")==1,"error dedup across restarts");
        Check(lines.Count(d=>d.RootElement.GetProperty("level").GetString()=="Fatal")==2,"fatal errors never suppressed");
        Check(lines.All(d=>d.RootElement.GetProperty("schema_version").GetInt32()==1&&d.RootElement.GetProperty("data").ValueKind==JsonValueKind.Object),"structured JSONL schema");
        Parallel.For(0,16,i=>{using(var j=new Journal(log)) j.Write("Info","parallel","Fixed message",i.ToString());});
        Check(File.ReadAllLines(Path.Combine(log,"full.jsonl")).All(s=>JsonDocument.Parse(s).RootElement.ValueKind==JsonValueKind.Object),"concurrent logs remain parseable");
        using(var journal=new Journal(log)) for(int i=0;i<30;i++) journal.Write("Info","rotation","Bounded test payload",new string('字',250000));
        Check(Directory.GetFiles(log,"full-*.jsonl.gz").Length==3,"only three compressed archives retained");
        Check(Directory.GetFiles(log,"*.jsonl").All(f=>new FileInfo(f).Length<=Journal.MaxBytes),"UTF-8 log size hard limit");
        string invalid=Path.Combine(root,"not-directory");File.WriteAllText(invalid,"x");
        using(var journal=new Journal(invalid)) journal.Write("Error","io","Fixed message","IOException");
        Check(true,"logging failure does not crash");
        count+=FeedTests.Run(original,root);
        if(args.Length>2) count+=EntryTests.Run(args[2],root);
        Check(Hash.File(fixture)==originalHash,"official input still intact after full suite");
        Console.WriteLine("ALL "+count+" CHECKS PASSED");
        return 0;
    }
}
