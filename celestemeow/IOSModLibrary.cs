using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
            public List<ModMetadata> Modules = new List<ModMetadata>();
        }

        private sealed class InstalledPackage
        {
            public string Path;
            public PackageInfo Package;
        }

        private static readonly IDeserializer MetadataDeserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CelesteMods");
        private static readonly string ModsDirectory = Path.Combine(Root, "Mods");
        private static readonly string AutoUpdateSettingPath = Path.Combine(Root, "auto-update.txt");
        private static ModDependencyResolver dependencyGraphCache = ModDependencyResolver.Empty;

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
            client.Timeout = TimeSpan.FromMinutes(15);
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
            using (HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (Stream stream = await response.Content.ReadAsStreamAsync())
                using (JsonDocument document = await JsonDocument.ParseAsync(stream))
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
        }

        public static async Task<string> InstallAsync(CatalogMod catalogMod, IProgress<string> progress)
        {
            Directory.CreateDirectory(ModsDirectory);
            string tempPath = Path.Combine(Root, "download-" + Guid.NewGuid().ToString("N") + ".zip.part");
            try
            {
                await DownloadWithMirrorsAsync(catalogMod.Url, catalogMod.MirrorName, tempPath, progress);
                PackageInfo package = InspectArchive(tempPath);
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

        public static List<string> SetEnabled(string name, bool enabled)
        {
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
            List<string> disabled = new List<string>();
            HashSet<string> unavailableModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModMetadata module in removedPackage.Modules)
                unavailableModules.Add(module.Name);

            bool changed;
            do
            {
                changed = false;
                foreach (InstalledPackage candidate in installed.Values.Distinct())
                {
                    if (!enabled.Contains(candidate.Package.Name)) continue;
                    bool requiresRemovedPackage = candidate.Package.Modules.Any(module => module.Dependencies.Any(dependency =>
                        dependency != null && unavailableModules.Contains(dependency.Name ?? "")));
                    if (!requiresRemovedPackage) continue;

                    enabled.Remove(candidate.Package.Name);
                    disabled.Add(candidate.Package.Name);
                    foreach (ModMetadata module in candidate.Package.Modules)
                        unavailableModules.Add(module.Name);
                    changed = true;
                }
            } while (changed);
            return disabled.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> Remove(InstalledMod mod)
        {
            if (mod == null) return new List<string>();
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
                    PackageInfo replacement = InspectArchive(tempPath);
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
            dependencyGraphCache = await ModDependencyResolver.DownloadEverestGraphAsync(GetSmallTextAsync, progress, DependencyGraphCachePath());
            if (!dependencyGraphCache.IsAvailable)
                throw new IOException("Celeste couldn't verify the required mod list. Connect to the internet and try again.");
            ApplyDependencyGraph(rootPackage, dependencyGraphCache);
            Dictionary<string, InstalledPackage> installed = LoadInstalledPackages();
            Dictionary<string, UpdateInfo> catalog = HasUnsatisfiedDependencies(rootPackage, rootPackage, installed)
                ? await DownloadUpdateCatalogAsync()
                : new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
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
                    throw new InvalidDataException("The bundled " + dependency.Name + " version does not satisfy the required " + dependency.Version + ".");
                return;
            }

            ModMetadata pendingModule = FindModule(pendingPackage, dependency.Name);
            if (pendingModule != null && VersionSatisfies(dependency.Version, pendingModule.Version)) return;

            InstalledPackage current;
            if (installed.TryGetValue(dependency.Name, out current))
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
                if (!catalog.TryGetValue(dependency.Name, out update) || update.Hashes.Count == 0)
                    throw new FileNotFoundException("A required dependency could not be found in Everest's update catalog: " + dependency.Name + ".");

                progress?.Report("Installing required dependency " + dependency.Name + "...");
                string tempPath = Path.Combine(Root, "dependency-" + Guid.NewGuid().ToString("N") + ".zip.part");
                try
                {
                    await DownloadWithMirrorsAsync(update.Url, update.MirrorName, tempPath, progress);
                    string hash = ComputeXxHash64(tempPath);
                    if (!update.Hashes.Any(expected => string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("The dependency download checksum did not match Everest's catalog for " + dependency.Name + ".");

                    PackageInfo package = InspectArchive(tempPath);
                    ApplyDependencyGraph(package, dependencyGraphCache);
                    ModMetadata downloadedModule = FindModule(package, dependency.Name);
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
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                return await Http.GetStringAsync(url, timeout.Token);
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
                if (installed.TryGetValue(name, out candidate))
                    return candidate.Package.Modules.SelectMany(module => module.Dependencies);
                return null;
            });

            foreach (ModDependency dependency in closure)
            {
                if (IsBuiltInDependency(dependency.Name))
                {
                    if (!IsBuiltInDependencySatisfied(dependency))
                        missing.Add(FormatDependency(dependency));
                    continue;
                }

                ModMetadata pending = FindModule(pendingPackage, dependency.Name);
                if (pending != null && VersionSatisfies(dependency.Version, pending.Version)) continue;

                InstalledPackage candidate;
                ModMetadata actual = installed.TryGetValue(dependency.Name, out candidate)
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
                    if (dependency != null && installed.TryGetValue(dependency.Name ?? "", out optional) && enabled.Contains(optional.Package.Name))
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
                    throw new InvalidDataException(parent.Name + " requires " + dependency.Name + " " + dependency.Version + ", which is newer than the bundled version.");
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
            if (!installed.TryGetValue(dependency.Name, out dependencyPackage))
                throw new InvalidDataException(parent.Name + " requires " + dependency.Name + ", which is not installed.");
            ModMetadata dependencyModule = FindModule(dependencyPackage.Package, dependency.Name);
            if (dependencyModule == null || !VersionSatisfies(dependency.Version, dependencyModule.Version))
                throw new InvalidDataException(parent.Name + " requires a newer compatible version of " + dependency.Name + ".");

            enabled.Add(dependencyPackage.Package.Name);
            VisitPackage(dependencyPackage.Package, installed, enabled, visiting, visited, order);
        }

        private static ModMetadata FindModule(PackageInfo package, string name)
        {
            return package.Modules.FirstOrDefault(module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase));
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
            if (string.Equals(dependency.Name, "Everest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dependency.Name, "EverestCore", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dependency.Name, "API", StringComparison.OrdinalIgnoreCase))
                return VersionSatisfies(dependency.Version, "1.6696.0");
            if (string.Equals(dependency.Name, "Celeste", StringComparison.OrdinalIgnoreCase))
                return VersionSatisfies(dependency.Version, "1.4.0.0");
            return true;
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
                    using (HttpResponseMessage response = await Http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        using (Stream input = await response.Content.ReadAsStreamAsync())
                        using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                            await input.CopyToAsync(output);
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
                    if (File.Exists(destination)) File.Delete(destination);
                }
            }
            throw new IOException("The mod could not be downloaded from GameBanana or its mirrors.", lastError);
        }

        private static PackageInfo InspectArchive(string path)
        {
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry descriptor = FindDescriptor(archive);
                if (descriptor == null) throw new InvalidDataException("Root everest.yaml was not found.");
                string yaml;
                using (StreamReader reader = new StreamReader(descriptor.Open())) yaml = reader.ReadToEnd();
                List<ModMetadata> modules;
                try
                {
                    modules = MetadataDeserializer.Deserialize<List<ModMetadata>>(yaml);
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException("everest.yaml could not be parsed.", exception);
                }
                modules = (modules ?? new List<ModMetadata>()).Where(module => module != null && !string.IsNullOrWhiteSpace(module.Name)).ToList();
                if (modules.Count == 0) throw new InvalidDataException("everest.yaml does not define a mod name.");
                foreach (ModMetadata module in modules)
                {
                    if (string.IsNullOrWhiteSpace(module.Version)) module.Version = "1.0";
                    ParseVersion(module.Version, null);
                    if (module.Dependencies == null) module.Dependencies = new List<ModDependency>();
                    if (module.OptionalDependencies == null) module.OptionalDependencies = new List<ModDependency>();
                    foreach (ModDependency dependency in module.Dependencies.Concat(module.OptionalDependencies))
                    {
                        if (dependency == null || string.IsNullOrWhiteSpace(dependency.Name))
                            throw new InvalidDataException("everest.yaml contains a dependency without a name.");
                        if (!string.IsNullOrWhiteSpace(dependency.Version)) ParseVersion(dependency.Version, null);
                    }
                }
                string name = modules[0].Name.Trim();
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
                return new PackageInfo { Name = name, MapCount = mapCount, AssemblyCount = assemblyCount, AssetCount = assetCount, Status = status, Modules = modules };
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
