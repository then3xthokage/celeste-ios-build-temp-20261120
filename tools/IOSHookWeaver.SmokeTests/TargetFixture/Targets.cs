using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

#pragma warning disable CA2255
public static class ModuleInitializerProbe
{
    public static int Value;

    [ModuleInitializer]
    public static void Initialize() => Value = 73;
}
#pragma warning restore CA2255

public sealed unsafe class TestTarget
{
    private int value = 5;
    private readonly int[] values = new[] { 1 };

    public ref int GetRef() => ref value;
    public ref int DetourGetRef()
    {
        value += 1000;
        return ref value;
    }
    public int ReadRef()
    {
        ref int result = ref GetRef();
        result++;
        return result;
    }

    public Span<int> GetSpan() => values;
    public int ReadSpan() => GetSpan()[0];

    public int SumSpan(Span<int> input)
    {
        input[0]++;
        return input[0];
    }

    public int ExerciseSpanArgument()
    {
        int[] input = new[] { 2 };
        return SumSpan(input);
    }

    public byte* GetPointer(byte* input) => input;
    public int ExercisePointer()
    {
        byte[] values = new[] { (byte)3 };
        fixed (byte* input = values)
            return GetPointer(input) == input ? 1 : 0;
    }

    public int InvokeFunctionPointer(delegate*<int, int> function, int input) => function(input);
    public static int ModifiedFunctionPointer(TestTarget target, delegate*<int, int> function, int input) => function(input) + 10;
    private static int FunctionPointerTarget(int input) => input + 1;
    public int ExerciseFunctionPointer()
    {
        delegate*<int, int> function = &FunctionPointerTarget;
        return InvokeFunctionPointer(function, 8);
    }

    public int AddByRef(ref int input)
    {
        input += 2;
        return input;
    }

    public int ExerciseByRefArgument()
    {
        int input = 3;
        int returned = AddByRef(ref input);
        return input + returned;
    }

    public int AddIn(in int input) => value + input;
    public int ExerciseInArgument()
    {
        int input = 7;
        return AddIn(in input);
    }

    public ref readonly int GetReadOnlyRef() => ref values[0];
    public int ExerciseReadOnlyRef() => GetReadOnlyRef();
}

