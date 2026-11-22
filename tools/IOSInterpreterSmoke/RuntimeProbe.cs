using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using MonoMod;
using MonoMod.Cil;
using MonoMod.Utils;
using celestemeow;

namespace IOSInterpreterSmoke;

internal static class RuntimeProbe
{
    public static void Run()
    {
        IOSHookRuntime.Initialize();
        VerifyCecilGeneratedMethod();

        using Stream fixtureResource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProbeFixture.dll");
        if (fixtureResource == null)
            throw new FileNotFoundException("The hook fixture was not embedded in the iOS smoke app.");
        using MemoryStream fixtureImage = new MemoryStream();
        fixtureResource.CopyTo(fixtureImage);
        byte[] original = fixtureImage.ToArray();
        byte[] woven = IOSHookWeaver.WeaveAssembly(original, out string assemblyName, out int wovenCount,
            out string[] skipped, AppContext.BaseDirectory);
        if (assemblyName != "ProbeFixture" || wovenCount == 0 || skipped.Length != 2)
            throw new InvalidOperationException("The iOS load-time weaver did not preserve only the module initializer entry points.");

        Assembly fixture = Assembly.Load(woven);
        IOSILHookRuntime.RegisterAssemblySnapshot(fixture, woven);
        VerifyNLuaCodeGeneration(fixture);
        RuntimeHelpers.RunModuleConstructor(fixture.ManifestModule.ModuleHandle);
        Type moduleState = fixture.GetType("ProbeModuleInitializer", throwOnError: true);
        if ((int)moduleState.GetField("Value").GetValue(null) != 73)
            throw new InvalidOperationException("The downloaded assembly's module initializer did not run before hook dispatch.");

        Type targetType = fixture.GetType("ProbeTarget", throwOnError: true);
        MethodInfo add = targetType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static);
        Type hooksType = fixture.GetType("ProbeHooks", throwOnError: true);
        Type hookType = fixture.GetType("ProbeAddHook", throwOnError: true);
        Delegate hook = Delegate.CreateDelegate(hookType, hooksType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static));
        IOSHookRuntime.AddOn(add, hook);
        AssertEqual("On hook on loaded mod image", 15, add.Invoke(null, new object[] { 6, 4 }));
        IOSHookRuntime.RemoveOn(add, hook);
        AssertEqual("On hook removal", 10, add.Invoke(null, new object[] { 6, 4 }));

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
    }

    private static void VerifyCecilGeneratedMethod()
    {
        Switches.SetSwitchValue("DMDType", "cecil");
        using DynamicMethodDefinition dmd = new DynamicMethodDefinition("ios_interpreter_dmd_smoke", typeof(int), Type.EmptyTypes);
        dmd.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 42);
        dmd.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
        AssertEqual("MonoMod Cecil generated method", 42, dmd.Generate().Invoke(null, null));
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
