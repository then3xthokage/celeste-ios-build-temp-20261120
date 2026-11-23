using System;
using System.Runtime.CompilerServices;

public delegate int ProbeAddOriginal(int left, int right);
public delegate int ProbeAddHook(ProbeAddOriginal original, int left, int right);
public delegate void ProbeModuleInitializerOriginal();
public delegate void ProbeModuleInitializerHook(ProbeModuleInitializerOriginal original);
public delegate ref T ProbeGenericEchoOriginal<T>(ref T value);
public delegate int LuaCallbackSmoke(int value);
public delegate void ProbeRefLikeConstructorOriginal(ref ProbeRefLikeConstructorTarget target, int value, out bool initialized);
public delegate void ProbeRefLikeConstructorHook(ProbeRefLikeConstructorOriginal original,
    ref ProbeRefLikeConstructorTarget target, int value, out bool initialized);

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

public static class ProbeModuleInitializerHooks
{
    public static int Calls;

    public static void OnInitialize(ProbeModuleInitializerOriginal original)
    {
        Calls++;
        original();
    }
}

public static class ProbeTarget
{
    public static int Add(int left, int right) => left + right;
    public static int DetourAdd(int left, int right) => left + right + 100;
    public static int DetourAddTwenty(int left, int right) => left + right + 20;
    public static int DetourAddHundred(int left, int right) => left + right + 100;
    public static int Constant() => 5;
}

public static unsafe class ProbeManagedCalli
{
    public static int Invoke(int value)
    {
        delegate* managed<int, int> target = &AddSeventeen;
        return target(value);
    }

    private static int AddSeventeen(int value) => value + 17;
}

public ref struct ProbeRefLikeConstructorTarget
{
    private int value;

    public ProbeRefLikeConstructorTarget(int initialValue, out bool initialized)
    {
        value = initialValue;
        initialized = true;
    }

    public int Value => value;
}

public static class ProbeRefLikeConstructor
{
    public static int CreateAndRead(int value)
    {
        ProbeRefLikeConstructorTarget target = new ProbeRefLikeConstructorTarget(value, out bool initialized);
        return initialized ? target.Value : -1;
    }
}

public sealed class ProbeEverestStyleConstructorTarget
{
    public int Value;
    public int PostValue;
    public static int CleanupCalls;

    public ProbeEverestStyleConstructorTarget(bool initialize, int value)
    {
        try
        {
            if (initialize)
            {
                orig_ctor(value);
                PostValue = 4;
                return;
            }
            throw new InvalidOperationException("constructor branch failed");
        }
        finally
        {
            CleanupCalls++;
        }
    }

    public void orig_ctor(int value) => Value = value;
}

public delegate void ProbeEverestStyleConstructorOriginal(
    ProbeEverestStyleConstructorTarget target, bool initialize, int value);
public delegate void ProbeEverestStyleConstructorHook(
    ProbeEverestStyleConstructorOriginal original, ProbeEverestStyleConstructorTarget target,
    bool initialize, int value);

public static class ProbeEverestStyleConstructorHooks
{
    public static int Calls;

    public static void OnConstructor(ProbeEverestStyleConstructorOriginal original,
        ProbeEverestStyleConstructorTarget target, bool initialize, int value)
    {
        Calls++;
        original(target, initialize, value);
        target.PostValue++;
    }
}

public sealed class ProbeOuiUpdateStatsTarget
{
    public int OriginalCalls;
    public int PostCalls;
    public bool ChapterMode;
    public bool? HeartWiggle;
    public bool? SideMode;
    public bool? OverrideHeartWiggle;
    public static int DetourCalls;
    public static Action<ProbeOuiUpdateStatsTarget, bool, bool?, bool?, bool?> Original;

    public void UpdateStats(bool chapterMode, bool? heartWiggle, bool? sideMode, bool? overrideHeartWiggle)
    {
        OriginalCalls++;
        ChapterMode = chapterMode;
        HeartWiggle = heartWiggle;
        SideMode = sideMode;
        OverrideHeartWiggle = overrideHeartWiggle;
    }

    public static void DetourUpdateStats(ProbeOuiUpdateStatsTarget target, bool chapterMode,
        bool? heartWiggle, bool? sideMode, bool? overrideHeartWiggle)
    {
        DetourCalls++;
        Original(target, chapterMode, heartWiggle, sideMode, overrideHeartWiggle);
        target.PostCalls++;
    }
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
    public static int RefLikeConstructorCalls;

    public static int Add(ProbeAddOriginal original, int left, int right) => original(left, right) + 5;
    public static int AddTwenty(ProbeAddOriginal original, int left, int right) => original(left, right) + 20;
    public static int AddHundred(ProbeAddOriginal original, int left, int right) => original(left, right) + 100;

    public static void OnRefLikeConstructor(ProbeRefLikeConstructorOriginal original,
        ref ProbeRefLikeConstructorTarget target, int value, out bool initialized)
    {
        RefLikeConstructorCalls++;
        original(ref target, value, out initialized);
    }

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
