using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace celestemeow
{
    internal sealed class ModDependency
    {
        public ModDependency() { }
        public string Name { get; set; }
        public string Version { get; set; }
    }

    internal sealed class DependencyCatalogEntry
    {
        public DependencyCatalogEntry() { }
        public List<ModDependency> Dependencies { get; set; } = new List<ModDependency>();
        public List<ModDependency> OptionalDependencies { get; set; } = new List<ModDependency>();
    }

    /// <summary>
    /// Resolves package requirements using Everest's live graph plus each
    /// package's own manifest. It is deliberately keyed by mod name, so new
    /// campaigns and helpers work without adding mod-specific code here.
    /// </summary>
    internal sealed class ModDependencyResolver
    {
        private const string GraphPointerUrl = "https://everestapi.github.io/modgraph.txt";
        private const string MirrorUrl = "https://everestapi.github.io/updatermirror/mod_dependency_graph.yaml";
        private static readonly IDeserializer Deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        private readonly Dictionary<string, DependencyCatalogEntry> graph;

        public static ModDependencyResolver Empty { get; } = new ModDependencyResolver(
            new Dictionary<string, DependencyCatalogEntry>(StringComparer.OrdinalIgnoreCase), false);

        public bool IsAvailable { get; private set; }

        private ModDependencyResolver(Dictionary<string, DependencyCatalogEntry> graph, bool isAvailable)
        {
            this.graph = graph ?? new Dictionary<string, DependencyCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            IsAvailable = isAvailable;
        }

        public static async Task<ModDependencyResolver> DownloadEverestGraphAsync(
            Func<string, Task<string>> getSmallText,
            IProgress<string> progress,
            string cachePath = null)
        {
            if (getSmallText == null) throw new ArgumentNullException(nameof(getSmallText));
            progress?.Report("Checking Everest's dependency list...");

            string databaseUrl;
            try
            {
                databaseUrl = (await getSmallText(GraphPointerUrl)).Trim();
                if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out Uri parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("Everest returned an invalid dependency graph address.");
            }
            catch
            {
                databaseUrl = MirrorUrl;
            }

            try
            {
                return ParseAndCache(await getSmallText(databaseUrl), cachePath);
            }
            catch when (!string.Equals(databaseUrl, MirrorUrl, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return ParseAndCache(await getSmallText(MirrorUrl), cachePath);
                }
                catch
                {
                }
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath))
            {
                try
                {
                    ModDependencyResolver cached = Parse(File.ReadAllText(cachePath));
                    progress?.Report("Using the saved Everest dependency list while offline.");
                    return cached;
                }
                catch
                {
                    try { File.Delete(cachePath); } catch { }
                }
            }

            progress?.Report("Everest's dependency list is unavailable and no saved copy could be loaded.");
            return Empty;
        }

        private static ModDependencyResolver ParseAndCache(string yaml, string cachePath)
        {
            ModDependencyResolver resolver = Parse(yaml);
            if (string.IsNullOrWhiteSpace(cachePath)) return resolver;
            string temporaryPath = cachePath + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(temporaryPath, yaml);
                File.Move(temporaryPath, cachePath, true);
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
            return resolver;
        }

        internal static ModDependencyResolver Parse(string yaml)
        {
            try
            {
                Dictionary<string, DependencyCatalogEntry> parsed = Deserializer.Deserialize<Dictionary<string, DependencyCatalogEntry>>(yaml)
                    ?? new Dictionary<string, DependencyCatalogEntry>();
                Dictionary<string, DependencyCatalogEntry> normalized = new Dictionary<string, DependencyCatalogEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, DependencyCatalogEntry> item in parsed)
                {
                    if (string.IsNullOrWhiteSpace(item.Key)) continue;
                    DependencyCatalogEntry entry = item.Value ?? new DependencyCatalogEntry();
                    if (entry.Dependencies == null) entry.Dependencies = new List<ModDependency>();
                    if (entry.OptionalDependencies == null) entry.OptionalDependencies = new List<ModDependency>();
                    string name = item.Key.Trim();
                    DependencyCatalogEntry existing;
                    if (!normalized.TryGetValue(name, out existing))
                    {
                        normalized[name] = entry;
                    }
                    else
                    {
                        Merge(existing.Dependencies, entry.Dependencies);
                        Merge(existing.OptionalDependencies, entry.OptionalDependencies);
                    }
                }
                if (normalized.Count == 0)
                    throw new InvalidDataException("Everest's dependency graph contains no mod entries.");
                return new ModDependencyResolver(normalized, true);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Everest's dependency graph could not be parsed.", exception);
            }
        }

        public void ApplyTo(string moduleName, List<ModDependency> dependencies, List<ModDependency> optionalDependencies)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) return;
            DependencyCatalogEntry entry;
            if (!graph.TryGetValue(moduleName, out entry) || entry == null) return;
            Merge(dependencies, entry.Dependencies);
            Merge(optionalDependencies, entry.OptionalDependencies);
        }

        /// <summary>
        /// Returns a dependency-first, de-duplicated closure. An optional
        /// manifest lookup lets installed or pending packages add requirements
        /// omitted from a remote graph; optional requirements are never forced.
        /// </summary>
        public IReadOnlyList<ModDependency> ResolveRequiredClosure(
            IEnumerable<ModDependency> roots,
            Func<string, IEnumerable<ModDependency>> manifestDependencies = null)
        {
            List<ModDependency> order = new List<ModDependency>();
            Dictionary<string, ModDependency> resolved = new Dictionary<string, ModDependency>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<ModDependency> visit = null;
            visit = dependency =>
            {
                if (dependency == null || string.IsNullOrWhiteSpace(dependency.Name)) return;
                string name = dependency.Name.Trim();
                ModDependency node;
                if (!resolved.TryGetValue(name, out node))
                {
                    node = new ModDependency { Name = name, Version = dependency.Version };
                    resolved.Add(name, node);
                }
                else if (IsHigherVersion(dependency.Version, node.Version))
                {
                    node.Version = dependency.Version;
                }

                if (visited.Contains(name)) return;
                if (!visiting.Add(name))
                    throw new InvalidDataException("The required mod dependencies contain a cycle at " + name + ".");

                DependencyCatalogEntry entry;
                if (graph.TryGetValue(name, out entry) && entry != null)
                    foreach (ModDependency child in entry.Dependencies) visit(child);

                if (manifestDependencies != null)
                {
                    IEnumerable<ModDependency> extra = manifestDependencies(name);
                    if (extra != null)
                        foreach (ModDependency child in extra) visit(child);
                }

                visiting.Remove(name);
                visited.Add(name);
                order.Add(node);
            };

            if (roots != null)
                foreach (ModDependency root in roots) visit(root);
            return order;
        }

        private static void Merge(List<ModDependency> target, List<ModDependency> source)
        {
            if (target == null || source == null) return;
            foreach (ModDependency dependency in source)
            {
                if (dependency == null || string.IsNullOrWhiteSpace(dependency.Name)) continue;
                ModDependency existing = target.FirstOrDefault(item => string.Equals(item.Name, dependency.Name, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    target.Add(new ModDependency { Name = dependency.Name.Trim(), Version = dependency.Version });
                else if (string.IsNullOrWhiteSpace(existing.Version) || IsHigherVersion(dependency.Version, existing.Version))
                    existing.Version = dependency.Version;
            }
        }

        private static bool IsHigherVersion(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            Version parsedCandidate = ParseVersion(candidate);
            if (parsedCandidate == null) return false;
            if (string.IsNullOrWhiteSpace(current)) return true;
            Version parsedCurrent = ParseVersion(current);
            return parsedCurrent == null || parsedCandidate.CompareTo(parsedCurrent) > 0;
        }

        private static Version ParseVersion(string value)
        {
            string numeric = value.Trim();
            int suffix = numeric.IndexOf('-');
            if (suffix >= 0) numeric = numeric.Substring(0, suffix);
            Version parsed;
            return Version.TryParse(numeric, out parsed) ? parsed : null;
        }
    }
}
