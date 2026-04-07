using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SomeEngine.Assets;

public sealed class AssetDatabase
{
    private readonly string _projectRoot;
    private readonly string _manifestDirectory;

    public AssetDatabase(string projectRoot, string? manifestDirectory = null)
    {
        AssetTypeRegistry.EnsureConfigured();
        _projectRoot = Path.GetFullPath(projectRoot);
        _manifestDirectory = Path.GetFullPath(manifestDirectory ?? Path.Combine(_projectRoot, "Library", "AssetManifest"));
        Manifest = File.Exists(Path.Combine(_manifestDirectory, AssetManifest.AssetIndexFileName))
            ? AssetManifest.Load(_manifestDirectory)
            : new AssetManifest();
    }

    public AssetManifest Manifest { get; private set; }

    public TAsset? Load<TAsset>(string sourcePath, string? subAssetKey = null)
        where TAsset : class, IAsset
    {
        string fullPath = ToProjectFullPath(sourcePath);

        // source 文件（如 .slang）→ 检查过期 → 按需 Import
        if (File.Exists(fullPath) && AssetTypeRegistry.MatchImporter(fullPath) != null && !IsUpToDate(fullPath, sourcePath))
        {
            Import(sourcePath);
        }

        // .asset 文件不在 manifest 中 → 直接读并注册
        if (Resolve(sourcePath, subAssetKey) == null && File.Exists(fullPath) && AssetTypeRegistry.Match(fullPath) != null)
        {
            Import(sourcePath);
        }

        AssetGuid? guid = Resolve(sourcePath, subAssetKey);
        return guid is AssetGuid assetGuid ? Load<TAsset>(assetGuid) : null;
    }

    public TAsset? Load<TAsset>(AssetGuid guid)
        where TAsset : class, IAsset
    {
        if (!Manifest.TryGetAsset(guid, out AssetManifestRecord record))
        {
            return null;
        }

        IAssetTypeHandler? handler = AssetTypeRegistry.Match(ToProjectFullPath(record.Path));
        return handler?.Load(ToProjectFullPath(record.Path)) as TAsset;
    }

    public IReadOnlyList<AssetGuid> Import(string sourcePath)
    {
        string fullPath = ToProjectFullPath(sourcePath);
        if (AssetTypeRegistry.MatchImporter(fullPath) is IAssetImporter importer)
        {
            SourceMeta sourceMeta = SourceMetaManager.GetOrCreate(fullPath, importer.ImporterName);
            IReadOnlyList<ImportedAsset> importedAssets = importer.Import(_projectRoot, fullPath);

            // 直接注册进 manifest，不扫描
            Manifest.AddSource(sourceMeta.SourceGuid, ToManifestPath(fullPath));
            List<AssetGuid> result = [];
            foreach (ImportedAsset imported in importedAssets)
            {
                if (imported.Asset.AssetGuid.IsEmpty)
                {
                    continue;
                }

                IAssetTypeHandler? handler = AssetTypeRegistry.Match(imported.OutputPath);
                IReadOnlyList<AssetGuid> deps = handler?.GetDependencies(imported.Asset) ?? [];
                Manifest.AddAsset(
                    imported.Asset.AssetGuid,
                    imported.Asset.Name,
                    ToManifestPath(imported.OutputPath),
                    handler?.AssetType ?? string.Empty,
                    sourceMeta.SourceGuid,
                    imported.SubAssetKey,
                    deps);
                result.Add(imported.Asset.AssetGuid);
            }

            Manifest.Save(_manifestDirectory);
            return result;
        }

        // 手写 .asset 文件（Material 等）：直接读并注册
        if (File.Exists(fullPath) && AssetTypeRegistry.Match(fullPath) is IAssetTypeHandler directHandler)
        {
            IAsset? asset = directHandler.Load(fullPath);
            if (asset != null && !asset.AssetGuid.IsEmpty)
            {
                AssetMeta? meta = AssetMetaManager.TryLoad(fullPath);
                IReadOnlyList<AssetGuid> deps = directHandler.GetDependencies(asset);
                Manifest.AddAsset(
                    asset.AssetGuid,
                    asset.Name,
                    ToManifestPath(fullPath),
                    directHandler.AssetType,
                    meta?.SourceGuid ?? SourceGuid.Empty,
                    meta?.SubAssetKey ?? string.Empty,
                    deps);
                Manifest.Save(_manifestDirectory);
                return [asset.AssetGuid];
            }
        }

        throw new NotSupportedException($"No importer or handler is registered for '{fullPath}'.");
    }

