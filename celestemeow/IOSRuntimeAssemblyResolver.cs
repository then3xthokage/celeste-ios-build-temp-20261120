using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
            context.ResolvingUnmanagedDll += (assembly, libraryName) =>
                ResolveLinkedNativeLibrary(assembly, libraryName, trace);
        }

        private static IntPtr ResolveLinkedNativeLibrary(Assembly assembly, string libraryName, Action<string> trace)
        {
            if (!string.Equals(libraryName, "__Internal", StringComparison.Ordinal))
                return IntPtr.Zero;

            try
            {
                // Celeste and FNA are loaded after app linking, so their __Internal imports need the executable handle.
                IntPtr mainProgram = NativeLibrary.GetMainProgramHandle();
                if (mainProgram == IntPtr.Zero)
                {
                    SafeTrace(trace, "Could not obtain the iOS executable handle for __Internal imports from " +
                        (assembly?.GetName().Name ?? "an unknown assembly") + ".");
                    return IntPtr.Zero;
                }

                SafeTrace(trace, "Resolved __Internal native imports from " +
                    (assembly?.GetName().Name ?? "an unknown assembly") + " against the app executable.");
                return mainProgram;
            }
            catch (Exception exception)
            {
                SafeTrace(trace, "Could not resolve __Internal imports from " +
                    (assembly?.GetName().Name ?? "an unknown assembly") + " against the app executable: " + exception);
                return IntPtr.Zero;
            }
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

            SafeTrace(trace, "Could not resolve managed assembly " + requested.FullName + "; searched " +
                string.Join(", ", directories));
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
                {
                    SafeTrace(trace, "Skipping bundled assembly " + path + ": requested name " +
                        requested.Name + ", file contains " + candidate.Name + ".");
                    return null;
                }

                if (requested.Version != null && candidate.Version != null && candidate.Version < requested.Version)
                {
                    SafeTrace(trace, "Skipping bundled assembly " + path + ": requested version " +
                        requested.Version + ", file contains older version " + candidate.Version + ".");
                    return null;
                }

                byte[] requestedToken = requested.GetPublicKeyToken();
                byte[] candidateToken = candidate.GetPublicKeyToken();
                if (requestedToken != null && requestedToken.Length != 0 &&
                    !BytesEqual(requestedToken, candidateToken))
                {
                    SafeTrace(trace, "Skipping bundled assembly " + path + ": its public key token does not match " +
                        requested.FullName + ".");
                    return null;
                }

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
