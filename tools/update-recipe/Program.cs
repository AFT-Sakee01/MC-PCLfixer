using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PclRepair;

static class UpdateRecipeGenerator
{
    // Do not export arbitrary initializer contents from unreviewed input.
    const string ReviewedReleaseHash="6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386";

    static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2) throw new InvalidOperationException("Expected <reviewed-official-exe> <new-recipe-json>, or --verify <reviewed-official-exe>.");
            if(args[0]=="--verify") Verify(args[1]);
            else Generate(args[0],args[1]);
            return 0;
        }
        catch(Exception ex)
        {
            // Raw exceptions can contain paths or data from the inspected assembly.
            Console.Error.WriteLine("Update recipe operation refused: "+ex.GetType().Name);
            return 2;
        }
    }

    static ModuleDefinition Reviewed(string path)
    {
        byte[] bytes=File.ReadAllBytes(path);
        if(Hash.Bytes(bytes)!=ReviewedReleaseHash) throw new InvalidOperationException("Input was not the reviewed official release.");
        return Modules.Read(bytes);
    }

    static MethodDefinition Initializer(ModuleDefinition module)
    {
        return module.GetType("PCL.ModSecret").Methods.Single(m=>m.FullName==UpdateCompatibility.MethodName);
    }

    static Instruction Literal(MethodDefinition method)
    {
        return method.Body.Instructions.Single(i=>i.OpCode.Code==Code.Stsfld
            &&i.Operand is FieldReference&&((FieldReference)i.Operand).FullName==UpdateCompatibility.FieldName).Previous;
    }

    static void Generate(string input,string output)
    {
        if(Path.GetExtension(output)!=".json") throw new InvalidOperationException("Output must be a new JSON file.");
        using(var module=Reviewed(input))
        {
            var method=Initializer(module);
            var literal=Literal(method);
            if(literal.OpCode.Code!=Code.Ldstr||(string)literal.Operand!="EmptyMark"+new string('*',812))
                throw new InvalidOperationException("Reviewed marker assignment changed.");
            var references=module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody)
                .SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is FieldReference
                    &&((FieldReference)i.Operand).FullName==UpdateCompatibility.FieldName).ToList();
            if(references.Count!=2||references.Count(i=>i.OpCode.Code==Code.Stsfld)!=1
                ||references.Count(i=>i.OpCode.Code==Code.Ldsfld)!=1)
                throw new InvalidOperationException("Reviewed marker use changed.");
            var recipe=new UpdateRecipe{method_name=UpdateCompatibility.MethodName,
                field_name=UpdateCompatibility.FieldName,original_value=(string)literal.Operand,
                replacement_value=UpdateCompatibility.ReplacementValue,before_hash=Bodies.Fingerprint(method)};
            literal.Operand=recipe.replacement_value;
            recipe.after_hash=Bodies.Fingerprint(method);
            // Persist only the non-secret marker, method identity and fingerprints.
            // No complete ModSecret initializer or other constants leave memory.
            byte[] bytes=Json.Encode(recipe);
            using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {file.Write(bytes,0,bytes.Length);file.Flush(true);}
            Console.WriteLine("before_hash="+recipe.before_hash);
            Console.WriteLine("after_hash="+recipe.after_hash);
        }
    }

    static void Verify(string input)
    {
        using(var module=Reviewed(input))
        {
            if(UpdateCompatibility.IsPrepared(module)) throw new Exception();
            UpdateCompatibility.Apply(module);
            if(!UpdateCompatibility.IsPrepared(module)) throw new Exception();
            string first=Bodies.Fingerprint(Initializer(module));
            UpdateCompatibility.Apply(module);
            if(Bodies.Fingerprint(Initializer(module))!=first) throw new Exception();
            using(var output=new MemoryStream())
            {
                module.Write(output);
                using(var reread=Modules.Read(output.ToArray()))
                    if(!UpdateCompatibility.IsPrepared(reread)) throw new Exception();
            }
            Literal(Initializer(module)).Operand="Unknown marker";
            bool refused=false;
            try { UpdateCompatibility.Apply(module); }
            catch(Unsupported) { refused=true; }
            if(!refused) throw new Exception();
        }
        Console.WriteLine("PASS: original detection, marker change, idempotence, write/reread, unknown marker refusal.");
    }
}