    public AssetGuid? Resolve(string sourcePath, string? subAssetKey = null)
    {
        string manifestPath = ToManifestPath(sourcePath);
        if (Manifest.TryGetAssetByPath(manifestPath, out AssetManifestRecord assetRecord))
        {
            return assetRecord.Guid;
        }

        if (!Manifest.TryGetSourceGuid(manifestPath, out SourceGuid sourceGuid))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(subAssetKey))
        {
            return Manifest.TryGetAssetBySourceAndSubAssetKey(sourceGuid, subAssetKey, out AssetManifestRecord record)
                ? record.Guid
                : null;
        }

        IReadOnlyList<AssetGuid> assets = Manifest.GetAssetsBySource(sourceGuid);
        return assets.Count == 1 ? assets[0] : null;
    }

    public IReadOnlyList<AssetManifestRecord> List(string? assetType = null) => Manifest.List(assetType);
    public IReadOnlyList<AssetGuid> GetDependencies(AssetGuid guid) => Manifest.GetDependencies(guid);
    public IReadOnlyList<AssetGuid> GetReferencers(AssetGuid guid) => Manifest.GetReferencers(guid);

    public IReadOnlyList<AssetDiagnostic> Validate()
    {
        List<AssetDiagnostic> diagnostics = [];

        // 检查 manifest 中的 source 记录对应的源文件是否还存在
        foreach ((SourceGuid sourceGuid, string sourcePath) in Manifest.Sources)
        {
            string fullPath = ToProjectFullPath(sourcePath);
            if (File.Exists(fullPath))
            {
                continue;
            }

            diagnostics.Add(new AssetDiagnostic
            {
                Kind = AssetDiagnosticKind.OrphanSourceMeta,
                Severity = AssetDiagnosticSeverity.Warning,
                Path = sourcePath,
                Message = $"Source '{sourcePath}' tracked in manifest but file does not exist.",
                SourceGuid = sourceGuid,
            });
        }

        // 检查 manifest 中的 asset 记录对应的 .asset 文件是否存在
        foreach ((AssetGuid assetGuid, AssetManifestRecord record) in Manifest.Assets)
        {
            string fullPath = ToProjectFullPath(record.Path);
            if (File.Exists(fullPath))
            {
                continue;
            }

            diagnostics.Add(new AssetDiagnostic
            {
                Kind = AssetDiagnosticKind.MissingAssetFile,
                Severity = AssetDiagnosticSeverity.Error,
                Path = record.Path,
                Message = $"Asset '{record.Path}' tracked in manifest but file does not exist.",
                AssetGuid = assetGuid,
                SourceGuid = record.SourceGuid,
            });
        }

        // 检查依赖完整性
        foreach ((AssetGuid ownerGuid, IReadOnlyList<AssetGuid> dependencies) in Manifest.Dependencies)
        {
            if (!Manifest.TryGetAsset(ownerGuid, out AssetManifestRecord owner))
            {
                continue;
            }

            foreach (AssetGuid dependency in dependencies)
            {
                if (Manifest.Assets.ContainsKey(dependency))
                {
                    continue;
                }

                diagnostics.Add(new AssetDiagnostic
                {
                    Kind = AssetDiagnosticKind.DanglingReference,
                    Severity = AssetDiagnosticSeverity.Warning,
                    Path = owner.Path,
                    Message = $"Asset '{owner.Path}' references missing asset guid '{dependency}'.",
                    AssetGuid = ownerGuid,
                    SourceGuid = owner.SourceGuid,
                    RelatedAssetGuid = dependency,
                });
            }
        }

        return diagnostics
            .OrderBy(static diagnostic => diagnostic.Kind)
            .ThenBy(static diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.AssetGuid.ToString(), StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.RelatedAssetGuid.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    private string ToProjectFullPath(string path)
        => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_projectRoot, path));

    private string ToManifestPath(string path) => AssetIoHelpers.ToManifestPath(_projectRoot, path);

    private bool IsUpToDate(string fullSourcePath, string sourcePath)
    {
        string manifestPath = ToManifestPath(sourcePath);
        if (!Manifest.TryGetSourceGuid(manifestPath, out SourceGuid sourceGuid))
        {
            return false;
        }

        IReadOnlyList<AssetGuid> assets = Manifest.GetAssetsBySource(sourceGuid);
        if (assets.Count == 0)
        {
            return false;
        }

        DateTime sourceTime = File.GetLastWriteTimeUtc(fullSourcePath);
        foreach (AssetGuid guid in assets)
        {
            if (!Manifest.TryGetAsset(guid, out AssetManifestRecord record))
            {
                return false;
            }

            string assetPath = ToProjectFullPath(record.Path);
            if (!File.Exists(assetPath) || File.GetLastWriteTimeUtc(assetPath) < sourceTime)
            {
                return false;
            }
        }

        return true;
    }
}
