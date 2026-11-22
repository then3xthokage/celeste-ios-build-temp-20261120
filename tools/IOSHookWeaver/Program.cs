using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using Mono.Cecil;
using Mono.Cecil.Cil;
using celestemeow;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using RuntimeMethodBase = System.Reflection.MethodBase;
using RuntimeMethodInfo = System.Reflection.MethodInfo;
using RuntimeMethodHandle = System.RuntimeMethodHandle;
using RuntimeTypeHandle = System.RuntimeTypeHandle;
using BindingFlags = System.Reflection.BindingFlags;
using FieldInfo = System.Reflection.FieldInfo;

public static class IOSHookWeaver
{
    private const string DispatchKey = "Celeste.IOSHookRuntime.Dispatch";
    private static readonly Dictionary<string, string> IOSStaticLibraries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SDL2"] = "__Internal",
        ["FNA3D"] = "__Internal",
        ["FAudio"] = "__Internal",
        ["libtheorafile"] = "__Internal"
    };
    private static readonly ConcurrentDictionary<ReflectionGetterKey, Delegate> ReflectionGetters = new ConcurrentDictionary<ReflectionGetterKey, Delegate>();

    private readonly struct ReflectionGetterKey : IEquatable<ReflectionGetterKey>
    {
        public readonly Type DeclaringType;
        public readonly string FieldName;
        public readonly Type DelegateType;
        public readonly Type InstanceType;
        public readonly Type ReturnType;

        public ReflectionGetterKey(Type declaringType, string fieldName, Type delegateType, Type instanceType, Type returnType)
        {
            DeclaringType = declaringType;
            FieldName = fieldName;
            DelegateType = delegateType;
            InstanceType = instanceType;
            ReturnType = returnType;
        }

        public bool Equals(ReflectionGetterKey other)
        {
            return DeclaringType == other.DeclaringType && FieldName == other.FieldName &&
                DelegateType == other.DelegateType && InstanceType == other.InstanceType && ReturnType == other.ReturnType;
        }

        public override bool Equals(object obj) => obj is ReflectionGetterKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(DeclaringType, FieldName, DelegateType, InstanceType, ReturnType);
    }

    private sealed class ReflectionFieldGetter<TInstance, TReturn>
    {
        private readonly FieldInfo field;

        public ReflectionFieldGetter(FieldInfo field) => this.field = field;

        public TReturn Get(TInstance instance)
        {
            object value = field.GetValue(field.IsStatic ? null : (object)instance);
            return value == null ? default(TReturn) : (TReturn)value;
        }
    }

    /// <summary>
    /// Creates a field getter without Reflection.Emit. DJMapHelper uses this
    /// path for three Celeste fields that are read through DynamicMethod on JIT
    /// runtimes; reflection keeps the same behavior on the iOS interpreter.
    /// </summary>
    public static Delegate CreateReflectionFieldGetter(Type declaringType, string fieldName, Type delegateType, Type instanceType, Type returnType)
    {
        if (declaringType == null) throw new ArgumentNullException(nameof(declaringType));
        if (fieldName == null) throw new ArgumentNullException(nameof(fieldName));
        if (delegateType == null) throw new ArgumentNullException(nameof(delegateType));
        if (instanceType == null) throw new ArgumentNullException(nameof(instanceType));
        if (returnType == null) throw new ArgumentNullException(nameof(returnType));

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        FieldInfo field = declaringType.GetField(fieldName, flags);
        if (field == null)
        {
            for (Type parent = declaringType.BaseType; field == null && parent != null; parent = parent.BaseType)
                field = parent.GetField(fieldName, flags | BindingFlags.DeclaredOnly);
        }
        if (field == null) return null;
        if (!returnType.IsAssignableFrom(field.FieldType))
            throw new InvalidCastException(field.Name + " is of type " + field.FieldType + ", it cannot be assigned to " + returnType + ".");

        ReflectionGetterKey key = new ReflectionGetterKey(declaringType, fieldName, delegateType, instanceType, returnType);
        return ReflectionGetters.GetOrAdd(key, _ =>
        {
            Type getterType = typeof(ReflectionFieldGetter<,>).MakeGenericType(instanceType, returnType);
            object getter = Activator.CreateInstance(getterType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { field }, null);
            System.Reflection.MethodInfo getMethod = getterType.GetMethod("Get", BindingFlags.Instance | BindingFlags.Public);
            return Delegate.CreateDelegate(delegateType, getter, getMethod);
        });
    }

#if !__IOS__
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: IOSHookWeaver <input-assembly> <output-assembly>");
            return 2;
        }

        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (!File.Exists(input)) throw new FileNotFoundException("Input assembly was not found.", input);
        string assemblyName;
        int woven;
        string[] skipped;
        byte[] result = WeaveAssembly(File.ReadAllBytes(input), out assemblyName, out woven, out skipped, Path.GetDirectoryName(input));
        string outputDirectory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        string temporary = output + ".tmp";
        File.WriteAllBytes(temporary, result);
        File.Move(temporary, output, true);
        Console.WriteLine("Wove " + woven + " methods from " + assemblyName + ".");
        if (skipped.Length > 0)
        {
            Console.WriteLine("Skipped " + skipped.Length + " methods whose signatures cannot be represented by object-based hook dispatch:");
            foreach (string method in skipped) Console.WriteLine("  " + method);
        }
        return 0;
    }
