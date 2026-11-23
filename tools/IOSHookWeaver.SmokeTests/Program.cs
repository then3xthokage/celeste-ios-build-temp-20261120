using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using celestemeow;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

internal static class Program
{
    private static MethodBase legacyTrampolineOverride;

    private static int Main(string[] args)
    {
        VerifyBundledRuntimeAssemblyResolver();
        VerifyDependencyResolver();
        string projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        string fixtureOutput = Path.Combine(projectDirectory, "TargetFixture", "bin", configuration, "net8.0");
        string targetPath = Path.Combine(fixtureOutput, "TargetFixture.dll");
        string gameAssemblyPath = Path.Combine(fixtureOutput, "Celeste.dll");
        string dependencyPath = Path.Combine(projectDirectory, "ModDependencyFixture", "bin", configuration, "net8.0", "ModDependencyFixture.dll");
        string hookPath = Path.Combine(projectDirectory, "HookFixture", "bin", configuration, "net8.0", "HookFixture.dll");

        byte[] source = ApplyEverestStyleConstructorShape(File.ReadAllBytes(targetPath));
        string assemblyName;
        int wovenCount;
        string[] skipped;
        byte[] woven = IOSHookWeaver.WeaveAssembly(source, out assemblyName, out wovenCount, out skipped, fixtureOutput);
        if (skipped.Length != 0)
            throw new InvalidOperationException("The executable fixture methods should all receive hook gates: " + string.Join(" | ", skipped));
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
        VerifyEverestStyleConstructor(targetAssembly);
        VerifyHookableModuleInitializer(woven, hookPath);
        VerifyTextCopyClipboard(projectDirectory, configuration, fixtureOutput);
        VerifyCeilingUltraFastFieldGetter(projectDirectory, configuration, fixtureOutput);
        VerifyChroniaExternalLinks(projectDirectory, configuration, fixtureOutput);
        IOSHookRuntime.ConfigureGameAssembly(targetPath);
        Assembly everestRuntime = LoadEverestRuntimeAssembly(projectDirectory, fixtureOutput, gameAssemblyPath);
        VerifyEverestDualStackConnection(everestRuntime);
        VerifyEverestDependencyDownloaderHookGates(projectDirectory, configuration, gameAssemblyPath);
        VerifyLegacyDetourTrampoline(everestRuntime, targetAssembly);
        VerifyLegacyHookManagedRoute(everestRuntime, targetAssembly);

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

        Type genericTargetDefinition = genericTargetType.GetGenericTypeDefinition();
        MethodInfo openGenericTypeTarget = genericTargetDefinition.GetMethod("GetRef");
        object openGenericTypeHookIdentity = new object();
        IOSHookRuntime.AddOnReflected(openGenericTypeTarget, crossHooksType.GetMethod("OnGenericType"),
            null, openGenericTypeHookIdentity, null);
        Type stringGenericTargetType = genericTargetDefinition.MakeGenericType(typeof(string));
        object stringGenericTarget = Activator.CreateInstance(stringGenericTargetType, new object[] { "open-type" });
        AssertEqual("open generic type reflected hook", "open-type",
            stringGenericTargetType.GetMethod("GetRef").Invoke(stringGenericTarget, null).ToString());
        IOSHookRuntime.RemoveOnReflected(openGenericTypeTarget, openGenericTypeHookIdentity);

        MethodInfo openGenericCombinedTarget = genericTargetDefinition.GetMethod("Echo").GetGenericMethodDefinition();
        object openGenericCombinedIdentity = new object();
        IOSHookRuntime.AddOnReflected(openGenericCombinedTarget,
            crossHooksType.GetMethod("OnGenericCombined"), null, openGenericCombinedIdentity, null);
        MethodInfo stringIntCombinedTarget = stringGenericTargetType.GetMethod("Echo").MakeGenericMethod(typeof(int));
        AssertEqual("open generic declaring type and method reflected hook", "29",
            stringIntCombinedTarget.Invoke(stringGenericTarget, new object[] { 29 }).ToString());
        IOSHookRuntime.RemoveOnReflected(openGenericCombinedTarget, openGenericCombinedIdentity);

        object openGenericInstanceDetourIdentity = new object();
        IOSHookRuntime.AddDetour(openGenericCombinedTarget,
            genericTargetDefinition.GetMethod("DetourEcho"), openGenericInstanceDetourIdentity, null);
        AssertEqual("open generic instance detour", "31",
            stringIntCombinedTarget.Invoke(stringGenericTarget, new object[] { 31 }).ToString());
        AssertEqual("open generic instance detour callback count", "1",
            stringGenericTargetType.GetField("DetourCalls").GetValue(stringGenericTarget).ToString());
        IOSHookRuntime.RemoveDetour(openGenericCombinedTarget, openGenericInstanceDetourIdentity);

        MethodInfo openGenericMethodTarget = genericMethodType.GetMethod("Echo").GetGenericMethodDefinition();
        MethodInfo genericMethodHookDefinition = crossHooksType.GetMethod("OnGenericMethod");
        object openGenericMethodHookIdentity = new object();
        IOSHookRuntime.AddOnReflected(openGenericMethodTarget, genericMethodHookDefinition,
            null, openGenericMethodHookIdentity, null);
        MethodInfo stringGenericMethodTarget = openGenericMethodTarget.MakeGenericMethod(typeof(string));
        AssertEqual("open generic method reflected hook", "open-method",
            stringGenericMethodTarget.Invoke(null, new object[] { "open-method" }).ToString());
        IOSHookRuntime.RemoveOnReflected(openGenericMethodTarget, openGenericMethodHookIdentity);

        MethodInfo genericDetourDefinition = crossHooksType.GetMethod("DetourGenericMethod");
        object openGenericDetourIdentity = new object();
        IOSHookRuntime.AddDetour(openGenericMethodTarget, genericDetourDefinition, openGenericDetourIdentity, null);
        AssertEqual("open generic method reflected detour", "open-detour",
            stringGenericMethodTarget.Invoke(null, new object[] { "open-detour" }).ToString());
        IOSHookRuntime.RemoveDetour(openGenericMethodTarget, openGenericDetourIdentity);
        AssertEqual("open generic method detour callback count", "1",
            ReadStatic<int>(crossHooksType, "GenericDetourCalls").ToString());

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

        Type refLikeConstructorType = targetAssembly.GetType("RefLikeConstructorTarget", true);
        ConstructorInfo refLikeConstructor = refLikeConstructorType.GetConstructor(new[] { typeof(int), typeof(bool).MakeByRefType() });
        Delegate refLikeConstructorHook = Delegate.CreateDelegate(hookAssembly.GetType("HookRefLikeConstructor", true),
            crossHooksType.GetMethod("OnRefLikeConstructor"), true);
        IOSHookRuntime.AddOn(refLikeConstructor, refLikeConstructorHook);
        int refLikeConstructorValue = (int)targetAssembly.GetType("RefLikeConstructorProbe", true)
            .GetMethod("CreateAndRead").Invoke(null, new object[] { 31 });
        int refLikeConstructorCalls = ReadStatic<int>(crossHooksType, "RefLikeConstructorCalls");
        IOSHookRuntime.RemoveOn(refLikeConstructor, refLikeConstructorHook);

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
        AssertEqual("byref-like constructor hook without boxing", "31/1", $"{refLikeConstructorValue}/{refLikeConstructorCalls}");
        AssertEqual("branchy class constructor hook", "13/123", $"{branchBaseValue}/{branchValue}");
        AssertEqual("generic chained class constructor hook", "29/1", $"{genericChainedBaseValue}/{genericChainedValue}");
        AssertEqual("generic targets", "31/17/23", $"{genericTypeRef}/{genericMethodRef}/{genericCombinedRef}");
        AssertEqual("generic callbacks", "2/2/2", $"{genericTypeCalls}/{genericMethodCalls}/{genericCombinedCalls}");
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

        Console.WriteLine("Typed hook smoke passed: woven=" + wovenCount + "; patched Everest On/IL integration and removal; full-body Everest constructor wrappers with branches; open generic IL, reflected On hooks, and detours across generic methods and declaring types; native decoder imports; hookable Windows HLS import shim; app-bundle and in-memory mod dependency resolution; ref-return, exact ref-readonly modifiers, in/ref, Span, pointer and function-pointer signatures; value-type instance detours and constructors; branchy and generic constructor chains; static constructors; generic type/method combinations; reflected callbacks; and cross-load-context dispatch.");
        return 0;
    }

