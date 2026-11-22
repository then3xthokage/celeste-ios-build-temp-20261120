using System;
using Celeste.Mod;
using ModDependencyFixture;

public delegate ref int CrossHookGetRef(OrigGetRef orig, TestTarget target);
public delegate Span<int> CrossHookGetSpan(OrigGetSpan orig, TestTarget target);
public delegate int CrossHookSumSpan(OrigSumSpan orig, TestTarget target, Span<int> input);
public unsafe delegate byte* CrossHookGetPointer(OrigGetPointer orig, TestTarget target, byte* input);
public unsafe delegate int CrossHookFunctionPointer(OrigFunctionPointer orig, TestTarget target, delegate*<int, int> function, int input);
public delegate int CrossHookAddByRef(OrigAddByRef orig, TestTarget target, ref int input);
public delegate int CrossHookAddIn(OrigAddIn orig, TestTarget target, in int input);
public delegate ref readonly int CrossHookReadOnlyRef(OrigReadOnlyRef orig, TestTarget target);
public delegate ref T GenericTypeOrig<T>(GenericRefTarget<T> target);
public delegate ref T GenericTypeHook<T>(GenericTypeOrig<T> orig, GenericRefTarget<T> target);
public delegate ref T GenericMethodOrig<T>(ref T input);
public delegate ref T GenericMethodHook<T>(GenericMethodOrig<T> orig, ref T input);
public delegate ref TValue GenericCombinedOrig<TTarget, TValue>(GenericRefTarget<TTarget> target, ref TValue input);
public delegate ref TValue GenericCombinedHook<TTarget, TValue>(GenericCombinedOrig<TTarget, TValue> orig,
    GenericRefTarget<TTarget> target, ref TValue input);

public static class CrossHooks
{
    public static LogLevel OptionalLogLevel(LogLevel value = LogLevel.Debug) => value;
    public static ExternalMode OptionalExternalMode(ExternalMode value = ExternalMode.Default) => value;
    public static int GenericTypeCalls;
    public static int GenericMethodCalls;
    public static int GenericCombinedCalls;
    public static int InCalls;
    public static int ReadOnlyCalls;

    public static unsafe int OnFunctionPointer(OrigFunctionPointer orig, TestTarget target,
        delegate*<int, int> function, int input) => orig(target, function, input) + 100;

    public static int OnAddIn(OrigAddIn orig, TestTarget target, in int input)
    {
        InCalls++;
        return orig(target, in input) + 100;
    }

    public static ref readonly int OnReadOnlyRef(OrigReadOnlyRef orig, TestTarget target)
    {
        ReadOnlyCalls++;
        return ref orig(target);
    }

    public static ref T OnGenericType<T>(GenericTypeOrig<T> orig, GenericRefTarget<T> target)
    {
        GenericTypeCalls++;
        return ref orig(target);
    }

    public static ref T OnGenericMethod<T>(GenericMethodOrig<T> orig, ref T input)
    {
        GenericMethodCalls++;
        return ref orig(ref input);
    }

    public static ref TValue OnGenericCombined<TTarget, TValue>(GenericCombinedOrig<TTarget, TValue> orig,
        GenericRefTarget<TTarget> target, ref TValue input)
    {
        GenericCombinedCalls++;
        return ref orig(target, ref input);
    }

    public static ref int OnGetRef(OrigGetRef orig, TestTarget target)
    {
        ref int result = ref orig(target);
        result += 10;
        return ref result;
    }

    public static ref int OnGetRefSecond(OrigGetRef orig, TestTarget target)
    {
        ref int result = ref orig(target);
        result += 100;
        return ref result;
    }

    public static ref int OnGetRefReflected(OrigGetRef orig, TestTarget target)
    {
        ref int result = ref orig(target);
        result += 1000;
        return ref result;
    }

    public static Span<int> OnGetSpan(OrigGetSpan orig, TestTarget target)
    {
        Span<int> result = orig(target);
        result[0] += 20;
        return result;
    }

    public static int OnSumSpan(OrigSumSpan orig, TestTarget target, Span<int> input)
    {
        input[0] += 10;
        return orig(target, input) + 100;
    }

    public static unsafe byte* OnGetPointer(OrigGetPointer orig, TestTarget target, byte* input) => orig(target, input);

    public static int OnAddByRef(OrigAddByRef orig, TestTarget target, ref int input) => orig(target, ref input) + 100;
}
