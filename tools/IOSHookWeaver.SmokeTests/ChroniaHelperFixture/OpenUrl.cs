using System.Diagnostics;

namespace ChroniaHelper
{
    public static class OpenUrl
    {
        public static void Open(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