    private static void VerifyBundledRuntimeAssemblyResolver()
    {
        string projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        string fnaFixturePath = Path.Combine(projectDirectory, "BundledResolverFixture", "bin", configuration,
            "net8.0", "FNA.dll");
        string everestFixturePath = Path.Combine(projectDirectory, "EverestResolverFixture", "bin", configuration,
            "net8.0", "EverestResolverFixture.dll");
        string gameAssemblyFixturePath = Path.Combine(projectDirectory, "TargetFixture", "bin", configuration,
            "net8.0", "Celeste.dll");
        if (!File.Exists(fnaFixturePath))
            throw new FileNotFoundException("The bundled FNA resolver fixture was not built.", fnaFixturePath);
        if (!File.Exists(everestFixturePath))
            throw new FileNotFoundException("The bundled Everest resolver fixture was not built.", everestFixturePath);
        if (!File.Exists(gameAssemblyFixturePath))
            throw new FileNotFoundException("The bundled Celeste resolver fixture was not built.", gameAssemblyFixturePath);

        string appBundle = Path.Combine(Path.GetTempPath(), "CelesteIOSRuntimeResolverSmoke-" + Guid.NewGuid().ToString("N"));
        string runtimeDirectory = Path.Combine(appBundle, "RuntimeAssemblies");
        string everestDirectory = Path.Combine(appBundle, "RuntimeAssemblies", "Everest");
        Directory.CreateDirectory(runtimeDirectory);
        Directory.CreateDirectory(everestDirectory);
        File.Copy(gameAssemblyFixturePath, Path.Combine(appBundle, "Celeste.dll"));
        File.Copy(fnaFixturePath, Path.Combine(runtimeDirectory, "FNA.dll"));
        File.Copy(everestFixturePath, Path.Combine(everestDirectory, "EverestResolverFixture.dll"));

        string locatedGameAssembly = IOSRuntimeAssemblyResolver.FindAssemblyPath(appBundle, "Celeste");
        if (!string.Equals(locatedGameAssembly, Path.Combine(appBundle, "Celeste.dll"), StringComparison.Ordinal))
            throw new InvalidOperationException("The launcher did not locate Celeste.dll at the app bundle root.");

        AssemblyLoadContext context = new AssemblyLoadContext("NestedRuntimeResolverSmoke", isCollectible: true);
        try
        {
            IOSRuntimeAssemblyResolver.Attach(context, appBundle, null);
            Assembly gameFixture = context.LoadFromAssemblyPath(locatedGameAssembly);
            if (!string.Equals(gameFixture.GetName().Name, "Celeste", StringComparison.Ordinal))
                throw new InvalidOperationException("The launcher loaded the wrong assembly from the app bundle root.");

            Assembly fnaFixture = context.LoadFromAssemblyName(new AssemblyName("FNA, Version=24.1.0.0"));
            Type fnaProbe = fnaFixture.GetType("BundledResolverFixture.Probe", throwOnError: true);
            string fnaValue = (string)fnaProbe.GetProperty("Value").GetValue(null);
            if (fnaValue != "loaded from RuntimeAssemblies/FNA.dll")
                throw new InvalidOperationException("The bundled FNA resolver loaded the wrong assembly.");

            Assembly everestFixture = context.LoadFromAssemblyName(new AssemblyName("EverestResolverFixture"));
            Type everestProbe = everestFixture.GetType("EverestResolverFixture.Probe", throwOnError: true);
            string everestValue = (string)everestProbe.GetProperty("Value").GetValue(null);
            if (everestValue != "loaded from RuntimeAssemblies/Everest")
                throw new InvalidOperationException("The nested Everest resolver loaded the wrong assembly.");
        }
        finally
        {
            context.Unload();
            try { if (Directory.Exists(appBundle)) Directory.Delete(appBundle, recursive: true); }
            catch { }
        }

        Console.WriteLine("PASS iOS runtime resolver locates Celeste.dll at the app bundle root and loads FNA and Everest assemblies from their bundled folders.");
    }

