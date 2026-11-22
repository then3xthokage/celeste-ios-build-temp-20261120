using System.Runtime.CompilerServices;

public delegate int ProbeAddOriginal(int left, int right);
public delegate int ProbeAddHook(ProbeAddOriginal original, int left, int right);
public delegate ref T ProbeGenericEchoOriginal<T>(ref T value);
public delegate int LuaCallbackSmoke(int value);

public interface ILuaClassSmoke
{
    int GetValue();
}

#pragma warning disable CA2255
public static class ProbeModuleInitializer
{
    public static int Value;

    [ModuleInitializer]
    public static void Initialize() => Value = 73;
}
#pragma warning restore CA2255

public static class ProbeTarget
{
    public static int Add(int left, int right) => left + right;
    public static int Constant() => 5;
}

public static class ProbeGenericTypeTarget<T>
{
    public static string GenericTypeConstant() => "original-open-type";
    public static string GenericMethodConstant<U>() => "original-open-method-on-type";
}

public static class ProbeGenericMethodTarget
{
    public static string GenericConstant<T>() => "original-generic";
    public static ref T Echo<T>(ref T value) => ref value;
}

public sealed class ProbeGenericInstanceTarget<T>
{
    public int DetourCalls;

    public ref U Echo<U>(ref U value) => ref value;

    public ref U DetourEcho<U>(ref U value)
    {
        DetourCalls++;
        return ref value;
    }
}

public static class ProbeHooks
{
    public static int GenericTypeHookCalls;
    public static int GenericUntypedMethodHookCalls;
    public static int GenericMethodHookCalls;
    public static int GenericMethodDetourCalls;

    public static int Add(ProbeAddOriginal original, int left, int right) => original(left, right) + 5;

    public static string OnGenericType<T>()
    {
        GenericTypeHookCalls++;
        return "hooked-open-type";
    }

    public static string OnGenericConstant<T>()
    {
        GenericUntypedMethodHookCalls++;
        return "hooked-open-method";
    }

    public static ref T OnGenericMethod<T>(ProbeGenericEchoOriginal<T> original, ref T value)
    {
        GenericMethodHookCalls++;
        return ref original(ref value);
    }

    public static ref T DetourGenericMethod<T>(ref T value)
    {
        GenericMethodDetourCalls++;
        return ref value;
    }
}
