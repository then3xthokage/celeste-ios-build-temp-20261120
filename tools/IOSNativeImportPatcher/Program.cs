using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static readonly Dictionary<string, string> StaticLibraries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SDL2"] = "__Internal",
        ["FNA3D"] = "__Internal",
        ["FAudio"] = "__Internal",
        ["libtheorafile"] = "__Internal"
    };

    private static readonly HashSet<string> DynamicSDLImports = new HashSet<string>(StringComparer.Ordinal)
    {
        "SDL_AndroidBackButton",
        "SDL_AndroidGetActivity",
        "SDL_AndroidGetExternalStoragePath",
        "SDL_AndroidGetExternalStorageState",
        "SDL_AndroidGetInternalStoragePath",
        "SDL_AndroidGetJNIEnv",
        "SDL_AndroidRequestPermission",
        "SDL_GetAndroidSDKVersion",
        "SDL_IsAndroidTV",
        "SDL_IsChromebook",
        "SDL_IsDeXMode",
        "SDL_SetWindowsMessageHook",
        "SDL_WinRTGetDeviceFamily",
        "SDL_WinRTRunApp"
    };

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: IOSNativeImportPatcher <input-assembly> <output-assembly>");
            return 2;
        }

        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (!File.Exists(input)) throw new FileNotFoundException("Input assembly was not found.", input);
        Directory.CreateDirectory(Path.GetDirectoryName(output));

        Dictionary<string, int> rewritten = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int retainedDynamicSDL = 0;
        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true, ReadSymbols = false }))
        {
            Dictionary<string, ModuleReference> moduleReferences = assembly.MainModule.ModuleReferences
                .ToDictionary(reference => reference.Name, reference => reference, StringComparer.OrdinalIgnoreCase);
            foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
            foreach (MethodDefinition method in type.Methods)
            {
                if (method.IsPInvokeImpl && method.PInvokeInfo != null)
                {
                    PInvokeInfo info = method.PInvokeInfo;
                    string library = info.Module?.Name;
                    if (library != null && StaticLibraries.TryGetValue(library, out string pinvokeReplacement))
                    {
                        string entryPoint = string.IsNullOrEmpty(info.EntryPoint) ? method.Name : info.EntryPoint;
                        if (string.Equals(library, "SDL2", StringComparison.OrdinalIgnoreCase) && DynamicSDLImports.Contains(entryPoint))
                            retainedDynamicSDL++;
                        else
                        {
                            if (!moduleReferences.TryGetValue(pinvokeReplacement, out ModuleReference moduleReference))
                            {
                                moduleReference = new ModuleReference(pinvokeReplacement);
                                assembly.MainModule.ModuleReferences.Add(moduleReference);
                                moduleReferences.Add(pinvokeReplacement, moduleReference);
                            }
                            method.PInvokeInfo = new PInvokeInfo(info.Attributes, info.EntryPoint, moduleReference);
                            rewritten[library] = rewritten.TryGetValue(library, out int count) ? count + 1 : 1;
                        }
                    }
                }

            foreach (CustomAttribute attribute in method.CustomAttributes)
            {
                if (!string.Equals(attribute.AttributeType.FullName, "System.Runtime.InteropServices.DllImportAttribute", StringComparison.Ordinal) ||
                    attribute.ConstructorArguments.Count == 0 || attribute.ConstructorArguments[0].Value is not string library ||
                    !StaticLibraries.TryGetValue(library, out string replacement))
                    continue;

                string entryPoint = method.Name;
                foreach (CustomAttributeNamedArgument property in attribute.Properties)
                    if (string.Equals(property.Name, "EntryPoint", StringComparison.Ordinal) && property.Argument.Value is string namedEntryPoint)
                        entryPoint = namedEntryPoint;

                if (string.Equals(library, "SDL2", StringComparison.OrdinalIgnoreCase) && DynamicSDLImports.Contains(entryPoint))
                {
                    retainedDynamicSDL++;
                    continue;
                }

                attribute.ConstructorArguments[0] = new CustomAttributeArgument(attribute.ConstructorArguments[0].Type, replacement);
                rewritten[library] = rewritten.TryGetValue(library, out int count) ? count + 1 : 1;
            }
            }

            string temporary = output + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
            assembly.Write(temporary);
            File.Move(temporary, output, true);
        }

        List<string> missing = new List<string>();
        if (!rewritten.TryGetValue("SDL2", out int sdlImports) || sdlImports == 0)
            missing.Add("SDL2");
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("No static imports were found for: " + string.Join(", ", missing));
            return 1;
        }

        Console.WriteLine("Rewrote native imports: " + string.Join(", ", rewritten) + "; kept " + retainedDynamicSDL + " platform-specific SDL imports dynamic.");
        return 0;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
}
