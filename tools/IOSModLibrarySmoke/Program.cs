using System.IO.Compression;
using System.Reflection;
using celestemeow;

namespace IOSModLibrarySmoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        VerifyNormalizedNames();
        VerifyGameBananaCandidateRanking();
        VerifyDependencySearchQueryVariants();
        VerifyOfficialGameBananaApiParsing();
        VerifyOfficialGameBananaChecksum();
        VerifyEverestVersionCompatibility();
        VerifyUniqueModuleAlias();
        VerifyAmbiguousModuleAliasIsRejected();
        VerifyEverestDummyModuleFallbackKeepsCampaignMaps();
        VerifyEmptyManifestVersionUsesEverestFallback();
        if (args.Contains("--live-gamebanana", StringComparer.OrdinalIgnoreCase))
            await VerifyLiveGameBananaDependencyAsync();
        Console.WriteLine("PASS iOS mod dependency and map-package smoke checks");
        return 0;
    }

    private static void VerifyNormalizedNames()
    {
        Require(ModDependencyResolver.NormalizeName(" Needle-Helper ") == "NEEDLEHELPER",
            "Dependency aliases should ignore surrounding whitespace and punctuation.");
    }

    private static void VerifyGameBananaCandidateRanking()
    {
        Type candidateType = typeof(IOSModLibrary).Assembly.GetType("celestemeow.CatalogMod", throwOnError: true);
        object exact = Activator.CreateInstance(candidateType, nonPublic: true);
        SetProperty(candidateType, exact, "Name", "Needle Helper");
        int exactScore = (int)InvokePrivate("GetDependencySearchScore", exact, "NEEDLEHELPER");
        Require(exactScore == 2, "An exact normalized GameBanana title should rank first.");

        object unrelated = Activator.CreateInstance(candidateType, nonPublic: true);
        SetProperty(candidateType, unrelated, "Name", "Other Helper");
        int unrelatedScore = (int)InvokePrivate("GetDependencySearchScore", unrelated, "NEEDLEHELPER");
        Require(unrelatedScore == 0, "An unrelated GameBanana result should not receive an alias score.");
    }

    private static void VerifyDependencySearchQueryVariants()
    {
        List<string> memeQueries = (List<string>)InvokePrivate("GetDependencySearchQueries", "MemeSides");
        Require(memeQueries.Contains("Meme Sides"),
            "PascalCase dependency names should also search their spaced GameBanana display title.");

        List<string> kirbyQueries = (List<string>)InvokePrivate("GetDependencySearchQueries", "KirbyHelperMechanics");
        Require(kirbyQueries.Contains("Kirby Helper Mechanics"),
            "Long PascalCase dependency names should split into readable search terms.");

        List<string> punctuatedQueries = (List<string>)InvokePrivate("GetDependencySearchQueries", "Needle-Helper");
        Require(punctuatedQueries.Contains("Needle Helper"),
            "Punctuation-separated dependency names should search their readable title.");
    }

    private static void VerifyOfficialGameBananaApiParsing()
    {
        const string searchJson = "{\"_aRecords\":[{\"_idRow\":377422,\"_sName\":\"Velvet CC-Side\"}]}";
        var results = (System.Collections.IList)InvokePrivate("ParseGameBananaSearchResults", searchJson);
        Require(results.Count == 1, "The official GameBanana search response should yield its mod record.");
        Require(GetProperty<long>(results[0], "GameBananaId") == 377422,
            "The official GameBanana mod ID should be retained for direct file lookup.");

        const string filesJson = "{\"Files().aFiles()\":{" +
            "\"100\":{\"_sFile\":\"old.zip\",\"_sDownloadUrl\":\"https://gamebanana.com/dl/100\",\"_tsDateAdded\":10,\"_nFilesize\":20}," +
            "\"101\":{\"_sFile\":\"new.zip\",\"_sDownloadUrl\":\"https://gamebanana.com/dl/101\",\"_tsDateAdded\":20,\"_nFilesize\":30}," +
            "\"102\":{\"_sFile\":\"untrusted.zip\",\"_sDownloadUrl\":\"https://example.org/mod.zip\",\"_tsDateAdded\":30,\"_nFilesize\":40}}}";
        var files = (System.Collections.IList)InvokePrivate("ParseGameBananaFiles", filesJson);
        Require(files.Count == 2, "The official GameBanana file list should be parsed and non-GameBanana hosts rejected.");
        Require(GetField<string>(files[0], "Name") == "new.zip",
            "Official file candidates should be checked newest first.");
        Require(GetField<string>(files[0], "MirrorName") == "101",
            "Official file IDs should be preserved for the Celeste community's mirror fallbacks.");
    }

    private static void VerifyOfficialGameBananaChecksum()
    {
        string path = Path.Combine(Path.GetTempPath(), "celeste-ios-gamebanana-hash-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            File.WriteAllText(path, "abc");
            InvokePrivate("VerifyGameBananaFileHash", path, "900150983cd24fb0d6963f7d28e17f72");
            bool rejected = false;
            try { InvokePrivate("VerifyGameBananaFileHash", path, "00000000000000000000000000000000"); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException) { rejected = true; }
            Require(rejected, "A GameBanana file with a mismatched checksum must be rejected.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task VerifyLiveGameBananaDependencyAsync()
    {
        var candidates = (System.Collections.IList)await InvokePrivateAsync("SearchGameBananaCatalogAsync", "Needle Helper");
        object candidate = candidates.Cast<object>()
            .FirstOrDefault(item => GetProperty<long>(item, "GameBananaId") == 53748);
        Require(candidate != null, "The live GameBanana search should find Cursed Needle Helper.");

        string path = Path.Combine(Path.GetTempPath(), "celeste-ios-live-dependency-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            object package = await InvokePrivateAsync(
                "DownloadDependencyFromGameBananaItemAsync",
                candidate,
                new ModDependency { Name = "NeedleHelper", Version = "1.0.0" },
                path,
                null);
            object module = InvokePrivate("FindModule", package, "NeedleHelper");
            Require(module != null && GetProperty<string>(module, "Name") == "Needle Helper",
                "The live package should pass exact Everest module and version verification.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void VerifyEverestVersionCompatibility()
    {
        Require((bool)InvokePrivate("VersionSatisfies", "1.2.0", "1.3.0"),
            "A newer minor version with the same major version should satisfy an Everest dependency.");
        Require(!(bool)InvokePrivate("VersionSatisfies", "1.2.0", "2.0.0"),
            "A different major version must not satisfy an Everest dependency.");
        Require((bool)InvokePrivate("VersionSatisfies", "1.2.0", "0.0.0-dummy"),
            "Everest dummy modules must retain Everest's 0.0.* compatibility behavior.");
    }

    private static void VerifyUniqueModuleAlias()
    {
        object package = InspectArchive(CreateArchive(
            "- Name: Needle Helper\n  Version: 1.0.0\n",
            "needle-helper.zip",
            includeMap: false), "needle-helper");
        object module = InvokePrivate("FindModule", package, "NeedleHelper");
        Require(module != null, "A unique punctuation-insensitive module alias should resolve.");
        Require(GetProperty<string>(module, "Name") == "Needle Helper",
            "Alias resolution should return the actual manifest spelling.");
    }

    private static void VerifyAmbiguousModuleAliasIsRejected()
    {
        object package = InspectArchive(CreateArchive(
            "- Name: Needle Helper\n  Version: 1.0.0\n- Name: Needle-Helper\n  Version: 1.0.0\n",
            "ambiguous-helper.zip",
            includeMap: false), "ambiguous-helper");
        object module = InvokePrivate("FindModule", package, "NeedleHelper");
        Require(module == null, "A module alias matching multiple modules must be rejected as ambiguous.");
    }

    private static void VerifyEverestDummyModuleFallbackKeepsCampaignMaps()
    {
        object package = InspectArchive(CreateArchive(
            "- Name: Broken Campaign\n  Version: 1\n",
            "broken-campaign.zip",
            includeMap: true), "BrokenCampaign");
        Require(GetField<int>(package, "MapCount") == 1,
            "A malformed Everest manifest should not prevent campaign maps from being discovered.");
        Require(GetField<bool>(package, "ManifestFallback"),
            "Malformed metadata should be marked as Everest's dummy-module fallback.");
        Require(GetField<string>(package, "Status").Contains("Everest dummy-module fallback", StringComparison.Ordinal),
            "The package status should explain the fallback used.");
        Require(GetFirstModuleName(package) == "_zip_BrokenCampaign",
            "The fallback module name should match Everest's ZIP dummy-module naming.");
    }

    private static void VerifyEmptyManifestVersionUsesEverestFallback()
    {
        object package = InspectArchive(CreateArchive(
            "- Name: Empty Version\n  Version: \"\"\n",
            "empty-version.zip",
            includeMap: false), "EmptyVersion");
        Require(GetField<bool>(package, "ManifestFallback"),
            "An explicitly empty version must use Everest's dummy-module fallback.");
    }

    private static string CreateArchive(string yaml, string name, bool includeMap)
    {
        string path = Path.Combine(Path.GetTempPath(), "celeste-ios-mod-smoke-" + Guid.NewGuid().ToString("N") + ".zip");
        using (FileStream stream = File.Create(path))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifest = archive.CreateEntry("everest.yaml");
            using (StreamWriter writer = new StreamWriter(manifest.Open())) writer.Write(yaml);
            if (includeMap)
            {
                ZipArchiveEntry map = archive.CreateEntry("Maps/Smoke.bin");
                using (Stream streamMap = map.Open()) streamMap.WriteByte(0x42);
            }
        }
        return path;
    }

    private static object InspectArchive(string archivePath, string fallbackName)
    {
        return InvokePrivate("InspectArchive", archivePath, fallbackName);
    }

    private static object InvokePrivate(string name, params object[] arguments)
    {
        MethodInfo method = typeof(IOSModLibrary).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null) throw new InvalidOperationException("Missing private method " + name + ".");
        return method.Invoke(null, arguments);
    }

    private static async Task<object> InvokePrivateAsync(string name, params object[] arguments)
    {
        MethodInfo method = typeof(IOSModLibrary).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null) throw new InvalidOperationException("Missing private method " + name + ".");
        Task task = (Task)method.Invoke(null, arguments);
        await task;
        return task.GetType().GetProperty("Result").GetValue(task);
    }

    private static void SetProperty(Type type, object instance, string name, object value)
    {
        PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null) throw new InvalidOperationException("Missing property " + name + ".");
        property.SetValue(instance, value);
    }

    private static T GetProperty<T>(object instance, string name)
    {
        PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null) throw new InvalidOperationException("Missing property " + name + ".");
        return (T)property.GetValue(instance);
    }

    private static T GetField<T>(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field == null) throw new InvalidOperationException("Missing field " + name + ".");
        return (T)field.GetValue(instance);
    }

    private static string GetFirstModuleName(object package)
    {
        object modules = GetField<object>(package, "Modules");
        object module = ((System.Collections.IList)modules)[0];
        return GetProperty<string>(module, "Name");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
