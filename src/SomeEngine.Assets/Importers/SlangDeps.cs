using SlangShaderSharp;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static class SlangDeps
{
    public static AssetGuid GuidFor(SourceGuid sourceGuid, string subAssetKey)
        => AssetGuid.FromSource(sourceGuid, subAssetKey);

    public static bool Matches(Schema.ShaderAsset asset, AssetMeta existingAsset)
    {
        if (string.IsNullOrWhiteSpace(asset.AssetGuid))
        {
            return false;
        }

        return AssetGuid.TryParse(asset.AssetGuid, out var assetGuid)
            && assetGuid == existingAsset.AssetGuid;
    }

    public static DependencyEntryData[] Collect(
        IModule module,
        string sourcePath,
        string projectRoot)
    {
        var dependencies = new Dictionary<string, DependencyEntryData>(
            StringComparer.OrdinalIgnoreCase);

        void Add(string dependencyPath)
        {
            string fullPath = Path.GetFullPath(dependencyPath);
            if (!File.Exists(fullPath))
            {
                return;
            }

            string relativePath = RelPath(projectRoot, fullPath);
            dependencies[relativePath] = new DependencyEntryData
            {
                RelativePath = relativePath,
                ContentHash = AssetFingerprint.FileSha256(fullPath),
            };
        }

        Add(sourcePath);
        int dependencyCount = module.GetDependencyFileCount();
        for (int i = 0; i < dependencyCount; i++)
        {
            Add(module.GetDependencyFilePath(i));
        }

        return dependencies
            .Values.OrderBy(static x => x.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public static AssetImportFingerprint? Refresh(
        IReadOnlyList<DependencyEntryData> dependencies,
        string projectRoot,
        uint importerVersion)
    {
        if (dependencies.Count == 0)
        {
            return null;
        }

        DependencyEntryData[] currentDependencies = new DependencyEntryData[dependencies.Count];
        for (int i = 0; i < dependencies.Count; i++)
        {
            string fullPath = AbsPath(projectRoot, dependencies[i].RelativePath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            currentDependencies[i] = new DependencyEntryData
            {
                RelativePath = RelPath(projectRoot, fullPath),
                ContentHash = AssetFingerprint.FileSha256(fullPath),
            };
        }

        return AssetFingerprint.Create(currentDependencies, importerVersion);
    }

    public static string Fingerprint(
        IReadOnlyList<DependencyEntryData> dependencies,
        uint importerVersion)
        => AssetFingerprint.ComputeContentFingerprint(dependencies, importerVersion);

    public static string ProjectRoot(string sourcePath)
    {
        string? current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "SomeEngine.slnx"))
                || File.Exists(Path.Combine(current, "Directory.Build.props")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        string cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (Path.Exists(cwd) && sourcePath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
        {
            return cwd;
        }

        return Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? cwd;
    }

    private static string RelPath(string projectRoot, string fullPath)
    {
        string normalizedRoot = Path.GetFullPath(projectRoot);
        string normalizedPath = Path.GetFullPath(fullPath);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(normalizedPath);
        }

        return Path.GetRelativePath(normalizedRoot, normalizedPath).Replace('\\', '/');
    }

    private static string AbsPath(string projectRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.GetFullPath(Path.Combine(projectRoot, relativePath));
    }
}
