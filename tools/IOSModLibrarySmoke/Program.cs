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
        VerifyCommunityModCatalogParsing();
        VerifyGitHubRepositorySearchParsing();
        VerifyGitHubReleaseAssetParsing();
        VerifyGitHubReleaseChecksum();
        VerifyEverestVersionCompatibility();
        VerifyUniqueModuleAlias();
        VerifyAmbiguousModuleAliasIsRejected();
        VerifyEverestDummyModuleFallbackKeepsCampaignMaps();
        VerifyEmptyManifestVersionUsesEverestFallback();
        if (args.Contains("--live-gamebanana", StringComparer.OrdinalIgnoreCase))
            await VerifyLiveGameBananaDependencyAsync();
        if (args.Contains("--live-github", StringComparer.OrdinalIgnoreCase))
            await VerifyLiveGitHubDependencyAsync();
        if (args.Contains("--live-legacy-version", StringComparer.OrdinalIgnoreCase))
            await VerifyLiveLegacyDependencyVersionAsync();
        if (args.Contains("--live-map-version-audit", StringComparer.OrdinalIgnoreCase))
            await AuditLiveMapDependencyVersionsAsync();
        if (args.Contains("--live-spark-audio", StringComparer.OrdinalIgnoreCase))
            await VerifyLiveDependencyAsync(new ModDependency { Name = "SparkInTheMachineAudio", Version = "1.0.2" });
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

        const string versionedFilesJson = "{\"Files().aFiles()\":{" +
            "\"201\":{\"_sFile\":\"legacy-compatible.zip\",\"_sDescription\":\"Version 0.1.0\",\"_sDownloadUrl\":\"https://gamebanana.com/dl/201\",\"_tsDateAdded\":10}," +
            "\"202\":{\"_sFile\":\"unknown-version.zip\",\"_sDownloadUrl\":\"https://gamebanana.com/dl/202\",\"_tsDateAdded\":20}," +
            "\"203\":{\"_sFile\":\"new-major.zip\",\"_sDescription\":\"v1.2.2\",\"_sDownloadUrl\":\"https://gamebanana.com/dl/203\",\"_tsDateAdded\":30}}}";
        var versionedFiles = (System.Collections.IList)InvokePrivate("ParseGameBananaFiles", versionedFilesJson);
        var dependencyFiles = (System.Collections.IList)InvokePrivate(
            "OrderGameBananaFilesForDependency", versionedFiles,
            new ModDependency { Name = "BGswitch", Version = "0.1.0" });
        Require(dependencyFiles.Count == 3,
            "Dependency recovery must consider every public GameBanana version, not stop at a fixed first-page limit.");
        Require(GetField<string>(dependencyFiles[0], "Name") == "legacy-compatible.zip",
            "An advertised Everest-compatible archive should be tried before unknown or incompatible versions.");
        Require(GetField<string>(dependencyFiles[2], "Name") == "new-major.zip",
            "An advertised incompatible major version should remain a fallback candidate without being preferred.");
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

    private static void VerifyGitHubRepositorySearchParsing()
    {
        const string json = "{\"items\":[" +
            "{\"full_name\":\"Magedeline/KirbyHelperMechanics\",\"html_url\":\"https://github.com/Magedeline/KirbyHelperMechanics\",\"private\":false}," +
            "{\"full_name\":\"someone/KirbyHelperMechanics\",\"html_url\":\"https://example.org/someone/KirbyHelperMechanics\",\"private\":false}," +
            "{\"full_name\":\"someone/PrivateCopy\",\"html_url\":\"https://github.com/someone/PrivateCopy\",\"private\":true}]}";
        var repositories = (System.Collections.IList)InvokePrivate("ParseGitHubRepositories", json);
        Require(repositories.Count == 1,
            "GitHub repository parsing should keep public GitHub URLs and reject private or non-GitHub results.");
        Require(GetField<string>(repositories[0], "FullName") == "Magedeline/KirbyHelperMechanics",
            "The GitHub owner/repository identity should be retained for release lookup.");
        int score = (int)InvokePrivate("GetGitHubDependencySearchScore", repositories[0], "KIRBYHELPERMECHANICS");
        Require(score == 2, "An exact normalized repository name should rank first for a missing dependency.");
    }

    private static void VerifyCommunityModCatalogParsing()
    {
        const string json = "{\"modInfo\":[" +
            "{\"modName\":\"Background Toggler\",\"modEverestYamlId\":\"BGswitch\",\"latestVersion\":\"1.2.2\",\"pageUrl\":\"https://gamebanana.com/mods/53642\"}," +
            "{\"modName\":\"Untrusted\",\"modEverestYamlId\":\"BGswitch\",\"pageUrl\":\"https://example.org/mods/53642\"}]}";
        var candidates = (System.Collections.IList)InvokePrivate("ParseCommunityModCatalog", json);
        Require(candidates.Count == 1,
            "The Celeste mod index should keep valid GameBanana pages and reject external page URLs.");
        Require(GetProperty<long>(candidates[0], "GameBananaId") == 53642,
            "The indexed page ID should be retained for direct official GameBanana file lookup.");
        Require((int)InvokePrivate("GetDependencySearchScore", candidates[0], "BGSWITCH") == 2,
            "The index's Everest module identity should rank as an exact dependency match.");
    }

    private static void VerifyGitHubReleaseAssetParsing()
    {
        const string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string json = "[{\"draft\":false,\"assets\":[" +
            "{\"name\":\"KirbyHelperMechanics.3.0.0.zip\",\"browser_download_url\":\"https://github.com/Magedeline/KirbyHelperMechanics/releases/download/1.2.2/KirbyHelperMechanics.3.0.0.zip\",\"size\":8258662,\"digest\":\"" + digest + "\"}," +
            "{\"name\":\"untrusted.zip\",\"browser_download_url\":\"https://example.org/mod.zip\",\"size\":100,\"digest\":\"" + digest + "\"}," +
            "{\"name\":\"source.zip\",\"browser_download_url\":\"https://github.com/Magedeline/KirbyHelperMechanics/archive/refs/heads/master.zip\",\"size\":100,\"digest\":\"" + digest + "\"}," +
            "{\"name\":\"readme.txt\",\"browser_download_url\":\"https://github.com/Magedeline/KirbyHelperMechanics/releases/download/1.2.2/readme.txt\",\"size\":100,\"digest\":\"" + digest + "\"}]}]";
        var assets = (System.Collections.IList)InvokePrivate(
            "ParseGitHubReleaseAssets", json, "Magedeline/KirbyHelperMechanics");
        Require(assets.Count == 1, "GitHub release parsing should keep only ZIP assets from that repository's release URL.");
        Require(GetField<string>(assets[0], "Name") == "KirbyHelperMechanics.3.0.0.zip",
            "The release ZIP name should be retained for progress reporting.");
        Require(GetField<string>(assets[0], "Digest") == digest,
            "The GitHub-provided SHA-256 digest should be retained for download verification.");
    }

    private static void VerifyGitHubReleaseChecksum()
    {
        string path = Path.Combine(Path.GetTempPath(), "celeste-ios-github-hash-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            File.WriteAllText(path, "abc");
            InvokePrivate("VerifyGitHubAssetSha256", path,
                "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            bool rejected = false;
            try { InvokePrivate("VerifyGitHubAssetSha256", path, "sha256:0000000000000000000000000000000000000000000000000000000000000000"); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException) { rejected = true; }
            Require(rejected, "A GitHub release with a mismatched SHA-256 digest must be rejected.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task VerifyLiveGitHubDependencyAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "celeste-ios-live-github-dependency-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            object package = await InvokePrivateAsync(
                "DownloadDependencyFromGameBananaAsync",
                new ModDependency { Name = "KirbyHelperMechanics", Version = "3.0.0" },
                path,
                null);
            object module = InvokePrivate("FindModule", package, "KirbyHelperMechanics");
            Require(module != null && GetProperty<string>(module, "Version") == "3.0.0",
                "The live public release should install the exact KirbyHelperMechanics module and required version.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task VerifyLiveLegacyDependencyVersionAsync()
    {
        var updateCatalog = (System.Collections.IDictionary)await InvokePrivateAsync("DownloadUpdateCatalogAsync");
        object latestUpdate = updateCatalog["DashToggleHelper"];
        Require(latestUpdate != null, "The live Everest updater catalog should contain DashToggleHelper.");
        string catalogVersion = GetField<string>(latestUpdate, "Version");
        Require(!(bool)InvokePrivate("VersionSatisfies", "0.11.0", catalogVersion),
            "The test map's older 0.11.0 dependency should not be satisfied by the current catalog release.");

        string path = Path.Combine(Path.GetTempPath(), "celeste-ios-legacy-version-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            object package = await InvokePrivateAsync(
                "DownloadDependencyFromGameBananaAsync",
                new ModDependency { Name = "DashToggleHelper", Version = "0.11.0" },
                path,
                null);
            object module = InvokePrivate("FindModule", package, "DashToggleHelper");
            Require(module != null && GetProperty<string>(module, "Version") == "0.11.0",
                "The resolver should find the archived GameBanana file that satisfies the old map requirement.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task AuditLiveMapDependencyVersionsAsync()
    {
        var requirements = new[]
        {
            new ModDependency { Name = "DashToggleHelper", Version = "0.11.0" },
            new ModDependency { Name = "Etselec", Version = "2.5.1" },
            new ModDependency { Name = "HooliganHelper", Version = "0.1.0" },
            new ModDependency { Name = "AletrisSandbox", Version = "0.1.0" },
            new ModDependency { Name = "BGswitch", Version = "0.1.0" },
            new ModDependency { Name = "DzhakeHelper", Version = "0.3" },
            new ModDependency { Name = "TheSecretOfCelesteMountain", Version = "2.1.2" },
            new ModDependency { Name = "TheSecretOfCelesteMountain", Version = "2.0.1" },
            new ModDependency { Name = "LuaCutscenes", Version = "0.3.0" }
        };
        var catalog = (System.Collections.IDictionary)await InvokePrivateAsync("DownloadUpdateCatalogAsync");
        int resolved = 0;
        foreach (ModDependency dependency in requirements)
        {
            string path = Path.Combine(Path.GetTempPath(), "celeste-ios-map-version-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                object update = catalog[dependency.Name];
                string latest = update == null ? "unlisted" : GetField<string>(update, "Version");
                bool latestSatisfies = update != null && (bool)InvokePrivate("VersionSatisfies", dependency.Version, latest);
                object package = await InvokePrivateAsync("DownloadDependencyFromGameBananaAsync", dependency, path, null);
                object module = InvokePrivate("FindModule", package, dependency.Name);
                string installed = module == null ? "missing" : GetProperty<string>(module, "Version");
                Require(module != null && (bool)InvokePrivate("VersionSatisfies", dependency.Version, installed),
                    "The downloaded historical release should satisfy " + dependency.Name + " " + dependency.Version + ".");
                resolved++;
                Console.WriteLine("PASS historical " + dependency.Name + " " + dependency.Version +
                    " (catalog " + latest + (latestSatisfies ? " compatible" : "; historical " + installed) + ")");
            }
            catch (Exception exception)
            {
                Console.WriteLine("UNRESOLVED " + dependency.Name + " " + dependency.Version + ": " + exception);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        Console.WriteLine("Historical map dependency audit resolved " + resolved + "/" + requirements.Length + ".");
    }

    private static async Task VerifyLiveDependencyAsync(ModDependency dependency)
    {
        string path = Path.Combine(Path.GetTempPath(), "celeste-ios-live-dependency-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            object package = await InvokePrivateAsync("DownloadDependencyFromGameBananaAsync", dependency, path, null);
            object module = InvokePrivate("FindModule", package, dependency.Name);
            string version = module == null ? "missing" : GetProperty<string>(module, "Version");
            Require(module != null && (bool)InvokePrivate("VersionSatisfies", dependency.Version, version),
                "The live GameBanana resolver must return a compatible " + dependency.Name + " package.");
            Console.WriteLine("PASS live dependency " + dependency.Name + " " + version +
                " (required " + dependency.Version + ")");
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
