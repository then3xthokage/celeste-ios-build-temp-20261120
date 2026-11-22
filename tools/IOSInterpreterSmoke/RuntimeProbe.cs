using System;
using System.IO;
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
}