    private static byte[] ApplyEverestStyleConstructorShape(byte[] source)
    {
        using (Mono.Cecil.AssemblyDefinition assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(
            new MemoryStream(source), new Mono.Cecil.ReaderParameters { InMemory = true }))
        {
            Mono.Cecil.TypeDefinition type = assembly.MainModule.GetType("EverestStyleConstructorTarget");
            Mono.Cecil.MethodDefinition constructor = type.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            Mono.Cecil.MethodDefinition original = type.Methods.Single(method => method.Name == "orig_ctor");
            Instruction baseCall = constructor.Body.Instructions.First(instruction =>
                instruction.Operand is Mono.Cecil.MethodReference called &&
                called.Name == ".ctor" && called.DeclaringType.FullName == "System.Object");
            Instruction loadThis = baseCall.Previous;
            if (loadThis == null || loadThis.OpCode.Code != Code.Ldarg_0)
                throw new InvalidOperationException("The constructor fixture no longer has a recognizable Object initializer.");

            constructor.Body.Instructions.Remove(loadThis);
            constructor.Body.Instructions.Remove(baseCall);
            Instruction aliasCall = constructor.Body.Instructions.First(instruction =>
                instruction.Operand is Mono.Cecil.MethodReference called && called.Name == "orig_ctor");
            aliasCall.OpCode = OpCodes.Callvirt;

            Instruction originalFirst = original.Body.Instructions[0];
            ILProcessor originalIl = original.Body.GetILProcessor();
            originalIl.InsertBefore(originalFirst, originalIl.Create(OpCodes.Ldarg_0));
            originalIl.InsertBefore(originalFirst, originalIl.Create(OpCodes.Call,
                assembly.MainModule.ImportReference((Mono.Cecil.MethodReference)baseCall.Operand)));

            Mono.Cecil.TypeDefinition directType = assembly.MainModule.GetType("DirectBranchConstructorTarget");
            Mono.Cecil.MethodDefinition directConstructor = directType.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            Instruction directBaseCall = directConstructor.Body.Instructions.First(instruction =>
                instruction.Operand is Mono.Cecil.MethodReference called &&
                called.Name == ".ctor" && called.DeclaringType.FullName == "System.Object");
            Instruction directLoadThis = directBaseCall.Previous;
            directConstructor.Body.Instructions.Remove(directLoadThis);
            directConstructor.Body.Instructions.Remove(directBaseCall);
            Instruction valueStore = directConstructor.Body.Instructions.First(instruction =>
                instruction.OpCode.Code == Code.Stfld &&
                instruction.Operand is Mono.Cecil.FieldReference field && field.Name == "Value");
            Instruction truePathStart = valueStore.Previous.Previous;
            ILProcessor directIl = directConstructor.Body.GetILProcessor();
            directIl.InsertBefore(truePathStart, directIl.Create(OpCodes.Ldarg_0));
            directIl.InsertBefore(truePathStart, directIl.Create(OpCodes.Call,
                assembly.MainModule.ImportReference((Mono.Cecil.MethodReference)directBaseCall.Operand)));

            using (MemoryStream output = new MemoryStream())
            {
                assembly.Write(output);
                return output.ToArray();
            }
        }
    }

    private static void VerifyEverestStyleConstructor(Assembly targetAssembly)
    {
        Type targetType = targetAssembly.GetType("EverestStyleConstructorTarget", true);
        ConstructorInfo constructor = targetType.GetConstructor(new[] { typeof(bool), typeof(int) });
        Type hooksType = targetAssembly.GetType("EverestStyleConstructorHooks", true);
        Type hookType = targetAssembly.GetType("HookEverestStyleConstructor", true);
        Delegate hook = Delegate.CreateDelegate(hookType, hooksType.GetMethod("OnConstructor"), true);
        IOSHookRuntime.AddOn(constructor, hook);

        object instance = Activator.CreateInstance(targetType, new object[] { true, 41 });
        AssertEqual("Everest wrapper constructor body and finally", "41/5/1/1",
            $"{targetType.GetField("Value").GetValue(instance)}/{targetType.GetField("PostValue").GetValue(instance)}/{ReadStatic<int>(hooksType, "Calls")}/{ReadStatic<int>(targetType, "CleanupCalls")}");

        bool originalBranchPreserved = false;
        try
        {
            Activator.CreateInstance(targetType, new object[] { false, 41 });
        }
        catch (Exception exception)
        {
            while (exception is TargetInvocationException invocation && invocation.InnerException != null)
                exception = invocation.InnerException;
            if (!(exception is InvalidOperationException)) throw;
            originalBranchPreserved = true;
        }
        IOSHookRuntime.RemoveOn(constructor, hook);
        if (!originalBranchPreserved)
            throw new InvalidOperationException("The hooked Everest-style constructor did not preserve its throwing branch.");
        AssertEqual("Everest wrapper constructor finally on throw", "2",
            ReadStatic<int>(targetType, "CleanupCalls").ToString());

        Type directType = targetAssembly.GetType("DirectBranchConstructorTarget", true);
        ConstructorInfo directConstructor = directType.GetConstructor(new[] { typeof(bool), typeof(int) });
        Type directHooksType = targetAssembly.GetType("DirectBranchConstructorHooks", true);
        Type directHookType = targetAssembly.GetType("HookDirectBranchConstructor", true);
        Delegate directHook = Delegate.CreateDelegate(directHookType,
            directHooksType.GetMethod("OnConstructor"), true);
        IOSHookRuntime.AddOn(directConstructor, directHook);
        object directInstance = Activator.CreateInstance(directType, new object[] { true, 53 });
        AssertEqual("whole-body direct initializer constructor", "53/5/1",
            $"{directType.GetField("Value").GetValue(directInstance)}/{directType.GetField("PostValue").GetValue(directInstance)}/{ReadStatic<int>(directHooksType, "Calls")}");
        IOSHookRuntime.RemoveOn(directConstructor, directHook);
    }

