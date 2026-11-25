using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace celestemeow
{
    internal sealed class InstalledMod
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Enabled { get; set; }
        public bool Supported { get; set; }
        public bool DependenciesMissing { get; set; }
        public int MapCount { get; set; }
        public string Status { get; set; }
    }

    internal sealed class CatalogMod
    {
        public string Name { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public string FileDescription { get; set; }
        public string Url { get; set; }
        public string MirrorName { get; set; }
        public string DownloadName { get; set; }
        public long Size { get; set; }
        public long GameBananaId { get; set; }

        public string DisplayName
        {
            get
            {
                return string.IsNullOrWhiteSpace(FileDescription) || string.Equals(FileDescription, Name, StringComparison.OrdinalIgnoreCase)
                    ? Name
                    : Name + " — " + FileDescription;
            }
        }
    }

    internal static class IOSModLibrary
    {
        private sealed class ModMetadata
        {
            public ModMetadata() { }
            public string Name { get; set; }
            public string Version { get; set; }
            public string DLL { get; set; }
            public List<ModDependency> Dependencies { get; set; } = new List<ModDependency>();
            public List<ModDependency> OptionalDependencies { get; set; } = new List<ModDependency>();
        }

        private sealed class UpdateInfo
        {
            public string Name;
            public string Version;
            public string Url;
            public string MirrorName;
            public List<string> Hashes = new List<string>();
        }

        private sealed class UpdateCatalogEntry
        {
            public UpdateCatalogEntry() { }
            public string Version { get; set; }
            public string URL { get; set; }
            public string MirrorName { get; set; }
            public List<string> xxHash { get; set; } = new List<string>();
        }

        private sealed class PackageInfo
        {
            public string Name;
            public int MapCount;
            public int AssemblyCount;
            public int AssetCount;
            public string Status;
            public bool ManifestFallback;
            public List<ModMetadata> Modules = new List<ModMetadata>();
        }

        private sealed class InstalledPackage
        {
            public string Path;
            public PackageInfo Package;
        }

        private sealed class GameBananaFile
        {
            public string Name;
            public string Description;
            public string Url;
            public string Md5;
            public string MirrorName;
            public long Size;
            public long Added;
        }

        private sealed class GitHubRepository
        {
            public string FullName;
            public string Name;
            public string Url;
        }

        private sealed class GitHubReleaseAsset
        {
            public string Repository;
            public string Name;
            public string Url;
            public string Digest;
            public long Size;
        }

        private static readonly IDeserializer MetadataDeserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly object CommunityCatalogLock = new object();
        private static List<CatalogMod> communityModCatalogCache;
        private const int MaxDependencySearchCandidates = 12;
        private const int MaxGitHubRepositories = 8;
        private const int MaxGitHubReleaseAssets = 8;
        private const int CelesteGameBananaId = 6460;
        private static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan DownloadHeaderTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);
        private const string CommunityModCatalogUrl = "https://maddie480.ovh/celeste/custom-entity-catalog.json";
        private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CelesteMods");
        private static readonly string ModsDirectory = Path.Combine(Root, "Mods");
        private static readonly string AutoUpdateSettingPath = Path.Combine(Root, "auto-update.txt");
        private static ModDependencyResolver dependencyGraphCache = ModDependencyResolver.LoadCached(DependencyGraphCachePath());

        public static string ModsPath { get { return ModsDirectory; } }
        public static string DataPath { get { return Root; } }

        public static void PrepareForLaunch()
        {
            Directory.CreateDirectory(ModsDirectory);
            List<InstalledMod> installed = GetInstalledMods();
            string blacklistPath = Path.Combine(ModsDirectory, "blacklist.txt");
            List<string> blacklist = new List<string>
            {
                "# Disabled by the Celeste iOS mod manager."
            };
            blacklist.AddRange(installed
                .Where(mod => !mod.Enabled || !mod.Supported)
                .Select(mod => Path.GetFileName(mod.Path))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(blacklistPath, blacklist);
        }

        public static bool AutoUpdateOnLaunch
        {
            get
            {
                return !File.Exists(AutoUpdateSettingPath) || File.ReadAllText(AutoUpdateSettingPath).Trim() != "0";
            }
        }

        public static void SetAutoUpdateOnLaunch(bool enabled)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(AutoUpdateSettingPath, enabled ? "1" : "0");
        }

        public static async Task ResolveEnabledDependenciesAsync(IProgress<string> progress)
        {
            dependencyGraphCache = ModDependencyResolver.Empty;
            Dictionary<string, InstalledPackage> installed = LoadInstalledPackages();
            HashSet<string> enabled = ReadEnabledNames();
            List<PackageInfo> roots = installed.Values
                .Select(item => item.Package)
                .Where(package => enabled.Contains(package.Name))
                .Distinct()
                .ToList();
            if (roots.Count == 0) return;

            dependencyGraphCache = await ModDependencyResolver.DownloadEverestGraphAsync(GetSmallTextAsync, progress, DependencyGraphCachePath());
            if (!dependencyGraphCache.IsAvailable)
                throw new IOException("Celeste couldn't verify the required mod list. Connect to the internet and try again.");
            installed = LoadInstalledPackages();
            roots = installed.Values
                .Select(item => item.Package)
                .Where(package => enabled.Contains(package.Name))
                .Distinct()
                .ToList();

            bool needsCatalog = roots.Any(package => HasUnsatisfiedDependencies(package, package, installed));
            Dictionary<string, UpdateInfo> catalog = needsCatalog
                ? await DownloadUpdateCatalogAsync()
                : new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> installedNames = new List<string>();
            foreach (PackageInfo package in roots)
                foreach (ModMetadata module in package.Modules)
                    foreach (ModDependency dependency in module.Dependencies)
                        await EnsureDependencyAsync(dependency, package, installed, enabled, catalog, resolving, installedNames, progress);
            SaveEnabledNames(enabled);
        }

        public static void PrepareLoadOrder()
        {
            Dictionary<string, InstalledPackage> installed = LoadInstalledPackages();
            HashSet<string> enabled = ReadEnabledNames();
            List<PackageInfo> roots = installed.Values
                .Select(item => item.Package)
                .Where(package => enabled.Contains(package.Name))
                .Distinct()
                .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> order = new List<string>();
            HashSet<string> visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageInfo package in roots) VisitPackage(package, installed, enabled, visiting, visited, order);
            SaveEnabledNames(enabled);
            Directory.CreateDirectory(Root);
            File.WriteAllLines(Path.Combine(Root, "load-order.txt"), order.Select(name => Convert.ToBase64String(Encoding.UTF8.GetBytes(name))).ToArray());
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient();
            // Each metadata request and each download phase has its own bounded timeout.
            // A total HttpClient timeout would incorrectly abort large campaigns that are
            // still making steady progress.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Celeste-iOS-Mod-Manager/1.0");
            return client;
        }

        public static List<InstalledMod> GetInstalledMods()
        {
            Directory.CreateDirectory(ModsDirectory);
            HashSet<string> enabled = ReadEnabledNames();
            string[] files = Directory.GetFiles(ModsDirectory, "*.zip");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            List<InstalledMod> result = new List<InstalledMod>();
            Dictionary<string, PackageInfo> packagesByPath = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                PackageInfo info;
                try
                {
                    info = InspectArchive(path);
                    ApplyDependencyGraph(info, dependencyGraphCache);
                }
                catch (Exception exception)
                {
                    result.Add(new InstalledMod
                    {
                        Name = Path.GetFileNameWithoutExtension(path),
                        Path = path,
                        Enabled = false,
                        Supported = false,
                        MapCount = 0,
                        Status = "Could not read package: " + exception.Message
                    });
                    continue;
                }

                result.Add(new InstalledMod
                {
                    Name = info.Name,
                    Path = path,
                    Enabled = enabled.Contains(info.Name),
                    Supported = true,
                    MapCount = info.MapCount,
                    Status = info.Status
                });
                packagesByPath[path] = info;
            }

            if (!File.Exists(EnabledPath()))
            {
                SaveEnabledNames(result.Select(mod => mod.Name));
                foreach (InstalledMod mod in result) mod.Enabled = true;
            }

            Dictionary<string, InstalledPackage> installedPackages = new Dictionary<string, InstalledPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PackageInfo> entry in packagesByPath) IndexPackage(installedPackages, entry.Key, entry.Value);
            foreach (InstalledMod mod in result)
            {
                if (!mod.Enabled) continue;
                PackageInfo package;
                if (!packagesByPath.TryGetValue(mod.Path, out package)) continue;
                List<string> missing;
                try
                {
                    missing = GetMissingDependencies(package, installedPackages);
                }
                catch (InvalidDataException exception)
                {
                    missing = new List<string> { "dependency data needs attention: " + exception.Message };
                }
                if (missing.Count > 0)
                {
                    mod.DependenciesMissing = true;
                    mod.Status += " · missing: " + string.Join(", ", missing);
                }
            }
            return result;
        }

        public static async Task<List<CatalogMod>> SearchCatalogAsync(string query)
        {
            string url = string.IsNullOrWhiteSpace(query)
                ? "https://maddie480.ovh/celeste/gamebanana-list?page=1&sort=downloads"
                : "https://maddie480.ovh/celeste/gamebanana-search?q=" + Uri.EscapeDataString(query.Trim());
            string json = await GetSmallTextAsync(url);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                List<CatalogMod> result = new List<CatalogMod>();
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    string name = GetString(item, "Name");
                    string author = GetString(item, "Author");
                    string description = GetString(item, "Description");
                    if (!item.TryGetProperty("Files", out JsonElement files) || files.ValueKind != JsonValueKind.Array) continue;
                    foreach (JsonElement file in files.EnumerateArray())
                    {
                        if (!GetBool(file, "HasEverestYaml") || !GetBool(file, "IsLatestVersion")) continue;
                        string fileUrl = GetString(file, "URL");
                        if (string.IsNullOrWhiteSpace(fileUrl)) continue;
                        result.Add(new CatalogMod
                        {
                            Name = name,
                            Author = author,
                            Description = description,
                            FileDescription = GetString(file, "Description"),
                            Url = fileUrl,
                            MirrorName = GetString(file, "MirrorName"),
                            DownloadName = GetString(file, "Name"),
                            Size = GetLong(file, "Size")
                        });
                    }
                }
                return result.Take(100).ToList();
            }
        }

        public static async Task<string> InstallAsync(CatalogMod catalogMod, IProgress<string> progress)
        {
            Directory.CreateDirectory(ModsDirectory);
            string tempPath = Path.Combine(Root, "download-" + Guid.NewGuid().ToString("N") + ".zip.part");
            try
            {
                await DownloadWithMirrorsAsync(catalogMod.Url, catalogMod.MirrorName, tempPath, progress);
                PackageInfo package = InspectArchive(tempPath, catalogMod.Name);
                HashSet<string> enabled = ReadEnabledNames();
                List<string> dependenciesInstalled = await InstallRequiredDependenciesAsync(package, enabled, progress);
                string destination = Path.Combine(ModsDirectory, SafeFileName(package.Name) + ".zip");
                ReplaceArchive(tempPath, destination);
                enabled.Add(package.Name);
                SaveEnabledNames(enabled);
                string dependencyMessage = dependenciesInstalled.Count == 0 ? "" : " Also installed required dependencies: " + string.Join(", ", dependenciesInstalled) + ".";
                return "Installed and enabled " + package.Name + "." + dependencyMessage + " " + package.Status;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        public static async Task<List<string>> SetEnabledAsync(string name, bool enabled, IProgress<string> progress)
        {
            await RefreshDependencyGraphAsync(progress);
            InstalledMod mod = GetInstalledMods().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (mod == null) throw new FileNotFoundException("That mod is not installed.");
            HashSet<string> names = ReadEnabledNames();
            List<string> alsoDisabled = new List<string>();
            if (enabled)
            {
                names.Add(mod.Name);
            }
            else
            {
                names.Remove(mod.Name);
                Dictionary<string, InstalledPackage> installed = LoadInstalledPackages();
                InstalledPackage removedPackage;
                if (installed.TryGetValue(mod.Name, out removedPackage))
                    alsoDisabled = DisableRequiredDependents(removedPackage.Package, installed, names);
            }
            SaveEnabledNames(names);
            return alsoDisabled.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> DisableRequiredDependents(PackageInfo removedPackage, Dictionary<string, InstalledPackage> installed, HashSet<string> enabled)
        {
            List<ModDependencyPackageState> packages = installed.Values
                .Distinct()
                .Select(candidate => new ModDependencyPackageState
                {
                    PackageName = candidate.Package.Name,
                    ModuleNames = candidate.Package.Modules.Select(module => module.Name).ToList(),
                    RequiredModuleNames = candidate.Package.Modules
                        .SelectMany(module => module.Dependencies)
                        .Where(dependency => dependency != null)
                        .Select(dependency => dependency.Name)
                        .ToList()
                })
                .ToList();
            return ModDependencyResolver.DisableEnabledDependents(
                removedPackage.Modules.Select(module => module.Name), packages, enabled);
        }

        public static async Task<List<string>> RemoveAsync(InstalledMod mod, IProgress<string> progress)
        {
            if (mod == null) return new List<string>();
            await RefreshDependencyGraphAsync(progress);
            Dictionary<string, InstalledPackage> installed = LoadInstalledPackages();
            InstalledPackage removedPackage;
            HashSet<string> names = ReadEnabledNames();
            names.Remove(mod.Name);
            List<string> alsoDisabled = installed.TryGetValue(mod.Name, out removedPackage)
                ? DisableRequiredDependents(removedPackage.Package, installed, names)
                : new List<string>();
            if (File.Exists(mod.Path)) File.Delete(mod.Path);
            SaveEnabledNames(names);
            return alsoDisabled;
        }

        private static async Task RefreshDependencyGraphAsync(IProgress<string> progress)
        {
            dependencyGraphCache = await ModDependencyResolver.DownloadEverestGraphAsync(
                GetSmallTextAsync, progress, DependencyGraphCachePath());
            if (!dependencyGraphCache.IsAvailable)
                throw new IOException("Celeste couldn't verify the mod dependencies. Connect to the internet and try again; no enabled mods were changed.");
        }

        public static async Task<string> UpdateAllAsync(IProgress<string> progress)
        {
            List<InstalledMod> installed = GetInstalledMods();
            if (installed.Count == 0) return "No mods are installed.";
            Dictionary<string, UpdateInfo> catalog = await DownloadUpdateCatalogAsync();
            int available = 0;
            int updated = 0;
            foreach (InstalledMod mod in installed)
            {
                PackageInfo info = InspectArchive(mod.Path);
                UpdateInfo update;
                if (!catalog.TryGetValue(info.Name, out update) || update.Hashes.Count == 0) continue;
                string localHash = ComputeXxHash64(mod.Path);
                if (update.Hashes.Any(hash => string.Equals(hash, localHash, StringComparison.OrdinalIgnoreCase))) continue;
                available++;
                progress?.Report("Updating " + info.Name + "...");
                string tempPath = Path.Combine(Root, "update-" + Guid.NewGuid().ToString("N") + ".zip.part");
                try
                {
                    await DownloadWithMirrorsAsync(update.Url, update.MirrorName, tempPath, progress);
                    string downloadedHash = ComputeXxHash64(tempPath);
                    if (!update.Hashes.Any(hash => string.Equals(hash, downloadedHash, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("The downloaded update did not match Everest's checksum for " + info.Name + ".");
                    PackageInfo replacement = InspectArchive(tempPath, info.Name);
                    if (!string.Equals(replacement.Name, info.Name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The update package name did not match the installed mod.");
                    string backupPath = mod.Path + ".previous";
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(mod.Path, backupPath);
                    try
                    {
                        File.Move(tempPath, mod.Path);
                        File.Delete(backupPath);
                    }
                    catch
                    {
                        if (File.Exists(mod.Path)) File.Delete(mod.Path);
                        if (File.Exists(backupPath)) File.Move(backupPath, mod.Path);
                        throw;
                    }
                    updated++;
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
            }
            return available == 0 ? "All installed mods are up to date." : "Updated " + updated + " of " + available + " available update(s).";
        }

        private static async Task<Dictionary<string, UpdateInfo>> DownloadUpdateCatalogAsync()
        {
            string updateUrl;
            try
            {
                updateUrl = (await GetSmallTextAsync("https://everestapi.github.io/modupdater.txt")).Trim();
                if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out Uri parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("Everest returned an invalid update database address.");
            }
            catch
            {
                updateUrl = "https://everestapi.github.io/updatermirror/everest_update.yaml";
            }

            try
            {
                string yaml = await GetSmallTextAsync(updateUrl);
                return ParseUpdateCatalog(yaml);
            }
            catch when (updateUrl != "https://everestapi.github.io/updatermirror/everest_update.yaml")
            {
                string yaml = await GetSmallTextAsync("https://everestapi.github.io/updatermirror/everest_update.yaml");
                return ParseUpdateCatalog(yaml);
            }
        }

        private static Dictionary<string, UpdateInfo> ParseUpdateCatalog(string yaml)
        {
            Dictionary<string, UpdateCatalogEntry> entries;
            try
            {
                entries = MetadataDeserializer.Deserialize<Dictionary<string, UpdateCatalogEntry>>(yaml)
                    ?? new Dictionary<string, UpdateCatalogEntry>();
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Everest's update catalog could not be parsed.", exception);
            }

            Dictionary<string, UpdateInfo> result = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, UpdateCatalogEntry> entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null) continue;
                result[entry.Key] = new UpdateInfo
                {
                    Name = entry.Key,
                    Version = entry.Value.Version,
                    Url = entry.Value.URL,
                    MirrorName = entry.Value.MirrorName,
                    Hashes = (entry.Value.xxHash ?? new List<string>())
                        .Where(hash => !string.IsNullOrWhiteSpace(hash))
                        .Select(hash => hash.Trim())
                        .ToList()
                };
            }
            return result;
        }

        private static async Task<List<string>> InstallRequiredDependenciesAsync(PackageInfo rootPackage, HashSet<string> enabled, IProgress<string> progress)
        {
            Directory.CreateDirectory(ModsDirectory);
            await RefreshDependencyGraphAsync(progress);
            ApplyDependencyGraph(rootPackage, dependencyGraphCache);
            Dictionary<string, InstalledPackage> installed = LoadInstalledPackages();
            Dictionary<string, UpdateInfo> catalog = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
            if (HasUnsatisfiedDependencies(rootPackage, rootPackage, installed))
            {
                try
                {
                    catalog = await DownloadUpdateCatalogAsync();
                }
                catch (Exception exception)
                {
                    progress?.Report("Everest's verified update catalog is unavailable; searching GameBanana for required packages.");
                    Console.WriteLine("Everest update catalog unavailable during dependency install: " + exception);
                }
            }
            HashSet<string> resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> installedNames = new List<string>();
            foreach (ModMetadata module in rootPackage.Modules)
            {
                foreach (ModDependency dependency in module.Dependencies)
                    await EnsureDependencyAsync(dependency, rootPackage, installed, enabled, catalog, resolving, installedNames, progress);
            }
            SaveEnabledNames(enabled);
            return installedNames;
        }

        private static async Task EnsureDependencyAsync(
            ModDependency dependency,
            PackageInfo pendingPackage,
            Dictionary<string, InstalledPackage> installed,
            HashSet<string> enabled,
            Dictionary<string, UpdateInfo> catalog,
            HashSet<string> resolving,
            List<string> installedNames,
            IProgress<string> progress)
        {
            if (dependency == null || string.IsNullOrWhiteSpace(dependency.Name)) return;
            if (IsBuiltInDependency(dependency.Name))
            {
                if (!IsBuiltInDependencySatisfied(dependency))
                    throw new InvalidDataException(DescribeBuiltInDependencyMismatch(dependency));
                return;
            }

            ModMetadata pendingModule = FindModule(pendingPackage, dependency.Name);
            if (pendingModule != null && VersionSatisfies(dependency.Version, pendingModule.Version)) return;

            InstalledPackage current;
            if (TryGetInstalledPackage(installed, dependency.Name, out current))
            {
                ModMetadata installedModule = FindModule(current.Package, dependency.Name);
                if (installedModule != null && VersionSatisfies(dependency.Version, installedModule.Version))
                {
                    if (!resolving.Add(dependency.Name))
                        throw new InvalidDataException("The required mod dependencies contain a cycle at " + dependency.Name + ".");
                    try
                    {
                        foreach (ModMetadata module in current.Package.Modules)
                            foreach (ModDependency child in module.Dependencies)
                                await EnsureDependencyAsync(child, current.Package, installed, enabled, catalog, resolving, installedNames, progress);
                    }
                    finally
                    {
                        resolving.Remove(dependency.Name);
                    }
                    enabled.Add(current.Package.Name);
                    return;
                }
            }

            if (!resolving.Add(dependency.Name))
                throw new InvalidDataException("The required mod dependencies contain a cycle at " + dependency.Name + ".");

            try
            {
                UpdateInfo update;
                bool hasVerifiedUpdate = TryGetCatalogUpdate(catalog, dependency.Name, out update) &&
                    update != null && update.Hashes != null && update.Hashes.Count > 0;

                progress?.Report("Installing required dependency " + dependency.Name + "...");
                string tempPath = Path.Combine(Root, "dependency-" + Guid.NewGuid().ToString("N") + ".zip.part");
                try
                {
                    PackageInfo package;
                    ModMetadata downloadedModule;
                    if (hasVerifiedUpdate)
                    {
                        await DownloadWithMirrorsAsync(update.Url, update.MirrorName, tempPath, progress);
                        string hash = ComputeXxHash64(tempPath);
                        if (!update.Hashes.Any(expected => string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException("The dependency download checksum did not match Everest's catalog for " + dependency.Name + ".");
                        package = InspectArchive(tempPath, dependency.Name);

                        downloadedModule = FindModule(package, dependency.Name);
                        if (downloadedModule == null || !VersionSatisfies(dependency.Version, downloadedModule.Version))
                        {
                            progress?.Report(
                                "Everest's current " + dependency.Name + " package does not satisfy version " + dependency.Version + "; checking compatible archived releases...");
                            package = await DownloadDependencyFromGameBananaAsync(dependency, tempPath, progress);
                        }
                    }
                    else
                    {
                        package = await DownloadDependencyFromGameBananaAsync(dependency, tempPath, progress);
                    }

                    ApplyDependencyGraph(package, dependencyGraphCache);
                    downloadedModule = FindModule(package, dependency.Name);
                    if (downloadedModule == null || !VersionSatisfies(dependency.Version, downloadedModule.Version))
                        throw new InvalidDataException("The downloaded package does not contain a compatible version of " + dependency.Name + ".");

                    foreach (ModMetadata module in package.Modules)
                    {
                        foreach (ModDependency child in module.Dependencies)
                            await EnsureDependencyAsync(child, package, installed, enabled, catalog, resolving, installedNames, progress);
                    }

                    string previousPackageName = current == null ? null : current.Package.Name;
                    string destination = current != null && File.Exists(current.Path)
                        ? current.Path
                        : Path.Combine(ModsDirectory, SafeFileName(package.Name) + ".zip");
                    ReplaceArchive(tempPath, destination);
                    if (!string.IsNullOrWhiteSpace(previousPackageName) && !string.Equals(previousPackageName, package.Name, StringComparison.OrdinalIgnoreCase))
                        enabled.Remove(previousPackageName);
                    if (current != null)
                        foreach (ModMetadata previousModule in current.Package.Modules) installed.Remove(previousModule.Name);
                    IndexPackage(installed, destination, package);
                    enabled.Add(package.Name);
                    installedNames.Add(package.Name);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
            }
            finally
            {
                resolving.Remove(dependency.Name);
            }
        }

        private static async Task<PackageInfo> DownloadDependencyFromGameBananaAsync(
            ModDependency dependency,
            string tempPath,
            IProgress<string> progress)
        {
            Exception gameBananaError;
            try
            {
                return await DownloadDependencyFromGameBananaCatalogAsync(dependency, tempPath, progress);
            }
            catch (Exception exception)
            {
                gameBananaError = exception;
                progress?.Report("No compatible package was found in GameBanana search; checking the Celeste mod index...");
            }

            Exception communityError;
            try
            {
                List<CatalogMod> communityCandidates = await SearchCommunityModCatalogAsync(dependency.Name, progress);
                if (communityCandidates.Count == 0)
                    throw new FileNotFoundException("The Celeste mod index has no exact GameBanana page for " + dependency.Name + ".");

                Exception lastCommunityError = null;
                foreach (CatalogMod candidate in communityCandidates)
                {
                    try
                    {
                        progress?.Report("Checking the listed GameBanana page for " + dependency.Name + "...");
                        return await DownloadDependencyFromGameBananaItemAsync(candidate, dependency, tempPath, progress);
                    }
                    catch (Exception exception)
                    {
                        lastCommunityError = exception;
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    }
                }

                throw new FileNotFoundException(
                    "The listed GameBanana page did not provide a compatible " + dependency.Name + " package.",
                    lastCommunityError);
            }
            catch (Exception exception)
            {
                communityError = exception;
                progress?.Report("No compatible package was found in the Celeste mod index; checking GitHub releases...");
            }

            try
            {
                return await DownloadDependencyFromGitHubAsync(dependency, tempPath, progress);
            }
            catch (Exception githubError)
            {
                throw new FileNotFoundException(
                    "No compatible public package for " + dependency.Name + " was found in Everest's verified catalog, GameBanana, the Celeste mod index, or GitHub releases.",
                    new AggregateException(gameBananaError, communityError, githubError));
            }
        }

        private static async Task<PackageInfo> DownloadDependencyFromGameBananaCatalogAsync(
            ModDependency dependency,
            string tempPath,
            IProgress<string> progress)
        {
            List<CatalogMod> searchResults = new List<CatalogMod>();
            Exception lastSearchError = null;
            foreach (string query in GetDependencySearchQueries(dependency.Name))
            {
                try
                {
                    searchResults.AddRange(await SearchCatalogAsync(query));
                }
                catch (Exception exception)
                {
                    lastSearchError = exception;
                }

                try
                {
                    searchResults.AddRange(await SearchGameBananaCatalogAsync(query));
                }
                catch (Exception exception)
                {
                    lastSearchError = exception;
                }
            }

            if (searchResults.Count == 0 && lastSearchError != null)
                throw new IOException(
                    "Everest has no verified download for " + dependency.Name + ", and GameBanana search is unavailable.",
                    lastSearchError);

            string normalizedName = ModDependencyResolver.NormalizeName(dependency.Name);
            List<CatalogMod> candidates = searchResults
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Url))
                .GroupBy(candidate => candidate.GameBananaId > 0
                        ? "gamebanana:" + candidate.GameBananaId.ToString(CultureInfo.InvariantCulture)
                        : string.IsNullOrWhiteSpace(candidate.MirrorName) ? candidate.Url : candidate.MirrorName,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select((candidate, index) => new
                {
                    Candidate = candidate,
                    Index = index,
                    Score = GetDependencySearchScore(candidate, normalizedName)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Index)
                .Take(MaxDependencySearchCandidates)
                .Select(item => item.Candidate)
                .ToList();

            if (candidates.Count == 0)
                throw new FileNotFoundException(
                    "Everest has no verified download for " + dependency.Name + ", and GameBanana returned no mod packages for it.");

            Exception lastError = null;
            foreach (CatalogMod candidate in candidates)
            {
                try
                {
                    progress?.Report("Checking " + candidate.DisplayName + " for required module " + dependency.Name + "...");
                    if (candidate.GameBananaId > 0)
                    {
                        PackageInfo directPackage = await DownloadDependencyFromGameBananaItemAsync(
                            candidate, dependency, tempPath, progress);
                        if (directPackage != null) return directPackage;
                        continue;
                    }

                    await DownloadWithMirrorsAsync(candidate.Url, candidate.MirrorName, tempPath, progress);
                    PackageInfo package = InspectArchive(tempPath, candidate.Name);
                    ModMetadata module = FindModule(package, dependency.Name);
                    if (module == null)
                    {
                        lastError = new InvalidDataException(
                            "The package " + candidate.DisplayName + " does not contain module " + dependency.Name + ".");
                    }
                    else if (!VersionSatisfies(dependency.Version, module.Version))
                    {
                        lastError = new InvalidDataException(
                            "The package " + candidate.DisplayName + " provides " + dependency.Name + " " + module.Version +
                            ", which does not satisfy version " + dependency.Version + ".");
                    }
                    else
                    {
                        progress?.Report("Found compatible " + dependency.Name + " " + module.Version + " on GameBanana.");
                        return package;
                    }
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }

                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            throw new FileNotFoundException(
                "No GameBanana package found a compatible " + dependency.Name + " module after checking " + candidates.Count + " candidate(s).",
                lastError);
        }

        private static async Task<List<CatalogMod>> SearchGameBananaCatalogAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<CatalogMod>();
            string url = "https://gamebanana.com/apiv11/Util/Search/Results?_sSearchString=" +
                Uri.EscapeDataString(query.Trim()) + "&_idGameRow=" + CelesteGameBananaId.ToString(CultureInfo.InvariantCulture) +
                "&_sModelName=Mod&_nPage=1&_nPerpage=30";
            return ParseGameBananaSearchResults(await GetSmallTextAsync(url));
        }

        private static async Task<List<CatalogMod>> SearchCommunityModCatalogAsync(
            string dependencyName,
            IProgress<string> progress)
        {
            string normalizedName = ModDependencyResolver.NormalizeName(dependencyName);
            if (normalizedName.Length == 0) return new List<CatalogMod>();

            List<CatalogMod> catalog = await GetCommunityModCatalogAsync(progress);
            return catalog
                .Where(candidate =>
                    ModDependencyResolver.NormalizeName(candidate.DownloadName) == normalizedName ||
                    ModDependencyResolver.NormalizeName(candidate.Name) == normalizedName)
                .GroupBy(candidate => candidate.GameBananaId)
                .Select(group => group.First())
                .ToList();
        }

        private static async Task<List<CatalogMod>> GetCommunityModCatalogAsync(IProgress<string> progress)
        {
            lock (CommunityCatalogLock)
            {
                if (communityModCatalogCache != null) return communityModCatalogCache;
            }

            string cachePath = Path.Combine(Root, "custom-entity-catalog.json");
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromDays(1))
            {
                try
                {
                    List<CatalogMod> cached = ParseCommunityModCatalog(File.ReadAllText(cachePath));
                    if (cached.Count > 0)
                    {
                        lock (CommunityCatalogLock) communityModCatalogCache = cached;
                        return cached;
                    }
                }
                catch
                {
                }
            }

            try
            {
                progress?.Report("Refreshing the Celeste helper and mod page index...");
                string json = await GetSmallTextAsync(CommunityModCatalogUrl);
                List<CatalogMod> parsed = ParseCommunityModCatalog(json);
                if (parsed.Count == 0)
                    throw new InvalidDataException("The Celeste mod index did not contain any usable GameBanana pages.");

                try
                {
                    Directory.CreateDirectory(Root);
                    string temporaryPath = cachePath + ".tmp";
                    File.WriteAllText(temporaryPath, json);
                    File.Move(temporaryPath, cachePath, true);
                }
                catch
                {
                }

                lock (CommunityCatalogLock) communityModCatalogCache = parsed;
                return parsed;
            }
            catch (Exception exception)
            {
                if (File.Exists(cachePath))
                {
                    try
                    {
                        List<CatalogMod> cached = ParseCommunityModCatalog(File.ReadAllText(cachePath));
                        if (cached.Count > 0)
                        {
                            progress?.Report("Using the saved Celeste mod page index while offline.");
                            lock (CommunityCatalogLock) communityModCatalogCache = cached;
                            return cached;
                        }
                    }
                    catch
                    {
                    }
                }

                throw new IOException("The Celeste helper and mod page index could not be loaded.", exception);
            }
        }

        private static List<CatalogMod> ParseCommunityModCatalog(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement mods;
                if (!document.RootElement.TryGetProperty("modInfo", out mods) || mods.ValueKind != JsonValueKind.Array)
                    return new List<CatalogMod>();

                List<CatalogMod> result = new List<CatalogMod>();
                foreach (JsonElement item in mods.EnumerateArray())
                {
                    string moduleName = GetString(item, "modEverestYamlId").Trim();
                    string name = GetString(item, "modName").Trim();
                    string pageUrl = GetString(item, "pageUrl").Trim();
                    Uri parsedUrl;
                    if (string.IsNullOrWhiteSpace(moduleName)) moduleName = name;
                    if (string.IsNullOrWhiteSpace(moduleName) ||
                        !Uri.TryCreate(pageUrl, UriKind.Absolute, out parsedUrl) ||
                        parsedUrl.Scheme != Uri.UriSchemeHttps ||
                        !string.Equals(parsedUrl.Host, "gamebanana.com", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string[] path = parsedUrl.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    long id;
                    if (path.Length != 2 || !string.Equals(path[0], "mods", StringComparison.OrdinalIgnoreCase) ||
                        !long.TryParse(path[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
                        continue;

                    result.Add(new CatalogMod
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? moduleName : name,
                        DownloadName = moduleName,
                        Url = pageUrl,
                        GameBananaId = id
                    });
                }
                return result;
            }
        }

        private static List<CatalogMod> ParseGameBananaSearchResults(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement records;
                if (!document.RootElement.TryGetProperty("_aRecords", out records) || records.ValueKind != JsonValueKind.Array)
                    return new List<CatalogMod>();
                List<CatalogMod> result = new List<CatalogMod>();
                foreach (JsonElement item in records.EnumerateArray())
                {
                    long id = GetLong(item, "_idRow");
                    string name = GetString(item, "_sName");
                    if (id <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                    result.Add(new CatalogMod
                    {
                        Name = name,
                        Url = "https://gamebanana.com/mods/" + id.ToString(CultureInfo.InvariantCulture),
                        GameBananaId = id
                    });
                }
                return result;
            }
        }

        private static async Task<List<GameBananaFile>> GetGameBananaFilesAsync(long gameBananaId)
        {
            string url = "https://api.gamebanana.com/Core/Item/Data?itemtype=Mod&itemid=" +
                gameBananaId.ToString(CultureInfo.InvariantCulture) + "&fields=Files().aFiles()&return_keys=1&format=json";
            return ParseGameBananaFiles(await GetSmallTextAsync(url));
        }

        private static List<GameBananaFile> ParseGameBananaFiles(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement files;
                if (!document.RootElement.TryGetProperty("Files().aFiles()", out files) || files.ValueKind != JsonValueKind.Object)
                    return new List<GameBananaFile>();
                List<GameBananaFile> result = new List<GameBananaFile>();
                foreach (JsonProperty property in files.EnumerateObject())
                {
                    JsonElement file = property.Value;
                    string fileUrl = GetString(file, "_sDownloadUrl");
                    Uri parsedUrl;
                    if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out parsedUrl) || parsedUrl.Scheme != Uri.UriSchemeHttps ||
                        (!string.Equals(parsedUrl.Host, "gamebanana.com", StringComparison.OrdinalIgnoreCase) &&
                         !parsedUrl.Host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    result.Add(new GameBananaFile
                    {
                        Name = GetString(file, "_sFile"),
                        Description = GetString(file, "_sDescription"),
                        Url = fileUrl,
                        Md5 = GetString(file, "_sMd5Checksum"),
                        MirrorName = property.Name,
                        Size = GetLong(file, "_nFilesize"),
                        Added = GetLong(file, "_tsDateAdded")
                    });
                }
                return result.OrderByDescending(file => file.Added).ToList();
            }
        }

        private static List<GameBananaFile> OrderGameBananaFilesForDependency(
            IEnumerable<GameBananaFile> files,
            ModDependency dependency)
        {
            if (files == null) return new List<GameBananaFile>();
            return files
                .Select((file, index) => new
                {
                    File = file,
                    Index = index,
                    Compatibility = GetAdvertisedGameBananaVersionCompatibility(file, dependency)
                })
                .OrderByDescending(item => item.Compatibility)
                .ThenBy(item => item.Index)
                .Select(item => item.File)
                .ToList();
        }

        private static int GetAdvertisedGameBananaVersionCompatibility(GameBananaFile file, ModDependency dependency)
        {
            if (file == null || dependency == null || string.IsNullOrWhiteSpace(dependency.Version)) return 1;
            Match match = Regex.Match(file.Description ?? string.Empty, @"(?<![\d.])v?(\d+(?:\.\d+){1,3})(?![\d.])", RegexOptions.IgnoreCase);
            if (!match.Success) return 1;
            Version advertised = ParseVersion(match.Groups[1].Value, null);
            if (advertised == null) return 1;
            return VersionSatisfies(dependency.Version, advertised.ToString()) ? 2 : 0;
        }

        private static async Task<PackageInfo> DownloadDependencyFromGameBananaItemAsync(
            CatalogMod candidate,
            ModDependency dependency,
            string tempPath,
            IProgress<string> progress)
        {
            List<GameBananaFile> files = await GetGameBananaFilesAsync(candidate.GameBananaId);
            if (files.Count == 0)
                throw new FileNotFoundException("GameBanana has no public download files for " + candidate.Name + ".");

            Exception lastError = null;
            foreach (GameBananaFile file in OrderGameBananaFilesForDependency(files, dependency))
            {
                try
                {
                    progress?.Report("Checking " + file.Name + " from " + candidate.Name + "...");
                    await DownloadWithMirrorsAsync(file.Url, file.MirrorName, tempPath, progress);
                    VerifyGameBananaFileHash(tempPath, file.Md5);
                    PackageInfo package = InspectArchive(tempPath, candidate.Name);
                    ModMetadata module = FindModule(package, dependency.Name);
                    if (module == null)
                        throw new InvalidDataException("The GameBanana file does not contain module " + dependency.Name + ".");
                    if (!VersionSatisfies(dependency.Version, module.Version))
                        throw new InvalidDataException("The GameBanana file provides " + dependency.Name + " " + module.Version +
                            ", which does not satisfy version " + dependency.Version + ".");
                    progress?.Report("Found compatible " + dependency.Name + " " + module.Version + " on GameBanana.");
                    return package;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }

                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }

            throw new InvalidDataException(
                "No public file on the GameBanana page " + candidate.Name + " contains a compatible " + dependency.Name + " module.",
                lastError);
        }

        private static async Task<PackageInfo> DownloadDependencyFromGitHubAsync(
            ModDependency dependency,
            string tempPath,
            IProgress<string> progress)
        {
            List<GitHubRepository> searchResults = new List<GitHubRepository>();
            Exception lastSearchError = null;
            foreach (string query in GetDependencySearchQueries(dependency.Name))
            {
                try
                {
                    searchResults.AddRange(await SearchGitHubRepositoriesAsync(query));
                    if (searchResults.Any(repository =>
                        GetGitHubDependencySearchScore(repository, ModDependencyResolver.NormalizeName(dependency.Name)) == 2))
                        break;
                }
                catch (Exception exception)
                {
                    lastSearchError = exception;
                }
            }

            string normalizedName = ModDependencyResolver.NormalizeName(dependency.Name);
            List<GitHubRepository> candidates = searchResults
                .Where(repository => repository != null && !string.IsNullOrWhiteSpace(repository.FullName))
                .GroupBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select((repository, index) => new
                {
                    Repository = repository,
                    Index = index,
                    Score = GetGitHubDependencySearchScore(repository, normalizedName)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Index)
                .Take(MaxGitHubRepositories)
                .Select(item => item.Repository)
                .ToList();

            if (candidates.Count == 0)
                throw new FileNotFoundException(
                    "GitHub repository search found no likely source for " + dependency.Name + ".",
                    lastSearchError);

            Exception lastError = null;
            foreach (GitHubRepository repository in candidates)
            {
                List<GitHubReleaseAsset> assets;
                try
                {
                    assets = await GetGitHubReleaseAssetsAsync(repository.FullName);
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    continue;
                }

                foreach (GitHubReleaseAsset asset in assets.Take(MaxGitHubReleaseAssets))
                {
                    try
                    {
                        progress?.Report("Checking " + asset.Name + " from " + repository.FullName + "...");
                        await DownloadGitHubReleaseAssetAsync(asset, tempPath, progress);
                        VerifyGitHubAssetSha256(tempPath, asset.Digest);

                        PackageInfo package = InspectArchive(tempPath, repository.Name);
                        ModMetadata module = FindModule(package, dependency.Name);
                        if (module == null)
                        {
                            lastError = new InvalidDataException(
                                "The GitHub release asset does not contain module " + dependency.Name + ".");
                        }
                        else if (!VersionSatisfies(dependency.Version, module.Version))
                        {
                            lastError = new InvalidDataException(
                                "The GitHub release asset provides " + dependency.Name + " " + module.Version +
                                ", which does not satisfy version " + dependency.Version + ".");
                        }
                        else
                        {
                            progress?.Report("Found compatible " + dependency.Name + " " + module.Version + " on GitHub.");
                            return package;
                        }
                    }
                    catch (Exception exception)
                    {
                        lastError = exception;
                    }

                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }

            throw new FileNotFoundException(
                "No public GitHub release asset contained a compatible " + dependency.Name + " module after checking " + candidates.Count + " repository candidate(s).",
                lastError);
        }

        private static async Task<List<GitHubRepository>> SearchGitHubRepositoriesAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<GitHubRepository>();
            string url = "https://api.github.com/search/repositories?q=" +
                Uri.EscapeDataString(query.Trim() + " in:name") + "&per_page=20";
            return ParseGitHubRepositories(await GetGitHubApiJsonAsync(url));
        }

        private static List<GitHubRepository> ParseGitHubRepositories(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement items;
                if (!document.RootElement.TryGetProperty("items", out items) || items.ValueKind != JsonValueKind.Array)
                    return new List<GitHubRepository>();

                List<GitHubRepository> result = new List<GitHubRepository>();
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (GetBool(item, "private")) continue;
                    string fullName = GetString(item, "full_name").Trim();
                    string pageUrl = GetString(item, "html_url").Trim();
                    string[] components = fullName.Split('/');
                    Uri parsedUrl;
                    if (components.Length != 2 ||
                        !Regex.IsMatch(components[0], "^[A-Za-z0-9_.-]+$") ||
                        !Regex.IsMatch(components[1], "^[A-Za-z0-9_.-]+$") ||
                        !Uri.TryCreate(pageUrl, UriKind.Absolute, out parsedUrl) ||
                        parsedUrl.Scheme != Uri.UriSchemeHttps ||
                        !string.Equals(parsedUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(parsedUrl.AbsolutePath.Trim('/'), fullName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(new GitHubRepository
                    {
                        FullName = fullName,
                        Name = components[1],
                        Url = pageUrl
                    });
                }
                return result;
            }
        }

        private static int GetGitHubDependencySearchScore(GitHubRepository repository, string normalizedName)
        {
            if (repository == null || normalizedName.Length == 0) return 0;
            string repositoryName = ModDependencyResolver.NormalizeName(repository.Name);
            if (repositoryName.Length == 0) return 0;
            if (repositoryName == normalizedName) return 2;
            if (repositoryName.Contains(normalizedName) || normalizedName.Contains(repositoryName)) return 1;
            return 0;
        }

        private static async Task<List<GitHubReleaseAsset>> GetGitHubReleaseAssetsAsync(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName) ||
                !Regex.IsMatch(fullName, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
                throw new InvalidDataException("GitHub returned an invalid repository name.");

            string url = "https://api.github.com/repos/" + fullName + "/releases?per_page=10";
            return ParseGitHubReleaseAssets(await GetGitHubApiJsonAsync(url), fullName);
        }

        private static List<GitHubReleaseAsset> ParseGitHubReleaseAssets(string json, string fullName)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return new List<GitHubReleaseAsset>();

                List<GitHubReleaseAsset> result = new List<GitHubReleaseAsset>();
                foreach (JsonElement release in document.RootElement.EnumerateArray())
                {
                    if (GetBool(release, "draft")) continue;
                    JsonElement assets;
                    if (!release.TryGetProperty("assets", out assets) || assets.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string name = GetString(asset, "name").Trim();
                        string url = GetString(asset, "browser_download_url").Trim();
                        string digest = GetString(asset, "digest").Trim();
                        long size = GetLong(asset, "size");
                        Uri parsedUrl;
                        string expectedPathPrefix = "/" + fullName + "/releases/download/";
                        if (string.IsNullOrWhiteSpace(name) ||
                            !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            size <= 0 ||
                            !Uri.TryCreate(url, UriKind.Absolute, out parsedUrl) ||
                            parsedUrl.Scheme != Uri.UriSchemeHttps ||
                            !string.Equals(parsedUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                            !parsedUrl.AbsolutePath.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(digest) && !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")))
                            continue;

                        result.Add(new GitHubReleaseAsset
                        {
                            Repository = fullName,
                            Name = name,
                            Url = url,
                            Digest = digest,
                            Size = size
                        });
                    }
                }
                return result;
            }
        }

        private static async Task<string> GetGitHubApiJsonAsync(string url)
        {
            Uri parsedUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsedUrl) ||
                parsedUrl.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(parsedUrl.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A GitHub API request used an unexpected address.");

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, parsedUrl))
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                using (HttpResponseMessage response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(timeout.Token);
                }
            }
        }

        private static async Task DownloadGitHubReleaseAssetAsync(
            GitHubReleaseAsset asset,
            string destination,
            IProgress<string> progress)
        {
            Uri url;
            if (asset == null ||
                !Uri.TryCreate(asset.Url, UriKind.Absolute, out url) ||
                url.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !url.AbsolutePath.StartsWith("/" + asset.Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub returned an unexpected release-download address.");

            progress?.Report("Downloading from github.com...");
            using (HttpResponseMessage response = await GetResponseWithTimeoutAsync(asset.Url, DownloadHeaderTimeout))
            {
                response.EnsureSuccessStatusCode();
                Uri finalUrl = response.RequestMessage == null ? null : response.RequestMessage.RequestUri;
                if (finalUrl == null ||
                    finalUrl.Scheme != Uri.UriSchemeHttps ||
                    (!string.Equals(finalUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(finalUrl.Host, "githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
                     !finalUrl.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("GitHub redirected the release download to an unexpected host.");

                long? responseSize = response.Content.Headers.ContentLength;
                if (responseSize.HasValue && responseSize.Value != asset.Size)
                    throw new InvalidDataException("The GitHub release file size did not match its API metadata.");

                using (Stream input = await response.Content.ReadAsStreamAsync())
                using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    await CopyWithIdleTimeoutAsync(input, output, DownloadIdleTimeout, finalUrl.Host);
            }

            if (!File.Exists(destination) || new FileInfo(destination).Length != asset.Size)
                throw new InvalidDataException("The GitHub release download was incomplete.");
        }

        private static void VerifyGitHubAssetSha256(string path, string expectedDigest)
        {
            if (string.IsNullOrWhiteSpace(expectedDigest)) return;
            if (!Regex.IsMatch(expectedDigest, "^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("GitHub returned an invalid release checksum.");

            string expectedHash = expectedDigest.Substring("sha256:".Length);
            string actualHash;
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                StringBuilder value = new StringBuilder(64);
                foreach (byte part in sha256.ComputeHash(stream))
                    value.Append(part.ToString("x2", CultureInfo.InvariantCulture));
                actualHash = value.ToString();
            }
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The GitHub release checksum did not match for " + Path.GetFileName(path) + ".");
        }

        private static void VerifyGameBananaFileHash(string path, string expectedMd5)
        {
            if (string.IsNullOrWhiteSpace(expectedMd5)) return;
            if (!Regex.IsMatch(expectedMd5, "^[0-9a-fA-F]{32}$"))
                throw new InvalidDataException("GameBanana returned an invalid file checksum.");
            string actualMd5;
            using (MD5 md5 = MD5.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                StringBuilder value = new StringBuilder(32);
                foreach (byte part in md5.ComputeHash(stream))
                    value.Append(part.ToString("x2", CultureInfo.InvariantCulture));
                actualMd5 = value.ToString();
            }
            if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The GameBanana download checksum did not match for " + Path.GetFileName(path) + ".");
        }

        private static List<string> GetDependencySearchQueries(string dependencyName)
        {
            List<string> queries = new List<string>();
            Action<string> add = query =>
            {
                string trimmed = (query ?? "").Trim();
                if (trimmed.Length > 0 && !queries.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    queries.Add(trimmed);
            };

            string name = (dependencyName ?? "").Trim();
            add(name);
            string separated = name.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
            separated = Regex.Replace(separated, @"(?<=[a-z0-9])(?=[A-Z])", " ");
            separated = Regex.Replace(separated, @"(?<=[A-Z])(?=[A-Z][a-z])", " ");
            separated = Regex.Replace(separated, @"\s+", " ").Trim();
            add(separated);
            add(ModDependencyResolver.NormalizeName(name));
            return queries;
        }

        private static int GetDependencySearchScore(CatalogMod candidate, string normalizedName)
        {
            if (normalizedName.Length == 0) return 0;
            string candidateName = ModDependencyResolver.NormalizeName(candidate.Name);
            if (candidateName.Length == 0) return 0;
            string fileName = Path.GetFileNameWithoutExtension(candidate.DownloadName ?? "");
            if (candidateName == normalizedName ||
                ModDependencyResolver.NormalizeName(candidate.FileDescription) == normalizedName ||
                ModDependencyResolver.NormalizeName(fileName) == normalizedName)
                return 2;
            if (candidateName.Contains(normalizedName) || normalizedName.Contains(candidateName))
                return 1;
            return 0;
        }

        private static Dictionary<string, InstalledPackage> LoadInstalledPackages()
        {
            Dictionary<string, InstalledPackage> result = new Dictionary<string, InstalledPackage>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(ModsDirectory)) return result;
            foreach (string path in Directory.GetFiles(ModsDirectory, "*.zip"))
            {
                try
                {
                    PackageInfo package = InspectArchive(path);
                    ApplyDependencyGraph(package, dependencyGraphCache);
                    IndexPackage(result, path, package);
                }
                catch { }
            }
            return result;
        }

        private static async Task<string> GetSmallTextAsync(string url)
        {
            using (CancellationTokenSource timeout = new CancellationTokenSource(MetadataRequestTimeout))
                return await Http.GetStringAsync(url, timeout.Token);
        }

        private static async Task<HttpResponseMessage> GetResponseWithTimeoutAsync(string url, TimeSpan timeout)
        {
            Uri address;
            string host = Uri.TryCreate(url, UriKind.Absolute, out address) ? address.Host : "the download source";
            using (CancellationTokenSource requestTimeout = new CancellationTokenSource(timeout))
            {
                try
                {
                    return await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    throw new TimeoutException(
                        "No response arrived from " + host + " within " + timeout.TotalSeconds.ToString("0") + " seconds.",
                        exception);
                }
            }
        }

        internal static async Task CopyWithIdleTimeoutAsync(Stream input, Stream output, TimeSpan idleTimeout, string sourceHost)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));

            byte[] buffer = new byte[81920];
            while (true)
            {
                int count;
                using (CancellationTokenSource readTimeout = new CancellationTokenSource(idleTimeout))
                {
                    try
                    {
                        count = await input.ReadAsync(buffer, 0, buffer.Length, readTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception)
                    {
                        string source = string.IsNullOrWhiteSpace(sourceHost) ? "the download source" : sourceHost;
                        throw new TimeoutException(
                            "No download data arrived from " + source + " for " + idleTimeout.TotalSeconds.ToString("0") + " seconds.",
                            exception);
                    }
                }

                if (count == 0) break;
                await output.WriteAsync(buffer, 0, count).ConfigureAwait(false);
            }

            await output.FlushAsync().ConfigureAwait(false);
        }

        private static void ApplyDependencyGraph(PackageInfo package, ModDependencyResolver graph)
        {
            if (package == null || graph == null || !graph.IsAvailable) return;
            foreach (ModMetadata module in package.Modules)
                graph.ApplyTo(module.Name, module.Dependencies, module.OptionalDependencies);
        }

        private static void IndexPackage(Dictionary<string, InstalledPackage> index, string path, PackageInfo package)
        {
            InstalledPackage installed = new InstalledPackage { Path = path, Package = package };
            foreach (ModMetadata module in package.Modules)
                if (!string.IsNullOrWhiteSpace(module.Name)) index[module.Name] = installed;
        }

        private static bool HasUnsatisfiedDependencies(PackageInfo package, PackageInfo pendingPackage, Dictionary<string, InstalledPackage> installed)
        {
            foreach (string missing in GetMissingDependencies(package, installed, pendingPackage))
            {
                if (!string.IsNullOrWhiteSpace(missing)) return true;
            }
            return false;
        }

        private static List<string> GetMissingDependencies(
            PackageInfo package,
            Dictionary<string, InstalledPackage> installed,
            PackageInfo pendingPackage = null)
        {
            List<string> missing = new List<string>();
            List<ModDependency> roots = package.Modules.SelectMany(module => module.Dependencies).ToList();
            IReadOnlyList<ModDependency> closure = dependencyGraphCache.ResolveRequiredClosure(roots, name =>
            {
                ModMetadata pending = FindModule(pendingPackage, name);
                if (pending != null) return pending.Dependencies;
                InstalledPackage candidate;
                if (TryGetInstalledPackage(installed, name, out candidate))
                    return candidate.Package.Modules.SelectMany(module => module.Dependencies);
                return null;
            });

            foreach (ModDependency dependency in closure)
            {
                if (IsBuiltInDependency(dependency.Name))
                {
                    if (!IsBuiltInDependencySatisfied(dependency))
                        missing.Add(DescribeBuiltInDependencyMismatch(dependency));
                    continue;
                }

                ModMetadata pending = FindModule(pendingPackage, dependency.Name);
                if (pending != null && VersionSatisfies(dependency.Version, pending.Version)) continue;

                InstalledPackage candidate;
                ModMetadata actual = TryGetInstalledPackage(installed, dependency.Name, out candidate)
                    ? FindModule(candidate.Package, dependency.Name)
                    : null;
                if (actual == null || !VersionSatisfies(dependency.Version, actual.Version))
                    missing.Add(FormatDependency(dependency));
            }
            return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string FormatDependency(ModDependency dependency)
        {
            return dependency.Name + (string.IsNullOrWhiteSpace(dependency.Version) ? "" : " ≥ " + dependency.Version);
        }

        private static void VisitPackage(
            PackageInfo package,
            Dictionary<string, InstalledPackage> installed,
            HashSet<string> enabled,
            HashSet<string> visiting,
            HashSet<string> visited,
            List<string> order)
        {
            if (visited.Contains(package.Name)) return;
            if (!visiting.Add(package.Name)) throw new InvalidDataException("The enabled mod dependency order contains a cycle at " + package.Name + ".");

            foreach (ModMetadata module in package.Modules)
            {
                foreach (ModDependency dependency in module.Dependencies)
                    VisitDependency(dependency, package, installed, enabled, visiting, visited, order);
                foreach (ModDependency dependency in module.OptionalDependencies)
                {
                    InstalledPackage optional;
                    if (dependency != null && TryGetInstalledPackage(installed, dependency.Name, out optional) && enabled.Contains(optional.Package.Name))
                        VisitDependency(dependency, package, installed, enabled, visiting, visited, order);
                }
            }

            visiting.Remove(package.Name);
            visited.Add(package.Name);
            order.Add(package.Name);
        }

        private static void VisitDependency(
            ModDependency dependency,
            PackageInfo parent,
            Dictionary<string, InstalledPackage> installed,
            HashSet<string> enabled,
            HashSet<string> visiting,
            HashSet<string> visited,
            List<string> order)
        {
            if (dependency == null || string.IsNullOrWhiteSpace(dependency.Name)) return;
            if (IsBuiltInDependency(dependency.Name))
            {
                if (!IsBuiltInDependencySatisfied(dependency))
                    throw new InvalidDataException(parent.Name + ": " + DescribeBuiltInDependencyMismatch(dependency));
                return;
            }
            ModMetadata sameArchive = FindModule(parent, dependency.Name);
            if (sameArchive != null)
            {
                if (!VersionSatisfies(dependency.Version, sameArchive.Version))
                    throw new InvalidDataException(parent.Name + " includes an incompatible " + dependency.Name + " module.");
                return;
            }

            InstalledPackage dependencyPackage;
            if (!TryGetInstalledPackage(installed, dependency.Name, out dependencyPackage))
                throw new InvalidDataException(parent.Name + " requires " + dependency.Name + ", which is not installed.");
            ModMetadata dependencyModule = FindModule(dependencyPackage.Package, dependency.Name);
            if (dependencyModule == null || !VersionSatisfies(dependency.Version, dependencyModule.Version))
                throw new InvalidDataException(parent.Name + " requires a newer compatible version of " + dependency.Name + ".");

            enabled.Add(dependencyPackage.Package.Name);
            VisitPackage(dependencyPackage.Package, installed, enabled, visiting, visited, order);
        }

        private static ModMetadata FindModule(PackageInfo package, string name)
        {
            if (package == null || string.IsNullOrWhiteSpace(name)) return null;
            ModMetadata exact = package.Modules.FirstOrDefault(module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            string normalizedName = ModDependencyResolver.NormalizeName(name);
            if (normalizedName.Length == 0) return null;
            List<ModMetadata> matches = package.Modules
                .Where(module => ModDependencyResolver.NormalizeName(module.Name) == normalizedName)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool TryGetInstalledPackage(
            Dictionary<string, InstalledPackage> installed,
            string moduleName,
            out InstalledPackage package)
        {
            if (installed.TryGetValue(moduleName ?? "", out package)) return true;
            string normalizedName = ModDependencyResolver.NormalizeName(moduleName);
            if (normalizedName.Length == 0)
            {
                package = null;
                return false;
            }
            List<InstalledPackage> matches = installed.Values
                .Distinct()
                .Where(candidate => FindModule(candidate.Package, moduleName) != null)
                .ToList();
            if (matches.Count == 1)
            {
                package = matches[0];
                return true;
            }
            package = null;
            return false;
        }

        private static bool TryGetCatalogUpdate(
            Dictionary<string, UpdateInfo> catalog,
            string moduleName,
            out UpdateInfo update)
        {
            if (catalog.TryGetValue(moduleName ?? "", out update)) return true;
            string normalizedName = ModDependencyResolver.NormalizeName(moduleName);
            if (normalizedName.Length == 0)
            {
                update = null;
                return false;
            }
            List<UpdateInfo> matches = catalog
                .Where(item => ModDependencyResolver.NormalizeName(item.Key) == normalizedName)
                .Select(item => item.Value)
                .Where(item => item != null)
                .ToList();
            if (matches.Count == 1)
            {
                update = matches[0];
                return true;
            }
            update = null;
            return false;
        }

        private static bool IsBuiltInDependency(string name)
        {
            return string.Equals(name, "Everest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "EverestCore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "API", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Celeste", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBuiltInDependencySatisfied(ModDependency dependency)
        {
            if (dependency == null || string.IsNullOrWhiteSpace(dependency.Version)) return true;
            string installedVersion = GetBuiltInDependencyVersion(dependency.Name);
            return installedVersion == null || VersionSatisfies(dependency.Version, installedVersion);
        }

        private static string DescribeBuiltInDependencyMismatch(ModDependency dependency)
        {
            string installedVersion = GetBuiltInDependencyVersion(dependency?.Name);
            if (installedVersion == null)
                return "A required bundled game component is unavailable.";
            return "Requires " + dependency.Name + " " + dependency.Version + "; this iOS build includes " +
                dependency.Name + " " + installedVersion + ". Choose a mod release compatible with the bundled game and Everest versions.";
        }

        private static string GetBuiltInDependencyVersion(string name)
        {
            if (string.Equals(name, "Everest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "EverestCore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "API", StringComparison.OrdinalIgnoreCase))
                return "1.6696.0";
            if (string.Equals(name, "Celeste", StringComparison.OrdinalIgnoreCase))
                return "1.4.0.0";
            return null;
        }

        private static bool VersionSatisfies(string requiredText, string installedText)
        {
            Version required = ParseVersion(requiredText, new Version(1, 0));
            Version installed = ParseVersion(installedText, new Version(1, 0));
            if (installed.Major == 0 && installed.Minor == 0) return true;
            if (installed.Major != required.Major || installed.Minor < required.Minor) return false;
            if (installed.Minor == required.Minor && installed.Build < required.Build) return false;
            return installed.Minor != required.Minor || installed.Build != required.Build || installed.Revision >= required.Revision;
        }

        private static Version ParseVersion(string value, Version fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string versionText = value.Trim();
            int suffix = versionText.IndexOf('-');
            if (suffix >= 0) versionText = versionText.Substring(0, suffix);
            Version parsed;
            if (Version.TryParse(versionText, out parsed)) return parsed;
            if (fallback != null) return fallback;
            throw new InvalidDataException("The mod metadata contains an invalid version: " + value + ".");
        }

        private static void ReplaceArchive(string source, string destination)
        {
            if (!File.Exists(destination))
            {
                File.Move(source, destination);
                return;
            }

            string backup = destination + ".previous";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(destination, backup);
            try
            {
                File.Move(source, destination);
                File.Delete(backup);
            }
            catch
            {
                if (File.Exists(destination)) File.Delete(destination);
                if (File.Exists(backup)) File.Move(backup, destination);
                throw;
            }
        }

        private static async Task DownloadWithMirrorsAsync(string url, string mirrorName, string destination, IProgress<string> progress)
        {
            List<string> urls = new List<string>();
            if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
            if (!string.IsNullOrWhiteSpace(mirrorName))
            {
                urls.Add("https://celestemodupdater.0x0a.de/banana-mirror/" + Uri.EscapeDataString(mirrorName) + ".zip");
                urls.Add("https://celeste.weg.fan/api/v2/download/gamebanana-files/" + Uri.EscapeDataString(mirrorName));
                urls.Add("https://banana-mirror-mods.celestemods.com/" + Uri.EscapeDataString(mirrorName) + ".zip");
                urls.Add("https://library.risingsunlight.dev/celeste/mods/" + Uri.EscapeDataString(mirrorName) + ".zip");
            }
            Exception lastError = null;
            foreach (string candidate in urls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Uri candidateUri;
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out candidateUri) || candidateUri.Scheme != Uri.UriSchemeHttps)
                {
                    lastError = new InvalidDataException("A mod download source did not use HTTPS.");
                    continue;
                }
                try
                {
                    progress?.Report("Downloading from " + candidateUri.Host + "...");
                    using (HttpResponseMessage response = await GetResponseWithTimeoutAsync(candidate, DownloadHeaderTimeout))
                    {
                        response.EnsureSuccessStatusCode();
                        using (Stream input = await response.Content.ReadAsStreamAsync())
                        using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                            await CopyWithIdleTimeoutAsync(input, output, DownloadIdleTimeout, candidateUri.Host);
                    }
                    using (ZipArchive archive = ZipFile.OpenRead(destination))
                    {
                        if (FindDescriptor(archive) == null) throw new InvalidDataException("The download is not an Everest mod ZIP.");
                    }
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    if (exception is TimeoutException)
                        progress?.Report("The source stopped responding; trying the next download source...");
                    if (File.Exists(destination)) File.Delete(destination);
                }
            }
            throw new IOException("The mod could not be downloaded from GameBanana or its mirrors.", lastError);
        }

        private static PackageInfo InspectArchive(string path, string fallbackName = null)
        {
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry descriptor = FindDescriptor(archive);
                List<ModMetadata> modules = null;
                string manifestFailure = null;
                try
                {
                    if (descriptor == null) throw new InvalidDataException("Root everest.yaml was not found.");
                    string yaml;
                    using (StreamReader reader = new StreamReader(descriptor.Open())) yaml = reader.ReadToEnd();
                    modules = MetadataDeserializer.Deserialize<List<ModMetadata>>(yaml);
                    modules = (modules ?? new List<ModMetadata>()).Where(module => module != null && !string.IsNullOrWhiteSpace(module.Name)).ToList();
                    if (modules.Count == 0) throw new InvalidDataException("everest.yaml does not define a mod name.");
                    foreach (ModMetadata module in modules)
                    {
                        if (module.Version == null) module.Version = "1.0";
                        else if (string.IsNullOrWhiteSpace(module.Version)) throw new InvalidDataException("everest.yaml contains an empty module version.");
                        ParseVersion(module.Version, null);
                        if (module.Dependencies == null) module.Dependencies = new List<ModDependency>();
                        if (module.OptionalDependencies == null) module.OptionalDependencies = new List<ModDependency>();
                        foreach (ModDependency dependency in module.Dependencies.Concat(module.OptionalDependencies))
                        {
                            if (dependency == null || string.IsNullOrWhiteSpace(dependency.Name))
                                throw new InvalidDataException("everest.yaml contains a dependency without a name.");
                            if (dependency.Version == null) dependency.Version = "1.0";
                            else if (string.IsNullOrWhiteSpace(dependency.Version)) throw new InvalidDataException("everest.yaml contains an empty dependency version.");
                            ParseVersion(dependency.Version, null);
                        }
                    }
                }
                catch (Exception exception)
                {
                    manifestFailure = exception.Message;
                    modules = null;
                }
                bool manifestFallback = modules == null;
                string fallbackPackageName = SafeFileName(string.IsNullOrWhiteSpace(fallbackName)
                    ? Path.GetFileNameWithoutExtension(path)
                    : fallbackName);
                if (manifestFallback)
                {
                    // Everest's ZIP loader creates a dummy module when its root
                    // manifest cannot be parsed, then still crawls the archive
                    // for maps and assets. Match that behavior so malformed
                    // campaign packages remain installable on iOS.
                    modules = new List<ModMetadata>
                    {
                        new ModMetadata
                        {
                            Name = "_zip_" + fallbackPackageName,
                            Version = "0.0.0-dummy"
                        }
                    };
                }
                string name = manifestFallback ? fallbackPackageName : modules[0].Name.Trim();
                int mapCount = 0;
                int assemblyCount = 0;
                int assetCount = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    ValidateArchivePath(entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string normalized = entry.FullName.Replace('\\', '/');
                    if (normalized.Equals("everest.yaml", StringComparison.OrdinalIgnoreCase) || normalized.Equals("everest.yml", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsMapEntry(normalized))
                    {
                        mapCount++;
                        continue;
                    }
                    if (Path.GetExtension(normalized).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        assemblyCount++;
                        continue;
                    }
                    if (!normalized.StartsWith("Loenn/", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith("Ahorn/", StringComparison.OrdinalIgnoreCase)) assetCount++;
                }
                string status = mapCount + " map(s) · " + assemblyCount + " code assembly(s) · " + assetCount + " other file(s)";
                if (modules.Count > 1) status += " · contains " + modules.Count + " mod modules";
                if (manifestFallback)
                    status += " · Everest dummy-module fallback (" + manifestFailure + ")";
                return new PackageInfo
                {
                    Name = name,
                    MapCount = mapCount,
                    AssemblyCount = assemblyCount,
                    AssetCount = assetCount,
                    Status = status,
                    ManifestFallback = manifestFallback,
                    Modules = modules
                };
            }
        }

        private static ZipArchiveEntry FindDescriptor(ZipArchive archive)
        {
            return archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, "everest.yaml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.FullName, "everest.yml", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsMapEntry(string path)
        {
            string normalized = path.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized.Substring(2);
            return normalized.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateArchivePath(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                (normalized.Length > 1 && normalized[1] == ':') ||
                normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("The mod ZIP contains an unsafe file path.");
        }

        private static string GetString(JsonElement item, string name)
        {
            JsonElement value;
            if (!item.TryGetProperty(name, out value) || value.ValueKind == JsonValueKind.Null) return "";
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            return value.ToString();
        }

        private static bool GetBool(JsonElement item, string name)
        {
            JsonElement value;
            if (!item.TryGetProperty(name, out value)) return false;
            if (value.ValueKind == JsonValueKind.True) return true;
            return value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static long GetLong(JsonElement item, string name)
        {
            JsonElement value;
            long result;
            return item.TryGetProperty(name, out value) && value.TryGetInt64(out result) ? result : 0;
        }

        private static string EnabledPath()
        {
            return Path.Combine(Root, "enabled.txt");
        }

        private static string DependencyGraphCachePath()
        {
            return Path.Combine(Root, "dependency-graph.yaml");
        }

        private static HashSet<string> ReadEnabledNames()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = EnabledPath();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadAllLines(path))
            {
                try { result.Add(Encoding.UTF8.GetString(Convert.FromBase64String(line))); }
                catch { }
            }
            return result;
        }

        private static void SaveEnabledNames(IEnumerable<string> names)
        {
            Directory.CreateDirectory(Root);
            string[] encoded = names.Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => Convert.ToBase64String(Encoding.UTF8.GetBytes(name)))
                .ToArray();
            File.WriteAllLines(EnabledPath(), encoded);
        }

        private static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder();
            foreach (char character in name)
            {
                if (character == '/' || character == '\\' || character == ':' || Array.IndexOf(invalid, character) >= 0) continue;
                builder.Append(character);
            }
            string result = builder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(result) || result == "." || result == "..") throw new InvalidDataException("The mod name cannot be used as a file name.");
            return result;
        }

        private static string ComputeXxHash64(string path)
        {
            const ulong p1 = 11400714785074694791UL;
            const ulong p2 = 14029467366897019727UL;
            const ulong p3 = 1609587929392839161UL;
            const ulong p4 = 9650029242287828579UL;
            const ulong p5 = 2870177450012600261UL;
            ulong v1 = unchecked(p1 + p2);
            ulong v2 = p2;
            ulong v3 = 0;
            ulong v4 = unchecked(0UL - p1);
            ulong total = 0;
            byte[] buffer = new byte[32768];
            byte[] tail = new byte[32];
            int tailLength = 0;
            bool large = false;
            using (FileStream stream = File.OpenRead(path))
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += (uint)read;
                    int offset = 0;
                    if (tailLength + read < 32)
                    {
                        Buffer.BlockCopy(buffer, 0, tail, tailLength, read);
                        tailLength += read;
                        continue;
                    }
                    if (tailLength > 0)
                    {
                        int fill = 32 - tailLength;
                        Buffer.BlockCopy(buffer, 0, tail, tailLength, fill);
                        ProcessStripe(tail, 0, ref v1, ref v2, ref v3, ref v4);
                        large = true;
                        offset += fill;
                        tailLength = 0;
                    }
                    while (offset + 32 <= read)
                    {
                        ProcessStripe(buffer, offset, ref v1, ref v2, ref v3, ref v4);
                        large = true;
                        offset += 32;
                    }
                    tailLength = read - offset;
                    if (tailLength > 0) Buffer.BlockCopy(buffer, offset, tail, 0, tailLength);
                }
            }

            ulong hash;
            if (large)
            {
                hash = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
                hash = MergeRound(hash, v1);
                hash = MergeRound(hash, v2);
                hash = MergeRound(hash, v3);
                hash = MergeRound(hash, v4);
            }
            else hash = p5;
            hash += total;
            int tailOffset = 0;
            while (tailOffset + 8 <= tailLength)
            {
                ulong lane = Round(0, ReadUInt64(tail, tailOffset));
                hash ^= lane;
                hash = RotateLeft(hash, 27) * p1 + p4;
                tailOffset += 8;
            }
            if (tailOffset + 4 <= tailLength)
            {
                hash ^= (ulong)ReadUInt32(tail, tailOffset) * p1;
                hash = RotateLeft(hash, 23) * p2 + p3;
                tailOffset += 4;
            }
            while (tailOffset < tailLength)
            {
                hash ^= tail[tailOffset] * p5;
                hash = RotateLeft(hash, 11) * p1;
                tailOffset++;
            }
            hash ^= hash >> 33;
            hash *= p2;
            hash ^= hash >> 29;
            hash *= p3;
            hash ^= hash >> 32;
            return hash.ToString("x16", CultureInfo.InvariantCulture);

            ulong Round(ulong accumulator, ulong input)
            {
                accumulator += input * p2;
                accumulator = RotateLeft(accumulator, 31);
                return accumulator * p1;
            }

            ulong MergeRound(ulong accumulator, ulong value)
            {
                accumulator ^= Round(0, value);
                return accumulator * p1 + p4;
            }

            void ProcessStripe(byte[] bytes, int offset, ref ulong a, ref ulong b, ref ulong c, ref ulong d)
            {
                a = Round(a, ReadUInt64(bytes, offset));
                b = Round(b, ReadUInt64(bytes, offset + 8));
                c = Round(c, ReadUInt64(bytes, offset + 16));
                d = Round(d, ReadUInt64(bytes, offset + 24));
            }

            ulong ReadUInt64(byte[] bytes, int offset)
            {
                return (ulong)bytes[offset]
                    | ((ulong)bytes[offset + 1] << 8)
                    | ((ulong)bytes[offset + 2] << 16)
                    | ((ulong)bytes[offset + 3] << 24)
                    | ((ulong)bytes[offset + 4] << 32)
                    | ((ulong)bytes[offset + 5] << 40)
                    | ((ulong)bytes[offset + 6] << 48)
                    | ((ulong)bytes[offset + 7] << 56);
            }

            uint ReadUInt32(byte[] bytes, int offset)
            {
                return (uint)bytes[offset] | ((uint)bytes[offset + 1] << 8) | ((uint)bytes[offset + 2] << 16) | ((uint)bytes[offset + 3] << 24);
            }

            ulong RotateLeft(ulong value, int count)
            {
                return (value << count) | (value >> (64 - count));
            }
        }
    }
}
