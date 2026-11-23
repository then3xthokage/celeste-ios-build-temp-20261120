using System.IO.Compression;
using System.Reflection;
using celestemeow;

namespace IOSModLibrarySmoke;

internal static class Program
{
    private static int Main()
    {
        VerifyNormalizedNames();
        VerifyGameBananaCandidateRanking();
        VerifyEverestVersionCompatibility();
        VerifyUniqueModuleAlias();
        VerifyAmbiguousModuleAliasIsRejected();
        VerifyEverestDummyModuleFallbackKeepsCampaignMaps();
        VerifyEmptyManifestVersionUsesEverestFallback();
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
        Type candidateType = typeof(IOSModLibrary).GetNestedType("CatalogMod", BindingFlags.NonPublic);
        object exact = Activator.CreateInstance(candidateType, nonPublic: true);
        SetProperty(candidateType, exact, "Name", "Needle Helper");
        int exactScore = (int)InvokePrivate("GetDependencySearchScore", exact, "NEEDLEHELPER");
        Require(exactScore == 2, "An exact normalized GameBanana title should rank first.");

        object unrelated = Activator.CreateInstance(candidateType, nonPublic: true);
        SetProperty(candidateType, unrelated, "Name", "Other Helper");
        int unrelatedScore = (int)InvokePrivate("GetDependencySearchScore", unrelated, "NEEDLEHELPER");
        Require(unrelatedScore == 0, "An unrelated GameBanana result should not receive an alias score.");
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
