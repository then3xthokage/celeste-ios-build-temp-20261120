using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using MonoMod;
using MonoMod.Cil;
using MonoMod.Utils;
using celestemeow;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace IOSInterpreterSmoke;

public static class RuntimeProbe
{
    public static Action<string> Progress { get; set; }
    private static int moduleInitializerHookCalls;

    public static void Run()
    {
        Mark("initialize hook runtime");
        IOSHookRuntime.Initialize();
        Mark("Reflection.Emit probes");
        VerifyRuntimeCodeGeneration();
        Mark("Cecil generated method probe");
        VerifyCecilGeneratedMethod();

        Mark("read and weave hook fixture");
        using Stream fixtureResource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProbeFixture.dll");
        if (fixtureResource == null)
            throw new FileNotFoundException("The hook fixture was not embedded in the iOS smoke app.");
        using MemoryStream fixtureImage = new MemoryStream();
        fixtureResource.CopyTo(fixtureImage);
        byte[] original = ApplyEverestStyleConstructorShape(fixtureImage.ToArray());
        byte[] woven = IOSHookWeaver.WeaveAssembly(original, out string assemblyName, out int wovenCount,
            out string[] skipped, AppContext.BaseDirectory);
        if (assemblyName != "ProbeFixture" || wovenCount == 0 || skipped.Length != 0)
            throw new InvalidOperationException("The iOS load-time weaver did not gate every executable fixture method: " + string.Join(" | ", skipped));

        Assembly fixture = Assembly.Load(woven);
        IOSILHookRuntime.RegisterAssemblySnapshot(fixture, woven);

        Mark("register module initializer hook");
        int moduleInitializerToken;
        using (Mono.Cecil.AssemblyDefinition definition = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(woven)))
        {
            Mono.Cecil.MethodDefinition initializer = definition.MainModule.GetType("<Module>").Methods
                .FirstOrDefault(method => method.Name == ".cctor");
            moduleInitializerToken = initializer?.MetadataToken.ToInt32() ?? 0;
        }
        if (moduleInitializerToken == 0)
            throw new MissingMethodException("The woven fixture did not contain its module initializer.");
        MethodBase moduleInitializer = fixture.ManifestModule.ResolveMethod(moduleInitializerToken);
        Delegate moduleHook = CreateExternalModuleInitializerHook(fixture);
        IOSHookRuntime.AddOn(moduleInitializer, moduleHook);

        Mark("NLua generated callback and proxy probes");
        VerifyNLuaCodeGeneration(fixture);
        Mark("execute hooked module initializer");
        RuntimeHelpers.RunModuleConstructor(fixture.ManifestModule.ModuleHandle);
        Type moduleState = fixture.GetType("ProbeModuleInitializer", throwOnError: true);
        if ((int)moduleState.GetField("Value").GetValue(null) != 73)
            throw new InvalidOperationException("The downloaded assembly's module initializer did not run before hook dispatch.");
        AssertEqual("Global module initializer On hook on iOS interpreter", 1,
            moduleInitializerHookCalls);

        Mark("Everest-style constructor chain");
        VerifyEverestStyleConstructor(fixture);

        Mark("byref-like constructor hook");
        Type refLikeConstructorType = fixture.GetType("ProbeRefLikeConstructorTarget", throwOnError: true);
        ConstructorInfo refLikeConstructor = refLikeConstructorType.GetConstructor(
            new[] { typeof(int), typeof(bool).MakeByRefType() });
        Type refLikeHookType = fixture.GetType("ProbeRefLikeConstructorHook", throwOnError: true);
        Type probeHooksType = fixture.GetType("ProbeHooks", throwOnError: true);
        Delegate refLikeHook = Delegate.CreateDelegate(refLikeHookType,
            probeHooksType.GetMethod("OnRefLikeConstructor", BindingFlags.Public | BindingFlags.Static));
        IOSHookRuntime.AddOn(refLikeConstructor, refLikeHook);
        Type refLikeProbeType = fixture.GetType("ProbeRefLikeConstructor", throwOnError: true);
        AssertEqual("Byref-like constructor On hook on iOS interpreter", 29,
            refLikeProbeType.GetMethod("CreateAndRead").Invoke(null, new object[] { 29 }));
        AssertEqual("Byref-like constructor callback on iOS interpreter", 1,
            probeHooksType.GetField("RefLikeConstructorCalls").GetValue(null));
        IOSHookRuntime.RemoveOn(refLikeConstructor, refLikeHook);

