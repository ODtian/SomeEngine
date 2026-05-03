using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Assets;

public static class AssetFingerprint
{
    public static AssetImportFingerprint Create(
        IReadOnlyList<DependencyEntryData> dependencies,
        uint importerVersion,
        params string[] extraParts)
    {
        return new AssetImportFingerprint
        {
            ContentFingerprint = ComputeContentFingerprint(dependencies, importerVersion, extraParts),
            Dependencies = dependencies
                .OrderBy(static dependency => dependency.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            ImporterVersion = importerVersion,
        };
    }

    public static DependencyEntryData? TryCreateFileDependency(string projectRoot, string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        return new DependencyEntryData
        {
            RelativePath = MakeRelativePath(projectRoot, fullPath),
            ContentHash = ComputeFileSha256(fullPath),
        };
    }

    public static string ComputeContentFingerprint(
        IReadOnlyList<DependencyEntryData> dependencies,
        uint importerVersion,
        params string[] extraParts)
    {
        var builder = new StringBuilder();
        foreach (DependencyEntryData dependency in dependencies.OrderBy(static x => x.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(dependency.RelativePath);
            builder.Append(':');
            builder.Append(dependency.ContentHash);
            builder.Append('\n');
        }

        foreach (string part in extraParts)
        {
            builder.Append("||");
            builder.Append(part);
        }

        builder.Append("||");
        builder.Append(importerVersion);
        return ComputeSha256(builder.ToString());
    }

    public static string ComputeFileSha256(string fullPath)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));

    public static string ComputeSha256(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string MakeRelativePath(string projectRoot, string fullPath)
    {
        string relativePath = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(fullPath));
        return relativePath.Replace('\\', '/');
    }
}
