using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclRepair
{
    public sealed class EmbeddedResolver : DefaultAssemblyResolver
    {
        public ModuleDefinition Source;
        readonly Dictionary<string,AssemblyDefinition> cache = new Dictionary<string,AssemblyDefinition>();
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            AssemblyDefinition value;
            if(cache.TryGetValue(name.Name,out value)) return value;
            // Single-file .NET runtimes and Linux have no .NET Framework installation.
            // Bundle the redistributable reference metadata; never load it for execution.
            if(name.Name=="mscorlib")
            {
                using(var reference=Assembly.GetExecutingAssembly().GetManifestResourceStream("framework-mscorlib.dll"))
                {
                    if(reference!=null)
                    {
                        var buffer=new MemoryStream();reference.CopyTo(buffer);buffer.Position=0;
                        value=AssemblyDefinition.ReadAssembly(buffer,new ReaderParameters{AssemblyResolver=this});
                        cache.Add(name.Name,value);
                        return value;
                    }
                }
            }
            if(Source!=null)
            {
                var pclResources=Source.Resources.OfType<EmbeddedResource>().FirstOrDefault(r=>r.Name=="PCL.Resources.resources");
                if(pclResources!=null)
                {
                    using(var resourceStream=pclResources.GetResourceStream())
                    using(var reader=new ResourceReader(resourceStream))
                    {
                        var entries=reader.GetEnumerator();
                        while(entries.MoveNext())
                        {
                            if(!String.Equals((string)entries.Key,name.Name,StringComparison.OrdinalIgnoreCase)) continue;
                            string typeName; byte[] raw;
                            // GetResourceData avoids deserializing arbitrary resource objects.
                            reader.GetResourceData((string)entries.Key,out typeName,out raw);
                            if(typeName!="ResourceTypeCode.ByteArray" || raw.Length<6
                                || BitConverter.ToInt32(raw,0)!=raw.Length-4 || raw[4]!='M' || raw[5]!='Z')
                                throw new Unsupported("Invalid embedded assembly resource.");
                            value=AssemblyDefinition.ReadAssembly(new MemoryStream(raw,4,raw.Length-4),new ReaderParameters{AssemblyResolver=this});
                            cache.Add(name.Name,value);
                            return value;
                        }
                    }
                }
                var resource=Source.Resources.OfType<EmbeddedResource>().FirstOrDefault(r=>
                    r.Name.Equals("costura."+name.Name.ToLowerInvariant()+".dll.compressed",StringComparison.OrdinalIgnoreCase)
                    || r.Name.Equals("costura."+name.Name.ToLowerInvariant()+".dll",StringComparison.OrdinalIgnoreCase));
                if(resource!=null)
                {
                    using(var input=resource.GetResourceStream())
                    using(var output=new MemoryStream())
                    {
                        if(resource.Name.EndsWith(".compressed"))
                        { using(var unzip=new DeflateStream(input,CompressionMode.Decompress,true)) unzip.CopyTo(output); }
                        else input.CopyTo(output);
                        value=AssemblyDefinition.ReadAssembly(new MemoryStream(output.ToArray()),new ReaderParameters{AssemblyResolver=this});
                        cache.Add(name.Name,value);
                        return value;
                    }
                }
            }
            return base.Resolve(name);
        }
    }
    public static class Modules
    {
        // Costura dependencies are metadata input only; never execute the inspected assembly.
        public static ModuleDefinition Read(byte[] bytes)
        {
            var resolver=new EmbeddedResolver();
            var module=ModuleDefinition.ReadModule(new MemoryStream(bytes),new ReaderParameters{AssemblyResolver=resolver});
            resolver.Source=module;
            return module;
        }
    }
    [DataContract]
    public sealed class Recipe
    {
        [DataMember(Order=0)] public int schema_version = 1;
        [DataMember(Order=1)] public string donor_sha256;
        [DataMember(Order=2)] public string restricted_sha256;
        [DataMember(Order=3)] public List<MethodRecipe> methods = new List<MethodRecipe>();
        [DataMember(Order=4)] public List<GuardRecipe> guards = new List<GuardRecipe>();
    }
    [DataContract]
    public sealed class GuardRecipe
    {
        [DataMember(Order=0)] public string name;
        [DataMember(Order=1)] public string hash;
    }
    [DataContract]
    public sealed class MethodRecipe
    {
        [DataMember(Order=0)] public string name;
        [DataMember(Order=1)] public string before_hash;
        [DataMember(Order=2)] public string after_hash;
        [DataMember(Order=3)] public BodyRecipe body;
    }
    [DataContract]
    public sealed class BodyRecipe
    {
        [DataMember(Order=0)] public bool init_locals;
        [DataMember(Order=1)] public List<string> locals = new List<string>();
        [DataMember(Order=2)] public List<OpRecipe> instructions = new List<OpRecipe>();
        [DataMember(Order=3)] public List<HandlerRecipe> handlers = new List<HandlerRecipe>();
    }
    [DataContract]
    public sealed class OpRecipe
    {
        [DataMember(Order=0)] public string code;
        [DataMember(Order=1)] public string kind;
        [DataMember(Order=2)] public string value;
    }
    [DataContract]
    public sealed class HandlerRecipe
    {
        [DataMember(Order=0)] public string kind;
        [DataMember(Order=1)] public int try_start;
        [DataMember(Order=2)] public int try_end;
        [DataMember(Order=3)] public int handler_start;
        [DataMember(Order=4)] public int handler_end;
        [DataMember(Order=5)] public int filter_start;
        [DataMember(Order=6)] public string catch_type;
    }
    public static class Json
    {
        public static byte[] Encode<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }
        public static T Decode<T>(byte[] data)
        {
            using (var stream = new MemoryStream(data))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }
    public static class Hash
    {
        public static bool IsSha256(string value)
        {
            return value!=null&&value.Length==64&&value.All(c=>"0123456789abcdef".Contains(c));
        }
        public static string Bytes(byte[] bytes)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static string File(string path) { return Bytes(System.IO.File.ReadAllBytes(path)); }
    }
    public sealed class Unsupported : Exception
    {
        public Unsupported(string message) : base(message) {}
    }
    public static class Bodies
    {
        static readonly Dictionary<string, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Name);

        static bool Symbol(string value)
        {
            return !String.IsNullOrWhiteSpace(value)&&value.Length<=16384;
        }
        static bool Index(string value,int count,bool allowThis=false)
        {
            int index;
            return Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out index)
                &&index>=(allowThis?-1:0)&&index<count;
        }
        static bool Hex(string value,int count)
        {
            return value!=null&&value.Length==count&&value.All(c=>"0123456789abcdefABCDEF".Contains(c));
        }
        public static void Validate(BodyRecipe recipe)
        {
            if(recipe==null||recipe.locals==null||recipe.instructions==null||recipe.handlers==null
                ||recipe.locals.Count>65535||recipe.instructions.Count==0||recipe.instructions.Count>100000
                ||recipe.handlers.Count>10000||recipe.locals.Any(name=>!Symbol(name)))
                throw new Unsupported("Invalid recipe method body.");
            int count=recipe.instructions.Count;
            foreach(var op in recipe.instructions)
            {
                OpCode code;
                if(op==null||op.code==null||!Codes.TryGetValue(op.code,out code))
                    throw new Unsupported("Unknown recipe opcode.");
                bool valid=false;
                int intValue; long longValue; sbyte signedByte; byte unsignedByte;
                switch(op.kind)
                {
                    case "none": valid=op.value==null&&code.OperandType==OperandType.InlineNone; break;
                    case "branch": valid=(code.OperandType==OperandType.InlineBrTarget||code.OperandType==OperandType.ShortInlineBrTarget)&&Index(op.value,count); break;
                    case "switch": valid=code.OperandType==OperandType.InlineSwitch&&op.value!=null
                        &&(op.value.Length==0||op.value.Split(',').All(n=>Index(n,count))); break;
                    case "local": valid=(code.OperandType==OperandType.InlineVar||code.OperandType==OperandType.ShortInlineVar)&&Index(op.value,recipe.locals.Count); break;
                    case "parameter": valid=(code.OperandType==OperandType.InlineArg||code.OperandType==OperandType.ShortInlineArg)&&Index(op.value,65535,true); break;
                    case "type": valid=(code.OperandType==OperandType.InlineType||code.OperandType==OperandType.InlineTok)&&Symbol(op.value); break;
                    case "method": valid=(code.OperandType==OperandType.InlineMethod||code.OperandType==OperandType.InlineTok)&&Symbol(op.value); break;
                    case "field": valid=(code.OperandType==OperandType.InlineField||code.OperandType==OperandType.InlineTok)&&Symbol(op.value); break;
                    case "string": valid=code.OperandType==OperandType.InlineString&&op.value!=null&&op.value.Length<=2*1024*1024; break;
                    case "sbyte": valid=code.OperandType==OperandType.ShortInlineI&&SByte.TryParse(op.value,NumberStyles.Integer,CultureInfo.InvariantCulture,out signedByte); break;
                    case "byte": valid=code.OperandType==OperandType.ShortInlineI&&Byte.TryParse(op.value,NumberStyles.Integer,CultureInfo.InvariantCulture,out unsignedByte); break;
                    case "int": valid=code.OperandType==OperandType.InlineI&&Int32.TryParse(op.value,NumberStyles.Integer,CultureInfo.InvariantCulture,out intValue); break;
                    case "long": valid=code.OperandType==OperandType.InlineI8&&Int64.TryParse(op.value,NumberStyles.Integer,CultureInfo.InvariantCulture,out longValue); break;
                    case "float": valid=code.OperandType==OperandType.ShortInlineR&&Hex(op.value,8); break;
                    case "double": valid=code.OperandType==OperandType.InlineR&&Hex(op.value,16); break;
                }
                if(!valid) throw new Unsupported("Recipe operand does not match its opcode or valid range.");
            }
            foreach(var h in recipe.handlers)
            {
                if(h==null||h.try_start<0||h.try_start>=count||h.handler_start<0||h.handler_start>=count
                    ||h.try_end< -1||h.try_end>=count||h.handler_end< -1||h.handler_end>=count
                    ||(h.try_end<0?count:h.try_end)<=h.try_start||(h.handler_end<0?count:h.handler_end)<=h.handler_start
                    ||(h.kind!="Catch"&&h.kind!="Filter"&&h.kind!="Finally"&&h.kind!="Fault")
                    ||(h.kind=="Catch"?!Symbol(h.catch_type):h.catch_type!=null)
                    ||(h.kind=="Filter"?(h.filter_start<0||h.filter_start>=h.handler_start):h.filter_start!=-1))
                    throw new Unsupported("Invalid recipe exception handler.");
            }
        }

        public static BodyRecipe Capture(MethodDefinition method)
        {
            if (!method.HasBody) throw new Unsupported("Method has no IL body: " + method.FullName);
            var body = method.Body;
            var result = new BodyRecipe { init_locals = body.InitLocals };
            foreach (var v in body.Variables) result.locals.Add(v.VariableType.FullName);
            foreach (var instruction in body.Instructions)
            {
                object operand = instruction.Operand;
                var op = new OpRecipe { code = instruction.OpCode.Name, kind = "none", value = null };
                if (operand is Instruction) { op.kind = "branch"; op.value = body.Instructions.IndexOf((Instruction)operand).ToString(); }
                else if (operand is Instruction[]) { op.kind = "switch"; op.value = String.Join(",", ((Instruction[])operand).Select(i => body.Instructions.IndexOf(i).ToString())); }
                else if (operand is VariableDefinition) { op.kind = "local"; op.value = ((VariableDefinition)operand).Index.ToString(); }
                else if (operand is ParameterDefinition) { op.kind = "parameter"; op.value = ((ParameterDefinition)operand).Index.ToString(); }
                else if (operand is MethodReference) { op.kind = "method"; op.value = ((MethodReference)operand).FullName; }
                else if (operand is FieldReference) { op.kind = "field"; op.value = ((FieldReference)operand).FullName; }
                else if (operand is TypeReference) { op.kind = "type"; op.value = ((TypeReference)operand).FullName; }
                else if (operand is string) { op.kind = "string"; op.value = (string)operand; }
                else if (operand is sbyte) { op.kind = "sbyte"; op.value = operand.ToString(); }
                else if (operand is byte) { op.kind = "byte"; op.value = operand.ToString(); }
                else if (operand is int) { op.kind = "int"; op.value = operand.ToString(); }
                else if (operand is long) { op.kind = "long"; op.value = operand.ToString(); }
                // Preserve IEEE bits; decimal round-trip formatting differs between CLR versions.
                else if (operand is float) { op.kind = "float"; op.value = BitConverter.ToInt32(BitConverter.GetBytes((float)operand),0).ToString("X8"); }
                else if (operand is double) { op.kind = "double"; op.value = BitConverter.DoubleToInt64Bits((double)operand).ToString("X16"); }
                else if (operand != null) throw new Unsupported("Unsupported IL operand: " + operand.GetType().Name);
                result.instructions.Add(op);
            }
            Func<Instruction,int> index = i => i == null ? -1 : body.Instructions.IndexOf(i);
            foreach (var h in body.ExceptionHandlers)
                result.handlers.Add(new HandlerRecipe {
                    kind = h.HandlerType.ToString(), try_start = index(h.TryStart), try_end = index(h.TryEnd),
                    handler_start = index(h.HandlerStart), handler_end = index(h.HandlerEnd),
                    filter_start = index(h.FilterStart), catch_type = h.CatchType == null ? null : h.CatchType.FullName
                });
            return result;
        }
        // Symbolic operands make fingerprints independent of metadata tokens and file offsets.
        // We deliberately require all affected method bodies to match; future changed logic is never silently overwritten.
        public static string Fingerprint(MethodDefinition method) { return Hash.Bytes(Json.Encode(Capture(method))); }

        public static void Replace(MethodDefinition method, BodyRecipe recipe, Symbols symbols)
        {
            Validate(recipe);
            var body = new Mono.Cecil.Cil.MethodBody(method) { InitLocals = recipe.init_locals, MaxStackSize = 64 };
            foreach (var name in recipe.locals) body.Variables.Add(new VariableDefinition(symbols.Type(name)));
            foreach (var op in recipe.instructions)
            {
                OpCode code;
                if (!Codes.TryGetValue(op.code, out code)) throw new Unsupported("Unknown opcode.");
                var instruction = Instruction.Create(OpCodes.Nop);
                instruction.OpCode = code;
                body.Instructions.Add(instruction);
            }
            for (int i = 0; i < recipe.instructions.Count; i++)
            {
                var op = recipe.instructions[i];
                object value;
                switch (op.kind)
                {
                    case "none": value = null; break;
                    case "branch": value = body.Instructions[Int32.Parse(op.value)]; break;
                    case "switch": value = op.value.Length==0 ? new Instruction[0] : op.value.Split(',').Select(n => body.Instructions[Int32.Parse(n)]).ToArray(); break;
                    case "local": value = body.Variables[Int32.Parse(op.value)]; break;
                    case "parameter":
                        int parameter=Int32.Parse(op.value,CultureInfo.InvariantCulture);
                        if(parameter>=method.Parameters.Count||(parameter==-1&&!method.HasThis))
                            throw new Unsupported("Recipe parameter does not exist on the target method.");
                        value=parameter==-1?body.ThisParameter:method.Parameters[parameter]; break;
                    case "type": value = symbols.Type(op.value); break;
                    case "method": value = symbols.Method(op.value); break;
                    case "field": value = symbols.Field(op.value); break;
                    case "string": value = op.value; break;
                    case "sbyte": value = SByte.Parse(op.value, CultureInfo.InvariantCulture); break;
                    case "byte": value = Byte.Parse(op.value, CultureInfo.InvariantCulture); break;
                    case "int": value = Int32.Parse(op.value, CultureInfo.InvariantCulture); break;
                    case "long": value = Int64.Parse(op.value, CultureInfo.InvariantCulture); break;
                    case "float": value = BitConverter.ToSingle(BitConverter.GetBytes(UInt32.Parse(op.value,NumberStyles.HexNumber)),0); break;
                    case "double": value = BitConverter.Int64BitsToDouble(unchecked((long)UInt64.Parse(op.value,NumberStyles.HexNumber))); break;
                    default: throw new Unsupported("Unsupported recipe operand.");
                }
                body.Instructions[i].Operand = value;
            }
            Func<int,Instruction> at = n => n < 0 ? null : body.Instructions[n];
            foreach (var h in recipe.handlers)
                body.ExceptionHandlers.Add(new ExceptionHandler((ExceptionHandlerType)Enum.Parse(typeof(ExceptionHandlerType), h.kind)) {
                    TryStart=at(h.try_start), TryEnd=at(h.try_end), HandlerStart=at(h.handler_start),
                    HandlerEnd=at(h.handler_end), FilterStart=at(h.filter_start),
                    CatchType=h.catch_type == null ? null : symbols.Type(h.catch_type)
                });
            method.Body = body;
        }
    }

    public sealed class Symbols
    {
        readonly Dictionary<string,TypeReference> types = new Dictionary<string,TypeReference>();
        readonly Dictionary<string,MethodReference> methods = new Dictionary<string,MethodReference>();
        readonly Dictionary<string,FieldReference> fields = new Dictionary<string,FieldReference>();
        public readonly Dictionary<string,MethodDefinition> Definitions = new Dictionary<string,MethodDefinition>();
        public Symbols(ModuleDefinition module)
        {
            foreach (var t in module.GetTypeReferences()) AddType(t);
            foreach (var m in module.GetMemberReferences()) AddMember(m);
            foreach (var t in module.GetTypes())
            {
                AddType(t);
                foreach (var f in t.Fields) AddMember(f);
                foreach (var m in t.Methods)
                {
                    Definitions.Add(m.FullName,m);
                    AddMember(m);
                    if (!m.HasBody) continue;
                    foreach (var v in m.Body.Variables) AddType(v.VariableType);
                    foreach (var i in m.Body.Instructions) if (i.Operand is MemberReference) AddMember((MemberReference)i.Operand);
                    foreach (var h in m.Body.ExceptionHandlers) if (h.CatchType != null) AddType(h.CatchType);
                }
            }
        }
        void AddType(TypeReference t)
        {
            if (t == null || types.ContainsKey(t.FullName)) return;
            types.Add(t.FullName,t);
            if (t is TypeSpecification) AddType(((TypeSpecification)t).ElementType);
            if (t is GenericInstanceType) foreach (var a in ((GenericInstanceType)t).GenericArguments) AddType(a);
        }
        void AddMember(MemberReference member)
        {
            if (member is TypeReference) { AddType((TypeReference)member); return; }
            AddType(member.DeclaringType);
            var method = member as MethodReference;
            if (method != null)
            {
                methods[method.FullName]=method;
                AddType(method.ReturnType);
                foreach (var p in method.Parameters) AddType(p.ParameterType);
                if (method is GenericInstanceMethod)
                    foreach (var a in ((GenericInstanceMethod)method).GenericArguments) AddType(a);
            }
            var field = member as FieldReference;
            if (field != null) { fields[field.FullName]=field; AddType(field.FieldType); }
        }
        public TypeReference Type(string name)
        {
            TypeReference value;
            if (!types.TryGetValue(name,out value)) throw new Unsupported("Missing type: " + name);
            return value;
        }
        public MethodReference Method(string name)
        {
            MethodReference value;
            if (!methods.TryGetValue(name,out value)) throw new Unsupported("Missing method: " + name);
            return value;
        }
        public FieldReference Field(string name)
        {
            FieldReference value;
            if (!fields.TryGetValue(name,out value)) throw new Unsupported("Missing field: " + name);
            return value;
        }
    }
}