        Mark("ordinary On hook and removal");
        Type targetType = fixture.GetType("ProbeTarget", throwOnError: true);
        MethodInfo add = targetType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static);
        Type hooksType = fixture.GetType("ProbeHooks", throwOnError: true);
        Type hookType = fixture.GetType("ProbeAddHook", throwOnError: true);
        Delegate hook = Delegate.CreateDelegate(hookType, hooksType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static));
        IOSHookRuntime.AddOn(add, hook);
        AssertEqual("On hook on loaded mod image", 15, add.Invoke(null, new object[] { 6, 4 }));
        IOSHookRuntime.RemoveOn(add, hook);
        AssertEqual("On hook removal", 10, add.Invoke(null, new object[] { 6, 4 }));

        Mark("ILContext hook and removal");
        MethodInfo constant = targetType.GetMethod("Constant", BindingFlags.Public | BindingFlags.Static);
        ILContext.Manipulator editConstant = context =>
        {
            Instruction literal = null;
            foreach (Instruction candidate in context.Body.Instructions)
            {
                if (candidate.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_5)
                {
                    literal = candidate;
                    break;
                }
            }
            if (literal == null)
                throw new InvalidOperationException("The IL smoke target's constant instruction was not found.");
            literal.OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4;
            literal.Operand = 11;
        };
        IOSILHookRuntime.Add(constant, editConstant, null);
        AssertEqual("ILContext sidecar on iOS interpreter", 11, constant.Invoke(null, null));
        IOSILHookRuntime.Remove(constant, editConstant);
        AssertEqual("IL hook removal", 5, constant.Invoke(null, null));

        Mark("open generic type IL hooks");
        Type genericTypeDefinition = fixture.GetType("ProbeGenericTypeTarget`1", throwOnError: true);
        Type genericType = genericTypeDefinition.MakeGenericType(typeof(int));
        MethodInfo genericTypeMethodDefinition = genericTypeDefinition.GetMethod("GenericTypeConstant", BindingFlags.Public | BindingFlags.Static);
        MethodInfo genericTypeMethod = genericType.GetMethod("GenericTypeConstant", BindingFlags.Public | BindingFlags.Static);
        ILContext.Manipulator editGenericType = context => ReplaceString(
            context, "original-open-type", "patched-open-type");
        IOSILHookRuntime.Add(genericTypeMethodDefinition, editGenericType, null);
        AssertEqual("Open generic type ILContext sidecar on iOS interpreter", "patched-open-type", genericTypeMethod.Invoke(null, null)?.ToString());
        IOSILHookRuntime.Remove(genericTypeMethodDefinition, editGenericType);
        AssertEqual("Open generic type IL hook removal on iOS interpreter", "original-open-type", genericTypeMethod.Invoke(null, null)?.ToString());

        MethodInfo genericTypeMethodOnClosedType = genericType.GetMethod("GenericMethodConstant", BindingFlags.Public | BindingFlags.Static)
            .GetGenericMethodDefinition();
        MethodInfo genericTypeMethodClosed = genericTypeMethodOnClosedType.MakeGenericMethod(typeof(string));
        ILContext.Manipulator editGenericMethodOnType = context => ReplaceString(
            context, "original-open-method-on-type", "patched-open-method-on-type");
        IOSILHookRuntime.Add(genericTypeMethodOnClosedType, editGenericMethodOnType, null);
        AssertEqual("Open generic method on a closed type IL sidecar on iOS interpreter", "patched-open-method-on-type",
            genericTypeMethodClosed.Invoke(null, null)?.ToString());
        IOSILHookRuntime.Remove(genericTypeMethodOnClosedType, editGenericMethodOnType);
        AssertEqual("Open generic method on a closed type hook removal on iOS interpreter", "original-open-method-on-type",
            genericTypeMethodClosed.Invoke(null, null)?.ToString());

        Mark("open generic method IL hooks");
        Type genericTargetType = fixture.GetType("ProbeGenericMethodTarget", throwOnError: true);
        MethodInfo genericDefinition = genericTargetType.GetMethod("GenericConstant", BindingFlags.Public | BindingFlags.Static);
        MethodInfo genericClosed = genericDefinition.MakeGenericMethod(typeof(int));
        ILContext.Manipulator editGeneric = context =>
        {
            foreach (Instruction instruction in context.Body.Instructions)
            {
                if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Ldstr ||
                    !string.Equals(instruction.Operand as string, "original-generic", StringComparison.Ordinal)) continue;
                instruction.Operand = "patched-generic";
                return;
            }
            throw new InvalidOperationException("The open generic IL smoke target's string instruction was not found.");
        };
        IOSILHookRuntime.Add(genericDefinition, editGeneric, null);
        AssertEqual("Open generic ILContext sidecar on iOS interpreter", "patched-generic", genericClosed.Invoke(null, null)?.ToString());
        IOSILHookRuntime.Remove(genericDefinition, editGeneric);
        AssertEqual("Open generic IL hook removal on iOS interpreter", "original-generic", genericClosed.Invoke(null, null)?.ToString());

        Mark("generic reflected On hooks");
        Type genericHooks = fixture.GetType("ProbeHooks", throwOnError: true);
        object openUntypedHookIdentity = new object();
        IOSHookRuntime.AddOnReflected(genericDefinition,
            genericHooks.GetMethod("OnGenericConstant", BindingFlags.Public | BindingFlags.Static),
            null, openUntypedHookIdentity, null);
        AssertEqual("Open generic method reflected On hook on iOS interpreter",
            "hooked-open-method", genericClosed.Invoke(null, null)?.ToString());
        IOSHookRuntime.RemoveOnReflected(genericDefinition, openUntypedHookIdentity);
        AssertEqual("Open generic method reflected On hook removal on iOS interpreter",
            "original-generic", genericClosed.Invoke(null, null)?.ToString());
        AssertEqual("Open generic method reflected On callback count", 1,
            genericHooks.GetField("GenericUntypedMethodHookCalls").GetValue(null));

        Type openHookedTypeDefinition = genericTypeDefinition;
        MethodInfo openHookedTypeTarget = openHookedTypeDefinition.GetMethod("GenericTypeConstant");
        Type openHookedType = openHookedTypeDefinition.MakeGenericType(typeof(int));
        MethodInfo closedHookedTypeTarget = openHookedType.GetMethod("GenericTypeConstant");
        MethodInfo openTypeHook = fixture.GetType("ProbeHooks", throwOnError: true)
            .GetMethod("OnGenericType", BindingFlags.Public | BindingFlags.Static);
        object openTypeHookIdentity = new object();
        IOSHookRuntime.AddOnReflected(openHookedTypeTarget, openTypeHook, null, openTypeHookIdentity, null);
        AssertEqual("Open generic type reflected On hook on iOS interpreter",
            "hooked-open-type", closedHookedTypeTarget.Invoke(null, null)?.ToString());
        IOSHookRuntime.RemoveOnReflected(openHookedTypeTarget, openTypeHookIdentity);
        AssertEqual("Open generic type reflected On hook removal on iOS interpreter",
            "original-open-type", closedHookedTypeTarget.Invoke(null, null)?.ToString());
        AssertEqual("Open generic type reflected On callback count", 1,
            fixture.GetType("ProbeHooks").GetField("GenericTypeHookCalls").GetValue(null));

        Type genericEchoType = fixture.GetType("ProbeGenericMethodTarget", throwOnError: true);
        MethodInfo openEchoTarget = genericEchoType.GetMethod("Echo", BindingFlags.Public | BindingFlags.Static)
            .GetGenericMethodDefinition();
        MethodInfo echoIntTarget = openEchoTarget.MakeGenericMethod(typeof(int));
        Type probeHooks = genericHooks;
        object openEchoHookIdentity = new object();
        IOSHookRuntime.AddOnReflected(openEchoTarget,
            probeHooks.GetMethod("OnGenericMethod", BindingFlags.Public | BindingFlags.Static),
            null, openEchoHookIdentity, null);
        object[] echoArguments = { 37 };
        AssertEqual("Open generic method reflected On hook on iOS interpreter", 37,
            echoIntTarget.Invoke(null, echoArguments));
        IOSHookRuntime.RemoveOnReflected(openEchoTarget, openEchoHookIdentity);
        AssertEqual("Open generic method reflected On hook removal on iOS interpreter", 41,
            echoIntTarget.Invoke(null, new object[] { 41 }));
        AssertEqual("Open generic method reflected On callback count", 1,
            probeHooks.GetField("GenericMethodHookCalls").GetValue(null));

        Mark("generic reflected detour hooks");
        object openEchoDetourIdentity = new object();
        IOSHookRuntime.AddDetour(openEchoTarget,
            probeHooks.GetMethod("DetourGenericMethod", BindingFlags.Public | BindingFlags.Static),
            openEchoDetourIdentity, null);
        AssertEqual("Open generic method reflected detour on iOS interpreter", 53,
            echoIntTarget.Invoke(null, new object[] { 53 }));
        IOSHookRuntime.RemoveDetour(openEchoTarget, openEchoDetourIdentity);
        AssertEqual("Open generic method detour removal on iOS interpreter", 59,
            echoIntTarget.Invoke(null, new object[] { 59 }));
        AssertEqual("Open generic method reflected detour callback count", 1,
            probeHooks.GetField("GenericMethodDetourCalls").GetValue(null));

        Mark("generic instance detour hooks");
        Type genericInstanceTargetDefinition = fixture.GetTypes().Single(type =>
            type.IsGenericTypeDefinition &&
            type.Name.StartsWith("ProbeGenericInstanceTarget", StringComparison.Ordinal));
        Type genericInstanceTargetType = genericInstanceTargetDefinition.MakeGenericType(typeof(string));
        object genericInstanceTarget = Activator.CreateInstance(genericInstanceTargetType);
        MethodInfo openInstanceEchoTarget = genericInstanceTargetDefinition.GetMethod("Echo")
            .GetGenericMethodDefinition();
        MethodInfo openInstanceDetour = genericInstanceTargetDefinition.GetMethod("DetourEcho")
            .GetGenericMethodDefinition();
        MethodInfo closedInstanceEchoTarget = genericInstanceTargetType.GetMethod("Echo")
            .MakeGenericMethod(typeof(int));
        object openInstanceDetourIdentity = new object();
        IOSHookRuntime.AddDetour(openInstanceEchoTarget, openInstanceDetour, openInstanceDetourIdentity, null);
        AssertEqual("Open generic instance detour on iOS interpreter", 67,
            closedInstanceEchoTarget.Invoke(genericInstanceTarget, new object[] { 67 }));
        AssertEqual("Open generic instance detour callback count", 1,
            genericInstanceTargetType.GetField("DetourCalls").GetValue(genericInstanceTarget));
        IOSHookRuntime.RemoveDetour(openInstanceEchoTarget, openInstanceDetourIdentity);
        AssertEqual("Open generic instance detour removal on iOS interpreter", 71,
            closedInstanceEchoTarget.Invoke(genericInstanceTarget, new object[] { 71 }));
        Mark("all runtime probes complete");
    }

    private static void Mark(string stage) => Progress?.Invoke(stage);

    private static Delegate CreateExternalModuleInitializerHook(Assembly fixture)
    {
        Type originalDelegate = fixture.GetType("ProbeModuleInitializerOriginal", throwOnError: true);
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("CelesteIOS.ExternalModuleInitializerHook"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("hook");
        TypeBuilder delegateBuilder = module.DefineType("ExternalModuleInitializerHook",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(MulticastDelegate));
        ConstructorBuilder constructor = delegateBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
        constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        MethodBuilder invoke = delegateBuilder.DefineMethod("Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            CallingConventions.Standard, typeof(void), new[] { originalDelegate });
        invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        Type hookDelegate = delegateBuilder.CreateType();

        TypeBuilder callbacks = module.DefineType("ExternalModuleInitializerCallbacks",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder callback = callbacks.DefineMethod("OnInitialize",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(void), new[] { originalDelegate });
        ILGenerator il = callback.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(RuntimeProbe).GetMethod(nameof(RecordModuleInitializerHook),
            BindingFlags.Public | BindingFlags.Static));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, originalDelegate.GetMethod("Invoke"));
        il.Emit(OpCodes.Ret);
        MethodInfo callbackMethod = callbacks.CreateType().GetMethod("OnInitialize", BindingFlags.Public | BindingFlags.Static);
        return Delegate.CreateDelegate(hookDelegate, callbackMethod);
    }

    public static void RecordModuleInitializerHook() => moduleInitializerHookCalls++;

    private static byte[] ApplyEverestStyleConstructorShape(byte[] source)
    {
        using (Mono.Cecil.AssemblyDefinition assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(
            new MemoryStream(source), new Mono.Cecil.ReaderParameters { InMemory = true }))
        {
            Mono.Cecil.TypeDefinition type = assembly.MainModule.GetType("ProbeEverestStyleConstructorTarget");
            Mono.Cecil.MethodDefinition constructor = type.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            Mono.Cecil.MethodDefinition original = type.Methods.Single(method => method.Name == "orig_ctor");
            Instruction baseCall = constructor.Body.Instructions.First(instruction =>
                instruction.Operand is Mono.Cecil.MethodReference called &&
                called.Name == ".ctor" && called.DeclaringType.FullName == "System.Object");
            Instruction loadThis = baseCall.Previous;
            if (loadThis == null || loadThis.OpCode.Code != Code.Ldarg_0)
                throw new InvalidOperationException("The iOS constructor fixture no longer has a recognizable Object initializer.");

            constructor.Body.Instructions.Remove(loadThis);
            constructor.Body.Instructions.Remove(baseCall);
            Instruction aliasCall = constructor.Body.Instructions.First(instruction =>
                instruction.Operand is Mono.Cecil.MethodReference called && called.Name == "orig_ctor");
            aliasCall.OpCode = Mono.Cecil.Cil.OpCodes.Callvirt;

            Instruction originalFirst = original.Body.Instructions[0];
            ILProcessor originalIl = original.Body.GetILProcessor();
            originalIl.InsertBefore(originalFirst, originalIl.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            originalIl.InsertBefore(originalFirst, originalIl.Create(Mono.Cecil.Cil.OpCodes.Call,
                assembly.MainModule.ImportReference((Mono.Cecil.MethodReference)baseCall.Operand)));

            using (MemoryStream output = new MemoryStream())
            {
                assembly.Write(output);
                return output.ToArray();
            }
        }
    }

    private static void VerifyEverestStyleConstructor(Assembly fixture)
    {
        Type targetType = fixture.GetType("ProbeEverestStyleConstructorTarget", throwOnError: true);
        ConstructorInfo constructor = targetType.GetConstructor(new[] { typeof(bool), typeof(int) });
        Type hooksType = fixture.GetType("ProbeEverestStyleConstructorHooks", throwOnError: true);
        Type hookType = fixture.GetType("ProbeEverestStyleConstructorHook", throwOnError: true);
        Delegate hook = Delegate.CreateDelegate(hookType,
            hooksType.GetMethod("OnConstructor", BindingFlags.Public | BindingFlags.Static));
        IOSHookRuntime.AddOn(constructor, hook);

        object instance = Activator.CreateInstance(targetType, new object[] { true, 41 });
        AssertEqual("Everest wrapper constructor body and finally on iOS interpreter", "41/5/1/1",
            $"{targetType.GetField("Value").GetValue(instance)}/{targetType.GetField("PostValue").GetValue(instance)}/" +
            $"{hooksType.GetField("Calls").GetValue(null)}/{targetType.GetField("CleanupCalls").GetValue(null)}");

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
            throw new InvalidOperationException("The iOS hook gate did not preserve the Everest-style constructor branch.");
        AssertEqual("Everest wrapper constructor finally on throw on iOS interpreter", 2,
            targetType.GetField("CleanupCalls").GetValue(null));
    }

    private static void VerifyCecilGeneratedMethod()
    {
        Switches.SetSwitchValue("DMDType", "cecil");
        using DynamicMethodDefinition dmd = new DynamicMethodDefinition("ios_interpreter_dmd_smoke", typeof(int), Type.EmptyTypes);
        dmd.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 42);
        dmd.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        AssertEqual("MonoMod Cecil generated method", 42, dmd.Generate().Invoke(null, null));
    }

    private static void VerifyRuntimeCodeGeneration()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new PlatformNotSupportedException("The iOS runtime does not report support for dynamic MSIL.");
        if (RuntimeFeature.IsDynamicCodeCompiled)
            throw new InvalidOperationException("The iOS smoke app must interpret generated MSIL without compiling native code.");

        GCLatencyMode originalLatencyMode = GCSettings.LatencyMode;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        finally
        {
            GCSettings.LatencyMode = originalLatencyMode;
        }

        DynamicMethod dynamicMethod = new DynamicMethod("ios_dynamic_method_smoke", typeof(int),
            new[] { typeof(int), typeof(int) }, typeof(RuntimeProbe).Module, true);
        ILGenerator dynamicIL = dynamicMethod.GetILGenerator();
        dynamicIL.Emit(OpCodes.Ldarg_0);
        dynamicIL.Emit(OpCodes.Ldarg_1);
        dynamicIL.Emit(OpCodes.Add);
        dynamicIL.Emit(OpCodes.Ret);
        Func<int, int, int> sum = (Func<int, int, int>)dynamicMethod.CreateDelegate(typeof(Func<int, int, int>));
        AssertEqual("CoreCLR interpreted DynamicMethod", 42, sum(19, 23));

        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("CelesteIOS.DynamicTypeSmoke"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("smoke");
        TypeBuilder generated = module.DefineType("GeneratedOverride", TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(RuntimeEmitBase));
        FieldBuilder bias = generated.DefineField("bias", typeof(int), FieldAttributes.Private | FieldAttributes.InitOnly);
        ConstructorBuilder constructor = generated.DefineConstructor(MethodAttributes.Public,
            CallingConventions.Standard, Type.EmptyTypes);
        ILGenerator constructorIL = constructor.GetILGenerator();
        constructorIL.Emit(OpCodes.Ldarg_0);
        constructorIL.Emit(OpCodes.Call, typeof(RuntimeEmitBase).GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null));
        constructorIL.Emit(OpCodes.Ldarg_0);
        constructorIL.Emit(OpCodes.Ldc_I4_7);
        constructorIL.Emit(OpCodes.Stfld, bias);
        constructorIL.Emit(OpCodes.Ret);

        MethodInfo baseMethod = typeof(RuntimeEmitBase).GetMethod(nameof(RuntimeEmitBase.Transform));
        MethodBuilder overrideMethod = generated.DefineMethod(nameof(RuntimeEmitBase.Transform),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(int), new[] { typeof(int) });
        ILGenerator overrideIL = overrideMethod.GetILGenerator();
        overrideIL.Emit(OpCodes.Ldarg_1);
        overrideIL.Emit(OpCodes.Ldarg_0);
        overrideIL.Emit(OpCodes.Ldfld, bias);
        overrideIL.Emit(OpCodes.Add);
        overrideIL.Emit(OpCodes.Ret);
        generated.DefineMethodOverride(overrideMethod, baseMethod);

        Type generatedType = generated.CreateType();
        RuntimeEmitBase instance = (RuntimeEmitBase)Activator.CreateInstance(generatedType);
        AssertEqual("CoreCLR interpreted dynamic assembly override", 49, instance.Transform(42));
    }

    private static void VerifyNLuaCodeGeneration(Assembly fixture)
    {
        Assembly nlua = typeof(NLua.Lua).Assembly;
        Type codeGeneration = nlua.GetType("NLua.CodeGeneration", throwOnError: true);
        PropertyInfo instance = codeGeneration.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (instance == null || instance.GetValue(null) == null)
            throw new InvalidOperationException("NLua's shared code-generation service did not initialize.");

        object generator = instance.GetValue(null);
        Type fakeFunctionType = nlua.GetType("NLua.LuaFunction", throwOnError: true);
        object fakeFunction = RuntimeHelpers.GetUninitializedObject(fakeFunctionType);
        Type callbackType = fixture.GetType("LuaCallbackSmoke", throwOnError: true);
        MethodInfo getDelegate = codeGeneration.GetMethod("GetDelegate", BindingFlags.Public | BindingFlags.Instance);
        Delegate callback = (Delegate)getDelegate.Invoke(generator, new[] { callbackType, fakeFunction });
        if (!callbackType.IsInstanceOfType(callback))
            throw new InvalidOperationException("NLua did not produce a delegate for the requested callback signature.");

        object fakeTable = RuntimeHelpers.GetUninitializedObject(nlua.GetType("NLua.LuaTable", throwOnError: true));
        Type luaClassInterface = fixture.GetType("ILuaClassSmoke", throwOnError: true);
        MethodInfo getClassInstance = codeGeneration.GetMethod("GetClassInstance", BindingFlags.Public | BindingFlags.Instance);
        object luaClass = getClassInstance.Invoke(generator, new[] { luaClassInterface, fakeTable });
        if (!luaClassInterface.IsInstanceOfType(luaClass))
            throw new InvalidOperationException("NLua did not create an implementation of the requested Lua interface.");
    }

    private static void AssertEqual(string label, int expected, object actual)
    {
        if (!(actual is int value) || value != expected)
            throw new InvalidOperationException(label + " expected " + expected + " but got " + (actual ?? "null") + ".");
    }

    private static void AssertEqual(string label, string expected, string actual)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(label + " expected " + expected + " but got " + (actual ?? "null") + ".");
    }

    private static void ReplaceString(ILContext context, string original, string replacement)
    {
        foreach (Instruction instruction in context.Body.Instructions)
        {
            if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Ldstr ||
                !string.Equals(instruction.Operand as string, original, StringComparison.Ordinal)) continue;
            instruction.Operand = replacement;
            return;
        }
        throw new InvalidOperationException("The open generic IL smoke target string instruction was not found.");
    }
}

public abstract class RuntimeEmitBase
{
    protected RuntimeEmitBase() { }
    public abstract int Transform(int value);
}
