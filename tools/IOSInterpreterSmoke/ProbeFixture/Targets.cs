using System.Runtime.CompilerServices;

public delegate int ProbeAddOriginal(int left, int right);
public delegate int ProbeAddHook(ProbeAddOriginal original, int left, int right);
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
}

public static class ProbeHooks
{
    public static int Add(ProbeAddOriginal original, int left, int right) => original(left, right) + 5;
}
