using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SomeEngine.Assets;

public sealed class AssetDatabase : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _manifestDirectory;
    private readonly IReadOnlyList<IAssetProvider> _providers;
    private readonly IReadOnlyList<IAssetImporter> _importers;

    // ── 按运行时类型分桶的缓存 ──
    private sealed class ProviderStore : IDisposable
    {
        public IAssetProvider Provider { get; }
        public Dictionary<AssetGuid, object> Cache { get; } = new();

        public ProviderStore(IAssetProvider provider) => Provider = provider;

        public void Dispose()
        {
            foreach (object item in Cache.Values)
                Provider.Destroy(item);
            Cache.Clear();
        }
    }

    private readonly Dictionary<Type, ProviderStore> _stores = new();

    public AssetDatabase(
        string projectRoot,
        IEnumerable<IAssetProvider> providers,
        IEnumerable<IAssetImporter> importers,
        string? manifestDirectory = null)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _manifestDirectory = Path.GetFullPath(manifestDirectory ?? Path.Combine(_projectRoot, "Library", "AssetManifest"));
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        _importers = importers?.ToArray() ?? throw new ArgumentNullException(nameof(importers));
        if (_providers.Count == 0)
        {
            throw new InvalidOperationException("No asset providers were provided to AssetDatabase.");
        }

        foreach (IAssetProvider provider in _providers)
        {
            RegisterStore(provider);
        }

        Manifest = File.Exists(Path.Combine(_manifestDirectory, AssetManifest.AssetIndexFileName))
            ? AssetManifest.Load(_manifestDirectory)
            : new AssetManifest();
    }

    public AssetManifest Manifest { get; private set; }

    public AssetGuid CreateAsset<TAsset>(
        string assetPath,
        TAsset asset,
        AssetSaveHandler<TAsset> save)
        where TAsset : class, IMutableAsset
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(save);

        string fullPath = ToProjectFullPath(assetPath);
        IAssetProvider provider = MatchProvider(fullPath)
            ?? throw new NotSupportedException($"No provider is registered for '{fullPath}'.");

        AssetGuid? registeredGuid = Resolve(assetPath);
        AssetGuid guid = asset.AssetGuid;
        if (!guid.IsEmpty && registeredGuid is AssetGuid existingGuid && existingGuid != guid)
        {
            throw new InvalidOperationException(
                $"Asset '{assetPath}' is already registered as '{existingGuid}', but the asset payload declares '{guid}'.");
        }

        if (guid.IsEmpty)
        {
            guid = registeredGuid ?? AssetGuid.New();
            asset.SetAssetGuid(guid);
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        save(asset, fullPath);
        RegisterAssetFile(fullPath, asset, provider);
        return guid;
    }

    // ── Load<T> by GUID: 运行时类型化加载 + 缓存 ──
    public T? Load<T>(AssetGuid guid) where T : class
    {
        if (!Manifest.TryGetAsset(guid, out AssetManifestRecord record))
        {
            return null;
        }

        if (!_stores.TryGetValue(typeof(T), out ProviderStore? store))
        {
            return null;
        }

        if (store.Cache.TryGetValue(guid, out object? cached))
        {
            return (T)cached;
        }

        string filePath = ToProjectFullPath(record.Path);
        if (!File.Exists(filePath))
        {
            return null;
        }

        object result = store.Provider.Create(guid, filePath);
        if (result is not T typed)
        {
            store.Provider.Destroy(result);
            throw new InvalidOperationException(
                $"Provider '{store.Provider.GetType().Name}' returned '{result.GetType().Name}' for requested '{typeof(T).Name}'.");
        }

        if (typed is IAsset asset && asset.AssetGuid != guid)
        {
            store.Provider.Destroy(result);
            throw new InvalidOperationException(
                $"Asset payload guid '{asset.AssetGuid}' does not match manifest guid '{guid}' for '{record.Path}'.");
        }

        store.Cache[guid] = typed;
        return typed;
    }

    // ── Load<T> by source path: resolve → Load<T>(guid) ──
    public T? Load<T>(string sourcePath, string? subAssetKey = null)
        where T : class
    {
        string fullPath = ToProjectFullPath(sourcePath);

        // source 文件（如 .slang）→ 检查过期 → 按需 Import
        IAssetImporter? importer = MatchImporter(fullPath);
        if (File.Exists(fullPath) && importer != null && !IsUpToDate(fullPath, sourcePath, importer))
        {
            Import(sourcePath);
        }

        AssetGuid? guid = Resolve(sourcePath, subAssetKey);
        return guid is AssetGuid assetGuid ? Load<T>(assetGuid) : null;
    }

    // ── Import: 源文件 → .asset 文件 → manifest 注册 ──
    public IReadOnlyList<AssetGuid> Import(string sourcePath)
    {
        string fullPath = ToProjectFullPath(sourcePath);
        if (MatchImporter(fullPath) is IAssetImporter importer)
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

                if (MatchProvider(imported.OutputPath) is IAssetProvider provider)
                {
                    RegisterAssetFile(
                        imported.OutputPath,
                        imported.Asset,
                        provider,
                        sourceMeta.SourceGuid,
                        imported.SubAssetKey,
                        saveManifest: false);
                }
                result.Add(imported.Asset.AssetGuid);
            }

            Manifest.Save(_manifestDirectory);
            return result;
        }

        // 手写 .asset 文件（Material 等）：直接读并注册
        if (File.Exists(fullPath) && MatchProvider(fullPath) is IAssetProvider directProvider)
        {
            object obj = directProvider.Create(AssetGuid.Empty, fullPath);
            try
            {
                if (obj is IAsset asset && !asset.AssetGuid.IsEmpty)
                {
                    AssetMeta? meta = AssetMetaManager.TryLoad(fullPath);
                    RegisterAssetFile(
                        fullPath,
                        asset,
                        directProvider,
                        meta?.SourceGuid ?? SourceGuid.Empty,
                        meta?.SubAssetKey ?? string.Empty);
                    return [asset.AssetGuid];
                }
            }
            finally
            {
                // Don't cache this temporary load; just for manifest registration
                directProvider.Destroy(obj);
            }
        }

        throw new NotSupportedException($"No importer or provider is registered for '{fullPath}'.");
    }

    // ── Resolve / Query ──
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

    public void Dispose()
    {
        foreach (ProviderStore store in _stores.Values)
            store.Dispose();
        _stores.Clear();
    }

    // ── Private helpers ──

    private string ToProjectFullPath(string path)
        => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_projectRoot, path));

    private string ToManifestPath(string path) => AssetIoHelpers.ToManifestPath(_projectRoot, path);

    private IAssetProvider? MatchProvider(string path)
    {
        string normalized = AssetIoHelpers.NormalizePath(path);
        return _providers.FirstOrDefault(provider => provider.Matches(normalized));
    }

    private IAssetImporter? MatchImporter(string path)
    {
        string normalized = AssetIoHelpers.NormalizePath(path);
        return _importers.FirstOrDefault(importer => importer.MatchesSourcePath(normalized));
    }

    private bool IsUpToDate(string fullSourcePath, string sourcePath, IAssetImporter importer)
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

        string sourceMetaPath = SourceMetaManager.GetMetaPath(fullSourcePath);
        if (!File.Exists(sourceMetaPath))
        {
            return false;
        }

        SourceMeta sourceMeta = SourceMetaManager.Load(fullSourcePath);
        if (!string.Equals(sourceMeta.Importer, importer.ImporterName, StringComparison.Ordinal))
        {
            return false;
        }

        AssetImportFingerprint? currentFingerprint = importer.GetFingerprint(_projectRoot, fullSourcePath, sourceMeta);
        if (currentFingerprint == null)
        {
            return false;
        }

        foreach (AssetGuid guid in assets)
        {
            if (!Manifest.TryGetAsset(guid, out AssetManifestRecord record))
            {
                return false;
            }

            string assetPath = ToProjectFullPath(record.Path);
            if (!File.Exists(assetPath))
            {
                return false;
            }

            AssetMeta? assetMeta = AssetMetaManager.TryLoad(assetPath);
            if (assetMeta == null
                || assetMeta.SourceGuid != sourceGuid
                || assetMeta.ImporterVersion != currentFingerprint.ImporterVersion
                || !string.Equals(assetMeta.ContentFingerprint, currentFingerprint.ContentFingerprint, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void RegisterStore(IAssetProvider provider)
    {
        Type runtimeType = provider.RuntimeType;
        if (_stores.ContainsKey(runtimeType))
        {
            return; // Already registered (e.g. multiple providers for the same type)
        }

        _stores[runtimeType] = new ProviderStore(provider);
    }

    private void RegisterAssetFile(
        string fullPath,
        IAsset asset,
        IAssetProvider provider,
        SourceGuid sourceGuid = default,
        string subAssetKey = "",
        bool saveManifest = true)
    {
        AssetMeta? meta = AssetMetaManager.TryLoad(fullPath);
        if (meta != null && meta.AssetGuid != asset.AssetGuid)
        {
            throw new InvalidOperationException(
                $"Asset meta guid '{meta.AssetGuid}' does not match payload guid '{asset.AssetGuid}' for '{fullPath}'.");
        }

        if (meta != null && !sourceGuid.IsEmpty && meta.SourceGuid != sourceGuid)
        {
            throw new InvalidOperationException(
                $"Asset meta source guid '{meta.SourceGuid}' does not match registration source guid '{sourceGuid}' for '{fullPath}'.");
        }

        IReadOnlyList<AssetGuid> deps = provider.GetDependencies(fullPath);
        Manifest.AddAsset(
            asset.AssetGuid,
            asset.Name,
            ToManifestPath(fullPath),
            provider.AssetType,
            sourceGuid,
            subAssetKey,
            deps);

        if (_stores.TryGetValue(provider.RuntimeType, out ProviderStore? store)
            && store.Cache.Remove(asset.AssetGuid, out object? cached))
        {
            provider.Destroy(cached);
        }

        if (saveManifest)
        {
            Manifest.Save(_manifestDirectory);
        }
    }
}
