using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static readonly Dictionary<string, string> StaticLibraries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SDL2"] = "__Internal",
        ["FNA3D"] = "__Internal",
        ["FAudio"] = "__Internal",
        ["libtheorafile"] = "__Internal",
        ["theorafilebun"] = "__Internal"
    };

    private static int Main(string[] args)
    {
        if (args.Length >= 3 && string.Equals(args[0], "--write-internal-imports", StringComparison.Ordinal))
            return WriteInternalImportManifest(args[1], args.Skip(2));

        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: IOSNativeImportPatcher <input-assembly> <output-assembly> | --write-internal-imports <output-file> <assembly>...");
            return 2;
        }

        string input = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (!File.Exists(input)) throw new FileNotFoundException("Input assembly was not found.", input);
        Directory.CreateDirectory(Path.GetDirectoryName(output));

        Dictionary<string, int> rewritten = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int stubbedPlatformImports = 0;
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
                        if (string.Equals(library, "SDL2", StringComparison.OrdinalIgnoreCase) && IsPlatformOnlySDLImport(entryPoint))
                        {
                            ConvertToManagedDefaultStub(method);
                            stubbedPlatformImports++;
                        }
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

                if (string.Equals(library, "SDL2", StringComparison.OrdinalIgnoreCase) && IsPlatformOnlySDLImport(entryPoint))
                {
                    ConvertToManagedDefaultStub(method);
                    stubbedPlatformImports++;
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

        Console.WriteLine("Rewrote native imports: " + string.Join(", ", rewritten) + "; replaced " + stubbedPlatformImports + " Android, Windows, and GDK SDL imports with managed default stubs.");
        return 0;
    }

    private static int WriteInternalImportManifest(string outputPath, IEnumerable<string> assemblyPaths)
    {
        HashSet<string> symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (string assemblyPath in assemblyPaths)
        {
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Assembly for native import manifest was not found.", assemblyPath);

            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
                assemblyPath,
                new ReaderParameters { InMemory = true, ReadSymbols = false }))
            {
                foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
                foreach (MethodDefinition method in type.Methods)
                {
                    PInvokeInfo info = method.PInvokeInfo;
                    if (!method.IsPInvokeImpl || info == null ||
                        !string.Equals(info.Module?.Name, "__Internal", StringComparison.Ordinal))
                        continue;

                    string entryPoint = string.IsNullOrEmpty(info.EntryPoint) ? method.Name : info.EntryPoint;
                    symbols.Add("_" + entryPoint);
                }
            }
        }

        if (symbols.Count == 0)
        {
            Console.Error.WriteLine("No __Internal P/Invoke entry points were found in the supplied assemblies.");
            return 1;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllLines(fullOutputPath, symbols.OrderBy(symbol => symbol, StringComparer.Ordinal));
        Console.WriteLine("Wrote " + symbols.Count + " __Internal native imports to " + fullOutputPath + ".");
        return 0;
    }

    private static bool IsPlatformOnlySDLImport(string entryPoint)
    {
        return entryPoint.StartsWith("SDL_Android", StringComparison.Ordinal) ||
            entryPoint.StartsWith("SDL_GetAndroid", StringComparison.Ordinal) ||
            entryPoint.StartsWith("SDL_IsAndroid", StringComparison.Ordinal) ||
            entryPoint.StartsWith("SDL_GDK", StringComparison.Ordinal) ||
            entryPoint.StartsWith("SDL_WinRT", StringComparison.Ordinal) ||
            entryPoint.StartsWith("SDL_RenderGetD3D", StringComparison.Ordinal) ||
            entryPoint == "SDL_IsChromebook" ||
            entryPoint == "SDL_IsDeXMode" ||
            entryPoint == "SDL_SetWindowsMessageHook";
    }

    private static void ConvertToManagedDefaultStub(MethodDefinition method)
    {
        method.PInvokeInfo = null;
        method.Attributes &= ~MethodAttributes.PInvokeImpl;
        method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
        foreach (CustomAttribute attribute in method.CustomAttributes
            .Where(candidate => candidate.AttributeType.FullName == "System.Runtime.InteropServices.DllImportAttribute")
            .ToArray())
            method.CustomAttributes.Remove(attribute);

        MethodBody body = new MethodBody(method) { InitLocals = true, MaxStackSize = 1 };
        ILProcessor il = body.GetILProcessor();
        if (method.ReturnType.MetadataType == MetadataType.Void)
        {
            il.Append(il.Create(OpCodes.Ret));
        }
        else
        {
            VariableDefinition value = new VariableDefinition(method.Module.ImportReference(method.ReturnType));
            body.Variables.Add(value);
            il.Append(il.Create(OpCodes.Ldloc, value));
            il.Append(il.Create(OpCodes.Ret));
        }
        method.Body = body;
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
