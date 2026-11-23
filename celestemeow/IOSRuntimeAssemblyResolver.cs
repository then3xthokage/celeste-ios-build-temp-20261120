using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace celestemeow
{
    internal static class IOSRuntimeAssemblyResolver
    {
        internal static string FindAssemblyPath(string appBundleDirectory, string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(appBundleDirectory))
                return null;
            if (string.IsNullOrWhiteSpace(assemblyName))
                throw new ArgumentException("An assembly name is required.", nameof(assemblyName));

            string assemblyFile = Path.GetFileName(assemblyName);
            if (!assemblyFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                assemblyFile += ".dll";

            string fullBaseDirectory = Path.GetFullPath(appBundleDirectory);
            string[] directories =
            {
                fullBaseDirectory,
                Path.Combine(fullBaseDirectory, "RuntimeAssemblies"),
                Path.Combine(fullBaseDirectory, "RuntimeAssemblies", "Everest"),
                Path.Combine(fullBaseDirectory, "celeste")
            };

            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory, assemblyFile);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            return null;
        }

        internal static void Attach(AssemblyLoadContext context, string appBaseDirectory, Action<string> trace)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(appBaseDirectory))
                throw new ArgumentException("An iOS app bundle directory is required.", nameof(appBaseDirectory));

            string fullBaseDirectory = Path.GetFullPath(appBaseDirectory);
            context.Resolving += (loadContext, requested) => Resolve(loadContext, requested, fullBaseDirectory, trace);
        }

        private static Assembly Resolve(
            AssemblyLoadContext context,
            AssemblyName requested,
            string appBaseDirectory,
            Action<string> trace)
        {
            if (context == null || requested == null || string.IsNullOrWhiteSpace(requested.Name))
                return null;

            string assemblyFile = requested.Name + ".dll";
            List<string> directories = new List<string>
            {
                appBaseDirectory,
                Path.Combine(appBaseDirectory, "RuntimeAssemblies"),
                Path.Combine(appBaseDirectory, "RuntimeAssemblies", "Everest"),
                Path.Combine(appBaseDirectory, "celeste")
            };

            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory))
                    continue;

                if (!string.IsNullOrWhiteSpace(requested.CultureName) &&
                    !string.Equals(requested.CultureName, "neutral", StringComparison.OrdinalIgnoreCase))
                {
                    Assembly localized = TryLoad(
                        context,
                        requested,
                        Path.Combine(directory, requested.CultureName, assemblyFile),
                        trace);
                    if (localized != null)
                        return localized;
                }

                Assembly assembly = TryLoad(context, requested, Path.Combine(directory, assemblyFile), trace);
                if (assembly != null)
                    return assembly;
            }

            return null;
        }

        private static Assembly TryLoad(
            AssemblyLoadContext context,
            AssemblyName requested,
            string path,
            Action<string> trace)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                AssemblyName candidate = AssemblyName.GetAssemblyName(path);
                if (!string.Equals(candidate.Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                    return null;

                if (requested.Version != null && candidate.Version != null && candidate.Version < requested.Version)
                    return null;

                byte[] requestedToken = requested.GetPublicKeyToken();
                byte[] candidateToken = candidate.GetPublicKeyToken();
                if (requestedToken != null && requestedToken.Length != 0 &&
                    !BytesEqual(requestedToken, candidateToken))
                    return null;

                Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
                SafeTrace(trace, "Resolved managed assembly " + requested.FullName + " from " + path);
                return assembly;
            }
            catch (Exception exception)
            {
                SafeTrace(trace, "Could not load bundled assembly " + path + ": " + exception);
                return null;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private static void SafeTrace(Action<string> trace, string message)
        {
            try { trace?.Invoke(message); }
            catch { }
        }
    }
}
