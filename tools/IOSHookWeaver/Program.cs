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
    private const string TypedDispatchKey = "Celeste.IOSHookRuntime.DispatchTyped";
    private static readonly Dictionary<string, string> IOSStaticLibraries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SDL2"] = "celeste-ios-native",
        ["FNA3D"] = "celeste-ios-native",
        ["FAudio"] = "celeste-ios-native",
        ["libtheorafile"] = "celeste-ios-native",
        ["theorafilebun"] = "celeste-ios-native",
        ["__Internal"] = "celeste-ios-native",
        ["libfmod"] = "celeste-ios-native",
        ["libfmodstudio"] = "celeste-ios-native",
        ["fmod"] = "celeste-ios-native",
        ["fmodstudio"] = "celeste-ios-native"
    };
    private static readonly ConcurrentDictionary<ReflectionGetterKey, Delegate> ReflectionGetters = new ConcurrentDictionary<ReflectionGetterKey, Delegate>();

    private sealed class IOSAssemblyResolver : DefaultAssemblyResolver
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, byte[]> images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssemblyDefinition> loaded = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);

        public IOSAssemblyResolver(IEnumerable<KeyValuePair<string, byte[]>> assemblyImages)
        {
            if (assemblyImages == null) return;
            foreach (KeyValuePair<string, byte[]> image in assemblyImages)
                if (!string.IsNullOrWhiteSpace(image.Key) && image.Value != null && !images.ContainsKey(image.Key))
                    images.Add(image.Key, image.Value);
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            AssemblyDefinition imageAssembly = ResolveImage(name);
            return imageAssembly ?? base.Resolve(name);
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            AssemblyDefinition imageAssembly = ResolveImage(name);
            if (imageAssembly != null) return imageAssembly;
            if (parameters == null)
                parameters = new ReaderParameters { AssemblyResolver = this, InMemory = true, ReadingMode = ReadingMode.Deferred };
            return base.Resolve(name, parameters);
        }

        private AssemblyDefinition ResolveImage(AssemblyNameReference name)
        {
            if (name != null && images.TryGetValue(name.Name, out byte[] image))
            {
                lock (sync)
                {
                    if (loaded.TryGetValue(name.FullName, out AssemblyDefinition cached)) return cached;
                    ReaderParameters reader = new ReaderParameters
                    {
                        AssemblyResolver = this,
                        InMemory = true,
                        ReadingMode = ReadingMode.Deferred,
                        ReadSymbols = false
                    };
                    AssemblyDefinition candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(image, false), reader);
                    if (IdentityMatches(name, candidate.Name))
                    {
                        loaded[name.FullName] = candidate;
                        return candidate;
                    }
                    candidate.Dispose();
                }
            }
            return null;
        }

        private static bool IdentityMatches(AssemblyNameReference requested, AssemblyNameDefinition actual)
        {
            if (!string.Equals(requested.Name, actual.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (requested.Version != null && requested.Version != actual.Version) return false;
            if (!string.IsNullOrWhiteSpace(requested.Culture) &&
                !string.Equals(requested.Culture, actual.Culture, StringComparison.OrdinalIgnoreCase)) return false;
            byte[] requestedToken = requested.PublicKeyToken;
            byte[] actualToken = actual.PublicKeyToken;
            return requestedToken == null || requestedToken.Length == 0 ||
                (actualToken != null && requestedToken.SequenceEqual(actualToken));
        }

        public new void Dispose()
        {
            lock (sync)
            {
                foreach (AssemblyDefinition assembly in loaded.Values) assembly.Dispose();
                loaded.Clear();
            }
            base.Dispose();
        }
    }

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
        if (args.Length == 2 && string.Equals(args[0], "--verify-celeste-main", StringComparison.Ordinal))
        {
            try
            {
                return VerifyCelesteLaunchMethod(args[1]);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Celeste entry-point verification failed: " + exception.Message);
                return 1;
            }
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: IOSHookWeaver <input-assembly> <output-assembly> [resolver-directory ...] | --verify-celeste-main <assembly>");
            return 2;
        }

        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (!File.Exists(input)) throw new FileNotFoundException("Input assembly was not found.", input);
        string assemblyName;
        int woven;
        string[] skipped;
        byte[] result = WeaveAssembly(File.ReadAllBytes(input), out assemblyName, out woven, out skipped,
            Path.GetDirectoryName(input), args.Skip(2).Where(Directory.Exists));
        string outputDirectory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        string temporary = output + ".tmp";
        File.WriteAllBytes(temporary, result);
        File.Move(temporary, output, true);
        Console.WriteLine("Wove " + woven + " methods from " + assemblyName + ".");
        if (skipped.Length > 0)
        {
            Console.WriteLine("Skipped " + skipped.Length + " methods that remain unhookable on this iOS backend:");
            foreach (string method in skipped) Console.WriteLine("  " + method);
        }
        return 0;
    }

    private static int VerifyCelesteLaunchMethod(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Patched Celeste assembly was not found.", fullPath);

        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(fullPath, new ReaderParameters
        {
            InMemory = true,
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = false
        }))
        {
            TypeDefinition celesteType = assembly.MainModule.GetType("Celeste.Celeste");
            MethodDefinition main = celesteType?.Methods.FirstOrDefault(method =>
                string.Equals(method.Name, "Main", StringComparison.Ordinal) && method.IsStatic &&
                string.Equals(method.ReturnType.FullName, "System.Void", StringComparison.Ordinal) &&
                method.Parameters.Count == 1 &&
                string.Equals(method.Parameters[0].ParameterType.FullName, "System.String[]", StringComparison.Ordinal));
            if (main == null || !main.HasBody || main.Body.Instructions.Count == 0)
                throw new InvalidDataException("The patched assembly does not contain static void Celeste.Celeste.Main(string[]) with a method body.");

            MethodDefinition originalMain = celesteType.Methods.FirstOrDefault(method =>
                string.Equals(method.Name, IOSHookIdentity.OriginalMethodName(main), StringComparison.Ordinal));
            if (originalMain == null || !originalMain.HasBody || originalMain.Body.Instructions.Count == 0)
                throw new InvalidDataException("The woven Celeste.Main wrapper has no callable original startup body.");

            bool wrapperCallsOriginal = main.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference calledMethod &&
                string.Equals(calledMethod.Name, originalMain.Name, StringComparison.Ordinal) &&
                string.Equals(calledMethod.DeclaringType.FullName, originalMain.DeclaringType.FullName, StringComparison.Ordinal));
            if (!wrapperCallsOriginal)
                throw new InvalidDataException("The woven Celeste.Main launch wrapper does not call its original startup body.");

            Instruction[] instructions = originalMain.Body.Instructions.ToArray();
            int dataKey = Array.FindIndex(instructions, instruction =>
                instruction.OpCode == OpCodes.Ldstr &&
                string.Equals(instruction.Operand as string, "Celeste.IOSHookRuntime.EverestDataPath", StringComparison.Ordinal));
            int getData = FindMethodCallIndex(instructions, "System.AppDomain", "GetData", dataKey + 1);
            int fullPathCall = FindMethodCallIndex(instructions, "System.IO.Path", "GetFullPath", getData + 1);
            int createDirectory = FindMethodCallIndex(instructions, "System.IO.Directory", "CreateDirectory", fullPathCall + 1);
            int getCurrentDirectory = FindMethodCallIndex(instructions, "System.Environment", "get_CurrentDirectory", createDirectory + 1);
            int setCurrentDirectory = FindMethodCallIndex(instructions, "System.Environment", "set_CurrentDirectory", getCurrentDirectory + 1);
            int buildFlag = Array.FindIndex(instructions, setCurrentDirectory + 1, instruction =>
                instruction.OpCode == OpCodes.Ldstr &&
                string.Equals(instruction.Operand as string, "BuildIsFNA.txt", StringComparison.Ordinal));
            int writeFlag = FindMethodCallIndex(instructions, "System.IO.File", "WriteAllText", buildFlag + 1);
            int nullGuard = FindMethodCallIndex(instructions, "System.String", "IsNullOrWhiteSpace", getData + 1);

            int[] mainInnerCalls = instructions
                .Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Operand is MethodReference calledMethod &&
                    string.Equals(calledMethod.DeclaringType.FullName, "Celeste.Celeste", StringComparison.Ordinal) &&
                    string.Equals(calledMethod.Name, "MainInner", StringComparison.Ordinal))
                .Select(item => item.index)
                .ToArray();
            bool restoresWorkingDirectoryBeforeEveryGameEntry = mainInnerCalls.Length > 0 && mainInnerCalls.All(mainInner =>
                Array.FindLastIndex(instructions, mainInner, mainInner - 1, instruction =>
                    instruction.Operand is MethodReference calledMethod &&
                    string.Equals(calledMethod.DeclaringType.FullName, "System.Environment", StringComparison.Ordinal) &&
                    string.Equals(calledMethod.Name, "set_CurrentDirectory", StringComparison.Ordinal)) > writeFlag);
            bool restoresWorkingDirectoryOnFailure = originalMain.Body.ExceptionHandlers.Any(handler =>
            {
                if (handler.HandlerType != ExceptionHandlerType.Finally || handler.HandlerStart == null || handler.HandlerEnd == null)
                    return false;
                int start = Array.IndexOf(instructions, handler.HandlerStart);
                int end = handler.HandlerEnd == null ? instructions.Length : Array.IndexOf(instructions, handler.HandlerEnd);
                if (start < 0) return false;
                if (end < 0) end = instructions.Length;
                return instructions.Skip(start).Take(end - start).Any(instruction =>
                    instruction.Operand is MethodReference calledMethod &&
                    string.Equals(calledMethod.DeclaringType.FullName, "System.Environment", StringComparison.Ordinal) &&
                    string.Equals(calledMethod.Name, "set_CurrentDirectory", StringComparison.Ordinal));
            });

            if (dataKey < 0 || getData <= dataKey || fullPathCall <= getData || createDirectory <= fullPathCall ||
                getCurrentDirectory <= createDirectory || setCurrentDirectory <= getCurrentDirectory ||
                nullGuard <= getData || nullGuard >= setCurrentDirectory || buildFlag <= setCurrentDirectory || writeFlag <= buildFlag ||
                !restoresWorkingDirectoryBeforeEveryGameEntry || !restoresWorkingDirectoryOnFailure)
                throw new InvalidDataException(
                    "Celeste.Celeste.Main must write bootstrap files in app data, restore the game bundle directory before each game entry, and restore it when startup fails.");

            Console.WriteLine("Verified Celeste.Main redirects bootstrap writes to app data and restores the game bundle directory before game startup and on failure: " + main.FullName + ".");
        }

        return 0;
    }

    private static int FindMethodCallIndex(Instruction[] instructions, string declaringType, string methodName, int startIndex)
    {
        if (instructions == null || startIndex < 0 || startIndex >= instructions.Length)
            return -1;

        for (int index = startIndex; index < instructions.Length; index++)
        {
            if (instructions[index].Operand is MethodReference method &&
                string.Equals(method.DeclaringType.FullName, declaringType, StringComparison.Ordinal) &&
                string.Equals(method.Name, methodName, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }
#endif

    public static byte[] WeaveAssembly(byte[] input, out string assemblyName, out int woven, out string[] skippedMethods,
        string resolverDirectory = null, IEnumerable<string> additionalResolverDirectories = null,
        IEnumerable<KeyValuePair<string, byte[]>> inMemoryAssemblies = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        using (IOSAssemblyResolver resolver = new IOSAssemblyResolver(inMemoryAssemblies))
        {
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
        if (additionalResolverDirectories != null)
            foreach (string directory in additionalResolverDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
                AddResolverDirectory(resolver, directory);
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
            PatchIOSNativeImports(assembly.MainModule, assembly.Name.Name);
            if (string.Equals(assembly.Name.Name, "TextCopy", StringComparison.OrdinalIgnoreCase))
                PatchTextCopyClipboard(assembly.MainModule);
            if (string.Equals(assembly.Name.Name, "DJMapHelper", StringComparison.OrdinalIgnoreCase))
                PatchFastReflectionFieldGetter(assembly.MainModule, "Celeste.Mod.DJMapHelper.Extensions.FastReflection");
            if (string.Equals(assembly.Name.Name, "CeilingUltra", StringComparison.OrdinalIgnoreCase))
                PatchFastReflectionFieldGetter(assembly.MainModule, "Celeste.Mod.CeilingUltra.Utils.FastReflection");
            if (string.Equals(assembly.Name.Name, "ChroniaHelper", StringComparison.OrdinalIgnoreCase))
                PatchIOSExternalLinks(assembly.MainModule);

            List<string> skipped = new List<string>();
            int count = 0;
            foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types).ToArray())
            foreach (MethodDefinition method in type.Methods.ToArray())
            {
                string reason = GetSkipReason(method);
                if (reason != null)
                {
                    if (!IsBackendGeneratedMethod(method) && method.HasBody && method.Body != null &&
                        method.Body.Instructions.Count > 0 && !method.IsAbstract && !method.IsPInvokeImpl)
                        skipped.Add(method.FullName + " :: " + reason);
                    continue;
                }
                if (IsAlreadyWoven(method))
                {
                    if (RequiresTypedDispatch(method) &&
                        !type.Methods.Any(candidate => candidate.Name == IOSHookIdentity.TypedOriginalAdapterName(method)))
                    {
                        MethodDefinition existingOriginal = type.Methods.FirstOrDefault(candidate =>
                            candidate.Name == IOSHookIdentity.OriginalMethodName(method));
                        if (existingOriginal != null)
                            CreateTypedOriginalAdapter(method, existingOriginal, IOSHookIdentity.TypedOriginalAdapterName(method));
                    }
                    continue;
                }
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
    }

    private static void PatchIOSNativeImports(ModuleDefinition module, string assemblyName)
    {
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

    private static void PatchTextCopyClipboard(ModuleDefinition module)
    {
        TypeDefinition clipboard = AllTypes(module.Types).FirstOrDefault(type => type.FullName == "TextCopy.ClipboardService");
        if (clipboard == null)
            throw new InvalidDataException("TextCopy assembly identity is present but TextCopy.ClipboardService is missing; its iOS clipboard compatibility rewrite needs review.");

        PatchClipboardFactory(clipboard, "CreateGet", "System.Func" + (char)96 + "1<System.String>", "Celeste.IOSClipboard.GetText");
        PatchClipboardFactory(clipboard, "CreateSet", "System.Action" + (char)96 + "1<System.String>", "Celeste.IOSClipboard.SetText");
        PatchClipboardFactory(clipboard, "CreateAsyncGet",
            "System.Func" + (char)96 + "2<System.Threading.CancellationToken,System.Threading.Tasks.Task" + (char)96 + "1<System.String>>",
            "Celeste.IOSClipboard.GetTextAsync");
        PatchClipboardFactory(clipboard, "CreateAsyncSet",
            "System.Func" + (char)96 + "3<System.String,System.Threading.CancellationToken,System.Threading.Tasks.Task>",
            "Celeste.IOSClipboard.SetTextAsync");
        Console.WriteLine("Applied iOS clipboard delegates to TextCopy.");
    }

    private static void PatchClipboardFactory(TypeDefinition clipboard, string methodName, string expectedReturnType, string dataKey)
    {
        MethodDefinition method = clipboard.Methods.SingleOrDefault(candidate => candidate.Name == methodName);
        if (method == null || !method.IsStatic || method.Parameters.Count != 0 || method.ReturnType.FullName != expectedReturnType)
            throw new InvalidDataException("TextCopy.ClipboardService." + methodName +
                " changed shape; the iOS clipboard compatibility rewrite needs review.");

        MethodBody body = new MethodBody(method) { MaxStackSize = 2 };
        ILProcessor il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Call, CreateCurrentDomainGetter(method.Module)));
        il.Append(il.Create(OpCodes.Ldstr, dataKey));
        il.Append(il.Create(OpCodes.Callvirt, CreateGetData(method.Module)));
        il.Append(il.Create(OpCodes.Castclass, method.ReturnType));
        il.Append(il.Create(OpCodes.Ret));
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

    private static void PatchFastReflectionFieldGetter(ModuleDefinition module, string typeName)
    {
        TypeDefinition type = AllTypes(module.Types).FirstOrDefault(candidate => candidate.FullName == typeName);
        MethodDefinition method = type?.Methods.FirstOrDefault(candidate =>
            candidate.Name == "CreateGetDelegateImpl" && candidate.HasGenericParameters && candidate.GenericParameters.Count == 2);
        if (method == null || method.Parameters.Count != 2 || method.Parameters[0].ParameterType.FullName != "System.Type" ||
            method.Parameters[1].ParameterType.MetadataType != MetadataType.String || method.ReturnType.MetadataType == MetadataType.Void)
            throw new InvalidDataException(typeName + ".CreateGetDelegateImpl changed shape; its iOS DynamicMethod compatibility rewrite needs review.");

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
        Console.WriteLine("Applied the iOS reflection-backed field getter rewrite to " + module.Assembly.Name.Name + ".");
    }

    private static void PatchIOSExternalLinks(ModuleDefinition module)
    {
        MethodReference helper = module.ImportReference(typeof(IOSHookWeaver).GetMethod(
            nameof(OpenExternalProcessIOS), BindingFlags.Public | BindingFlags.Static));
        int patched = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if ((instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) ||
                    !(instruction.Operand is MethodReference target) ||
                    target.DeclaringType.FullName != "System.Diagnostics.Process" ||
                    target.Name != "Start" || target.Parameters.Count != 1 ||
                    target.Parameters[0].ParameterType.FullName != "System.Diagnostics.ProcessStartInfo" ||
                    target.ReturnType.FullName != "System.Diagnostics.Process")
                    continue;

                if (instruction.Next == null || instruction.Next.OpCode != OpCodes.Pop)
                    throw new InvalidDataException("ChroniaHelper uses Process.Start's return value; its iOS external-link rewrite needs review.");
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = helper;
                patched++;
            }
        }
        Console.WriteLine("Applied iOS external-link handling to ChroniaHelper: " + patched + " call(s).");
    }

    public static object OpenExternalProcessIOS(object startInfo)
    {
        if (startInfo == null) return null;
        System.Reflection.PropertyInfo fileNameProperty = startInfo.GetType().GetProperty("FileName");
        string fileName = fileNameProperty?.GetValue(startInfo, null) as string;
        if (!Uri.TryCreate(fileName, UriKind.Absolute, out Uri uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        Action<string> openUrl = AppDomain.CurrentDomain.GetData("Celeste.IOSExternalLinks.OpenUrl") as Action<string>;
        openUrl?.Invoke(uri.AbsoluteUri);
        return null;
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
        if (IsBackendGeneratedMethod(method))
            return "generated iOS hook backend method remains direct";
        if (!method.HasBody || method.Body == null || method.Body.Instructions.Count == 0 ||
            method.IsAbstract || method.IsPInvokeImpl)
            return "method has no movable managed body";
        if (method.CallingConvention == MethodCallingConvention.VarArg) return "varargs cannot be boxed for managed dispatch";
        bool requiresTypedDispatch = RequiresTypedDispatch(method);
        if (requiresTypedDispatch && !CanUseTypedDispatch(method))
            return "signature requires a typed adapter but contains open generic parameters";
        return null;
    }

    private static bool IsBackendGeneratedMethod(MethodDefinition method)
    {
        return method.Name.StartsWith("__ios_original_", StringComparison.Ordinal) ||
            method.Name.StartsWith("__ios_original_static_", StringComparison.Ordinal) ||
            method.Name.StartsWith("__ios_typed_original_", StringComparison.Ordinal);
    }

    private static bool RequiresTypedDispatch(MethodDefinition method)
    {
        return (!method.IsStatic && method.DeclaringType.IsValueType && method.DeclaringType.IsByReferenceLike()) ||
            IsByReferenceSignatureType(method.ReturnType) || ContainsUnsupportedType(method.ReturnType) ||
            method.HasParameters && method.Parameters.Any(parameter => ContainsUnsupportedType(parameter.ParameterType));
    }

    private static bool IsByReferenceSignatureType(TypeReference type)
    {
        while (true)
        {
            if (type is OptionalModifierType optional)
            {
                type = optional.ElementType;
                continue;
            }
            if (type is RequiredModifierType required)
            {
                type = required.ElementType;
                continue;
            }
            return type is ByReferenceType;
        }
    }

    private static bool CanUseTypedDispatch(MethodDefinition method)
    {
        return method.CallingConvention != MethodCallingConvention.VarArg &&
            !(method.IsConstructor && method.IsStatic);
    }

    private static bool ContainsUnsupportedType(TypeReference type)
    {
        if (type is ByReferenceType byReference) return ContainsUnsupportedType(byReference.ElementType);
        if (type is GenericInstanceType generic)
            return generic.ElementType.IsByReferenceLike() || generic.GenericArguments.Any(ContainsUnsupportedType);
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
        TypeDefinition owner = method.DeclaringType.Name == "<Module>"
            ? method.Module.GetType(IOSHookIdentity.ModuleOriginalContainerName(method))
            : method.DeclaringType;
        return owner != null && owner.Methods.Any(candidate => candidate.Name == cloneName) &&
            owner.Fields.Any(candidate => candidate.Name == fieldName);
    }

    private static void Weave(MethodDefinition method)
    {
        TypeDefinition type = method.DeclaringType;
        string id = IOSHookIdentity.ForMethod(method);
        string originalName = "__ios_original_" + id;
        string gateName = "__ios_hooked_" + id;

        TypeDefinition typedDelegate = RequiresTypedDispatch(method)
            ? CreateTypedDelegateType(method, "__ios_typed_delegate_" + id)
            : null;

        // Split a simple initializer so it runs before On hooks. Everest may
        // also generate constructors whose initializer is an orig_ctor alias,
        // or whose branches / exception regions cross that point. Those stay
        // as a whole-body continuation: the cloned IL keeps its original CFG
        // and EH regions, and the gate still exposes the constructor normally.
        int constructorChainIndex = method.IsConstructor ? FindConstructorChainCall(method) : -1;
        MethodDefinition original = CloneOriginal(method, originalName);
        if (typedDelegate != null)
            CreateTypedOriginalAdapter(method, original, IOSHookIdentity.TypedOriginalAdapterName(method));
        if (constructorChainIndex >= 0)
        {
            for (int i = 0; i <= constructorChainIndex; i++)
                original.Body.Instructions.RemoveAt(0);
        }
        if (HasValueTypeThis(method))
            CreateValueTypeInvoker(method, original, "__ios_original_static_" + id);

        TypeDefinition gateOwner = method.DeclaringType.Name == "<Module>"
            ? GetModuleOriginalContainer(method)
            : method.DeclaringType;
        FieldAttributes gateAttributes = method.DeclaringType.Name == "<Module>"
            ? FieldAttributes.Assembly | FieldAttributes.Static
            : FieldAttributes.Private | FieldAttributes.Static;
        FieldDefinition gate = new FieldDefinition(
            gateName,
            gateAttributes,
            method.Module.TypeSystem.Boolean);
        gateOwner.Fields.Add(gate);
        FieldDefinition genericOpenGate = CreateGenericOpenGateField(method);
        MethodBody gateBody = typedDelegate == null
            ? CreateGateBody(method, original, gate, genericOpenGate)
            : CreateTypedGateBody(method, original, gate, typedDelegate, genericOpenGate);
        if (constructorChainIndex >= 0)
            PrependConstructorChainCall(method, gateBody, constructorChainIndex);
        method.Body = gateBody;
    }

    private static FieldDefinition CreateGenericOpenGateField(MethodDefinition method)
    {
        if (!method.DeclaringType.HasGenericParameters) return null;
        ModuleDefinition module = method.Module;
        TypeDefinition state = module.Types.FirstOrDefault(type => type.FullName == IOSHookIdentity.GenericGateStateTypeFullName);
        if (state == null)
        {
            state = new TypeDefinition(
                "CelesteIOS.Generated",
                "__CelesteIOSGenericGateState",
                Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Abstract |
                Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
                module.ImportReference(typeof(object)));
            module.Types.Add(state);
        }

        string name = IOSHookIdentity.GenericOpenGateFieldName(method);
        FieldDefinition field = state.Fields.FirstOrDefault(candidate => candidate.Name == name);
        if (field != null) return field;
        field = new FieldDefinition(name, FieldAttributes.Assembly | FieldAttributes.Static, module.TypeSystem.Boolean);
        state.Fields.Add(field);
        return field;
    }

    private static TypeDefinition CreateTypedDelegateType(MethodDefinition method, string name)
    {
        ModuleDefinition module = method.Module;
        TypeDefinition delegateType = new TypeDefinition(
            "CelesteIOS.Generated",
            name,
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed |
            Mono.Cecil.TypeAttributes.Class | Mono.Cecil.TypeAttributes.AnsiClass |
            Mono.Cecil.TypeAttributes.AutoClass,
            module.ImportReference(typeof(MulticastDelegate)));
        module.Types.Add(delegateType);

        Dictionary<GenericParameter, GenericParameter> genericMap = new Dictionary<GenericParameter, GenericParameter>();
        GenericParameter[] sourceGenericParameters = method.DeclaringType.GenericParameters.Concat(method.GenericParameters).ToArray();
        foreach (GenericParameter source in sourceGenericParameters)
        {
            GenericParameter clone = new GenericParameter("T" + delegateType.GenericParameters.Count, delegateType)
            {
                Attributes = source.Attributes
            };
            delegateType.GenericParameters.Add(clone);
            genericMap.Add(source, clone);
        }
        foreach (GenericParameter source in sourceGenericParameters)
        {
            GenericParameter clone = genericMap[source];
            foreach (GenericParameterConstraint constraint in source.Constraints)
                clone.Constraints.Add(new GenericParameterConstraint(ImportTypedSignatureType(module, constraint.ConstraintType, method, genericMap)));
        }

        MethodDefinition constructor = new MethodDefinition(
            ".ctor",
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
            Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
            module.TypeSystem.Void)
        {
            ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed,
            HasThis = true
        };
        constructor.Parameters.Add(new ParameterDefinition("target", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Object));
        constructor.Parameters.Add(new ParameterDefinition("method", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.IntPtr));
        delegateType.Methods.Add(constructor);

        MethodDefinition invoke = new MethodDefinition(
            "Invoke",
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
            Mono.Cecil.MethodAttributes.NewSlot | Mono.Cecil.MethodAttributes.Virtual,
            ImportTypedSignatureType(module, method.ReturnType, method, genericMap))
        {
            ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed,
            HasThis = true
        };
        if (!method.IsStatic)
        {
            TypeReference instanceType;
            if (method.DeclaringType.HasGenericParameters)
            {
                GenericInstanceType self = new GenericInstanceType(module.ImportReference(method.DeclaringType));
                foreach (GenericParameter parameter in method.DeclaringType.GenericParameters)
                    self.GenericArguments.Add(genericMap[parameter]);
                instanceType = self;
            }
            else
                instanceType = module.ImportReference(method.DeclaringType, method);
            if (method.DeclaringType.IsValueType)
                instanceType = new ByReferenceType(instanceType);
            invoke.Parameters.Add(new ParameterDefinition("instance", Mono.Cecil.ParameterAttributes.None, instanceType));
        }
        foreach (ParameterDefinition parameter in method.Parameters)
            invoke.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                ImportTypedSignatureType(module, parameter.ParameterType, method, genericMap)));
        delegateType.Methods.Add(invoke);
        return delegateType;
    }

    private static MethodDefinition CreateTypedOriginalAdapter(MethodDefinition source, MethodDefinition original, string name)
    {
        MethodDefinition adapter = new MethodDefinition(name,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            source.Module.TypeSystem.Void)
        {
            ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed
        };
        source.DeclaringType.Methods.Add(adapter);

        Dictionary<GenericParameter, GenericParameter> genericMap = new Dictionary<GenericParameter, GenericParameter>();
        foreach (GenericParameter parameter in source.DeclaringType.GenericParameters)
            genericMap.Add(parameter, parameter);
        foreach (GenericParameter parameter in source.GenericParameters)
        {
            GenericParameter clone = new GenericParameter(parameter.Name, adapter) { Attributes = parameter.Attributes };
            adapter.GenericParameters.Add(clone);
            genericMap.Add(parameter, clone);
        }
        foreach (GenericParameter parameter in source.GenericParameters)
        {
            GenericParameter clone = genericMap[parameter];
            foreach (GenericParameterConstraint constraint in parameter.Constraints)
                clone.Constraints.Add(new GenericParameterConstraint(
                    ImportTypedSignatureType(source.Module, constraint.ConstraintType, source, genericMap)));
        }

        adapter.ReturnType = ImportTypedSignatureType(source.Module, source.ReturnType, source, genericMap);
        if (!source.IsStatic)
        {
            TypeReference instanceType = GetSelfType(source);
            if (source.DeclaringType.IsValueType) instanceType = new ByReferenceType(instanceType);
            adapter.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None,
                source.Module.ImportReference(instanceType, adapter)));
        }
        foreach (ParameterDefinition parameter in source.Parameters)
            adapter.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                ImportTypedSignatureType(source.Module, parameter.ParameterType, source, genericMap)));

        MethodBody body = new MethodBody(adapter) { InitLocals = false, MaxStackSize = Math.Max(adapter.Parameters.Count, 1) };
        ILProcessor il = body.GetILProcessor();
        for (int index = 0; index < adapter.Parameters.Count; index++)
            il.Append(LoadArgument(il, index));
        il.Append(il.Create(OpCodes.Call, CreateOriginalCall(original, adapter)));
        il.Append(il.Create(OpCodes.Ret));
        adapter.Body = body;
        return adapter;
    }

    private static TypeReference ImportTypedSignatureType(ModuleDefinition module, TypeReference source,
        MethodDefinition context, Dictionary<GenericParameter, GenericParameter> genericMap)
    {
        if (source is GenericParameter genericParameter)
        {
            GenericParameter mapped;
            if (genericMap.TryGetValue(genericParameter, out mapped)) return mapped;
            GenericParameter[] positionalMatches = genericMap.Keys
                .Where(parameter => parameter.Type == genericParameter.Type && parameter.Position == genericParameter.Position)
                .ToArray();
            if (positionalMatches.Length == 1) return genericMap[positionalMatches[0]];
            throw new InvalidOperationException("Typed hook signature contains an unmapped generic parameter: " + source.FullName);
        }
        if (source is ByReferenceType byReference) return new ByReferenceType(ImportTypedSignatureType(module, byReference.ElementType, context, genericMap));
        if (source is PointerType pointer) return new PointerType(ImportTypedSignatureType(module, pointer.ElementType, context, genericMap));
        if (source is PinnedType pinned) return new PinnedType(ImportTypedSignatureType(module, pinned.ElementType, context, genericMap));
        if (source is SentinelType sentinel) return new SentinelType(ImportTypedSignatureType(module, sentinel.ElementType, context, genericMap));
        if (source is OptionalModifierType optional)
            return new OptionalModifierType(ImportTypedSignatureType(module, optional.ModifierType, context, genericMap),
                ImportTypedSignatureType(module, optional.ElementType, context, genericMap));
        if (source is RequiredModifierType required)
            return new RequiredModifierType(ImportTypedSignatureType(module, required.ModifierType, context, genericMap),
                ImportTypedSignatureType(module, required.ElementType, context, genericMap));
        if (source is ArrayType array)
        {
            ArrayType clone = new ArrayType(ImportTypedSignatureType(module, array.ElementType, context, genericMap), array.Rank);
            for (int index = 0; index < array.Dimensions.Count; index++)
            {
                ArrayDimension dimension = array.Dimensions[index];
                dimension.LowerBound = array.Dimensions[index].LowerBound;
                dimension.UpperBound = array.Dimensions[index].UpperBound;
                clone.Dimensions[index] = dimension;
            }
            return clone;
        }
        if (source is GenericInstanceType generic)
        {
            GenericInstanceType clone = new GenericInstanceType(ImportTypedSignatureType(module, generic.ElementType, context, genericMap));
            foreach (TypeReference argument in generic.GenericArguments)
                clone.GenericArguments.Add(ImportTypedSignatureType(module, argument, context, genericMap));
            return clone;
        }
        if (source is FunctionPointerType functionPointer)
        {
            FunctionPointerType clone = new FunctionPointerType
            {
                HasThis = functionPointer.HasThis,
                ExplicitThis = functionPointer.ExplicitThis,
                CallingConvention = functionPointer.CallingConvention,
                ReturnType = ImportTypedSignatureType(module, functionPointer.ReturnType, context, genericMap)
            };
            foreach (ParameterDefinition parameter in functionPointer.Parameters)
                clone.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                    ImportTypedSignatureType(module, parameter.ParameterType, context, genericMap)));
            return clone;
        }
        return module.ImportReference(source, context);
    }

    private static int FindConstructorChainCall(MethodDefinition method)
    {
        if (!method.IsConstructor || method.IsStatic || method.Body == null)
            return -1;
        int index = -1;
        for (int i = 0; i < method.Body.Instructions.Count; i++)
        {
            Instruction instruction = method.Body.Instructions[i];
            if ((instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt) ||
                !(instruction.Operand is MethodReference called))
                continue;
            if (IsConstructorInitializationCall(method, called))
            {
                index = i;
                break;
            }
        }
        if (index < 0) return -1;

        Dictionary<Instruction, int> positions = method.Body.Instructions
            .Select((instruction, instructionIndex) => new { instruction, instructionIndex })
            .ToDictionary(pair => pair.instruction, pair => pair.instructionIndex);
        for (int i = 0; i < index; i++)
        {
            object operand = method.Body.Instructions[i].Operand;
            if (operand is Instruction branch && positions.TryGetValue(branch, out int branchIndex) && branchIndex > index)
                return -1;
            if (operand is Instruction[] branches && branches.Any(branchTarget => positions.TryGetValue(branchTarget, out int branchIndex) && branchIndex > index))
                return -1;
        }
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

    private static bool IsConstructorInitializationCall(MethodDefinition method, MethodReference called)
    {
        string calledTypeName = GetGenericDefinitionFullName(called.DeclaringType);
        if (called.Name == ".ctor")
        {
            return calledTypeName == method.DeclaringType.FullName ||
                calledTypeName == GetGenericDefinitionFullName(method.DeclaringType.BaseType);
        }

        // Everest's MonoMod patcher can lower a constructor into a forwarding
        // call to its generated orig_ctor body. In those assemblies the actual
        // base/this initialization is inside that forwarding method, so this
        // call is the initialization boundary the iOS gate must preserve.
        // Treat only a same-type Everest constructor alias as that boundary;
        // ordinary helper calls remain in the hookable body.
        return calledTypeName == method.DeclaringType.FullName &&
            (called.Name == "orig_ctor" || called.Name.StartsWith("orig_ctor_", StringComparison.Ordinal));
    }

    private static string GetGenericDefinitionFullName(TypeReference type)
    {
        return type is GenericInstanceType generic ? generic.ElementType.FullName : type?.FullName;
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
            if (TryGetImplicitLocalIndex(source.OpCode.Code, out int localIndex, out OpCode localOpcode))
            {
                copy.OpCode = localOpcode;
                copy.Operand = variables[method.Body.Variables[localIndex]];
            }
            prefix.Add(copy);
        }
        for (int i = prefix.Count - 1; i >= 0; i--)
            gateBody.Instructions.Insert(0, prefix[i]);
        gateBody.MaxStackSize = Math.Max(gateBody.MaxStackSize, method.Body.MaxStackSize);
    }

    private static bool TryGetImplicitLocalIndex(Code code, out int index, out OpCode opcode)
    {
        switch (code)
        {
            case Code.Ldloc_0: index = 0; opcode = OpCodes.Ldloc_S; return true;
            case Code.Ldloc_1: index = 1; opcode = OpCodes.Ldloc_S; return true;
            case Code.Ldloc_2: index = 2; opcode = OpCodes.Ldloc_S; return true;
            case Code.Ldloc_3: index = 3; opcode = OpCodes.Ldloc_S; return true;
            case Code.Stloc_0: index = 0; opcode = OpCodes.Stloc_S; return true;
            case Code.Stloc_1: index = 1; opcode = OpCodes.Stloc_S; return true;
            case Code.Stloc_2: index = 2; opcode = OpCodes.Stloc_S; return true;
            case Code.Stloc_3: index = 3; opcode = OpCodes.Stloc_S; return true;
            default: index = -1; opcode = default; return false;
        }
    }

    private static MethodDefinition CloneOriginal(MethodDefinition source, string name)
    {
        MethodAttributes visibility = source.DeclaringType.Name == "<Module>"
            ? MethodAttributes.Public
            : MethodAttributes.Private;
        MethodAttributes attributes = visibility | MethodAttributes.HideBySig;
        if (source.IsStatic) attributes |= MethodAttributes.Static;
        MethodDefinition clone = new MethodDefinition(name, attributes, source.Module.TypeSystem.Void)
        {
            ImplAttributes = source.ImplAttributes & ~(MethodImplAttributes.Runtime | MethodImplAttributes.InternalCall | MethodImplAttributes.Native)
        };
        TypeDefinition originalOwner = source.DeclaringType.Name == "<Module>"
            ? GetModuleOriginalContainer(source)
            : source.DeclaringType;
        originalOwner.Methods.Add(clone);

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

    private static TypeDefinition GetModuleOriginalContainer(MethodDefinition source)
    {
        string fullName = IOSHookIdentity.ModuleOriginalContainerName(source);
        TypeDefinition container = source.Module.GetType(fullName);
        if (container != null) return container;
        int separator = fullName.LastIndexOf('.');
        container = new TypeDefinition(fullName.Substring(0, separator), fullName.Substring(separator + 1),
            Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Abstract |
            Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.Class |
            Mono.Cecil.TypeAttributes.BeforeFieldInit, source.Module.ImportReference(typeof(object)));
        source.Module.Types.Add(container);
        return container;
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

    private static MethodBody CreateGateBody(MethodDefinition method, MethodDefinition original, FieldDefinition gate, FieldDefinition genericOpenGate)
    {
        MethodBody body = new MethodBody(method) { InitLocals = true, MaxStackSize = 12 };
        ILProcessor il = body.GetILProcessor();
        Instruction dispatch = Instruction.Create(OpCodes.Nop);

        // A static constructor is one-shot. It always enters the dispatcher so
        // registering a hook never has to write its gate field (which could run
        // the very type initializer being hooked through reflection). Before
        // the launcher installs the dispatcher, however, it must execute its
        // preserved body directly. This lets module and type initializers in
        // Mono.Cecil, MonoMod.Utils, Celeste, and mods run during normal startup.
        bool alwaysDispatch = method.IsConstructor && method.IsStatic;
        if (alwaysDispatch)
        {
            il.Append(il.Create(OpCodes.Call, CreateCurrentDomainGetter(method.Module)));
            il.Append(il.Create(OpCodes.Ldstr, DispatchKey));
            il.Append(il.Create(OpCodes.Callvirt, CreateGetData(method.Module)));
            il.Append(il.Create(OpCodes.Brtrue, dispatch));
            il.Append(il.Create(OpCodes.Call, CreateOriginalCall(original, method)));
            il.Append(il.Create(OpCodes.Ret));
        }
        else
        {
            il.Append(il.Create(OpCodes.Ldsfld, CreateGateFieldReference(gate, method)));
            il.Append(il.Create(OpCodes.Brtrue, dispatch));
            if (genericOpenGate != null)
            {
                il.Append(il.Create(OpCodes.Ldsfld, genericOpenGate));
                il.Append(il.Create(OpCodes.Brtrue, dispatch));
            }
            if (!method.IsStatic) il.Append(il.Create(OpCodes.Ldarg_0));
            foreach (ParameterDefinition parameter in method.Parameters) il.Append(LoadArgument(il, parameter.Index + (method.IsStatic ? 0 : 1)));
            il.Append(il.Create(OpCodes.Call, CreateOriginalCall(original, method)));
            il.Append(il.Create(OpCodes.Ret));
        }

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

    private static MethodBody CreateTypedGateBody(MethodDefinition method, MethodDefinition original, FieldDefinition gate,
        TypeDefinition delegateType, FieldDefinition genericOpenGate)
    {
        MethodBody body = new MethodBody(method) { InitLocals = true, MaxStackSize = 8 };
        ILProcessor il = body.GetILProcessor();
        Instruction dispatch = Instruction.Create(OpCodes.Nop);

        il.Append(il.Create(OpCodes.Ldsfld, CreateGateFieldReference(gate, method)));
        il.Append(il.Create(OpCodes.Brtrue, dispatch));
        if (genericOpenGate != null)
        {
            il.Append(il.Create(OpCodes.Ldsfld, genericOpenGate));
            il.Append(il.Create(OpCodes.Brtrue, dispatch));
        }
        if (!method.IsStatic) il.Append(il.Create(OpCodes.Ldarg_0));
        foreach (ParameterDefinition parameter in method.Parameters)
            il.Append(LoadArgument(il, parameter.Index + (method.IsStatic ? 0 : 1)));
        il.Append(il.Create(OpCodes.Call, CreateOriginalCall(original, method)));
        il.Append(il.Create(OpCodes.Ret));

        il.Append(dispatch);
        VariableDefinition methodBase = new VariableDefinition(CreateType(method.Module, "System.Reflection", "MethodBase"));
        VariableDefinition dispatcher = new VariableDefinition(new GenericInstanceType(CreateType(method.Module, "System", "Func`3")));
        GenericInstanceType funcType = (GenericInstanceType)dispatcher.VariableType;
        funcType.GenericArguments.Add(CreateType(method.Module, "System.Reflection", "MethodBase"));
        funcType.GenericArguments.Add(CreateType(method.Module, "System", "Type"));
        funcType.GenericArguments.Add(CreateType(method.Module, "System", "Delegate"));
        TypeReference typedDelegateReference = CreateTypedDelegateInstance(method, delegateType);
        VariableDefinition typedDelegate = new VariableDefinition(typedDelegateReference);
        body.Variables.Add(methodBase);
        body.Variables.Add(dispatcher);
        body.Variables.Add(typedDelegate);

        il.Append(EmitMethodHandle(il, method));
        il.Append(il.Create(OpCodes.Stloc, methodBase));
        il.Append(il.Create(OpCodes.Call, CreateCurrentDomainGetter(method.Module)));
        il.Append(il.Create(OpCodes.Ldstr, TypedDispatchKey));
        il.Append(il.Create(OpCodes.Callvirt, CreateGetData(method.Module)));
        il.Append(il.Create(OpCodes.Castclass, funcType));
        il.Append(il.Create(OpCodes.Stloc, dispatcher));

        il.Append(il.Create(OpCodes.Ldloc, dispatcher));
        il.Append(il.Create(OpCodes.Ldloc, methodBase));
        il.Append(il.Create(OpCodes.Ldtoken, typedDelegateReference));
        il.Append(il.Create(OpCodes.Call, CreateTypeFromHandle(method.Module)));
        il.Append(il.Create(OpCodes.Callvirt, CreateTypedFuncInvoke(method.Module, funcType)));
        il.Append(il.Create(OpCodes.Castclass, typedDelegateReference));
        il.Append(il.Create(OpCodes.Stloc, typedDelegate));

        il.Append(il.Create(OpCodes.Ldloc, typedDelegate));
        if (!method.IsStatic) il.Append(il.Create(OpCodes.Ldarg_0));
        foreach (ParameterDefinition parameter in method.Parameters)
            il.Append(LoadArgument(il, parameter.Index + (method.IsStatic ? 0 : 1)));
        il.Append(il.Create(OpCodes.Callvirt, CreateTypedDelegateInvoke(method, delegateType, typedDelegateReference)));
        il.Append(il.Create(OpCodes.Ret));
        return body;
    }

    private static TypeReference CreateTypedDelegateInstance(MethodDefinition method, TypeDefinition delegateType)
    {
        if (!delegateType.HasGenericParameters) return delegateType;
        GenericInstanceType instance = new GenericInstanceType(delegateType);
        foreach (GenericParameter parameter in method.DeclaringType.GenericParameters) instance.GenericArguments.Add(parameter);
        foreach (GenericParameter parameter in method.GenericParameters) instance.GenericArguments.Add(parameter);
        return instance;
    }

    private static FieldReference CreateGateFieldReference(FieldDefinition gate, MethodDefinition method)
    {
        if (!method.DeclaringType.HasGenericParameters) return gate;
        GenericInstanceType declaringType = new GenericInstanceType(gate.DeclaringType);
        foreach (GenericParameter parameter in method.DeclaringType.GenericParameters)
            declaringType.GenericArguments.Add(parameter);
        return new FieldReference(gate.Name, gate.FieldType, declaringType);
    }

    private static MethodReference CreateTypedDelegateInvoke(MethodDefinition method, TypeDefinition delegateType,
        TypeReference delegateInstanceType)
    {
        MethodDefinition invoke = delegateType.Methods.First(candidate => candidate.Name == "Invoke");
        MethodReference reference = new MethodReference("Invoke", invoke.ReturnType, delegateInstanceType)
        {
            HasThis = true,
            ExplicitThis = false,
            CallingConvention = MethodCallingConvention.Default
        };
        foreach (ParameterDefinition parameter in invoke.Parameters)
            reference.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                parameter.ParameterType));
        return reference;
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
        MethodReference call = original;
        if (target.DeclaringType.HasGenericParameters)
        {
            GenericInstanceType declaringType = new GenericInstanceType(original.DeclaringType);
            foreach (GenericParameter parameter in target.DeclaringType.GenericParameters)
                declaringType.GenericArguments.Add(parameter);
            MethodReference declaringMember = new MethodReference(original.Name,
                original.Module.ImportReference(original.ReturnType, target), declaringType)
            {
                HasThis = original.HasThis,
                ExplicitThis = original.ExplicitThis,
                CallingConvention = original.CallingConvention
            };
            foreach (ParameterDefinition parameter in original.Parameters)
                declaringMember.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes,
                    original.Module.ImportReference(parameter.ParameterType, target)));
            foreach (GenericParameter parameter in original.GenericParameters)
                declaringMember.GenericParameters.Add(new GenericParameter(parameter.Name, declaringMember)
                {
                    Attributes = parameter.Attributes
                });
            call = declaringMember;
        }
        if (target.HasGenericParameters)
        {
            GenericInstanceMethod instantiation = new GenericInstanceMethod(call);
            foreach (GenericParameter parameter in target.GenericParameters) instantiation.GenericArguments.Add(parameter);
            call = instantiation;
        }
        return call;
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

    private static MethodReference CreateTypedFuncInvoke(ModuleDefinition module, GenericInstanceType funcType)
    {
        MethodReference method = module.ImportReference(typeof(Func<RuntimeMethodBase, Type, Delegate>).GetMethod("Invoke"));
        method.DeclaringType = funcType;
        return method;
    }

    private static MethodReference CreateTypeFromHandle(ModuleDefinition module)
    {
        return module.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));
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
