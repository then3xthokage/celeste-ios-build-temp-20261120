using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using celestemeow;
using Mono.Cecil.Cil;
using MonoMod.Cil;

internal static class Program
{
    private static int Main()
    {
        VerifyDependencyResolver();
        string projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        string fixtureOutput = Path.Combine(projectDirectory, "TargetFixture", "bin", configuration, "net8.0");
        string targetPath = Path.Combine(fixtureOutput, "TargetFixture.dll");
        string gameAssemblyPath = Path.Combine(fixtureOutput, "Celeste.dll");
        string dependencyPath = Path.Combine(projectDirectory, "ModDependencyFixture", "bin", configuration, "net8.0", "ModDependencyFixture.dll");
        string hookPath = Path.Combine(projectDirectory, "HookFixture", "bin", configuration, "net8.0", "HookFixture.dll");

        byte[] source = File.ReadAllBytes(targetPath);
        string assemblyName;
        int wovenCount;
        string[] skipped;
        byte[] woven = IOSHookWeaver.WeaveAssembly(source, out assemblyName, out wovenCount, out skipped, fixtureOutput);
        if (skipped.Length != 2 ||
            !skipped.Any(item => item.Contains("<Module>::.cctor", StringComparison.Ordinal)) ||
            !skipped.Any(item => item.Contains("ModuleInitializerProbe::Initialize", StringComparison.Ordinal)))
            throw new InvalidOperationException("Only the module initializer entry points should remain direct: " + string.Join(" | ", skipped));
        using (Mono.Cecil.AssemblyDefinition definition = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(woven)))
        {
            Mono.Cecil.TypeDefinition imports = definition.MainModule.GetType("VideoNativeImportFixture");
            AssertNativeImport(imports, "ReadVideo2", "tf_readvideo2");
            AssertNativeImport(imports, "ReadAudio2", "tf_readaudio2");
            AssertNativeImport(imports, "DecodeGranule", "tf_granpos_dec");
            AssertNativeImport(imports, "GetTheoraSize", "tf_gettheorasize");
            AssertColorImport(definition.MainModule.GetType("ColorNativeImportFixture"), "ColorHLSToRGB");
        }
        int rewovenCount;
        string[] reweaveSkipped;
        IOSHookWeaver.WeaveAssembly(woven, out assemblyName, out rewovenCount, out reweaveSkipped, fixtureOutput);
        if (rewovenCount != 0 || !skipped.SequenceEqual(reweaveSkipped))
            throw new InvalidOperationException("Reweaving an already woven assembly was not idempotent.");

