using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using PclRepair;

class MakeRecipe
{
    static bool entries;
    static bool Selected(MethodDefinition m)
    {
        return (m.DeclaringType.FullName=="PCL.ModModpack" && m.Name=="ModpackInstall")
            || (m.DeclaringType.FullName=="PCL.PageDownloadInstall" && m.Name=="LoaderInit")
            || (m.DeclaringType.FullName=="PCL.PageDownloadModify" && m.Name=="LoadInstanceFinished")
            || (m.DeclaringType.FullName=="PCL.PageDownloadResourceDetail" && m.Name=="InitializeStackResourceVersions")
            || (entries && ((m.DeclaringType.FullName=="PCL.PageDownloadLeft"&&m.Name==".ctor")
                || (m.DeclaringType.FullName=="PCL.PageResource"&&m.Name=="set_PageType")
                || (m.DeclaringType.FullName=="PCL.PageInstanceModDisabled"&&m.Name=="BtnDownload_Loaded")
                || (m.DeclaringType.FullName=="PCL.ModMinecraft"&&m.Name=="McDownloadClientUpdateHint")
                || (m.DeclaringType.FullName=="PCL.PageSelectRight"&&m.Name=="McInstanceListUI")
                || (m.DeclaringType.FullName=="PCL.PageSelectLeft"&&m.Name=="McFolderListUI")
                || (m.DeclaringType.FullName=="PCL.PageLaunchLeft"&&m.Name=="RefreshButtonsUI")));
    }
    static int Main(string[] args)
    {
        try { return Generate(args); }
        catch(Exception ex) { Console.Error.WriteLine(ex.GetType().FullName+": "+ex.Message); return 1; }
    }
    static int Generate(string[] args)
    {
        entries=args.Length==5&&args[4]=="2.99.0.1";
        if(entries&&Hash.File(args[1])!="0db34e19d9eeb6d496d20fabe786ec16bbd36ba6b0ccdc8d29cb7e909c114397")
            throw new Unsupported("Entry recipe generation requires the reviewed 2.99.0.1 input.");
        using(var donor=Modules.Read(File.ReadAllBytes(args[0])))
        using(var target=Modules.Read(File.ReadAllBytes(args[1])))
        {
            var before=new Symbols(target);
            var donorSymbols=new Symbols(donor);
            var recipe=new Recipe { schema_version=entries?2:1, donor_sha256=Hash.File(args[0]), restricted_sha256=Hash.File(args[1]) };
            foreach(var method in donor.GetTypes().SelectMany(t=>t.Methods).Where(Selected))
            {
                MethodDefinition original;
                if(!before.Definitions.TryGetValue(method.FullName,out original)) throw new Exception("Missing "+method.FullName);
                var body=Bodies.Capture(method);
                // VB renumbers delegate adapters when a new lambda is inserted. Match the
                // compiler helper's body and signature, not its ordinal, while generating
                // this audited recipe. Runtime patching still requires an exact fingerprint.
                foreach(var op in body.instructions.Where(o=>o.kind=="method"))
                {
                    try { before.Method(op.value); }
                    catch(Unsupported)
                    {
                        MethodDefinition helper;
                        if(!donorSymbols.Definitions.TryGetValue(op.value,out helper) || !helper.Name.StartsWith("_Lambda$"))
                            throw;
                        var helperHash=Bodies.Fingerprint(helper);
                        var candidates=before.Definitions.Values.Where(m=>m.HasBody && m.Name.StartsWith("_Lambda$")
                            && m.DeclaringType.FullName==helper.DeclaringType.FullName
                            && m.ReturnType.FullName==helper.ReturnType.FullName
                            && String.Join(",",m.Parameters.Select(p=>p.ParameterType.FullName))==String.Join(",",helper.Parameters.Select(p=>p.ParameterType.FullName))
                            && Bodies.Fingerprint(m)==helperHash).ToList();
                        if(candidates.Count==0) throw new Unsupported("Missing equivalent compiler adapter: "+op.value);
                        // Multiple wrappers with identical signatures and bodies are equivalent.
                        op.value=candidates.OrderBy(m=>m.FullName,StringComparer.Ordinal).First().FullName;
                    }
                }
                recipe.methods.Add(new MethodRecipe {name=method.FullName,before_hash=Bodies.Fingerprint(original),after_hash=Hash.Bytes(Json.Encode(body)),body=body});
                Bodies.Replace(original,body,before);
                Console.WriteLine(method.FullName+" : "+body.instructions.Count+" instructions");
            }
            if(recipe.methods.Count!=(entries?12:5)) throw new Exception("Unexpected reviewed method count.");
            foreach(var reference in recipe.methods.SelectMany(m=>m.body.instructions).Where(o=>o.kind=="method").Select(o=>o.value).Distinct().OrderBy(n=>n,StringComparer.Ordinal))
            {
                MethodDefinition dependency;
                if(before.Definitions.TryGetValue(reference,out dependency) && dependency.HasBody && !Selected(dependency))
                    recipe.guards.Add(new GuardRecipe{name=reference,hash=Bodies.Fingerprint(dependency)});
            }
            Directory.CreateDirectory(Path.GetDirectoryName(args[2]));
            File.WriteAllBytes(args[2],Json.Encode(recipe));
            target.Write(args[3]);
        }
        Console.WriteLine("Recipe and prototype written.");
        return 0;
    }
}
