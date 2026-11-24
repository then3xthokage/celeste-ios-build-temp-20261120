using System.Runtime.InteropServices;

public static class NativePInvokeFixture
{
    [DllImport("__Internal", EntryPoint = "getpid", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetProcessId();
}