        AssemblyLoadContext modContext = new FixtureLoadContext();
        Assembly targetAssembly = modContext.LoadFromStream(new MemoryStream(woven));
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(targetAssembly.ManifestModule.ModuleHandle);
        AssertEqual("module initializer runs before dispatcher startup", "73",
            ReadStatic<int>(targetAssembly.GetType("ModuleInitializerProbe", true), "Value").ToString());
        AssemblyLoadContext hookContext = new HookLoadContext(targetAssembly);
        Assembly hookAssembly = hookContext.LoadFromAssemblyPath(hookPath);
        Assembly dependencyAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dependencyPath);
        Type targetType = targetAssembly.GetType("TestTarget", true);
        Type crossHooksType = hookAssembly.GetType("CrossHooks", true);
        object instance = Activator.CreateInstance(targetType);

        IOSILHookRuntime.RegisterAssemblySnapshot(targetAssembly, woven);
        IOSILHookRuntime.RegisterAssemblySnapshot(dependencyAssembly, File.ReadAllBytes(dependencyPath));
        IOSHookRuntime.ConfigureGameAssembly(gameAssemblyPath);
        string isolatedModDirectory = Path.Combine(Path.GetTempPath(), "CelesteIOSHookSmoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolatedModDirectory);
            string isolatedHookPath = Path.Combine(isolatedModDirectory, "HookFixture.dll");
            File.Copy(hookPath, isolatedHookPath, true);
            byte[] wovenHook = IOSHookRuntime.WeaveAssembly(isolatedHookPath, File.ReadAllBytes(hookPath));
            if (wovenHook == null || wovenHook.Length == 0)
                throw new InvalidOperationException("The mod assembly resolver did not produce a woven image.");
        }
        finally
        {
            try { if (Directory.Exists(isolatedModDirectory)) Directory.Delete(isolatedModDirectory, true); }
            catch { }
        }

        IOSHookRuntime.Initialize();
        VerifyMonoModCecilBackend();
        VerifyTextCopyClipboard(projectDirectory, configuration, fixtureOutput);
        VerifyCeilingUltraFastFieldGetter(projectDirectory, configuration, fixtureOutput);
        VerifyChroniaExternalLinks(projectDirectory, configuration, fixtureOutput);
        IOSHookRuntime.ConfigureGameAssembly(targetPath);
        Assembly everestRuntime = LoadEverestRuntimeAssembly(projectDirectory, fixtureOutput, gameAssemblyPath);

        Type colorImportType = targetAssembly.GetType("ColorNativeImportFixture", true);
        MethodInfo colorImport = colorImportType.GetMethod("ColorHLSToRGB");
        Delegate colorHook = Delegate.CreateDelegate(targetAssembly.GetType("HookColorNative", true),
            targetAssembly.GetType("TestHooks", true).GetMethod("OnColorNative"), true);
        CallLegacyHookManager(everestRuntime, "Add", colorImport, colorHook);
        int colorHookValue = (int)colorImport.Invoke(null, new object[] { 160, 120, 0 });
        int colorHookCalls = ReadStatic<int>(targetAssembly.GetType("TestHooks", true), "ColorNativeCalls");
        CallLegacyHookManager(everestRuntime, "Remove", colorImport, colorHook);
        int colorAfterRemoval = (int)colorImport.Invoke(null, new object[] { 160, 120, 0 });

        Type genericILTypeDefinition = targetAssembly.GetType("GenericILTypeTarget`1", true);
        Type genericILType = genericILTypeDefinition.MakeGenericType(typeof(int));
        MethodInfo genericILTypeMethod = genericILType.GetMethod("GetTypeValue");
        MethodInfo genericILTypeMethodDefinition = genericILTypeDefinition.GetMethod("GetTypeValue");
        ILContext.Manipulator genericILTypeManipulator = ReplaceLiteral("original-type", "patched-type");
        IOSILHookRuntime.Add(genericILTypeMethodDefinition, genericILTypeManipulator, null);
        MethodInfo genericILTypeMethodOnClosedType = genericILType.GetMethod("GetGenericInstanceValue").GetGenericMethodDefinition();
        MethodInfo genericILTypeMethodClosed = genericILTypeMethodOnClosedType.MakeGenericMethod(typeof(string));
        ILContext.Manipulator genericILTypeMethodManipulator = ReplaceLiteral(
            "original-generic-instance", "patched-generic-instance");
        IOSILHookRuntime.Add(genericILTypeMethodOnClosedType, genericILTypeMethodManipulator, null);
        MethodInfo genericILInstanceMethod = genericILType.GetMethod("GetInstanceValue");
        IOSILHookRuntime.Add(genericILInstanceMethod, ReplaceLiteral("original-instance", "patched-instance"), null);
        object genericILInstance = Activator.CreateInstance(genericILType);
        Type genericILMethodType = targetAssembly.GetType("GenericILMethodTarget", true);
        MethodInfo genericILMethodDefinition = genericILMethodType.GetMethod("GetMethodValue");
        MethodInfo genericILMethod = genericILMethodDefinition.MakeGenericMethod(typeof(int));
        ILContext.Manipulator genericILManipulator = ReplaceLiteral("original-method", "patched-method");
        IOSILHookRuntime.Add(genericILMethodDefinition, genericILManipulator, null);
        Type constructorILType = targetAssembly.GetType("ConstructorILTarget", true);
        ConstructorInfo constructorIL = constructorILType.GetConstructor(Type.EmptyTypes);
        CallLegacyHookManager(everestRuntime, "Modify", constructorIL,
            (Delegate)ReplaceLiteral("original-constructor", "patched-constructor"));
        Delegate constructorOnHook = Delegate.CreateDelegate(targetAssembly.GetType("HookConstructorIL", true),
            targetAssembly.GetType("TestHooks", true).GetMethod("OnConstructorIL"), true);
        CallLegacyHookManager(everestRuntime, "Add", constructorIL, constructorOnHook);
        Type staticConstructorType = targetAssembly.GetType("StaticConstructorTarget", true);
        ConstructorInfo staticConstructor = staticConstructorType.TypeInitializer;
        CallLegacyHookManager(everestRuntime, "Modify", staticConstructor, (Delegate)ReplaceInt32(1, 2));
        Delegate staticConstructorHook = Delegate.CreateDelegate(targetAssembly.GetType("HookStaticConstructor", true),
            targetAssembly.GetType("TestHooks", true).GetMethod("OnStaticConstructor"), true);
        CallLegacyHookManager(everestRuntime, "Add", staticConstructor, staticConstructorHook);
        Type structILType = targetAssembly.GetType("StructILTarget", true);
        MethodInfo structILSetter = structILType.GetMethod("SetValue");
        IOSILHookRuntime.Add(structILSetter, ReplaceInt32(5, 9), null);

        Type genericTargetType = targetAssembly.GetType("GenericRefTarget`1", true).MakeGenericType(typeof(int));
        object genericTarget = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(genericTargetType);
        genericTargetType.GetField("value", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(genericTarget, 31);
        Type genericTypeDelegate = hookAssembly.GetType("GenericTypeHook`1", true).MakeGenericType(typeof(int));
        MethodInfo genericTypeHook = crossHooksType.GetMethod("OnGenericType").MakeGenericMethod(typeof(int));
        IOSHookRuntime.AddOn(genericTargetType.GetMethod("GetRef"),
            Delegate.CreateDelegate(genericTypeDelegate, genericTypeHook, true));

        Type genericMethodType = targetAssembly.GetType("GenericRefMethod", true);
        Type genericMethodDelegate = hookAssembly.GetType("GenericMethodHook`1", true).MakeGenericType(typeof(int));
        MethodInfo genericMethodHook = crossHooksType.GetMethod("OnGenericMethod").MakeGenericMethod(typeof(int));
        IOSHookRuntime.AddOn(genericMethodType.GetMethod("Echo").MakeGenericMethod(typeof(int)),
            Delegate.CreateDelegate(genericMethodDelegate, genericMethodHook, true));

        Type genericCombinedDelegate = hookAssembly.GetType("GenericCombinedHook`2", true)
            .MakeGenericType(typeof(int), typeof(int));
        MethodInfo genericCombinedHook = crossHooksType.GetMethod("OnGenericCombined")
            .MakeGenericMethod(typeof(int), typeof(int));
        IOSHookRuntime.AddOn(genericTargetType.GetMethod("Echo").MakeGenericMethod(typeof(int)),
            Delegate.CreateDelegate(genericCombinedDelegate, genericCombinedHook, true));

        MethodInfo refTarget = targetType.GetMethod("GetRef");
        Type origDelegateType = targetAssembly.GetType("OrigGetRef", true);
        Delegate typedOriginal = IOSHookRuntime.CreateOriginalDelegate(refTarget, origDelegateType);
        AssertEqual("typed original delegate", "5", ((int)typedOriginal.DynamicInvoke(instance)).ToString());
        Delegate refHook = Register(refTarget, hookAssembly, "OnGetRef", "CrossHookGetRef");
        Delegate refHookSecond = Register(refTarget, hookAssembly, "OnGetRefSecond", "CrossHookGetRef");
        Register(targetType.GetMethod("GetSpan"), hookAssembly, "OnGetSpan", "CrossHookGetSpan");
        Register(targetType.GetMethod("SumSpan"), hookAssembly, "OnSumSpan", "CrossHookSumSpan");
        Register(targetType.GetMethod("GetPointer"), hookAssembly, "OnGetPointer", "CrossHookGetPointer");
        AddReflected(targetType.GetMethod("InvokeFunctionPointer"), crossHooksType.GetMethod("OnFunctionPointer"));
        IOSHookRuntime.SetModifiedBody(targetType.GetMethod("InvokeFunctionPointer"),
            targetType.GetMethod("ModifiedFunctionPointer"));
        Register(targetType.GetMethod("AddByRef"), hookAssembly, "OnAddByRef", "CrossHookAddByRef");
        AddReflected(targetType.GetMethod("AddIn"), crossHooksType.GetMethod("OnAddIn"));
        MethodInfo readOnlyRefTarget = targetType.GetMethod("GetReadOnlyRef");
        AddReflected(readOnlyRefTarget, crossHooksType.GetMethod("OnReadOnlyRef"));
        VerifyCustomModifierPreservingAdapters(readOnlyRefTarget);

        int refFirst = Call<int>(targetType, instance, "ReadRef");
        int refSecond = Call<int>(targetType, instance, "ReadRef");
        IOSHookRuntime.RemoveOn(refTarget, refHookSecond);
        int refAfterRemoval = Call<int>(targetType, instance, "ReadRef");
        object reflectedIdentity = new object();
        IOSHookRuntime.AddOnReflected(refTarget, crossHooksType.GetMethod("OnGetRefReflected"),
            null, reflectedIdentity, null);
        int refAfterReflectedHook = Call<int>(targetType, instance, "ReadRef");
        IOSHookRuntime.RemoveOnReflected(refTarget, reflectedIdentity);
        IOSHookRuntime.RemoveOn(refTarget, refHook);
        object detourIdentity = new object();
        IOSHookRuntime.AddDetour(refTarget, targetType.GetMethod("DetourGetRef"), detourIdentity, null);
        Delegate detourTrampoline = IOSHookRuntime.CreateDetourTrampoline(refTarget, detourIdentity, origDelegateType);
        int detourTrampolineValue = (int)detourTrampoline.DynamicInvoke(instance);
        int detourFirst = Call<int>(targetType, instance, "ReadRef");
        int detourSecond = Call<int>(targetType, instance, "ReadRef");

        int spanFirst = Call<int>(targetType, instance, "ReadSpan");
        int spanSecond = Call<int>(targetType, instance, "ReadSpan");
        int spanArgument = Call<int>(targetType, instance, "ExerciseSpanArgument");
        int pointer = Call<int>(targetType, instance, "ExercisePointer");
        int functionPointer = Call<int>(targetType, instance, "ExerciseFunctionPointer");
        int byRefResult = Call<int>(targetType, instance, "ExerciseByRefArgument");
        int inResult = Call<int>(targetType, instance, "ExerciseInArgument");
        int readOnlyRefResult = Call<int>(targetType, instance, "ExerciseReadOnlyRef");

        Type structType = targetAssembly.GetType("TestStructTarget", true);
        object structInstance = Activator.CreateInstance(structType, new object[] { 4 });
        Delegate structHook = Delegate.CreateDelegate(targetAssembly.GetType("HookStructSpan", true),
            targetAssembly.GetType("TestHooks", true).GetMethod("OnStructSpan"), true);
        IOSHookRuntime.AddOn(structType.GetMethod("MutateSpan"), structHook);
        int structSpan = Call<int>(structType, structInstance, "ExerciseSpan");
        object structDetourInstance = Activator.CreateInstance(structType, new object[] { 4 });
        MethodInfo structDetourTarget = structType.GetMethod("Add");
        object structDetourIdentity = new object();
        IOSHookRuntime.AddDetour(structDetourTarget, structType.GetMethod("DetourAdd"), structDetourIdentity, null);
        int structDetourResult = (int)structDetourTarget.Invoke(structDetourInstance, new object[] { 3 });
        int structDetourValue = (int)structType.GetField("value", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(structDetourInstance);
        IOSHookRuntime.RemoveDetour(structDetourTarget, structDetourIdentity);

        Type structConstructorType = targetAssembly.GetType("TestStructConstructorTarget", true);
        ConstructorInfo structConstructor = structConstructorType.GetConstructor(new[] { typeof(int) });
        Delegate structConstructorHook = Delegate.CreateDelegate(targetAssembly.GetType("HookStructConstructor", true),
            targetAssembly.GetType("TestHooks", true).GetMethod("OnStructConstructor"), true);
        IOSHookRuntime.AddOn(structConstructor, structConstructorHook);
        object structConstructed = Activator.CreateInstance(structConstructorType, new object[] { 6 });
        int structConstructorValue = (int)structConstructorType.GetField("Value").GetValue(structConstructed);

        Type branchConstructorType = targetAssembly.GetType("BranchCtorTarget", true);
        ConstructorInfo branchConstructor = branchConstructorType.GetConstructor(new[] { typeof(bool) });
        Delegate branchConstructorHook = Delegate.CreateDelegate(targetAssembly.GetType("HookBranchCtor", true),
            targetAssembly.GetType("TestHooks", true).GetMethod("OnBranchCtor"), true);
        IOSHookRuntime.AddOn(branchConstructor, branchConstructorHook);
        object branchConstructed = Activator.CreateInstance(branchConstructorType, new object[] { true });
        int branchBaseValue = (int)branchConstructorType.BaseType.GetField("BaseValue").GetValue(branchConstructed);
        int branchValue = (int)branchConstructorType.GetField("Value").GetValue(branchConstructed);

        Type genericChainedConstructorType = targetAssembly.GetType("GenericChainedConstructor`1", true)
            .MakeGenericType(typeof(int));
        ConstructorInfo genericChainedConstructor = genericChainedConstructorType.GetConstructor(Type.EmptyTypes);
        Delegate genericChainedConstructorHook = Delegate.CreateDelegate(
            targetAssembly.GetType("HookGenericChainedConstructor", true),
            targetAssembly.GetType("TestHooks", true).GetMethod("OnGenericChainedConstructor"), true);
        IOSHookRuntime.AddOn(genericChainedConstructor, genericChainedConstructorHook);
        object genericChainedConstructed = Activator.CreateInstance(genericChainedConstructorType);
        int genericChainedBaseValue = (int)genericChainedConstructorType.BaseType.GetField("BaseValue").GetValue(genericChainedConstructed);
        int genericChainedValue = (int)genericChainedConstructorType.GetField("Value").GetValue(genericChainedConstructed);

        int genericTypeRef = Call<int>(genericTargetType, genericTarget, "ReadRef");
        int genericMethodRef = CallStatic<int>(genericMethodType, "ExerciseInt");
        int genericCombinedRef = Call<int>(genericTargetType, genericTarget, "ExerciseEcho");
        int genericTypeCalls = ReadStatic<int>(crossHooksType, "GenericTypeCalls");
        int genericMethodCalls = ReadStatic<int>(crossHooksType, "GenericMethodCalls");
        int genericCombinedCalls = ReadStatic<int>(crossHooksType, "GenericCombinedCalls");

        AssertEqual("ref-hook chain", "116/227/238/1249", $"{refFirst}/{refSecond}/{refAfterRemoval}/{refAfterReflectedHook}");
        AssertEqual("typed detours", "2250/3251", $"{detourFirst}/{detourSecond}");
        AssertEqual("typed detour trampoline", "1249", detourTrampolineValue.ToString());
        AssertEqual("span hooks", "21/41/113", $"{spanFirst}/{spanSecond}/{spanArgument}");
        AssertEqual("pointer signatures", "1/119", $"{pointer}/{functionPointer}");
        AssertEqual("byref signatures", "110/3358/41", $"{byRefResult}/{inResult}/{readOnlyRefResult}");
        AssertEqual("value-type instance and detour", "115/34/34",
            $"{structSpan}/{structDetourResult}/{structDetourValue}");
        AssertEqual("value-type constructor hook", "13", structConstructorValue.ToString());
        AssertEqual("branchy class constructor hook", "13/123", $"{branchBaseValue}/{branchValue}");
        AssertEqual("generic chained class constructor hook", "29/1", $"{genericChainedBaseValue}/{genericChainedValue}");
        AssertEqual("generic targets", "31/17/23", $"{genericTypeRef}/{genericMethodRef}/{genericCombinedRef}");
        AssertEqual("generic callbacks", "1/1/1", $"{genericTypeCalls}/{genericMethodCalls}/{genericCombinedCalls}");
        AssertEqual("generic IL hooks", "patched-type/patched-instance/patched-method",
            $"{genericILTypeMethod.Invoke(null, null)}/{genericILInstanceMethod.Invoke(genericILInstance, null)}/{genericILMethod.Invoke(null, null)}");
        AssertEqual("open generic method on a closed generic type", "patched-generic-instance",
            genericILTypeMethodClosed.Invoke(genericILInstance, null).ToString());
        IOSILHookRuntime.Remove(genericILMethodDefinition, genericILManipulator);
        AssertEqual("open generic IL hook removal", "original-method", genericILMethod.Invoke(null, null).ToString());
        IOSILHookRuntime.Remove(genericILTypeMethodOnClosedType, genericILTypeMethodManipulator);
        AssertEqual("open generic method on a closed type removal", "original-generic-instance",
            genericILTypeMethodClosed.Invoke(genericILInstance, null).ToString());
        IOSILHookRuntime.Remove(genericILTypeMethodDefinition, genericILTypeManipulator);
        AssertEqual("open generic declaring type IL hook removal", "original-type", genericILTypeMethod.Invoke(null, null).ToString());
        object constructorILInstance = Activator.CreateInstance(constructorILType);
        int staticConstructorValue = CallStatic<int>(staticConstructorType, "GetValue");
        object structILInstance = Activator.CreateInstance(structILType);
        structILSetter.Invoke(structILInstance, null);
        AssertEqual("class constructor and value-type IL hooks", "patched-constructor/hook/9",
            $"{constructorILType.GetField("Value").GetValue(constructorILInstance)}/{structILType.GetMethod("GetValue").Invoke(structILInstance, null)}");
        AssertEqual("static constructor On and IL hooks", "12", staticConstructorValue.ToString());
        AssertEqual("rewritten Windows HLS import remains hookable", "007F7F86/1", $"{colorHookValue:X8}/{colorHookCalls}");
        AssertEqual("Everest legacy On removal", "007F7F7F", colorAfterRemoval.ToString("X8"));

        Console.WriteLine("Typed hook smoke passed: woven=" + wovenCount + "; patched Everest On/IL integration and removal; open generic IL methods and declaring types; native decoder imports; hookable Windows HLS import shim; app-bundle and in-memory mod dependency resolution; ref-return, exact ref-readonly modifiers, in/ref, Span, pointer and function-pointer signatures; value-type instance detours and constructors; branchy and generic constructor chains; static constructors; generic type/method combinations; reflected callbacks; and cross-load-context dispatch.");
        return 0;
    }

    private static void VerifyMonoModCecilBackend()
    {
        string monoModUtilsPath = typeof(MonoMod.Utils.DynamicMethodDefinition).Assembly.Location;
        byte[] original = File.ReadAllBytes(monoModUtilsPath);
        byte[] woven = IOSHookWeaver.WeaveAssembly(original, out string assemblyName, out int wovenCount,
            out string[] skipped, Path.GetDirectoryName(monoModUtilsPath));
        if (assemblyName != "MonoMod.Utils" || wovenCount != 0 || skipped.Length != 0 || !original.SequenceEqual(woven))
            throw new InvalidOperationException("MonoMod.Utils hook-backend infrastructure must stay byte-for-byte unchanged.");

        MonoMod.Switches.SetSwitchValue("DMDType", "cecil");
        using MonoMod.Utils.DynamicMethodDefinition dmd = new MonoMod.Utils.DynamicMethodDefinition(
            "ios_dmd_smoke", typeof(int), Type.EmptyTypes);
        dmd.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 42);
        dmd.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        MethodInfo generated = dmd.Generate();
        object answer = generated.Invoke(null, null);
        if (!(answer is int value) || value != 42)
            throw new InvalidOperationException("MonoMod's Cecil DynamicMethodDefinition backend returned an unexpected value.");

        Console.WriteLine("MonoMod Cecil backend smoke passed: runtime infrastructure preserved and generated method executed.");
    }

    private static Assembly LoadEverestRuntimeAssembly(string projectDirectory, string fixtureOutput, string gameAssemblyPath)
    {
        string repoDirectory = Path.GetFullPath(Path.Combine(projectDirectory, "..", ".."));
        string runtimeDirectory = Path.Combine(repoDirectory, "Everest", "Celeste.Mod.mm", "bin", "Release", "net8.0");
        string runtimePath = Path.Combine(runtimeDirectory, "Celeste.Mod.mm.dll");
        if (!File.Exists(runtimePath))
            throw new FileNotFoundException("The patched Everest runtime was not built for the compatibility smoke test.", runtimePath);

        string[] resolverDirectories = new[]
        {
            runtimeDirectory,
            Path.Combine(repoDirectory, "Everest", "lib-stripped"),
            fixtureOutput,
            Path.GetDirectoryName(gameAssemblyPath),
            @"C:\Games\Celeste\orig"
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string directory in resolverDirectories)
            {
                string candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(runtimePath);
    }

    private static void CallLegacyHookManager(Assembly everestRuntime, string methodName, MethodBase target, Delegate callback)
    {
        Type endpointManager = everestRuntime.GetType("Celeste.Mod.Helpers.LegacyMonoMod.LegacyHookEndpointManager", true);
        MethodInfo method = endpointManager.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(candidate => candidate.Name == methodName && !candidate.IsGenericMethod &&
                candidate.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(new[] { typeof(MethodBase), typeof(Delegate) }));
        if (method == null)
            throw new MissingMethodException(endpointManager.FullName, methodName);
        try
        {
            method.Invoke(null, new object[] { target, callback });
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw new InvalidOperationException("Everest hook-manager invocation failed: " + exception.InnerException, exception.InnerException);
        }
    }

    private static object AddReflected(MethodInfo target, MethodInfo hook)
    {
        object identity = new object();
        IOSHookRuntime.AddOnReflected(target, hook, null, identity, null);
        return identity;
    }

    private static void VerifyCustomModifierPreservingAdapters(MethodInfo target)
    {
        string name = "CelesteIOS.Generated.__ios_typed_delegate_" + IOSHookIdentity.ForMethod(target);
        Type delegateType = target.Module.Assembly.GetType(name, true, false);
        MethodInfo expectedInvoke = delegateType.GetMethod("Invoke");
        Func<MethodBase, Type, Delegate> dispatch = AppDomain.CurrentDomain.GetData(IOSHookRuntime.TypedDispatchDataKey)
            as Func<MethodBase, Type, Delegate>;
        Delegate hooked = dispatch(target, delegateType);
        AssertAdapterSignature("typed hook adapter", hooked.Method, expectedInvoke, 1);
        IOSTypedHookInvocationState hookState = hooked.Target as IOSTypedHookInvocationState;
        if (hookState == null || hookState.Original == null)
            throw new InvalidOperationException("The readonly-reference hook did not receive its typed orig adapter.");
        AssertAdapterSignature("typed continuation adapter", hookState.Original.Method,
            hookState.Original.GetType().GetMethod("Invoke"), 1);
    }

    private static void AssertAdapterSignature(string label, MethodInfo adapter, MethodInfo expected, int boundStateCount)
    {
        if (adapter.ReturnType != expected.ReturnType ||
            !adapter.ReturnParameter.GetRequiredCustomModifiers().SequenceEqual(expected.ReturnParameter.GetRequiredCustomModifiers()) ||
            !adapter.ReturnParameter.GetOptionalCustomModifiers().SequenceEqual(expected.ReturnParameter.GetOptionalCustomModifiers()))
            throw new InvalidOperationException(label + " did not preserve the exact return signature.");
        ParameterInfo[] actualParameters = adapter.GetParameters().Skip(boundStateCount).ToArray();
        ParameterInfo[] expectedParameters = expected.GetParameters();
        if (actualParameters.Length != expectedParameters.Length)
            throw new InvalidOperationException(label + " did not preserve the parameter count.");
        for (int index = 0; index < actualParameters.Length; index++)
        {
            ParameterInfo actual = actualParameters[index];
            ParameterInfo wanted = expectedParameters[index];
            if (actual.ParameterType != wanted.ParameterType ||
                !actual.GetRequiredCustomModifiers().SequenceEqual(wanted.GetRequiredCustomModifiers()) ||
                !actual.GetOptionalCustomModifiers().SequenceEqual(wanted.GetOptionalCustomModifiers()))
                throw new InvalidOperationException(label + " did not preserve parameter " + index + " exactly.");
        }
    }

    private static void AssertNativeImport(Mono.Cecil.TypeDefinition type, string methodName, string entryPoint)
    {
        Mono.Cecil.MethodDefinition method = type.Methods.Single(candidate => candidate.Name == methodName);
        if (method.PInvokeInfo == null || method.PInvokeInfo.Module.Name != "__Internal" ||
            method.PInvokeInfo.EntryPoint != entryPoint)
            throw new InvalidOperationException("The iOS native importer did not map theorafilebun!" + entryPoint + " to the bundled native decoder.");
    }

    private static void VerifyTextCopyClipboard(string projectDirectory, string configuration, string fixtureOutput)
    {
        string sourcePath = Path.Combine(projectDirectory, "TextCopyFixture", "bin", configuration, "net8.0", "TextCopy.dll");
        byte[] source = File.ReadAllBytes(sourcePath);
        string assemblyName;
        int wovenCount;
        string[] skipped;
        byte[] woven = IOSHookWeaver.WeaveAssembly(source, out assemblyName, out wovenCount, out skipped, fixtureOutput);
        if (assemblyName != "TextCopy" || wovenCount == 0 || skipped.Length != 0)
            throw new InvalidOperationException("The iOS TextCopy compatibility rewrite did not weave its fixture cleanly.");

        string clipboardText = "clipboard-get";
        string valueReceived = null;
        AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.GetText", (Func<string>)(() => clipboardText));
        AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.SetText", (Action<string>)(value => valueReceived = value));
        AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.GetTextAsync",
            (Func<CancellationToken, Task<string>>)
                (token => Task.FromResult(clipboardText + "-async")));
        AppDomain.CurrentDomain.SetData("Celeste.IOSClipboard.SetTextAsync",
            (Func<string, CancellationToken, Task>)
                ((value, token) => { valueReceived = value + "-async"; return Task.CompletedTask; }));

        AssemblyLoadContext context = new FixtureLoadContext();
        Assembly assembly = context.LoadFromStream(new MemoryStream(woven));
        Type service = assembly.GetType("TextCopy.ClipboardService", true);
        AssertEqual("TextCopy synchronous clipboard get", "clipboard-get", (string)service.GetMethod("GetText").Invoke(null, null));
        service.GetMethod("SetText").Invoke(null, new object[] { "sync-write" });
        AssertEqual("TextCopy synchronous clipboard set", "sync-write", valueReceived);
        Task<string> getTask = (Task<string>)service.GetMethod("GetTextAsync").Invoke(null, new object[] { CancellationToken.None });
        AssertEqual("TextCopy asynchronous clipboard get", "clipboard-get-async", getTask.GetAwaiter().GetResult());
        Task setTask = (Task)service.GetMethod("SetTextAsync").Invoke(null, new object[] { "async-write", CancellationToken.None });
        setTask.GetAwaiter().GetResult();
        AssertEqual("TextCopy asynchronous clipboard set", "async-write-async", valueReceived);
    }

    private static void VerifyDependencyResolver()
    {
        const string graph = @"
Root:
  Dependencies:
    - Name: Alpha
      Version: 1.0.0
    - Name: beta
      Version: 1.0.0
  OptionalDependencies:
    - Name: OptionalHelper
Alpha:
  Dependencies:
    - Name: Beta
      Version: 1.0.0
    - Name: Gamma
      Version: 1.0.0
Beta:
  Dependencies:
    - Name: gamma
      Version: 2.0
Gamma: {}
";
        ModDependencyResolver resolver = ModDependencyResolver.Parse(graph);
        System.Collections.Generic.List<ModDependency> dependencies = new System.Collections.Generic.List<ModDependency>();
        System.Collections.Generic.List<ModDependency> optional = new System.Collections.Generic.List<ModDependency>();
        resolver.ApplyTo("root", dependencies, optional);
        AssertEqual("Dependency graph availability", "True", resolver.IsAvailable.ToString());
        AssertEqual("Optional dependency catalog merge", "OptionalHelper", optional.Single().Name);

        System.Collections.Generic.IReadOnlyList<ModDependency> closure = resolver.ResolveRequiredClosure(
            dependencies,
            name => string.Equals(name, "Gamma", StringComparison.OrdinalIgnoreCase)
                ? new[] { new ModDependency { Name = "ManifestOnly", Version = "3.0" } }
                : Array.Empty<ModDependency>());
        AssertEqual("Dependency-first closure", "ManifestOnly,gamma,Beta,Alpha", string.Join(",", closure.Select(item => item.Name)));
        AssertEqual("Highest transitive version", "2.0", closure.Single(item => string.Equals(item.Name, "Gamma", StringComparison.OrdinalIgnoreCase)).Version);
        if (closure.Any(item => item.Name == "OptionalHelper"))
            throw new InvalidOperationException("Optional dependencies must not be installed as required dependencies.");

        string graphCache = Path.Combine(Path.GetTempPath(), "CelesteIOSModGraph-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(graphCache, graph);
            ModDependencyResolver cached = ModDependencyResolver.LoadCached(graphCache);
            System.Collections.Generic.List<ModDependency> cachedRequirements = new System.Collections.Generic.List<ModDependency>();
            cached.ApplyTo("Root", cachedRequirements, new System.Collections.Generic.List<ModDependency>());
            AssertEqual("Cached dependency graph loaded before manager actions", "True", cached.IsAvailable.ToString());
            AssertEqual("Cached graph-only required dependency", "Alpha,beta", string.Join(",", cachedRequirements.Select(item => item.Name)));
        }
        finally
        {
            if (File.Exists(graphCache)) File.Delete(graphCache);
        }

        System.Collections.Generic.List<ModDependencyPackageState> packages = new System.Collections.Generic.List<ModDependencyPackageState>
        {
            new ModDependencyPackageState
            {
                PackageName = "Campaign",
                ModuleNames = new System.Collections.Generic.List<string> { "Campaign" },
                RequiredModuleNames = new System.Collections.Generic.List<string> { "Library" }
            },
            new ModDependencyPackageState
            {
                PackageName = "GameplayHelper",
                ModuleNames = new System.Collections.Generic.List<string> { "GameplayHelper" },
                RequiredModuleNames = new System.Collections.Generic.List<string> { "Campaign" }
            },
            new ModDependencyPackageState
            {
                PackageName = "Unrelated",
                ModuleNames = new System.Collections.Generic.List<string> { "Unrelated" }
            }
        };
        System.Collections.Generic.HashSet<string> enabledPackages = new System.Collections.Generic.HashSet<string>(
            new[] { "Campaign", "GameplayHelper", "Unrelated" }, StringComparer.OrdinalIgnoreCase);
        System.Collections.Generic.List<string> disabledPackages = ModDependencyResolver.DisableEnabledDependents(
            new[] { "Library" }, packages, enabledPackages);
        AssertEqual("Disable cascades through graph-only required dependencies", "Campaign,GameplayHelper", string.Join(",", disabledPackages));
        AssertEqual("Unrelated mod remains enabled", "Unrelated", string.Join(",", enabledPackages));

        ModDependencyResolver cyclic = ModDependencyResolver.Parse(@"
CycleA:
  Dependencies:
    - Name: CycleB
CycleB:
  Dependencies:
    - Name: CycleA
");
        bool cycleDetected = false;
        try { cyclic.ResolveRequiredClosure(new[] { new ModDependency { Name = "CycleA" } }); }
        catch (InvalidDataException) { cycleDetected = true; }
        if (!cycleDetected)
            throw new InvalidOperationException("The dependency resolver accepted a required dependency cycle.");
    }

    private static void VerifyCeilingUltraFastFieldGetter(string projectDirectory, string configuration, string fixtureOutput)
    {
        string sourcePath = Path.Combine(projectDirectory, "CeilingUltraFixture", "bin", configuration, "net8.0", "CeilingUltra.dll");
        string assemblyName;
        int wovenCount;
        string[] skipped;
        byte[] woven = IOSHookWeaver.WeaveAssembly(File.ReadAllBytes(sourcePath), out assemblyName, out wovenCount, out skipped, fixtureOutput);
        if (assemblyName != "CeilingUltra" || wovenCount == 0 || skipped.Length != 0)
            throw new InvalidOperationException("The CeilingUltra iOS DynamicMethod compatibility rewrite did not weave its fixture cleanly.");

        AssemblyLoadContext context = new FixtureLoadContext();
        Assembly assembly = context.LoadFromStream(new MemoryStream(woven));
        Type targetType = assembly.GetType("Celeste.Mod.CeilingUltra.Utils.GetterTarget", true);
        object target = Activator.CreateInstance(targetType);
        Type reflectionType = assembly.GetType("Celeste.Mod.CeilingUltra.Utils.FastReflection", true);
        MethodInfo factory = reflectionType.GetMethod("CreateGetDelegate").MakeGenericMethod(targetType, typeof(string));
        Delegate getter = (Delegate)factory.Invoke(null, new object[] { targetType, "hidden" });
        AssertEqual("CeilingUltra reflection-backed private field getter", "private-field", (string)getter.DynamicInvoke(target));
    }

    private static void VerifyChroniaExternalLinks(string projectDirectory, string configuration, string fixtureOutput)
    {
        string sourcePath = Path.Combine(projectDirectory, "ChroniaHelperFixture", "bin", configuration, "net8.0", "ChroniaHelper.dll");
        string assemblyName;
        int wovenCount;
        string[] skipped;
        byte[] woven = IOSHookWeaver.WeaveAssembly(File.ReadAllBytes(sourcePath), out assemblyName, out wovenCount, out skipped, fixtureOutput);
        if (assemblyName != "ChroniaHelper" || wovenCount == 0 || skipped.Length != 0)
            throw new InvalidOperationException("The ChroniaHelper iOS external-link rewrite did not weave its fixture cleanly.");

        string openedUrl = null;
        AppDomain.CurrentDomain.SetData("Celeste.IOSExternalLinks.OpenUrl", (Action<string>)(url => openedUrl = url));
        AssemblyLoadContext context = new FixtureLoadContext();
        Assembly assembly = context.LoadFromStream(new MemoryStream(woven));
        assembly.GetType("ChroniaHelper.OpenUrl", true).GetMethod("Open").Invoke(null, new object[] { "https://example.test/" });
        AssertEqual("ChroniaHelper external link", "https://example.test/", openedUrl);
    }

    private static void AssertColorImport(Mono.Cecil.TypeDefinition type, string methodName)
    {
        Mono.Cecil.MethodDefinition method = type.Methods.Single(candidate => candidate.Name == methodName);
        bool originalCallsShim = type.Methods.Any(candidate => candidate.Name.StartsWith("__ios_original_", StringComparison.Ordinal) &&
            candidate.HasBody && candidate.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Call &&
                instruction.Operand is Mono.Cecil.MethodReference called && called.Name == "ColorHLSToRGB"));
        if (method.PInvokeInfo != null || method.IsPInvokeImpl || !method.HasBody || !originalCallsShim)
            throw new InvalidOperationException("The Windows HLS import was not converted into the managed hookable iOS compatibility method.");
    }

    private static ILContext.Manipulator ReplaceLiteral(string from, string to)
    {
        return context =>
        {
            Instruction literal = context.Body.Instructions.FirstOrDefault(instruction =>
                instruction.OpCode == OpCodes.Ldstr && string.Equals(instruction.Operand as string, from, StringComparison.Ordinal));
            if (literal == null) throw new InvalidOperationException("Could not find the expected IL string literal " + from + ".");
            literal.Operand = to;
        };
    }

    private static ILContext.Manipulator ReplaceInt32(int from, int to)
    {
        return context =>
        {
            Instruction constant = context.Body.Instructions.FirstOrDefault(instruction =>
                instruction.OpCode.Code >= Code.Ldc_I4_M1 && instruction.OpCode.Code <= Code.Ldc_I4_8 &&
                ReadInt32(instruction) == from);
            if (constant == null) throw new InvalidOperationException("Could not find the expected IL integer " + from + ".");
            constant.OpCode = to >= -1 && to <= 8 ? OpCodes.Ldc_I4 : OpCodes.Ldc_I4;
            constant.Operand = to;
        };
    }

    private static int ReadInt32(Instruction instruction)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Ldc_I4_M1: return -1;
            case Code.Ldc_I4_0: return 0;
            case Code.Ldc_I4_1: return 1;
            case Code.Ldc_I4_2: return 2;
            case Code.Ldc_I4_3: return 3;
            case Code.Ldc_I4_4: return 4;
            case Code.Ldc_I4_5: return 5;
            case Code.Ldc_I4_6: return 6;
            case Code.Ldc_I4_7: return 7;
            case Code.Ldc_I4_8: return 8;
            default: return instruction.Operand is int value ? value : int.MinValue;
        }
    }

    private static T Call<T>(Type type, object instance, string name)
    {
        return (T)type.GetMethod(name).Invoke(instance, null);
    }

    private static T CallStatic<T>(Type type, string name)
    {
        return (T)type.GetMethod(name).Invoke(null, null);
    }

    private static T ReadStatic<T>(Type type, string name)
    {
        return (T)type.GetField(name).GetValue(null);
    }

    private static void AssertEqual(string label, string expected, string actual)
    {
        if (expected != actual)
            throw new InvalidOperationException(label + " expected " + expected + " but observed " + actual + ".");
    }

    private static Delegate Register(MethodInfo target, Assembly hookAssembly, string hookMethodName, string hookDelegateName)
    {
        Type hookDelegateType = hookAssembly.GetType(hookDelegateName, true);
        MethodInfo hookMethod = hookAssembly.GetType("CrossHooks", true).GetMethod(hookMethodName);
        Delegate hook = Delegate.CreateDelegate(hookDelegateType, hookMethod, true);
        IOSHookRuntime.AddOn(target, hook);
        return hook;
    }

    private sealed class FixtureLoadContext : AssemblyLoadContext
    {
        public FixtureLoadContext() : base("ios-hook-smoke-target") { }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            try { return Default.LoadFromAssemblyName(assemblyName); }
            catch { return null; }
        }
    }

    private sealed class HookLoadContext : AssemblyLoadContext
    {
        private readonly Assembly targetAssembly;

        public HookLoadContext(Assembly targetAssembly) : base("ios-hook-smoke-hook")
        {
            this.targetAssembly = targetAssembly;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == targetAssembly.GetName().Name) return targetAssembly;
            try { return Default.LoadFromAssemblyName(assemblyName); }
            catch { return null; }
        }
    }
}
