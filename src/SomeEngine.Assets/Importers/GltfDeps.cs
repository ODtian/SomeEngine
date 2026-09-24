using System.Text.Json;

namespace SomeEngine.Assets.Importers;

internal static class GltfDeps
{
    public static AssetImportFingerprint? Fingerprint(
        string projectRoot,
        string fullSourcePath,
        SourceMeta sourceMeta,
        GltfImporterSettings settings)
    {
        List<DependencyEntryData> dependencies = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Add(projectRoot, fullSourcePath, dependencies, seen))
        {
            return null;
        }

        if (!AddProject(projectRoot, settings.LitMaterialTemplate, dependencies, seen)
            || !AddProject(projectRoot, settings.UnlitMaterialTemplate, dependencies, seen))
        {
            return null;
        }

        if (string.Equals(Path.GetExtension(fullSourcePath), ".gltf", StringComparison.OrdinalIgnoreCase)
            && !AddUris(projectRoot, fullSourcePath, dependencies, seen))
        {
            return null;
        }

        string settingsFingerprint = sourceMeta.ImporterSettings?.GetRawText() ?? string.Empty;
        return AssetFingerprint.Create(dependencies, GltfSourceImporter.ImporterVersion, settingsFingerprint);
    }

    public static void SaveMeta(
        string outputPath,
        string? assetGuid,
        SourceGuid sourceGuid,
        string subAssetKey,
        AssetImportFingerprint fingerprint)
    {
        if (!AssetGuid.TryParse(assetGuid, out AssetGuid parsedGuid) || parsedGuid.IsEmpty)
        {
            return;
        }

        AssetMetaFiles.Save(
            outputPath,
            new AssetMeta
            {
                AssetGuid = parsedGuid,
                SourceGuid = sourceGuid,
                SubAssetKey = subAssetKey,
                ContentFingerprint = fingerprint.ContentFingerprint,
                Dependencies = fingerprint.Dependencies,
                ImporterVersion = fingerprint.ImporterVersion,
                AssetPath = Path.GetFullPath(outputPath),
            });
    }

    public static string FullPath(string projectRoot, string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectRoot, path));

    private static bool AddProject(
        string projectRoot,
        string projectRelativePath,
        List<DependencyEntryData> dependencies,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(projectRelativePath))
        {
            return false;
        }

        return Add(projectRoot, FullPath(projectRoot, projectRelativePath), dependencies, seen);
    }

    private static bool Add(
        string projectRoot,
        string fullPath,
        List<DependencyEntryData> dependencies,
        HashSet<string> seen)
    {
        DependencyEntryData? dependency = AssetFingerprint.TryFileDep(projectRoot, fullPath);
        if (dependency == null)
        {
            return false;
        }

        if (seen.Add(dependency.RelativePath))
        {
            dependencies.Add(dependency);
        }

        return true;
    }

    private static bool AddUris(
        string projectRoot,
        string fullSourcePath,
        List<DependencyEntryData> dependencies,
        HashSet<string> seen)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullSourcePath));
        JsonElement root = document.RootElement;
        string sourceDirectory = Path.GetDirectoryName(fullSourcePath)!;

        return AddUriArray(projectRoot, sourceDirectory, root, "buffers", dependencies, seen)
            && AddUriArray(projectRoot, sourceDirectory, root, "images", dependencies, seen);
    }

    private static bool AddUriArray(
        string projectRoot,
        string sourceDirectory,
        JsonElement root,
        string propertyName,
        List<DependencyEntryData> dependencies,
        HashSet<string> seen)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("uri", out JsonElement uriElement)
                || uriElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? uri = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(uri)
                || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string dependencyPath = Uri.UnescapeDataString(uri);
            if (!Add(projectRoot, Path.Combine(sourceDirectory, dependencyPath), dependencies, seen))
            {
                return false;
            }
        }

        return true;
    }
}
