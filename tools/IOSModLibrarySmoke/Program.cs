using System.IO.Compression;
using System.Reflection;
using celestemeow;

namespace IOSModLibrarySmoke;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        await VerifyDownloadStreamTimeoutsAsync();
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
        VerifyBuiltInDependencyMismatchDiagnostics();
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
            await AuditLiveMapDependencyVersionsAsync(false);
        if (args.Contains("--live-full-graph-version-audit", StringComparer.OrdinalIgnoreCase))
            await AuditLiveMapDependencyVersionsAsync(true);
        if (args.Contains("--live-spark-audio", StringComparer.OrdinalIgnoreCase))
            await VerifyLiveDependencyAsync(new ModDependency { Name = "SparkInTheMachineAudio", Version = "1.0.2" });
        Console.WriteLine("PASS iOS mod dependency and map-package smoke checks");
        return 0;
    }

    private static async Task VerifyDownloadStreamTimeoutsAsync()
    {
        byte[] payload = { 0x43, 0x65, 0x6c, 0x65, 0x73, 0x74, 0x65 };
        using (MemoryStream source = new MemoryStream(payload))
        using (MemoryStream destination = new MemoryStream())
        {
            await IOSModLibrary.CopyWithIdleTimeoutAsync(source, destination, TimeSpan.FromSeconds(1), "smoke.test");
            Require(payload.SequenceEqual(destination.ToArray()),
                "The package downloader should preserve a successful streamed archive body.");
        }

        bool timedOut = false;
        using (StallingStream source = new StallingStream())
        using (MemoryStream destination = new MemoryStream())
        {
            try
            {
                await IOSModLibrary.CopyWithIdleTimeoutAsync(source, destination, TimeSpan.FromMilliseconds(100), "stalled.test");
            }
            catch (TimeoutException exception)
            {
                timedOut = exception.Message.Contains("stalled.test", StringComparison.Ordinal);
            }
        }

        Require(timedOut, "A package source that stops sending bytes should time out with its host identified.");
        Console.WriteLine("PASS mod download streaming and stalled-source timeout");
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WaitForCancellationAsync(cancellationToken);
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            string actualVersion = module == null ? "missing" : GetProperty<string>(module, "Version");
            Console.WriteLine("Live GitHub fallback returned KirbyHelperMechanics " + actualVersion + ".");
            Require(module != null && (bool)InvokePrivate("VersionSatisfies", "3.0.0", actualVersion),
                "The live public release should install a KirbyHelperMechanics module version compatible with 3.0.0.");
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

    private static async Task AuditLiveMapDependencyVersionsAsync(bool includeEveryGraphEntry)
    {
        string[] campaignRoots =
        {
            "Glyph",
            "Into The Jungle",
            "StrawberryJam2021",
            "SecretSantaCollab2024",
            "SecretSantaCollab2025",
            "Even More Legally Distinct Secret Santa",
            "CrossoverCollab"
        };

        Func<string, Task<string>> getSmallText = async url =>
            (string)await InvokePrivateAsync("GetSmallTextAsync", url);
        ModDependencyResolver graph = await ModDependencyResolver.DownloadEverestGraphAsync(getSmallText, null);
        Require(graph.IsAvailable, "Everest's current dependency graph could not be loaded.");

        FieldInfo graphField = typeof(ModDependencyResolver).GetField("graph", BindingFlags.NonPublic | BindingFlags.Instance);
        Require(graphField != null, "The dependency graph index is unavailable to the live audit.");
        var graphEntries = (System.Collections.IDictionary)graphField.GetValue(graph);
        string[] roots = includeEveryGraphEntry
            ? graphEntries.Keys.Cast<string>().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
            : campaignRoots;
        var allRootDependencies = new List<ModDependency>();
        var failures = new List<string>();
        int optionalDependencyEdges = 0;

        foreach (string campaignRoot in roots)
        {
            string[] graphNames = includeEveryGraphEntry
                ? graphEntries.Contains(campaignRoot) ? new[] { campaignRoot } : Array.Empty<string>()
                : graphEntries.Keys.Cast<string>()
                    .Where(name => ModDependencyResolver.NormalizeName(name) == ModDependencyResolver.NormalizeName(campaignRoot))
                    .ToArray();
            if (graphNames.Length != 1)
            {
                failures.Add(campaignRoot + ": expected one Everest graph entry, found " + graphNames.Length + ".");
                continue;
            }

            List<ModDependency> direct = new List<ModDependency>();
            List<ModDependency> optional = new List<ModDependency>();
            graph.ApplyTo(graphNames[0], direct, optional);
            IReadOnlyList<ModDependency> closure = graph.ResolveRequiredClosure(direct);
            optionalDependencyEdges += optional.Count;
            allRootDependencies.AddRange(direct);
            if (!includeEveryGraphEntry)
                Console.WriteLine("ROOT " + graphNames[0] + ": " + direct.Count + " direct required, " +
                    closure.Count + " transitive required, " + optional.Count + " optional.");
        }

        if (includeEveryGraphEntry)
            Console.WriteLine("Everest dependency graph entries: " + roots.Length + ".");
        Require(failures.Count == 0, "One or more requested roots are missing from Everest's graph: " + string.Join(" | ", failures));
        List<ModDependency> requiredClosure = graph.ResolveRequiredClosure(allRootDependencies).ToList();
        System.Collections.IDictionary catalog =
            (System.Collections.IDictionary)await InvokePrivateAsync("DownloadUpdateCatalogAsync");
        int builtInCount = 0;
        int verifiedCatalogCount = 0;
        int historicalFallbackCount = 0;

        foreach (ModDependency dependency in requiredClosure)
        {
            if ((bool)InvokePrivate("IsBuiltInDependency", dependency.Name))
            {
                if (!(bool)InvokePrivate("IsBuiltInDependencySatisfied", dependency))
                    failures.Add(dependency.Name + " " + dependency.Version + ": the bundled game/Everest version is too old.");
                else
                    builtInCount++;
                continue;
            }

            object update = FindCatalogUpdate(catalog, dependency.Name);
            string latest = update == null ? null : GetField<string>(update, "Version");
            string url = update == null ? null : GetField<string>(update, "Url");
            string mirror = update == null ? null : GetField<string>(update, "MirrorName");
            List<string> hashes = update == null ? null : GetField<List<string>>(update, "Hashes");
            bool hasHttpsUrl = Uri.TryCreate(url, UriKind.Absolute, out Uri parsedUrl) && parsedUrl.Scheme == Uri.UriSchemeHttps;
            bool hasVerifiedSource = hashes != null && hashes.Count > 0 && (hasHttpsUrl || !string.IsNullOrWhiteSpace(mirror));
            bool catalogSatisfies = update != null && !string.IsNullOrWhiteSpace(latest) &&
                (bool)InvokePrivate("VersionSatisfies", dependency.Version, latest);

            if (hasVerifiedSource && catalogSatisfies)
            {
                verifiedCatalogCount++;
                continue;
            }

            Console.WriteLine("CHECKING ARCHIVED FALLBACK " + dependency.Name + " " + dependency.Version + ".");
            string path = Path.Combine(Path.GetTempPath(), "celeste-ios-map-version-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                object package = await InvokePrivateAsync("DownloadDependencyFromGameBananaAsync", dependency, path, null);
                object module = InvokePrivate("FindModule", package, dependency.Name);
                string installed = module == null ? "missing" : GetProperty<string>(module, "Version");
                Require(module != null && (bool)InvokePrivate("VersionSatisfies", dependency.Version, installed),
                    "No archived package satisfies " + dependency.Name + " " + dependency.Version + ".");
                historicalFallbackCount++;
            }
            catch (Exception exception)
            {
                string reason = exception.GetBaseException().Message;
                failures.Add(dependency.Name + " " + dependency.Version + ": " + reason);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        Console.WriteLine("Required packages: " + requiredClosure.Count + "; built in: " + builtInCount +
            "; current verified catalog versions: " + verifiedCatalogCount + "; archived fallbacks downloaded: " +
            historicalFallbackCount + "; optional edges retained: " + optionalDependencyEdges + ".");
        if (failures.Count != 0)
        {
            foreach (string failure in failures)
                Console.WriteLine("UNRESOLVED " + failure);
            throw new InvalidOperationException("Everest's live dependency audit has " + failures.Count + " unresolved requirement(s).");
        }

        Console.WriteLine(includeEveryGraphEntry
            ? "PASS live Everest full dependency graph and catalog audit."
            : "PASS live Everest campaign graph and dependency catalog audit.");
    }

    private static object FindCatalogUpdate(System.Collections.IDictionary catalog, string moduleName)
    {
        if (catalog.Contains(moduleName)) return catalog[moduleName];
        string normalized = ModDependencyResolver.NormalizeName(moduleName);
        if (normalized.Length == 0) return null;
        List<object> matches = catalog.Keys.Cast<object>()
            .Where(key => key is string name && ModDependencyResolver.NormalizeName(name) == normalized)
            .Select(key => catalog[key])
            .Where(value => value != null)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
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

    private static void VerifyBuiltInDependencyMismatchDiagnostics()
    {
        ModDependency celesteDependency = new ModDependency { Name = "Celeste", Version = "1.5.0" };
        Require(!(bool)InvokePrivate("IsBuiltInDependencySatisfied", celesteDependency),
            "A mod requiring a newer Celeste base must not be treated as installed.");
        string celesteMessage = (string)InvokePrivate("DescribeBuiltInDependencyMismatch", celesteDependency);
        Require(celesteMessage.Contains("Celeste 1.5.0", StringComparison.Ordinal) &&
            celesteMessage.Contains("includes Celeste 1.4.0.0", StringComparison.Ordinal),
            "A newer Celeste requirement should explain the bundled version and recommend a compatible release.");

        ModDependency compatibleEverest = new ModDependency { Name = "Everest", Version = "1.2.0" };
        Require((bool)InvokePrivate("IsBuiltInDependencySatisfied", compatibleEverest),
            "A compatible Everest 1.x requirement should be satisfied by the bundled Everest core.");
        ModDependency newerEverest = new ModDependency { Name = "Everest", Version = "2.0.0" };
        string everestMessage = (string)InvokePrivate("DescribeBuiltInDependencyMismatch", newerEverest);
        Require(!(bool)InvokePrivate("IsBuiltInDependencySatisfied", newerEverest) &&
            everestMessage.Contains("includes Everest 1.6696.0", StringComparison.Ordinal),
            "An incompatible Everest major version should show the bundled core version.");
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