    private static void VerifyMonoModCecilBackend()
    {
        string monoModUtilsPath = typeof(MonoMod.Utils.DynamicMethodDefinition).Assembly.Location;
        string runtimeDirectory = Path.GetDirectoryName(monoModUtilsPath);
        string probeDirectory = Path.Combine(Path.GetTempPath(), "CelesteIOSWovenMonoMod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        try
        {
            foreach (string path in Directory.GetFiles(runtimeDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith("Mono.Cecil", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith("MonoMod", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] original = File.ReadAllBytes(path);
                byte[] woven = IOSHookWeaver.WeaveAssembly(original, out string assemblyName, out int wovenCount,
                    out string[] skipped, runtimeDirectory);
                if (wovenCount == 0 || skipped.Length != 0)
                    throw new InvalidOperationException(assemblyName + " infrastructure did not receive complete hook gates: " +
                        string.Join(" | ", skipped));
                File.WriteAllBytes(Path.Combine(probeDirectory, Path.GetFileName(path)), woven);
            }
            if (!File.Exists(Path.Combine(probeDirectory, "MonoMod.Utils.dll")) ||
                !File.Exists(Path.Combine(probeDirectory, "Mono.Cecil.dll")))
                throw new FileNotFoundException("The complete woven Cecil hook backend was not staged beside Everest.", runtimeDirectory);

            string projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
            string probeAssembly = Path.Combine(projectDirectory, "WovenInfrastructureProbe", "bin", configuration, "net8.0", "WovenInfrastructureProbe.dll");
            ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(probeAssembly);
            startInfo.ArgumentList.Add(probeDirectory);
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            using Process process = Process.Start(startInfo);
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000))
            {
                process.Kill(true);
                throw new TimeoutException("The woven MonoMod backend probe exceeded two minutes.");
            }
            Task.WaitAll(standardOutput, standardError);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The woven MonoMod backend probe failed: " + standardError.Result + standardOutput.Result);
            Console.WriteLine(standardOutput.Result.Trim());
        }
        finally
        {
            try { if (Directory.Exists(probeDirectory)) Directory.Delete(probeDirectory, true); }
            catch { }
        }
    }

    private static void VerifyHookableModuleInitializer(byte[] wovenTarget, string hookPath)
    {
        AssemblyLoadContext targetContext = new FixtureLoadContext();
        Assembly targetAssembly = targetContext.LoadFromStream(new MemoryStream(wovenTarget));
        int moduleInitializerToken;
        using (Mono.Cecil.AssemblyDefinition definition = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(wovenTarget)))
        {
            Mono.Cecil.MethodDefinition initializer = definition.MainModule.GetType("<Module>").Methods
                .FirstOrDefault(method => method.Name == ".cctor");
            moduleInitializerToken = initializer?.MetadataToken.ToInt32() ?? 0;
        }
        MethodBase moduleInitializer = moduleInitializerToken == 0
            ? null
            : targetAssembly.ManifestModule.ResolveMethod(moduleInitializerToken);
        if (moduleInitializer == null)
            throw new MissingMethodException("The woven target module did not expose its module initializer.");

        AssemblyLoadContext hookContext = new HookLoadContext(targetAssembly);
        Assembly hookAssembly = hookContext.LoadFromAssemblyPath(hookPath);
        Delegate hook = Delegate.CreateDelegate(hookAssembly.GetType("HookModuleInitializer", true),
            hookAssembly.GetType("CrossHooks", true).GetMethod("OnModuleInitializer"), true);
        IOSHookRuntime.AddOn(moduleInitializer, hook);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(targetAssembly.ManifestModule.ModuleHandle);

        int calls = (int)hookAssembly.GetType("CrossHooks", true).GetField("ModuleInitializerCalls").GetValue(null);
        int value = ReadStatic<int>(targetAssembly.GetType("ModuleInitializerProbe", true), "Value");
        AssertEqual("module initializer On hook count", "1", calls.ToString());
        AssertEqual("module initializer original body", "73", value.ToString());
        Console.WriteLine("Module initializer hook smoke passed: bootstrap direct call and post-initialization hook both ran.");
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

    private static void VerifyEverestDependencyDownloaderHookGates(string projectDirectory, string configuration, string gameAssemblyPath)
    {
        string repoDirectory = Path.GetFullPath(Path.Combine(projectDirectory, "..", ".."));
        string runtimeDirectory = Path.Combine(repoDirectory, "Everest", "Celeste.Mod.mm", "bin", "Release", "net8.0");
        string runtimePath = Path.Combine(runtimeDirectory, "Celeste.Mod.mm.dll");
        if (!File.Exists(runtimePath))
            throw new FileNotFoundException("The patched Everest runtime was not built for dependency hook verification.", runtimePath);

        string[] resolverDirectories = new[]
        {
            runtimeDirectory,
            Path.Combine(repoDirectory, "Everest", "lib-stripped"),
            Path.GetDirectoryName(gameAssemblyPath),
            Path.Combine(repoDirectory, "Everest", "Celeste.Mod.mm", "bin", configuration, "net8.0")
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        string assemblyName;
        int wovenCount;
        string[] skipped;
        byte[] woven = IOSHookWeaver.WeaveAssembly(File.ReadAllBytes(runtimePath), out assemblyName, out wovenCount,
            out skipped, runtimeDirectory, resolverDirectories);
        if (skipped.Length != 0)
            throw new InvalidOperationException("The patched Everest runtime had unhookable methods: " + string.Join(" | ", skipped));

        string[] targets =
        {
            "Celeste.Mod.UI.OuiModOptions::CreateMenu",
            "Celeste.Mod.UI.OuiDependencyDownloader::Enter",
            "Celeste.Mod.UI.OuiDependencyDownloader::downloadAllDependencies",
            "Celeste.Mod.UI.OuiDependencyDownloader::downloadDependency",
            "Celeste.Mod.UI.OuiDependencyDownloader::Update",
            "Celeste.Mod.UI.OuiDependencyDownloader::Exit",
            "Celeste.Mod.Helpers.ModUpdaterHelper::DownloadModUpdateList",
            "Celeste.Mod.Helpers.ModUpdaterHelper::DownloadModDependencyGraph",
            "Celeste.Mod.Helpers.ModUpdaterHelper::GetAllMirrorUrls",
            "Celeste.Mod.Helpers.ModUpdaterHelper::VerifyChecksum",
            "Celeste.Mod.Helpers.ModUpdaterHelper::InstallModUpdate",
            "Celeste.Mod.Everest::SlowFullRestart"
        };

        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(woven),
            new ReaderParameters { InMemory = true }))
        {
            foreach (string target in targets)
            {
                string[] parts = target.Split(new[] { "::" }, StringSplitOptions.None);
                TypeDefinition type = assembly.MainModule.GetType(parts[0]);
                MethodDefinition[] methods = type == null
                    ? Array.Empty<MethodDefinition>()
                    : type.Methods.Where(method => method.Name == parts[1]).ToArray();
                if (methods.Length == 0)
                    throw new MissingMethodException(parts[0], parts[1]);

                foreach (MethodDefinition method in methods)
                {
                    string original = IOSHookIdentity.OriginalMethodName(method);
                    string gate = IOSHookIdentity.GateFieldName(method);
                    if (!type.Methods.Any(candidate => candidate.Name == original) ||
                        !type.Fields.Any(candidate => candidate.Name == gate))
                        throw new InvalidOperationException("Everest dependency path method did not receive an iOS hook gate: " + method.FullName);
                }
            }

            List<TypeDefinition> allTypes = new List<TypeDefinition>();
            Stack<TypeDefinition> pendingTypes = new Stack<TypeDefinition>(assembly.MainModule.Types);
            while (pendingTypes.Count > 0)
            {
                TypeDefinition type = pendingTypes.Pop();
                allTypes.Add(type);
                foreach (TypeDefinition nested in type.NestedTypes)
                    pendingTypes.Push(nested);
            }
            MethodDefinition[] allMethods = allTypes.SelectMany(type => type.Methods).ToArray();

            if (!allMethods.Any(method => method.HasBody && method.Body.Instructions.Any(instruction =>
                string.Equals(instruction.Operand as string, "EVEREST_TMPDIR", StringComparison.Ordinal))))
                throw new InvalidOperationException("Everest does not honor the app-provided writable iOS temporary directory.");
            if (!allMethods.Any(method => method.HasBody && method.Body.Instructions.Any(instruction =>
                string.Equals(instruction.Operand as string, "Celeste.IOSHookRuntime.EverestDataPath", StringComparison.Ordinal))))
                throw new InvalidOperationException("Everest does not honor the app-provided writable iOS mod directory.");

            TypeDefinition downloader = assembly.MainModule.GetType("Celeste.Mod.UI.OuiDependencyDownloader");
            MethodDefinition download = downloader?.Methods.FirstOrDefault(method => method.Name == "downloadDependency");
            MethodDefinition downloadBody = download == null
                ? null
                : downloader.Methods.FirstOrDefault(method => method.Name == IOSHookIdentity.OriginalMethodName(download));
            bool downloadsToEverestTemp = downloadBody?.Body?.Instructions.Any(instruction =>
                instruction.Operand is string path && path == "dependency-download.zip") == true &&
                downloadBody.Body.Instructions.Any(instruction => instruction.Operand is MethodReference reference &&
                    reference.Name == "get_PathTmp" && reference.DeclaringType.FullName == "Celeste.Mod.Everest");
            if (!downloadsToEverestTemp)
                throw new InvalidOperationException("Everest's in-game dependency downloader no longer writes through its configured temporary directory.");

            MethodDefinition restartCallbackMethod = allMethods.FirstOrDefault(method => method.HasBody &&
                method.Body.Instructions.Any(instruction =>
                    string.Equals(instruction.Operand as string, "Celeste.IOSHookRuntime.RequestCelesteRestart", StringComparison.Ordinal)) &&
                method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference reference &&
                    reference.Name == "add_OnShutdown"));
            bool desktopRestartFallback = allMethods.Any(method => method.Name.StartsWith("__ios_original_", StringComparison.Ordinal) &&
                method.DeclaringType.FullName.Contains("Celeste.Mod.Everest/<>c", StringComparison.Ordinal) &&
                method.HasBody && method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference fallback &&
                    fallback.Name == "StartCelesteProcess" && fallback.DeclaringType.FullName == "Celeste.Mod.BOOT"));
            bool restartReturnsToIOSLauncher = restartCallbackMethod != null && desktopRestartFallback;
            if (!restartReturnsToIOSLauncher)
                throw new InvalidOperationException("Everest's iOS full-restart path must return to the launcher while preserving the desktop restart fallback.");
        }

        Console.WriteLine("PASS Everest in-game dependency menu, catalog, writable-temp download, checksum/install, and iOS restart path are woven and wired (" + wovenCount + " methods woven).");
    }

    private static void VerifyEverestDualStackConnection(Assembly everestRuntime)
    {
        if (!Socket.OSSupportsIPv6)
        {
            Console.WriteLine("SKIP IPv6 socket fallback probe because this host does not support IPv6.");
            return;
        }

        Type clientType = everestRuntime.GetType("Celeste.Mod.Helpers.CompressedHttpClient", true);
        MethodInfo openConnection = clientType.GetMethod("OpenConnectionAsync", BindingFlags.NonPublic | BindingFlags.Static);
        if (openConnection == null)
            throw new MissingMethodException(clientType.FullName, "OpenConnectionAsync");

        TcpListener listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<Socket> accepted = listener.AcceptSocketAsync();
            Task<Stream> connected = (Task<Stream>)openConnection.Invoke(null, new object[]
            {
                new DnsEndPoint("localhost", port, AddressFamily.Unspecified),
                CancellationToken.None
            });

            if (!connected.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Everest did not connect to the IPv6-only local listener within five seconds.");
            using (Stream client = connected.GetAwaiter().GetResult())
            {
                if (!accepted.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The IPv6-only local listener did not accept Everest's connection.");
                using (Socket server = accepted.GetAwaiter().GetResult())
                    Console.WriteLine("PASS Everest compressed HTTP client falls back to IPv6 when its IPv4-first connection fails.");
            }
        }
        finally
        {
            listener.Stop();
        }
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

    private static void VerifyLegacyDetourTrampoline(Assembly everestRuntime, Assembly targetAssembly)
    {
        Type legacyDetourType = everestRuntime.GetType("Celeste.Mod.Helpers.LegacyMonoMod.LegacyDetour", true);
        ConstructorInfo constructor = legacyDetourType.GetConstructor(new[] { typeof(MethodBase), typeof(MethodBase) });
        if (constructor == null)
            throw new MissingMethodException(legacyDetourType.FullName, ".ctor(MethodBase, MethodBase)");

        Type targetType = targetAssembly.GetType("LegacyDetourTarget", true);
        MethodInfo targetMethod = targetType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo replacement = targetType.GetMethod("DetourAdd", BindingFlags.Public | BindingFlags.Static);
        object detour = constructor.Invoke(new object[] { targetMethod, replacement });
        try
        {
            object instance = Activator.CreateInstance(targetType);
            AssertEqual("Everest public LegacyDetour route", "25",
                targetMethod.Invoke(instance, new object[] { 2 }).ToString());

            Type originalDelegateType = targetAssembly.GetType("LegacyDetourOriginal", true);
            MethodInfo generateTrampoline = legacyDetourType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(method => method.Name == "GenerateTrampoline" && method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0);
            if (generateTrampoline == null)
                throw new MissingMethodException(legacyDetourType.FullName, "GenerateTrampoline<T>()");
            Delegate trampoline = (Delegate)generateTrampoline.MakeGenericMethod(originalDelegateType).Invoke(detour, null);
            object originalInstance = Activator.CreateInstance(targetType);
            AssertEqual("Everest GenerateTrampoline<T> original continuation", "7",
                trampoline.DynamicInvoke(new[] { originalInstance, (object)2 }).ToString());

            MethodInfo generateMethodBaseTrampoline = legacyDetourType.GetMethod("GenerateTrampoline",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(MethodBase) }, null)
                ?? throw new MissingMethodException(legacyDetourType.FullName, "GenerateTrampoline(MethodBase)");
            MethodInfo explicitTrampoline = (MethodInfo)generateMethodBaseTrampoline.Invoke(detour,
                new object[] { originalDelegateType.GetMethod("Invoke") });
            AssertEqual("Everest GenerateTrampoline(MethodBase) original continuation", "7",
                explicitTrampoline.CreateDelegate(originalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            MethodInfo defaultTrampoline = (MethodInfo)generateMethodBaseTrampoline.Invoke(detour, new object[] { null });
            AssertEqual("Everest GenerateTrampoline() default signature", "7",
                defaultTrampoline.CreateDelegate(originalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            FieldInfo callbackField = legacyDetourType.GetField("OnGenerateTrampoline", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingFieldException(legacyDetourType.FullName, "OnGenerateTrampoline");
            MethodBase overrideMethod = targetType.GetMethod("TrampolineOverride", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(targetType.FullName, "TrampolineOverride");
            legacyTrampolineOverride = overrideMethod;
            try
            {
                callbackField.SetValue(null, CreateTrampolineOverrideCallback(callbackField.FieldType));
                Delegate callbackTrampoline = (Delegate)generateTrampoline.MakeGenericMethod(originalDelegateType)
                    .Invoke(detour, null);
                AssertEqual("Everest GenerateTrampoline<T> preserves OnGenerateTrampoline callback", "907",
                    callbackTrampoline.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            }
            finally
            {
                callbackField.SetValue(null, null);
                legacyTrampolineOverride = null;
            }

        }
        finally
        {
            legacyDetourType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(detour, null);
        }

        object afterRemoval = Activator.CreateInstance(targetType);
        AssertEqual("Everest public LegacyDetour removal", "7",
            targetMethod.Invoke(afterRemoval, new object[] { 2 }).ToString());

        Type chainOriginalDelegateType = targetAssembly.GetType("LegacyDetourOriginal", true);
        MethodInfo chainGenerateTrampoline = legacyDetourType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(method => method.Name == "GenerateTrampoline" && method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0);
        object firstDetour = constructor.Invoke(new object[] { targetMethod,
            targetType.GetMethod("DetourAddTwenty", BindingFlags.Public | BindingFlags.Static) });
        object secondDetour = null;
        try
        {
            Delegate nextDelegate = (Delegate)chainGenerateTrampoline.MakeGenericMethod(chainOriginalDelegateType)
                .Invoke(firstDetour, null);
            MethodInfo generateMethodBaseTrampoline = legacyDetourType.GetMethod("GenerateTrampoline",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(MethodBase) }, null)
                ?? throw new MissingMethodException(legacyDetourType.FullName, "GenerateTrampoline(MethodBase)");
            MethodInfo nextMethod = (MethodInfo)generateMethodBaseTrampoline.Invoke(firstDetour,
                new object[] { chainOriginalDelegateType.GetMethod("Invoke") });

            AssertEqual("Generic detour continuation before downstream hook is added", "7",
                nextDelegate.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            AssertEqual("MethodBase detour continuation before downstream hook is added", "7",
                nextMethod.CreateDelegate(chainOriginalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            secondDetour = constructor.Invoke(new object[] { targetMethod,
                targetType.GetMethod("DetourAddHundred", BindingFlags.Public | BindingFlags.Static) });
            AssertEqual("Legacy detour route after downstream hook is added", "27",
                targetMethod.Invoke(Activator.CreateInstance(targetType), new object[] { 2 }).ToString());
            AssertEqual("Generic detour continuation includes downstream hook", "107",
                nextDelegate.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            AssertEqual("MethodBase detour continuation includes downstream hook", "107",
                nextMethod.CreateDelegate(chainOriginalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            legacyDetourType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(secondDetour, null);
            secondDetour = null;
            AssertEqual("Generic detour continuation updates after removal", "7",
                nextDelegate.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            AssertEqual("MethodBase detour continuation updates after removal", "7",
                nextMethod.CreateDelegate(chainOriginalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
        }
        finally
        {
            legacyDetourType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(firstDetour, null);
            if (secondDetour != null)
                legacyDetourType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(secondDetour, null);
        }
    }

    private static void VerifyLegacyHookManagedRoute(Assembly everestRuntime, Assembly targetAssembly)
    {
        Type legacyHookType = everestRuntime.GetType("Celeste.Mod.Helpers.LegacyMonoMod.LegacyHook", true);
        ConstructorInfo constructor = legacyHookType.GetConstructor(new[] { typeof(MethodBase), typeof(MethodInfo) });
        if (constructor == null)
            throw new MissingMethodException(legacyHookType.FullName, ".ctor(MethodBase, MethodInfo)");

        Type targetType = targetAssembly.GetType("LegacyDetourTarget", true);
        MethodInfo targetMethod = targetType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo hookMethod = targetAssembly.GetType("LegacyHookTargetHooks", true)
            .GetMethod("OnAdd", BindingFlags.Public | BindingFlags.Static);
        object hook = constructor.Invoke(new object[] { targetMethod, hookMethod });
        try
        {
            object detour = legacyHookType.GetProperty("Detour", BindingFlags.Public | BindingFlags.Instance)
                .GetValue(hook);
            if (detour == null)
                throw new InvalidOperationException("The iOS LegacyHook route did not expose its public LegacyDetour facade.");
            if (legacyHookType.GetField("TargetReal", BindingFlags.Public | BindingFlags.Instance).GetValue(hook) == null)
                throw new InvalidOperationException("The iOS LegacyHook route left TargetReal unset.");
            if (!(bool)detour.GetType().GetProperty("IsApplied").GetValue(detour))
                throw new InvalidOperationException("The LegacyHook's public Detour facade did not report its applied state.");

            object instance = Activator.CreateInstance(targetType);
            AssertEqual("Everest LegacyHook orig delegate route", "107",
                targetMethod.Invoke(instance, new object[] { 2 }).ToString());

            Type originalDelegateType = targetAssembly.GetType("LegacyDetourOriginal", true);
            MethodInfo generateTrampoline = detour.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(method => method.Name == "GenerateTrampoline" && method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0);
            Delegate trampoline = (Delegate)generateTrampoline.MakeGenericMethod(originalDelegateType).Invoke(detour, null);
            object originalInstance = Activator.CreateInstance(targetType);
            AssertEqual("Everest LegacyHook public Detour trampoline", "7",
                trampoline.DynamicInvoke(new[] { originalInstance, (object)2 }).ToString());

            MethodInfo generateMethodBaseTrampoline = legacyHookType.GetMethod("GenerateTrampoline",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(MethodBase) }, null)
                ?? throw new MissingMethodException(legacyHookType.FullName, "GenerateTrampoline(MethodBase)");
            MethodInfo explicitTrampoline = (MethodInfo)generateMethodBaseTrampoline.Invoke(hook,
                new object[] { originalDelegateType.GetMethod("Invoke") });
            AssertEqual("Everest LegacyHook GenerateTrampoline(MethodBase)", "7",
                explicitTrampoline.CreateDelegate(originalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            MethodInfo defaultTrampoline = (MethodInfo)generateMethodBaseTrampoline.Invoke(hook, new object[] { null });
            AssertEqual("Everest LegacyHook default MethodBase trampoline", "7",
                defaultTrampoline.CreateDelegate(originalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            FieldInfo callbackField = legacyHookType.GetField("OnGenerateTrampoline", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingFieldException(legacyHookType.FullName, "OnGenerateTrampoline");
            MethodBase overrideMethod = targetType.GetMethod("TrampolineOverride", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(targetType.FullName, "TrampolineOverride");
            legacyTrampolineOverride = overrideMethod;
            try
            {
                callbackField.SetValue(null, CreateTrampolineOverrideCallback(callbackField.FieldType));
                Delegate callbackTrampoline = (Delegate)legacyHookType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Single(method => method.Name == "GenerateTrampoline" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                    .MakeGenericMethod(originalDelegateType).Invoke(hook, null);
                AssertEqual("Everest LegacyHook GenerateTrampoline<T> preserves OnGenerateTrampoline callback", "907",
                    callbackTrampoline.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            }
            finally
            {
                callbackField.SetValue(null, null);
                legacyTrampolineOverride = null;
            }

            detour.GetType().GetMethod("Undo", BindingFlags.Public | BindingFlags.Instance).Invoke(detour, null);
            if ((bool)legacyHookType.GetProperty("IsApplied").GetValue(hook))
                throw new InvalidOperationException("Undo through the LegacyHook.Detour facade left the managed hook applied.");
            object afterUndo = Activator.CreateInstance(targetType);
            AssertEqual("Everest LegacyHook Detour undo", "7",
                targetMethod.Invoke(afterUndo, new object[] { 2 }).ToString());

            detour.GetType().GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance).Invoke(detour, null);
            object reapplied = Activator.CreateInstance(targetType);
            AssertEqual("Everest LegacyHook Detour reapply", "107",
                targetMethod.Invoke(reapplied, new object[] { 2 }).ToString());
        }
        finally
        {
            legacyHookType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(hook, null);
        }

        object afterDispose = Activator.CreateInstance(targetType);
        AssertEqual("Everest LegacyHook disposal", "7",
            targetMethod.Invoke(afterDispose, new object[] { 2 }).ToString());

        object firstLegacyHook = constructor.Invoke(new object[] { targetMethod,
            targetAssembly.GetType("LegacyHookTargetHooks", true)
                .GetMethod("OnAddTwenty", BindingFlags.Public | BindingFlags.Static) });
        object secondLegacyHook = null;
        try
        {
            Type originalDelegateType = targetAssembly.GetType("LegacyDetourOriginal", true);
            MethodInfo genericTrampolineFactory = legacyHookType.GetMethod("GenerateTrampoline",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException(legacyHookType.FullName, "GenerateTrampoline<T>()");
            Delegate liveGenericTrampoline = (Delegate)genericTrampolineFactory
                .MakeGenericMethod(originalDelegateType).Invoke(firstLegacyHook, null);
            MethodInfo methodTrampolineFactory = legacyHookType.GetMethod("GenerateTrampoline",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(MethodBase) }, null)
                ?? throw new MissingMethodException(legacyHookType.FullName, "GenerateTrampoline(MethodBase)");
            MethodInfo liveMethodTrampoline = (MethodInfo)methodTrampolineFactory.Invoke(firstLegacyHook,
                new object[] { originalDelegateType.GetMethod("Invoke") });

            AssertEqual("LegacyHook trampoline before downstream hook", "7",
                liveGenericTrampoline.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            AssertEqual("LegacyHook MethodBase trampoline before downstream hook", "7",
                liveMethodTrampoline.CreateDelegate(originalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            secondLegacyHook = constructor.Invoke(new object[] { targetMethod,
                targetAssembly.GetType("LegacyHookTargetHooks", true)
                    .GetMethod("OnAddHundred", BindingFlags.Public | BindingFlags.Static) });
            AssertEqual("LegacyHook live route with downstream hook", "127",
                targetMethod.Invoke(Activator.CreateInstance(targetType), new object[] { 2 }).ToString());
            AssertEqual("LegacyHook trampoline includes downstream hook", "107",
                liveGenericTrampoline.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            AssertEqual("LegacyHook MethodBase trampoline includes downstream hook", "107",
                liveMethodTrampoline.CreateDelegate(originalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());

            legacyHookType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(secondLegacyHook, null);
            secondLegacyHook = null;
            AssertEqual("LegacyHook trampoline follows downstream removal", "7",
                liveGenericTrampoline.DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
            AssertEqual("LegacyHook MethodBase trampoline follows downstream removal", "7",
                liveMethodTrampoline.CreateDelegate(originalDelegateType)
                    .DynamicInvoke(new[] { Activator.CreateInstance(targetType), (object)2 }).ToString());
        }
        finally
        {
            if (secondLegacyHook != null)
                legacyHookType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(secondLegacyHook, null);
            legacyHookType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance).Invoke(firstLegacyHook, null);
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

    private static Delegate CreateTrampolineOverrideCallback(Type callbackType)
    {
        Type identityType = callbackType.GetGenericArguments()[0];
        MethodInfo callback = typeof(Program).GetMethod(nameof(ReturnTrampolineOverride), BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(identityType);
        return Delegate.CreateDelegate(callbackType, callback, throwOnBindFailure: true);
    }

    private static MethodBase ReturnTrampolineOverride<TIdentity>(TIdentity identity, MethodBase signature) =>
        legacyTrampolineOverride;

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

        public HookLoadContext(Assembly targetAssembly) : base("ios-hook-smoke-hook-" + Guid.NewGuid().ToString("N"))
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
