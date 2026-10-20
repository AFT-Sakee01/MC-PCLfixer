using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclRepair
{
    [DataContract]
    public sealed class UpdateRecipe
    {
        [DataMember(Order=0)] public int schema_version=1;
        [DataMember(Order=1)] public string method_name;
        [DataMember(Order=2)] public string field_name;
        [DataMember(Order=3)] public string original_value;
        [DataMember(Order=4)] public string replacement_value;
        [DataMember(Order=5)] public string before_hash;
        [DataMember(Order=6)] public string after_hash;
    }

    public static class UpdateCompatibility
    {
        public const string MethodName="System.Void PCL.ModSecret::.cctor()";
        public const string FieldName="System.String PCL.ModSecret::UpdateMark";
        public const string ReplacementValue="PCL (Local Download Repair)";
        static readonly Lazy<UpdateRecipe> Recipe=new Lazy<UpdateRecipe>(LoadRecipe);

        static UpdateRecipe LoadRecipe()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("update-recipe.json"))
            using(var buffer=new MemoryStream())
            {
                if(stream==null) throw new Unsupported("Embedded update compatibility recipe is missing.");
                stream.CopyTo(buffer);
                var recipe=Json.Decode<UpdateRecipe>(buffer.ToArray());
                Validate(recipe);
                return recipe;
            }
        }

        public static UpdateRecipe LoadDefault()
        {
            // Return an independent value so consumers cannot mutate the embedded
            // fallback through a reference shared with future repair operations.
            return Json.Decode<UpdateRecipe>(Json.Encode(Recipe.Value));
        }

        public static void Validate(UpdateRecipe recipe)
        {
            if(recipe==null||recipe.schema_version!=1||recipe.method_name!=MethodName
                ||recipe.field_name!=FieldName||String.IsNullOrEmpty(recipe.original_value)
                ||recipe.original_value.Length>16384||recipe.original_value==ReplacementValue
                ||recipe.replacement_value!=ReplacementValue||!Hash.IsSha256(recipe.before_hash)
                ||!Hash.IsSha256(recipe.after_hash)||recipe.before_hash==recipe.after_hash)
                throw new Unsupported("Invalid update compatibility recipe.");
        }

        static MethodDefinition Initializer(ModuleDefinition module)
        {
            var type=module.GetType("PCL.ModSecret");
            var method=type==null?null:type.Methods.SingleOrDefault(m=>m.FullName==MethodName);
            if(method==null||!method.HasBody) throw new Unsupported("Update marker initializer is missing.");
            return method;
        }

        static Instruction MarkerLiteral(MethodDefinition method)
        {
            var stores=method.Body.Instructions.Where(i=>i.OpCode.Code==Code.Stsfld
                &&i.Operand is FieldReference&&((FieldReference)i.Operand).FullName==FieldName).ToList();
            if(stores.Count!=1||stores[0].Previous==null||stores[0].Previous.OpCode.Code!=Code.Ldstr)
                throw new Unsupported("Update marker assignment has changed.");
            return stores[0].Previous;
        }

        public static bool IsPrepared(ModuleDefinition module)
        {
            return IsPrepared(module,Recipe.Value);
        }

        public static bool IsPrepared(ModuleDefinition module,UpdateRecipe recipe)
        {
            Validate(recipe);
            var method=Initializer(module);
            string hash=Bodies.Fingerprint(method);
            bool prepared;
            if(hash==recipe.before_hash) prepared=false;
            else if(hash==recipe.after_hash) prepared=true;
            else throw new Unsupported("Update initializer has changed; no compatibility modification was made.");
            if((string)MarkerLiteral(method).Operand!=(prepared?recipe.replacement_value:recipe.original_value))
                throw new Unsupported("Update marker does not match the reviewed initializer.");
            return prepared;
        }

        public static void Apply(ModuleDefinition module)
        {
            Apply(module,Recipe.Value);
        }

        public static void Apply(ModuleDefinition module,UpdateRecipe recipe)
        {
            if(IsPrepared(module,recipe)) return;
            var method=Initializer(module);
            var literal=MarkerLiteral(method);
            // The embedded recipe audits official release 2.99.0.0; future signed
            // profiles must separately review this same updater contract.
            // Public ModSecret.vb omits updater implementation. In the reviewed
            // IL this field has exactly two references: this initializer writes it,
            // and UpdateStart's loader checks StartsWithF("Empty", false) before
            // choosing PatchUrls. A non-Empty marker selects official DownloadUrls.
            // A modified EXE cannot safely serve as the base for an official delta.
            // Change only this literal, and fingerprint the entire initializer:
            // never copy its unrelated constants, authentication or sponsor code.
            literal.Operand=recipe.replacement_value;
            if(Bodies.Fingerprint(method)!=recipe.after_hash)
            {
                literal.Operand=recipe.original_value;
                throw new Unsupported("Update compatibility verification failed.");
            }
        }
    }
}
