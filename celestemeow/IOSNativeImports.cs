using System;
using System.Runtime.InteropServices;

namespace celestemeow
{
    /// <summary>
    /// Roots the iOS decoder entry points used by dynamically loaded Everest
    /// mods so the static linker keeps them available through the app-native resolver.
    /// </summary>
    internal static class IOSNativeImports
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReadVideo2Delegate(IntPtr file, IntPtr buffer, int frames, IntPtr granulePosition);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReadAudio2Delegate(IntPtr file, IntPtr buffer, int samples, IntPtr granulePosition);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DecodeGranuleDelegate(IntPtr file, long granulePosition, IntPtr seconds, IntPtr frame);

        [DllImport("celeste-ios-native", EntryPoint = "tf_readvideo2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ReadVideo2(IntPtr file, IntPtr buffer, int frames, IntPtr granulePosition);

        [DllImport("celeste-ios-native", EntryPoint = "tf_readaudio2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ReadAudio2(IntPtr file, IntPtr buffer, int samples, IntPtr granulePosition);

        [DllImport("celeste-ios-native", EntryPoint = "tf_granpos_dec", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecodeGranule(IntPtr file, long granulePosition, IntPtr seconds, IntPtr frame);

        [DllImport("celeste-ios-native", EntryPoint = "tf_gettheorasize", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr GetTheoraSize();

        private static readonly ReadVideo2Delegate KeepReadVideo2 = ReadVideo2;
        private static readonly ReadAudio2Delegate KeepReadAudio2 = ReadAudio2;
        private static readonly DecodeGranuleDelegate KeepDecodeGranule = DecodeGranule;
        private static readonly Func<UIntPtr> KeepGetTheoraSize = GetTheoraSize;

        internal static void EnsureRequiredImportsAreLinked()
        {
            GC.KeepAlive(KeepReadVideo2);
            GC.KeepAlive(KeepReadAudio2);
            GC.KeepAlive(KeepDecodeGranule);
            GC.KeepAlive(KeepGetTheoraSize);
        }
    }
}
