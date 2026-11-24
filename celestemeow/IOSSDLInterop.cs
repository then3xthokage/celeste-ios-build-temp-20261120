using System;
using System.Runtime.InteropServices;
using System.Text;

namespace celestemeow
{
    /// <summary>
    /// Minimal SDL host entry points used by the launcher before FNA is loaded.
    /// Keeping this boundary native lets the pre-boot mod manager start without
    /// resolving the game's FNA assembly first.
    /// </summary>
    internal static class IOSSDLInterop
    {
        internal const int MessageBoxError = 0x00000010;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int SDLMainFunction(int argc, IntPtr argv);

        [DllImport("celeste-ios-native", EntryPoint = "SDL_SetHint", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetHintNative(IntPtr name, IntPtr value);

        [DllImport("celeste-ios-native", EntryPoint = "SDL_UIKitRunApp", CallingConvention = CallingConvention.Cdecl)]
        private static extern int UIKitRunAppNative(int argc, IntPtr argv, SDLMainFunction mainFunction);

        [DllImport("celeste-ios-native", EntryPoint = "SDL_ShowSimpleMessageBox", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ShowMessageBoxNative(int flags, IntPtr title, IntPtr message, IntPtr window);

        internal static int SetHint(string name, string value)
        {
            IntPtr nameUtf8 = IntPtr.Zero;
            IntPtr valueUtf8 = IntPtr.Zero;
            try
            {
                nameUtf8 = AllocateUtf8(name);
                valueUtf8 = AllocateUtf8(value);
                return SetHintNative(nameUtf8, valueUtf8);
            }
            finally
            {
                if (valueUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(valueUtf8);
                if (nameUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(nameUtf8);
            }
        }

        internal static int UIKitRunApp(int argc, IntPtr argv, SDLMainFunction mainFunction)
        {
            if (mainFunction == null) throw new ArgumentNullException(nameof(mainFunction));
            return UIKitRunAppNative(argc, argv, mainFunction);
        }

        internal static int ShowMessageBox(int flags, string title, string message, IntPtr window)
        {
            IntPtr titleUtf8 = IntPtr.Zero;
            IntPtr messageUtf8 = IntPtr.Zero;
            try
            {
                titleUtf8 = AllocateUtf8(title);
                messageUtf8 = AllocateUtf8(message);
                return ShowMessageBoxNative(flags, titleUtf8, messageUtf8, window);
            }
            finally
            {
                if (messageUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(messageUtf8);
                if (titleUtf8 != IntPtr.Zero) Marshal.FreeHGlobal(titleUtf8);
            }
        }

        private static IntPtr AllocateUtf8(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((value ?? string.Empty) + "\0");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                return pointer;
            }
            catch
            {
                Marshal.FreeHGlobal(pointer);
                throw;
            }
        }
    }
}