public static unsafe class VideoNativeImportFixture
{
    [DllImport("theorafilebun", EntryPoint = "tf_readvideo2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ReadVideo2(IntPtr file, IntPtr buffer, int numFrames, long* granulePosition);

    [DllImport("theorafilebun", EntryPoint = "tf_readaudio2", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ReadAudio2(IntPtr file, IntPtr buffer, int samples, long* granulePosition);

    [DllImport("theorafilebun", EntryPoint = "tf_granpos_dec", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DecodeGranule(IntPtr file, long granulePosition, double* seconds, long* frame);

    [DllImport("theorafilebun", EntryPoint = "tf_gettheorasize", CallingConvention = CallingConvention.Cdecl)]
    public static extern UIntPtr GetTheoraSize();
}

public static class ColorNativeImportFixture
{
    [DllImport("shlwapi.dll", EntryPoint = "ColorHLSToRGB", ExactSpelling = true)]
    public static extern int ColorHLSToRGB(int hue, int luminance, int saturation);
}

public struct TestStructTarget
{
    private int value;

    public TestStructTarget(int initial) => value = initial;
    public int MutateSpan(Span<int> input)
    {
        input[0] += value;
        return input[0];
    }

    public int Add(int amount)
    {
        value += amount;
        return value;
    }

    public int DetourAdd(int amount)
    {
        value += amount * 10;
        return value;
    }

    public int ExerciseSpan()
    {
        int[] input = new[] { 1 };
        return MutateSpan(input);
    }
}

public struct TestStructConstructorTarget
{
    public int Value;
    public TestStructConstructorTarget(int value) => Value = value;
}

public ref struct RefLikeConstructorTarget
{
    private int value;

    public RefLikeConstructorTarget(int initialValue, out bool initialized)
    {
        value = initialValue;
        initialized = true;
    }

    public int Value => value;
}

public static class RefLikeConstructorProbe
{
    public static int CreateAndRead(int value)
    {
        RefLikeConstructorTarget target = new RefLikeConstructorTarget(value, out bool initialized);
        return initialized ? target.Value : -1;
    }
}

public class BranchCtorBase
{
    public int BaseValue;
    public BranchCtorBase(int value) => BaseValue = value;
}

public class BranchCtorTarget : BranchCtorBase
{
    public int Value;
    public BranchCtorTarget(bool chooseFirst) : base(chooseFirst ? 13 : 17) => Value = 23;
}

public class GenericChainedConstructor<T> : BranchCtorBase
{
    public T Value;
    public GenericChainedConstructor() : this(default(T), 29) { }
    public GenericChainedConstructor(T value, int baseValue) : base(baseValue) => Value = value;
}

public sealed class EverestStyleConstructorTarget
{
    public int Value;
    public int PostValue;
    public static int CleanupCalls;

    public EverestStyleConstructorTarget(bool initialize, int value)
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

public delegate void OrigEverestStyleConstructor(EverestStyleConstructorTarget target, bool initialize, int value);
public delegate void HookEverestStyleConstructor(OrigEverestStyleConstructor orig,
    EverestStyleConstructorTarget target, bool initialize, int value);

public static class EverestStyleConstructorHooks
{
    public static int Calls;

    public static void OnConstructor(OrigEverestStyleConstructor orig,
        EverestStyleConstructorTarget target, bool initialize, int value)
    {
        Calls++;
        orig(target, initialize, value);
        target.PostValue++;
    }
}

public sealed class DirectBranchConstructorTarget
{
    public int Value;
    public int PostValue;

    public DirectBranchConstructorTarget(bool initialize, int value)
    {
        if (initialize)
        {
            Value = value;
            PostValue = 4;
            return;
        }
        throw new InvalidOperationException("direct constructor branch failed");
    }
}

public delegate void OrigDirectBranchConstructor(DirectBranchConstructorTarget target, bool initialize, int value);
public delegate void HookDirectBranchConstructor(OrigDirectBranchConstructor orig,
    DirectBranchConstructorTarget target, bool initialize, int value);

public static class DirectBranchConstructorHooks
{
    public static int Calls;

    public static void OnConstructor(OrigDirectBranchConstructor orig,
        DirectBranchConstructorTarget target, bool initialize, int value)
    {
        Calls++;
        orig(target, initialize, value);
        target.PostValue++;
    }
}

public sealed class GenericRefTarget<T>
{
    private T value;
    public int DetourCalls;

    public GenericRefTarget(T initial) => value = initial;
    public ref T GetRef() => ref value;
    public T ReadRef() => GetRef();
    public ref U Echo<U>(ref U input) => ref input;
    public ref U DetourEcho<U>(ref U input)
    {
        DetourCalls++;
        return ref input;
    }
    public int ExerciseEcho()
    {
        int input = 23;
        ref int output = ref Echo(ref input);
        return output;
    }
}

public static class GenericRefMethod
{
    public static ref T Echo<T>(ref T value) => ref value;
    public static int ExerciseInt()
    {
        int value = 17;
        return Echo(ref value);
    }
}

public class GenericILTypeTarget<T>
{
    public static string GetTypeValue() => "original-type";
    public string GetInstanceValue() => "original-instance";
    public string GetGenericInstanceValue<U>() => "original-generic-instance";
}

public static class GenericILMethodTarget
{
    public static string GetMethodValue<T>() => "original-method";
}

public static class StaticConstructorTarget
{
    public static int Value;

    static StaticConstructorTarget() => Value = 1;
    public static int GetValue() => Value;
}

public class ConstructorILTarget
{
    public string Value;

    public ConstructorILTarget()
    {
        Value = "original-constructor";
    }
}

public struct StructILTarget
{
    private int value;

    public void SetValue() => value = 5;
    public int GetValue() => value;
}

public delegate ref int OrigGetRef(TestTarget target);
public delegate ref int HookGetRef(OrigGetRef orig, TestTarget target);
public delegate Span<int> OrigGetSpan(TestTarget target);
public delegate Span<int> HookGetSpan(OrigGetSpan orig, TestTarget target);
public delegate int OrigSumSpan(TestTarget target, Span<int> input);
public delegate int HookSumSpan(OrigSumSpan orig, TestTarget target, Span<int> input);
public unsafe delegate byte* OrigGetPointer(TestTarget target, byte* input);
public unsafe delegate byte* HookGetPointer(OrigGetPointer orig, TestTarget target, byte* input);
public unsafe delegate int OrigFunctionPointer(TestTarget target, delegate*<int, int> function, int input);
public unsafe delegate int HookFunctionPointer(OrigFunctionPointer orig, TestTarget target, delegate*<int, int> function, int input);
public delegate int OrigAddByRef(TestTarget target, ref int input);
public delegate int HookAddByRef(OrigAddByRef orig, TestTarget target, ref int input);
public delegate int OrigAddIn(TestTarget target, in int input);
public delegate int HookAddIn(OrigAddIn orig, TestTarget target, in int input);
public delegate ref readonly int OrigReadOnlyRef(TestTarget target);
public delegate ref readonly int HookReadOnlyRef(OrigReadOnlyRef orig, TestTarget target);
public delegate int OrigStructSpan(ref TestStructTarget target, Span<int> input);
public delegate int HookStructSpan(OrigStructSpan orig, ref TestStructTarget target, Span<int> input);
public delegate void OrigStructConstructor(ref TestStructConstructorTarget target, int value);
public delegate void HookStructConstructor(OrigStructConstructor orig, ref TestStructConstructorTarget target, int value);
public delegate void OrigConstructorIL(ConstructorILTarget target);
public delegate void HookConstructorIL(OrigConstructorIL orig, ConstructorILTarget target);
public delegate void OrigStaticConstructor();
public delegate void HookStaticConstructor(OrigStaticConstructor orig);
public delegate void OrigBranchCtor(BranchCtorTarget target, bool chooseFirst);
public delegate void HookBranchCtor(OrigBranchCtor orig, BranchCtorTarget target, bool chooseFirst);
public delegate void OrigGenericChainedConstructor(GenericChainedConstructor<int> target);
public delegate void HookGenericChainedConstructor(OrigGenericChainedConstructor orig, GenericChainedConstructor<int> target);
public delegate int OrigColorNative(int hue, int luminance, int saturation);
public delegate int HookColorNative(OrigColorNative orig, int hue, int luminance, int saturation);

public static class TestHooks
{
    public static int RefCalls;
    public static int SpanCalls;
    public static int SpanArgumentCalls;
    public static int PointerCalls;
    public static int ByRefCalls;
    public static int InCalls;
    public static int ReadOnlyCalls;
    public static int ColorNativeCalls;
    public static int OnStructSpan(OrigStructSpan orig, ref TestStructTarget target, Span<int> input)
    {
        input[0] += 10;
        return orig(ref target, input) + 100;
    }

    public static ref int OnGetRef(OrigGetRef orig, TestTarget target)
    {
        ref int result = ref orig(target);
        result += 10;
        RefCalls++;
        return ref result;
    }

    public static Span<int> OnGetSpan(OrigGetSpan orig, TestTarget target)
    {
        SpanCalls++;
        Span<int> result = orig(target);
        result[0] += 20;
        return result;
    }

    public static int OnSumSpan(OrigSumSpan orig, TestTarget target, Span<int> input)
    {
        SpanArgumentCalls++;
        input[0] += 10;
        return orig(target, input) + 100;
    }

    public static unsafe byte* OnGetPointer(OrigGetPointer orig, TestTarget target, byte* input)
    {
        PointerCalls++;
        return orig(target, input);
    }

    public static int OnAddByRef(OrigAddByRef orig, TestTarget target, ref int input)
    {
        ByRefCalls++;
        return orig(target, ref input) + 100;
    }

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

    public static void OnStructConstructor(OrigStructConstructor orig, ref TestStructConstructorTarget target, int value)
    {
        orig(ref target, value * 2);
        target.Value++;
    }

    public static void OnConstructorIL(OrigConstructorIL orig, ConstructorILTarget target)
    {
        orig(target);
        target.Value += "/hook";
    }

    public static void OnStaticConstructor(OrigStaticConstructor orig)
    {
        orig();
        StaticConstructorTarget.Value += 10;
    }

    public static void OnBranchCtor(OrigBranchCtor orig, BranchCtorTarget target, bool chooseFirst)
    {
        orig(target, chooseFirst);
        target.Value += 100;
    }

    public static void OnGenericChainedConstructor(OrigGenericChainedConstructor orig, GenericChainedConstructor<int> target)
    {
        orig(target);
        target.Value++;
    }

    public static int OnColorNative(OrigColorNative orig, int hue, int luminance, int saturation)
    {
        ColorNativeCalls++;
        return orig(hue, luminance, saturation) + 7;
    }
}