#endif

    public static byte[] WeaveAssembly(byte[] input, out string assemblyName, out int woven, out string[] skippedMethods, string resolverDirectory = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
        if (!string.IsNullOrWhiteSpace(resolverDirectory) && Directory.Exists(resolverDirectory))
        {
            resolver.AddSearchDirectory(resolverDirectory);
            DirectoryInfo parent = Directory.GetParent(resolverDirectory);
            if (parent != null)
            {
                // CI stages game assemblies beside the Everest runtime. Let
                // Cecil resolve those cross-folder references while weaving
                // every runtime assembly.
                AddResolverDirectory(resolver, parent.FullName);
                AddResolverDirectory(resolver, Path.Combine(parent.FullName, "celeste"));
                AddResolverDirectory(resolver, Path.Combine(parent.FullName, "Everest", "lib-ext"));
            }
        }
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(input, false), new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            // Keep mod-only custom attributes lazy: their attribute types may
            // live in another mod assembly that is resolved by Everest's
            // AssemblyLoadContext after this byte image is woven.
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = false
        }))
        using (MemoryStream output = new MemoryStream())
        {
            HashSet<MethodDefinition> directNativeMethods = PatchIOSNativeImports(assembly.MainModule, assembly.Name.Name);
            if (string.Equals(assembly.Name.Name, "DJMapHelper", StringComparison.OrdinalIgnoreCase))
                PatchDjMapHelper(assembly.MainModule);

            List<string> skipped = new List<string>();
            int count = 0;
            foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
            foreach (MethodDefinition method in type.Methods.ToArray())
            {
                string reason = directNativeMethods.Contains(method)
                    ? "rewritten native compatibility method remains direct"
                    : GetSkipReason(method);
                if (reason != null)
                {
                    if (method.HasBody && !method.IsConstructor && !method.IsAbstract && !method.IsPInvokeImpl)
                        skipped.Add(method.FullName + " :: " + reason);
                    continue;
                }
                if (IsAlreadyWoven(method)) continue;
                Weave(method);
                count++;
            }

            assembly.Write(output);
            assemblyName = assembly.Name.Name;
            woven = count;
            skippedMethods = skipped.ToArray();
            return output.ToArray();
        }
    }

    private static HashSet<MethodDefinition> PatchIOSNativeImports(ModuleDefinition module, string assemblyName)
    {
        HashSet<MethodDefinition> directNativeMethods = new HashSet<MethodDefinition>();
        Dictionary<string, ModuleReference> moduleReferences = module.ModuleReferences
            .GroupBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        MethodReference colorConverter = null;
        int staticImportCount = 0;
        int colorImportCount = 0;

        foreach (TypeDefinition type in AllTypes(module.Types).ToArray())
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.IsPInvokeImpl || method.PInvokeInfo == null) continue;
            PInvokeInfo info = method.PInvokeInfo;
            string library = info.Module?.Name;
            string entryPoint = string.IsNullOrEmpty(info.EntryPoint) ? method.Name : info.EntryPoint;

            if (string.Equals(library, "shlwapi.dll", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entryPoint, "ColorHLSToRGB", StringComparison.Ordinal))
            {
                if (!method.IsStatic || method.Parameters.Count != 3 || method.ReturnType.MetadataType != MetadataType.Int32 ||
                    method.Parameters.Any(parameter => parameter.ParameterType.MetadataType != MetadataType.Int32))
                    throw new InvalidDataException(assemblyName + " imports shlwapi!ColorHLSToRGB with an unexpected signature; its iOS shim needs review.");

                if (colorConverter == null)
                {
                    RuntimeMethodInfo helper = typeof(IOSHookWeaver).GetMethod(nameof(ColorHLSToRGB), BindingFlags.Public | BindingFlags.Static);
                    if (helper == null) throw new MissingMethodException(typeof(IOSHookWeaver).FullName, nameof(ColorHLSToRGB));
                    colorConverter = module.ImportReference(helper);
                }
                MethodBody body = new MethodBody(method);
                ILProcessor il = body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_1));
                il.Append(il.Create(OpCodes.Ldarg_2));
                il.Append(il.Create(OpCodes.Call, colorConverter));
                il.Append(il.Create(OpCodes.Ret));
                body.MaxStackSize = 3;
                ConvertPInvokeMethodToManaged(method, body);
                directNativeMethods.Add(method);
                colorImportCount++;
                continue;
            }

            string replacement;
            if (library == null || !IOSStaticLibraries.TryGetValue(library, out replacement)) continue;
            ModuleReference replacementReference;
            if (!moduleReferences.TryGetValue(replacement, out replacementReference))
            {
                replacementReference = new ModuleReference(replacement);
                module.ModuleReferences.Add(replacementReference);
                moduleReferences.Add(replacement, replacementReference);
            }
            method.PInvokeInfo = new PInvokeInfo(info.Attributes, info.EntryPoint, replacementReference);
            staticImportCount++;
        }

        if (staticImportCount > 0 || colorImportCount > 0)
            Console.WriteLine("Applied iOS native compatibility rewrites to " + assemblyName + ": static imports=" + staticImportCount + ", ColorHLSToRGB=" + colorImportCount + ".");
        return directNativeMethods;
    }

    private static void ConvertPInvokeMethodToManaged(MethodDefinition method, MethodBody body)
    {
        method.PInvokeInfo = null;
        method.Attributes &= ~Mono.Cecil.MethodAttributes.PInvokeImpl;
        method.ImplAttributes = Mono.Cecil.MethodImplAttributes.IL | Mono.Cecil.MethodImplAttributes.Managed;
        foreach (CustomAttribute attribute in method.CustomAttributes
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.InteropServices.DllImportAttribute")
            .ToArray())
            method.CustomAttributes.Remove(attribute);
        method.Body = body;
    }

    /// <summary>
    /// Managed equivalent of shlwapi!ColorHLSToRGB for rewritten mod imports.
    /// It preserves Windows WORD truncation and COLORREF channel packing.
    /// </summary>
    public static int ColorHLSToRGB(int hue, int luminance, int saturation)
    {
        hue &= 0xFFFF;
        luminance &= 0xFFFF;
        saturation &= 0xFFFF;

        if (saturation == 0)
        {
            // Windows' export uses 160 as the achromatic hue sentinel; other
            // zero-saturation hue inputs return black on the native API.
            if (hue != 160) return 0;
            int gray = (luminance * 255 / 240) & 0xFF;
            return gray | (gray << 8) | (gray << 16);
        }

        int mid2 = luminance > 120
            ? saturation + luminance - (saturation * luminance + 120) / 240
            : ((saturation + 240) * luminance + 120) / 240;
        int mid1 = luminance * 2 - mid2;
        int red = (ConvertHue(hue + 80, mid1, mid2) * 255 + 120) / 240;
        int green = (ConvertHue(hue, mid1, mid2) * 255 + 120) / 240;
        int blue = (ConvertHue(hue - 80, mid1, mid2) * 255 + 120) / 240;
        return (red & 0xFF) | ((green & 0xFF) << 8) | ((blue & 0xFF) << 16);
    }

    private static int ConvertHue(int hue, int mid1, int mid2)
    {
        if (hue > 240) hue -= 240;
        else if (hue < 0) hue += 240;
        if (hue > 160) return mid1;
        if (hue > 120) hue = 160 - hue;
        else if (hue > 40) return mid2;
        return (hue * (mid2 - mid1) + 20) / 40 + mid1;
    }

    private static void PatchDjMapHelper(ModuleDefinition module)
    {
        TypeDefinition type = AllTypes(module.Types).FirstOrDefault(candidate =>
            candidate.FullName == "Celeste.Mod.DJMapHelper.Extensions.FastReflection");
        MethodDefinition method = type?.Methods.FirstOrDefault(candidate =>
            candidate.Name == "CreateGetDelegateImpl" && candidate.HasGenericParameters && candidate.GenericParameters.Count == 2);
        if (method == null || method.Parameters.Count != 2 || method.Parameters[0].ParameterType.FullName != "System.Type" ||
            method.Parameters[1].ParameterType.MetadataType != MetadataType.String || method.ReturnType.MetadataType == MetadataType.Void)
            throw new InvalidDataException("DJMapHelper's FastReflection.CreateGetDelegateImpl signature changed; its iOS DynamicMethod compatibility rewrite must be reviewed.");

        MethodBody body = new MethodBody(method);
        ILProcessor il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldtoken, method.ReturnType));
        il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), new[] { typeof(RuntimeTypeHandle) }))));
        for (int index = 0; index < 2; index++)
        {
            il.Append(il.Create(OpCodes.Ldtoken, method.GenericParameters[index]));
            il.Append(il.Create(OpCodes.Call, module.ImportReference(typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), new[] { typeof(RuntimeTypeHandle) }))));
        }

        System.Reflection.MethodInfo helper = typeof(IOSHookWeaver).GetMethod(nameof(CreateReflectionFieldGetter), BindingFlags.Public | BindingFlags.Static);
        il.Append(il.Create(OpCodes.Call, module.ImportReference(helper)));
        il.Append(il.Create(OpCodes.Castclass, method.ReturnType));
        il.Append(il.Create(OpCodes.Ret));
        method.Body = body;
        Console.WriteLine("Applied the iOS reflection-backed field getter rewrite to DJMapHelper.");
    }

    private static void AddResolverDirectory(DefaultAssemblyResolver resolver, string directory)
    {
        if (Directory.Exists(directory)) resolver.AddSearchDirectory(directory);
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    private static string GetSkipReason(MethodDefinition method)
    {
        // MonoMod's portable DMD backend calls these methods through a closed
        // generic self type. Keeping this tiny dispatch trampoline unmodified
        // avoids an AOT generic handle edge case while preserving the original
        // generated-code backend behavior.
        if (method.DeclaringType.FullName == "MonoMod.Utils.DMDGenerator`1" && method.Name == "Generate")
            return "MonoMod DMD generic dispatch stays direct for AOT generic method compatibility";
        if (!method.HasBody || method.Body == null || method.IsAbstract || method.IsPInvokeImpl)
            return "method has no movable managed body";
        if (method.IsConstructor && method.IsStatic)
            return "static constructors cannot be gated before type initialization";
        if (method.IsConstructor && FindConstructorChainCall(method) < 0)
            return "constructor chain call is not a straight-line base/this initializer";
        if (method.CallingConvention == MethodCallingConvention.VarArg) return "varargs cannot be boxed for managed dispatch";
        if (method.ReturnType.IsByReference) return "managed-reference return values cannot cross the object dispatch boundary";
        if (ContainsUnsupportedType(method.ReturnType)) return "return value cannot be boxed for managed dispatch";
        if (method.HasParameters && method.Parameters.Any(parameter => ContainsUnsupportedType(parameter.ParameterType)))
            return "one or more parameters cannot be boxed for managed dispatch";
        return null;
    }

    private static bool ContainsUnsupportedType(TypeReference type)
    {
        if (type is ByReferenceType byReference) return ContainsUnsupportedType(byReference.ElementType);
        if (type is GenericInstanceType generic) return generic.GenericArguments.Any(ContainsUnsupportedType);
        if (type is ArrayType array) return ContainsUnsupportedType(array.ElementType);
        if (type is OptionalModifierType optional) return ContainsUnsupportedType(optional.ElementType);
        if (type is RequiredModifierType required) return ContainsUnsupportedType(required.ElementType);
        if (type is PointerType || type is FunctionPointerType || type.MetadataType == MetadataType.TypedByReference) return true;
        if (type.IsByReferenceLike()) return true;
        return false;
    }

    private static bool IsAlreadyWoven(MethodDefinition method)
    {
        string cloneName = IOSHookIdentity.OriginalMethodName(method);
        string fieldName = IOSHookIdentity.GateFieldName(method);
        return method.DeclaringType.Methods.Any(candidate => candidate.Name == cloneName) &&
            method.DeclaringType.Fields.Any(candidate => candidate.Name == fieldName);
    }

    private static void Weave(MethodDefinition method)
    {
        TypeDefinition type = method.DeclaringType;
        string id = IOSHookIdentity.ForMethod(method);
        string originalName = "__ios_original_" + id;
        string gateName = "__ios_hooked_" + id;

        int constructorChainIndex = method.IsConstructor ? FindConstructorChainCall(method) : -1;
        MethodDefinition original = CloneOriginal(method, originalName);
        if (constructorChainIndex >= 0)
        {
            for (int i = 0; i <= constructorChainIndex; i++)
                original.Body.Instructions.RemoveAt(0);
        }
        if (HasValueTypeThis(method))
            CreateValueTypeInvoker(method, original, "__ios_original_static_" + id);

        FieldDefinition gate = new FieldDefinition(
            gateName,
            FieldAttributes.Private | FieldAttributes.Static,
            method.Module.TypeSystem.Boolean);
        type.Fields.Add(gate);
        MethodBody gateBody = CreateGateBody(method, original, gate);
        if (constructorChainIndex >= 0)
            PrependConstructorChainCall(method, gateBody, constructorChainIndex);
        method.Body = gateBody;
    }

    private static int FindConstructorChainCall(MethodDefinition method)
    {
        if (!method.IsConstructor || method.IsStatic || method.Body == null)
            return -1;
        int index = -1;
        for (int i = 0; i < method.Body.Instructions.Count; i++)
        {
            Instruction instruction = method.Body.Instructions[i];
            if (instruction.OpCode.Code != Code.Call || !(instruction.Operand is MethodReference called) || called.Name != ".ctor")
                continue;
            if (called.DeclaringType.FullName == method.DeclaringType.FullName ||
                called.DeclaringType.FullName == method.DeclaringType.BaseType?.FullName)
            {
                index = i;
                break;
            }
        }
        if (index < 0) return -1;

        for (int i = 0; i < index; i++)
        {
            FlowControl flow = method.Body.Instructions[i].OpCode.FlowControl;
            if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch || flow == FlowControl.Return || flow == FlowControl.Throw)
                return -1;
        }

        Dictionary<Instruction, int> positions = method.Body.Instructions
            .Select((instruction, instructionIndex) => new { instruction, instructionIndex })
            .ToDictionary(pair => pair.instruction, pair => pair.instructionIndex);
        for (int i = index + 1; i < method.Body.Instructions.Count; i++)
        {
            object operand = method.Body.Instructions[i].Operand;
            if (operand is Instruction branch && positions.TryGetValue(branch, out int branchIndex) && branchIndex <= index)
                return -1;
            if (operand is Instruction[] branches && branches.Any(branchTarget => positions.TryGetValue(branchTarget, out int branchIndex) && branchIndex <= index))
                return -1;
        }
        foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
        {
            if (BoundaryAtOrBefore(handler.TryStart, index, positions) ||
                BoundaryAtOrBefore(handler.TryEnd, index, positions) ||
                BoundaryAtOrBefore(handler.HandlerStart, index, positions) ||
                BoundaryAtOrBefore(handler.HandlerEnd, index, positions) ||
                BoundaryAtOrBefore(handler.FilterStart, index, positions))
                return -1;
        }
        return index;
    }

    private static bool BoundaryAtOrBefore(Instruction instruction, int boundary, Dictionary<Instruction, int> positions)
    {
        return instruction != null && positions.TryGetValue(instruction, out int index) && index <= boundary;
    }

    private static void PrependConstructorChainCall(MethodDefinition method, MethodBody gateBody, int chainIndex)
    {
        Dictionary<VariableDefinition, VariableDefinition> variables = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (VariableDefinition variable in method.Body.Variables)
        {
            VariableDefinition copy = new VariableDefinition(method.Module.ImportReference(variable.VariableType, method));
            gateBody.Variables.Add(copy);
            variables.Add(variable, copy);
        }
        Dictionary<ParameterDefinition, ParameterDefinition> parameters = method.Parameters.ToDictionary(parameter => parameter, parameter => parameter);
        Dictionary<Instruction, Instruction> instructions = new Dictionary<Instruction, Instruction>();
        for (int i = 0; i <= chainIndex; i++)
            instructions[method.Body.Instructions[i]] = Instruction.Create(OpCodes.Nop);
        List<Instruction> prefix = new List<Instruction>();
        for (int i = 0; i <= chainIndex; i++)
        {
            Instruction source = method.Body.Instructions[i];
            Instruction copy = instructions[source];
            copy.OpCode = source.OpCode;
            copy.Operand = MapOperand(source.Operand, method, instructions, variables, parameters);
            prefix.Add(copy);
        }
        for (int i = prefix.Count - 1; i >= 0; i--)
            gateBody.Instructions.Insert(0, prefix[i]);
        gateBody.MaxStackSize = Math.Max(gateBody.MaxStackSize, method.Body.MaxStackSize);
    }

    private static MethodDefinition CloneOriginal(MethodDefinition source, string name)
    {
        MethodAttributes attributes = MethodAttributes.Private | MethodAttributes.HideBySig;
        if (source.IsStatic) attributes |= MethodAttributes.Static;
        MethodDefinition clone = new MethodDefinition(name, attributes, source.Module.TypeSystem.Void)
        {
            ImplAttributes = source.ImplAttributes & ~(MethodImplAttributes.Runtime | MethodImplAttributes.InternalCall | MethodImplAttributes.Native)
        };
        source.DeclaringType.Methods.Add(clone);

        foreach (GenericParameter parameter in source.GenericParameters)
            clone.GenericParameters.Add(new GenericParameter(parameter.Name, clone) { Attributes = parameter.Attributes });
        for (int i = 0; i < source.GenericParameters.Count; i++)
            foreach (GenericParameterConstraint constraint in source.GenericParameters[i].Constraints)
                clone.GenericParameters[i].Constraints.Add(new GenericParameterConstraint(source.Module.ImportReference(constraint.ConstraintType, clone)));
        clone.ReturnType = source.Module.ImportReference(source.ReturnType, clone);

        Dictionary<ParameterDefinition, ParameterDefinition> parameters = new Dictionary<ParameterDefinition, ParameterDefinition>();
        foreach (ParameterDefinition parameter in source.Parameters)
        {
            ParameterDefinition copy = new ParameterDefinition(parameter.Name, parameter.Attributes, source.Module.ImportReference(parameter.ParameterType, clone));
            clone.Parameters.Add(copy);
            parameters.Add(parameter, copy);
        }

        clone.Body = CloneBody(source, clone, parameters);
        return clone;
    }

    private static MethodDefinition CreateValueTypeInvoker(MethodDefinition source, MethodDefinition original, string name)
    {
        MethodDefinition invoker = new MethodDefinition(name,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            source.Module.TypeSystem.Void)
        {
            ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed
        };
        source.DeclaringType.Methods.Add(invoker);
        CopyGenericParameters(source, invoker);
        invoker.ReturnType = source.Module.ImportReference(source.ReturnType, invoker);

        TypeReference selfType = GetSelfType(source);
        invoker.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None,
            new ByReferenceType(source.Module.ImportReference(selfType, invoker))));
        foreach (ParameterDefinition parameter in source.Parameters)
            invoker.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                source.Module.ImportReference(parameter.ParameterType, invoker)));

        MethodBody body = new MethodBody(invoker) { InitLocals = false, MaxStackSize = Math.Max(source.Parameters.Count + 1, 2) };
        ILProcessor il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        for (int i = 0; i < source.Parameters.Count; i++) il.Append(LoadArgument(il, i + 1));
        MethodReference call = original;
        if (source.HasGenericParameters)
        {
            GenericInstanceMethod genericCall = new GenericInstanceMethod(original);
            foreach (GenericParameter parameter in invoker.GenericParameters) genericCall.GenericArguments.Add(parameter);
            call = genericCall;
        }
        il.Append(il.Create(OpCodes.Call, call));
        il.Append(il.Create(OpCodes.Ret));
        invoker.Body = body;
        return invoker;
    }

    private static void CopyGenericParameters(MethodDefinition source, MethodDefinition destination)
    {
        foreach (GenericParameter parameter in source.GenericParameters)
            destination.GenericParameters.Add(new GenericParameter(parameter.Name, destination) { Attributes = parameter.Attributes });
        for (int i = 0; i < source.GenericParameters.Count; i++)
            foreach (GenericParameterConstraint constraint in source.GenericParameters[i].Constraints)
                destination.GenericParameters[i].Constraints.Add(new GenericParameterConstraint(
                    source.Module.ImportReference(constraint.ConstraintType, destination)));
    }

    private static MethodBody CloneBody(MethodDefinition source, MethodDefinition destination, Dictionary<ParameterDefinition, ParameterDefinition> parameters)
    {
        MethodBody body = new MethodBody(destination)
        {
            InitLocals = source.Body.InitLocals,
            MaxStackSize = Math.Max(source.Body.MaxStackSize, 8)
        };
        Dictionary<VariableDefinition, VariableDefinition> variables = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (VariableDefinition variable in source.Body.Variables)
        {
            VariableDefinition copy = new VariableDefinition(source.Module.ImportReference(variable.VariableType, destination));
            body.Variables.Add(copy);
            variables.Add(variable, copy);
        }

        Dictionary<Instruction, Instruction> instructions = new Dictionary<Instruction, Instruction>();
        foreach (Instruction instruction in source.Body.Instructions)
            instructions.Add(instruction, Instruction.Create(OpCodes.Nop));
        ILProcessor il = body.GetILProcessor();
        foreach (Instruction instruction in source.Body.Instructions)
        {
            Instruction copy = instructions[instruction];
            copy.OpCode = instruction.OpCode;
            copy.Operand = MapOperand(instruction.Operand, destination, instructions, variables, parameters);
            il.Append(copy);
        }
        foreach (ExceptionHandler handler in source.Body.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                CatchType = handler.CatchType,
                TryStart = Map(handler.TryStart, instructions),
                TryEnd = Map(handler.TryEnd, instructions),
                HandlerStart = Map(handler.HandlerStart, instructions),
                HandlerEnd = Map(handler.HandlerEnd, instructions),
                FilterStart = Map(handler.FilterStart, instructions)
            });
        }
        return body;
    }

    private static object MapOperand(object operand, MethodDefinition destination, Dictionary<Instruction, Instruction> instructions,
        Dictionary<VariableDefinition, VariableDefinition> variables, Dictionary<ParameterDefinition, ParameterDefinition> parameters)
    {
        if (operand is Instruction instruction) return instructions[instruction];
        if (operand is Instruction[] targets) return targets.Select(target => instructions[target]).ToArray();
        if (operand is VariableDefinition variable) return variables[variable];
        if (operand is ParameterDefinition parameter) return parameters[parameter];
        if (operand is TypeReference type) return destination.Module.ImportReference(type, destination);
        if (operand is MethodReference method) return destination.Module.ImportReference(method, destination);
        if (operand is FieldReference field) return destination.Module.ImportReference(field, destination);
        if (operand is CallSite callSite) return CloneCallSite(callSite, destination);
        return operand;
    }

    private static CallSite CloneCallSite(CallSite source, MethodDefinition destination)
    {
        CallSite copy = new CallSite(destination.Module.ImportReference(source.ReturnType, destination))
        {
            HasThis = source.HasThis,
            ExplicitThis = source.ExplicitThis,
            CallingConvention = source.CallingConvention
        };
        foreach (ParameterDefinition parameter in source.Parameters)
            copy.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, destination.Module.ImportReference(parameter.ParameterType, destination)));
        return copy;
    }

    private static Instruction Map(Instruction instruction, Dictionary<Instruction, Instruction> map)
    {
        return instruction == null ? null : map[instruction];
    }

    private static MethodBody CreateGateBody(MethodDefinition method, MethodDefinition original, FieldDefinition gate)
    {
        MethodBody body = new MethodBody(method) { InitLocals = true, MaxStackSize = 12 };
        ILProcessor il = body.GetILProcessor();
        Instruction dispatch = Instruction.Create(OpCodes.Nop);

        il.Append(il.Create(OpCodes.Ldsfld, gate));
        il.Append(il.Create(OpCodes.Brtrue, dispatch));
        if (!method.IsStatic) il.Append(il.Create(OpCodes.Ldarg_0));
        foreach (ParameterDefinition parameter in method.Parameters) il.Append(LoadArgument(il, parameter.Index + (method.IsStatic ? 0 : 1)));
        il.Append(il.Create(OpCodes.Call, CreateOriginalCall(original, method)));
        il.Append(il.Create(OpCodes.Ret));

        il.Append(dispatch);
        VariableDefinition arguments = new VariableDefinition(new ArrayType(method.Module.TypeSystem.Object));
        VariableDefinition methodBase = new VariableDefinition(CreateType(method.Module, "System.Reflection", "MethodBase"));
        VariableDefinition dispatcher = new VariableDefinition(CreateType(method.Module, "System", "Delegate"));
        VariableDefinition invocation = new VariableDefinition(new ArrayType(method.Module.TypeSystem.Object));
        body.Variables.Add(arguments);
        body.Variables.Add(methodBase);
        body.Variables.Add(dispatcher);
        body.Variables.Add(invocation);

        il.Append(EmitMethodHandle(il, method));
        il.Append(il.Create(OpCodes.Stloc, methodBase));

        il.Append(il.Create(OpCodes.Call, CreateCurrentDomainGetter(method.Module)));
        il.Append(il.Create(OpCodes.Ldstr, DispatchKey));
        il.Append(il.Create(OpCodes.Callvirt, CreateGetData(method.Module)));
        il.Append(il.Create(OpCodes.Castclass, dispatcher.VariableType));
        il.Append(il.Create(OpCodes.Stloc, dispatcher));

        il.Append(il.Create(OpCodes.Ldc_I4_3));
        il.Append(il.Create(OpCodes.Newarr, method.Module.TypeSystem.Object));
        il.Append(il.Create(OpCodes.Stloc, invocation));
        StoreInvocationValue(il, invocation, 0, il.Create(OpCodes.Ldloc, methodBase));
        if (method.IsStatic)
            StoreInvocationValue(il, invocation, 1, il.Create(OpCodes.Ldnull));
        else if (HasValueTypeThis(method))
            StoreInvocationValue(il, invocation, 1, il.Create(OpCodes.Ldnull));
        else
        {
            il.Append(il.Create(OpCodes.Ldloc, invocation));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }
        EmitArgumentArray(il, method, arguments);
        il.Append(il.Create(OpCodes.Ldloc, invocation));
        il.Append(il.Create(OpCodes.Ldc_I4_2));
        il.Append(il.Create(OpCodes.Ldloc, arguments));
        il.Append(il.Create(OpCodes.Stelem_Ref));
        il.Append(il.Create(OpCodes.Ldloc, dispatcher));
        il.Append(il.Create(OpCodes.Ldloc, invocation));
        il.Append(il.Create(OpCodes.Callvirt, CreateDynamicInvoke(method.Module)));

        VariableDefinition result = null;
        if (IsVoidReturnType(method.ReturnType))
            il.Append(il.Create(OpCodes.Pop));
        else
        {
            result = new VariableDefinition(method.Module.TypeSystem.Object);
            body.Variables.Add(result);
            il.Append(il.Create(OpCodes.Stloc, result));
        }

        EmitInstanceCopyBack(il, method, arguments);
        EmitByRefCopyBack(il, method, arguments);
        if (result != null)
        {
            il.Append(il.Create(OpCodes.Ldloc, result));
            if (method.ReturnType.IsValueType || method.ReturnType is GenericParameter)
                il.Append(il.Create(OpCodes.Unbox_Any, method.ReturnType));
            else
                il.Append(il.Create(OpCodes.Castclass, method.ReturnType));
        }
        il.Append(il.Create(OpCodes.Ret));
        return body;
    }

    private static bool IsVoidReturnType(TypeReference type)
    {
        while (true)
        {
            if (type is OptionalModifierType optional) type = optional.ElementType;
            else if (type is RequiredModifierType required) type = required.ElementType;
            else break;
        }
        return type.MetadataType == MetadataType.Void;
    }

    private static Instruction EmitMethodHandle(ILProcessor il, MethodDefinition method)
    {
        ModuleDefinition module = method.Module;
        MethodReference getMethod;
        MethodReference methodToken = method;
        if (method.HasGenericParameters)
        {
            GenericInstanceMethod instantiation = new GenericInstanceMethod(method);
            foreach (GenericParameter parameter in method.GenericParameters) instantiation.GenericArguments.Add(parameter);
            methodToken = instantiation;
        }
        Instruction loadMethod = il.Create(OpCodes.Ldtoken, methodToken);
        if (method.DeclaringType.HasGenericParameters)
        {
            il.Append(loadMethod);
            GenericInstanceType declaringType = new GenericInstanceType(method.DeclaringType);
            foreach (GenericParameter parameter in method.DeclaringType.GenericParameters) declaringType.GenericArguments.Add(parameter);
            il.Append(il.Create(OpCodes.Ldtoken, declaringType));
            RuntimeMethodInfo twoHandles = typeof(RuntimeMethodBase).GetMethod("GetMethodFromHandle", new[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) });
            getMethod = module.ImportReference(twoHandles);
        }
        else
        {
            il.Append(loadMethod);
            RuntimeMethodInfo oneHandle = typeof(RuntimeMethodBase).GetMethod("GetMethodFromHandle", new[] { typeof(RuntimeMethodHandle) });
            getMethod = module.ImportReference(oneHandle);
        }
        return il.Create(OpCodes.Call, getMethod);
    }

    private static MethodReference CreateOriginalCall(MethodDefinition original, MethodDefinition target)
    {
        if (!target.HasGenericParameters) return original;
        GenericInstanceMethod instantiation = new GenericInstanceMethod(original);
        foreach (GenericParameter parameter in target.GenericParameters) instantiation.GenericArguments.Add(parameter);
        return instantiation;
    }

    private static void EmitArgumentArray(ILProcessor il, MethodDefinition method, VariableDefinition arguments)
    {
        ModuleDefinition module = method.Module;
        int thisOffset = HasValueTypeThis(method) ? 1 : 0;
        il.Append(il.Create(OpCodes.Ldc_I4, method.Parameters.Count + thisOffset));
        il.Append(il.Create(OpCodes.Newarr, module.TypeSystem.Object));
        il.Append(il.Create(OpCodes.Stloc, arguments));
        if (thisOffset != 0)
        {
            TypeReference selfType = GetSelfType(method);
            il.Append(il.Create(OpCodes.Ldloc, arguments));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldobj, selfType));
            il.Append(il.Create(OpCodes.Box, selfType));
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }
        for (int index = 0; index < method.Parameters.Count; index++)
        {
            ParameterDefinition parameter = method.Parameters[index];
            TypeReference argumentType = parameter.ParameterType;
            il.Append(il.Create(OpCodes.Ldloc, arguments));
            il.Append(il.Create(OpCodes.Ldc_I4, index + thisOffset));
            if (argumentType is ByReferenceType byReference)
            {
                TypeReference element = byReference.ElementType;
                if (parameter.IsOut && !parameter.IsIn)
                    il.Append(il.Create(OpCodes.Ldnull));
                else
                {
                    il.Append(LoadArgument(il, index + (method.IsStatic ? 0 : 1)));
                    il.Append(il.Create(OpCodes.Ldobj, element));
                    if (NeedsBox(element)) il.Append(il.Create(OpCodes.Box, element));
                }
            }
            else
            {
                il.Append(LoadArgument(il, index + (method.IsStatic ? 0 : 1)));
                if (NeedsBox(argumentType)) il.Append(il.Create(OpCodes.Box, argumentType));
            }
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }
    }

    private static void EmitByRefCopyBack(ILProcessor il, MethodDefinition method, VariableDefinition arguments)
    {
        int thisOffset = HasValueTypeThis(method) ? 1 : 0;
        for (int index = 0; index < method.Parameters.Count; index++)
        {
            ParameterDefinition parameter = method.Parameters[index];
            if (!(parameter.ParameterType is ByReferenceType byReference) || parameter.IsIn) continue;
            TypeReference element = byReference.ElementType;
            il.Append(LoadArgument(il, index + (method.IsStatic ? 0 : 1)));
            il.Append(il.Create(OpCodes.Ldloc, arguments));
            il.Append(il.Create(OpCodes.Ldc_I4, index + thisOffset));
            il.Append(il.Create(OpCodes.Ldelem_Ref));
            if (NeedsBox(element)) il.Append(il.Create(OpCodes.Unbox_Any, element));
            else if (element.MetadataType != MetadataType.Object) il.Append(il.Create(OpCodes.Castclass, element));
            il.Append(il.Create(OpCodes.Stobj, element));
        }
    }

    private static void EmitInstanceCopyBack(ILProcessor il, MethodDefinition method, VariableDefinition arguments)
    {
        if (!HasValueTypeThis(method)) return;
        TypeReference selfType = GetSelfType(method);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldloc, arguments));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldelem_Ref));
        il.Append(il.Create(OpCodes.Unbox_Any, selfType));
        il.Append(il.Create(OpCodes.Stobj, selfType));
    }

    private static bool HasValueTypeThis(MethodDefinition method)
    {
        return !method.IsStatic && method.DeclaringType.IsValueType;
    }

    private static TypeReference GetSelfType(MethodDefinition method)
    {
        if (!method.DeclaringType.HasGenericParameters) return method.DeclaringType;
        GenericInstanceType instance = new GenericInstanceType(method.DeclaringType);
        foreach (GenericParameter parameter in method.DeclaringType.GenericParameters) instance.GenericArguments.Add(parameter);
        return instance;
    }

    private static bool NeedsBox(TypeReference type)
    {
        return type.IsValueType || type is GenericParameter;
    }

    private static Instruction LoadArgument(ILProcessor il, int index)
    {
        switch (index)
        {
            case 0: return il.Create(OpCodes.Ldarg_0);
            case 1: return il.Create(OpCodes.Ldarg_1);
            case 2: return il.Create(OpCodes.Ldarg_2);
            case 3: return il.Create(OpCodes.Ldarg_3);
            default: return il.Create(OpCodes.Ldarg, (short)index);
        }
    }

    private static TypeReference CreateType(ModuleDefinition module, string ns, string name)
    {
        return new TypeReference(ns, name, module, module.TypeSystem.CoreLibrary);
    }

    private static MethodReference CreateCurrentDomainGetter(ModuleDefinition module)
    {
        return module.ImportReference(typeof(AppDomain).GetProperty("CurrentDomain").GetMethod);
    }

    private static MethodReference CreateGetData(ModuleDefinition module)
    {
        return module.ImportReference(typeof(AppDomain).GetMethod("GetData", new[] { typeof(string) }));
    }

    private static MethodReference CreateFuncInvoke(ModuleDefinition module, GenericInstanceType funcType)
    {
        MethodReference method = module.ImportReference(typeof(Func<RuntimeMethodBase, object, object[], object>).GetMethod("Invoke"));
        method.DeclaringType = funcType;
        return method;
    }

    private static MethodReference CreateDynamicInvoke(ModuleDefinition module)
    {
        return module.ImportReference(typeof(Delegate).GetMethod("DynamicInvoke", new[] { typeof(object[]) }));
    }

    private static void StoreInvocationValue(ILProcessor il, VariableDefinition invocation, int index, Instruction loadValue)
    {
        il.Append(il.Create(OpCodes.Ldloc, invocation));
        il.Append(il.Create(OpCodes.Ldc_I4, index));
        il.Append(loadValue);
        il.Append(il.Create(OpCodes.Stelem_Ref));
    }
}

internal static class CecilTypeExtensions
{
    internal static bool IsByReferenceLike(this TypeReference type)
    {
        if (type.FullName.StartsWith("System.Span`", StringComparison.Ordinal) ||
            type.FullName.StartsWith("System.ReadOnlySpan`", StringComparison.Ordinal)) return true;
        try
        {
            TypeDefinition definition = type.Resolve();
            return definition != null && definition.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");
        }
        catch (AssemblyResolutionException)
        {
            return false;
        }
    }
}
