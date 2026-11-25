using System;
using System.IO;

namespace celestemeow
{
    internal static class IOSBuildIdentity
    {
        public static string Read(string appBundleDirectory)
        {
            try
            {
                string path = Path.Combine(appBundleDirectory ?? AppContext.BaseDirectory, "BuildInfo.txt");
                string identity = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(identity))
                    return "empty";

                return identity.Replace("\r\n", ", ").Replace("\n", ", ").Replace("\r", ", ");
            }
            catch
            {
                return "unavailable";
            }
        }
    }
}
