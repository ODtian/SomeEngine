using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SomeEngine.Assets;

public sealed class SourceMeta
{
    public required SourceGuid SourceGuid { get; init; }
    public required string Importer { get; init; }
    public JsonElement? ImporterSettings { get; init; }
}

public sealed class AssetMeta
{
    public required AssetGuid AssetGuid { get; init; }
    public required SourceGuid SourceGuid { get; init; }
    public required string SubAssetKey { get; init; }
    public required string ContentFingerprint { get; init; }
    public required IReadOnlyList<DependencyEntryData> Dependencies { get; init; }
    public required uint ImporterVersion { get; init; }
    public required string AssetPath { get; init; }
}

public static class SourceMetaManager
{
    public static SourceMeta GetOrCreate(string sourcePath, string importer = nameof(Importers.SlangShaderImporter))
    {
        string metaPath = GetMetaPath(sourcePath = Path.GetFullPath(sourcePath));
        if (File.Exists(metaPath))
        {
            return Load(sourcePath);
        }

        SourceMeta meta = new() { SourceGuid = SourceGuid.New(), Importer = importer };
        Save(sourcePath, meta);
        return meta;
    }

    public static SourceMeta Load(string sourcePath)
    {
        string metaPath = GetMetaPath(sourcePath = Path.GetFullPath(sourcePath));
        SourceMetaDocument document = JsonSerializer.Deserialize<SourceMetaDocument>(File.ReadAllText(metaPath), AssetIoHelpers.JsonOptions)
            ?? throw new InvalidOperationException($"Failed to read source meta '{metaPath}'.");
        return new SourceMeta
        {
            SourceGuid = SourceGuid.Parse(document.SourceGuid),
            Importer = document.Importer,
            ImporterSettings = document.ImporterSettings.HasValue
                && document.ImporterSettings.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                    ? document.ImporterSettings.Value.Clone()
                    : null,
        };
    }

    public static void Save(string sourcePath, SourceMeta meta)
    {
        string metaPath = GetMetaPath(sourcePath = Path.GetFullPath(sourcePath));
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(new SourceMetaDocument
        {
            SourceGuid = meta.SourceGuid.ToFlatString(),
            Importer = meta.Importer,
            ImporterSettings = meta.ImporterSettings?.Clone(),
        }, AssetIoHelpers.JsonOptions));
    }

    public static string GetMetaPath(string sourcePath) => Path.GetFullPath(sourcePath) + ".meta";

    private sealed class SourceMetaDocument
    {
        public string SourceGuid { get; set; } = string.Empty;
        public string Importer { get; set; } = string.Empty;
        public JsonElement? ImporterSettings { get; set; }
    }
}

public static class AssetMetaManager
{
    public static AssetMeta? TryLoad(string assetPath)
    {
        string metaPath = GetMetaPath(assetPath = Path.GetFullPath(assetPath));
        if (!File.Exists(metaPath))
        {
            return null;
        }

        AssetMetaDocument? document = JsonSerializer.Deserialize<AssetMetaDocument>(File.ReadAllText(metaPath), AssetIoHelpers.JsonOptions);
        if (document == null || !AssetGuid.TryParse(document.AssetGuid, out AssetGuid assetGuid))
        {
            return null;
        }

        SourceGuid sourceGuid = SourceGuid.TryParse(document.SourceGuid, out SourceGuid parsedSourceGuid) ? parsedSourceGuid : SourceGuid.Empty;
        return new AssetMeta
        {
            AssetGuid = assetGuid,
            SourceGuid = sourceGuid,
            SubAssetKey = document.SubAssetKey,
            ContentFingerprint = document.ContentFingerprint,
            Dependencies = document.Dependencies.Select(static entry => new DependencyEntryData
            {
                RelativePath = entry.Path,
                ContentHash = entry.Hash,
            }).ToArray(),
            ImporterVersion = document.ImporterVersion,
            AssetPath = assetPath,
        };
    }

    public static void Save(string assetPath, AssetMeta meta)
    {
        string metaPath = GetMetaPath(assetPath = Path.GetFullPath(assetPath));
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(new AssetMetaDocument
        {
            AssetGuid = meta.AssetGuid.ToFlatString(),
            SourceGuid = meta.SourceGuid.IsEmpty ? string.Empty : meta.SourceGuid.ToFlatString(),
            SubAssetKey = meta.SubAssetKey,
            ContentFingerprint = meta.ContentFingerprint,
            Dependencies = meta.Dependencies.Select(static entry => new AssetMetaDependencyDocument
            {
                Path = entry.RelativePath,
                Hash = entry.ContentHash,
            }).ToList(),
            ImporterVersion = meta.ImporterVersion,
        }, AssetIoHelpers.JsonOptions));
    }

    public static string GetMetaPath(string assetPath) => Path.GetFullPath(assetPath) + ".meta";
    public static bool IsAssetMetaPath(string metaPath) => metaPath.EndsWith(".asset.meta", StringComparison.OrdinalIgnoreCase);

    private sealed class AssetMetaDocument
    {
        public string AssetGuid { get; set; } = string.Empty;
        public string SourceGuid { get; set; } = string.Empty;
        public string SubAssetKey { get; set; } = string.Empty;
        public string ContentFingerprint { get; set; } = string.Empty;
        public List<AssetMetaDependencyDocument> Dependencies { get; set; } = [];
        public uint ImporterVersion { get; set; }
    }

    private sealed class AssetMetaDependencyDocument
    {
        public string Path { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }
}
