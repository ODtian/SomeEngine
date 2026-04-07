using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SomeEngine.Assets;

public readonly record struct AssetManifestRecord(
    AssetGuid Guid,
    string Name,
    string Path,
    string AssetType,
    SourceGuid SourceGuid,
    string SubAssetKey);

public sealed class AssetManifest
{
    public const string SourceIndexFileName = "source_index.json";
    public const string AssetIndexFileName = "asset_index.json";
    public const string DependencyGraphFileName = "dependency_graph.json";

    private readonly Dictionary<SourceGuid, string> _sources = [];
    private readonly Dictionary<string, SourceGuid> _sourceGuidsByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<AssetGuid, AssetManifestRecord> _assets = [];
    private readonly Dictionary<string, AssetGuid> _assetGuidsByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<AssetGuid, IReadOnlyList<AssetGuid>> _dependencies = [];
    private IReadOnlyDictionary<SourceGuid, IReadOnlyList<AssetGuid>>? _assetsBySource;
    private IReadOnlyDictionary<AssetGuid, IReadOnlyList<AssetGuid>>? _referencers;

    public IReadOnlyDictionary<SourceGuid, string> Sources => _sources;
    public IReadOnlyDictionary<AssetGuid, AssetManifestRecord> Assets => _assets;
    public IReadOnlyDictionary<AssetGuid, IReadOnlyList<AssetGuid>> Dependencies => _dependencies;

    public void AddSource(SourceGuid guid, string path)
    {
        if (guid.IsEmpty)
        {
            return;
        }

        string normalizedPath = AssetIoHelpers.NormalizePath(path);
        if (_sources.TryGetValue(guid, out string? previousPath))
        {
            _sourceGuidsByPath.Remove(previousPath);
        }

        _sources[guid] = normalizedPath;
        _sourceGuidsByPath[normalizedPath] = guid;
        _assetsBySource = null;
    }

    public void AddAsset(
        AssetGuid guid,
        string name,
        string path,
        string assetType,
        SourceGuid sourceGuid = default,
        string subAssetKey = "",
        IEnumerable<AssetGuid>? dependencies = null)
    {
        if (guid.IsEmpty)
        {
            throw new ArgumentException("Asset guid cannot be empty.", nameof(guid));
        }

        string normalizedPath = AssetIoHelpers.NormalizePath(path);
        if (_assets.TryGetValue(guid, out AssetManifestRecord existing))
        {
            _assetGuidsByPath.Remove(existing.Path);
        }

        _assets[guid] = new AssetManifestRecord(guid, name ?? string.Empty, normalizedPath, assetType ?? string.Empty, sourceGuid, subAssetKey ?? string.Empty);
        _assetGuidsByPath[normalizedPath] = guid;
        _dependencies[guid] = NormalizeDependencies(guid, dependencies);
        _assetsBySource = null;
        _referencers = null;
    }

    public bool TryGetSourcePath(SourceGuid guid, out string path) => _sources.TryGetValue(guid, out path!);
    public bool TryGetAsset(AssetGuid guid, out AssetManifestRecord record) => _assets.TryGetValue(guid, out record);
    public bool TryGetSourceGuid(string path, out SourceGuid guid) => _sourceGuidsByPath.TryGetValue(AssetIoHelpers.NormalizePath(path), out guid);

    public bool TryGetAssetByPath(string path, out AssetManifestRecord record)
    {
        if (_assetGuidsByPath.TryGetValue(AssetIoHelpers.NormalizePath(path), out AssetGuid guid) && _assets.TryGetValue(guid, out record))
        {
            return true;
        }

        record = default;
        return false;
    }

    public bool TryGetAssetBySourceAndSubAssetKey(SourceGuid sourceGuid, string subAssetKey, out AssetManifestRecord record)
    {
        EnsureIndexes();
        record = default;
        if (sourceGuid.IsEmpty || string.IsNullOrWhiteSpace(subAssetKey) || !_assetsBySource!.TryGetValue(sourceGuid, out IReadOnlyList<AssetGuid>? assetGuids))
        {
            return false;
        }

        foreach (AssetGuid guid in assetGuids)
        {
            if (_assets.TryGetValue(guid, out AssetManifestRecord asset) && string.Equals(asset.SubAssetKey, subAssetKey, StringComparison.Ordinal))
            {
                record = asset;
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<AssetGuid> GetAssetsBySource(SourceGuid sourceGuid)
    {
        EnsureIndexes();
        return _assetsBySource!.TryGetValue(sourceGuid, out IReadOnlyList<AssetGuid>? values) ? values : [];
    }

    public IReadOnlyList<AssetGuid> GetDependencies(AssetGuid guid) => _dependencies.TryGetValue(guid, out IReadOnlyList<AssetGuid>? values) ? values : [];

    public IReadOnlyList<AssetGuid> GetReferencers(AssetGuid guid)
    {
        EnsureIndexes();
        return _referencers!.TryGetValue(guid, out IReadOnlyList<AssetGuid>? values) ? values : [];
    }

    public IReadOnlyList<AssetManifestRecord> List(string? assetType = null)
        => _assets.Values
            .Where(record => string.IsNullOrWhiteSpace(assetType) || string.Equals(record.AssetType, assetType, StringComparison.Ordinal))
            .OrderBy(static record => record.Path, StringComparer.Ordinal)
            .ThenBy(static record => record.Guid.ToString(), StringComparer.Ordinal)
            .ToArray();

    public void Save(string manifestDirectory)
    {
        Directory.CreateDirectory(manifestDirectory = Path.GetFullPath(manifestDirectory));
        File.WriteAllText(Path.Combine(manifestDirectory, SourceIndexFileName), JsonSerializer.Serialize(new SourceIndexDocument
        {
            Sources = _sources.OrderBy(static pair => pair.Key.ToString(), StringComparer.Ordinal).Select(static pair => new SourceIndexEntryDocument
            {
                SourceGuid = pair.Key.ToFlatString(),
                Path = pair.Value,
            }).ToList(),
        }, AssetIoHelpers.JsonOptions));
        File.WriteAllText(Path.Combine(manifestDirectory, AssetIndexFileName), JsonSerializer.Serialize(new AssetIndexDocument
        {
            Assets = _assets.Values.OrderBy(static record => record.Guid.ToString(), StringComparer.Ordinal).Select(static record => new AssetIndexEntryDocument
            {
                AssetGuid = record.Guid.ToFlatString(),
                Name = record.Name,
                Path = record.Path,
                AssetType = record.AssetType,
                SourceGuid = record.SourceGuid.IsEmpty ? string.Empty : record.SourceGuid.ToFlatString(),
                SubAssetKey = record.SubAssetKey,
            }).ToList(),
        }, AssetIoHelpers.JsonOptions));
        File.WriteAllText(Path.Combine(manifestDirectory, DependencyGraphFileName), JsonSerializer.Serialize(new DependencyGraphDocument
        {
            Assets = _dependencies.OrderBy(static pair => pair.Key.ToString(), StringComparer.Ordinal).Select(static pair => new DependencyGraphEntryDocument
            {
                AssetGuid = pair.Key.ToFlatString(),
                Dependencies = pair.Value.Select(static guid => guid.ToFlatString()).ToList(),
            }).ToList(),
        }, AssetIoHelpers.JsonOptions));
    }

    public static AssetManifest Load(string manifestDirectory)
    {
        manifestDirectory = Path.GetFullPath(manifestDirectory);
        AssetManifest manifest = new();

        SourceIndexDocument sourceIndex = ReadDocument<SourceIndexDocument>(Path.Combine(manifestDirectory, SourceIndexFileName));
        foreach (SourceIndexEntryDocument entry in sourceIndex.Sources)
        {
            if (SourceGuid.TryParse(entry.SourceGuid, out SourceGuid guid))
            {
                manifest.AddSource(guid, entry.Path);
            }
        }

        Dictionary<AssetGuid, IReadOnlyList<AssetGuid>> dependencies = [];
        DependencyGraphDocument dependencyGraph = ReadDocument<DependencyGraphDocument>(Path.Combine(manifestDirectory, DependencyGraphFileName));
        foreach (DependencyGraphEntryDocument entry in dependencyGraph.Assets)
        {
            if (!AssetGuid.TryParse(entry.AssetGuid, out AssetGuid guid))
            {
                continue;
            }

            dependencies[guid] = entry.Dependencies
                .Select(static value => AssetGuid.TryParse(value, out AssetGuid dependency) ? dependency : AssetGuid.Empty)
                .Where(static dependency => !dependency.IsEmpty)
                .Distinct()
                .OrderBy(static dependency => dependency.ToString(), StringComparer.Ordinal)
                .ToArray();
        }

        AssetIndexDocument assetIndex = ReadDocument<AssetIndexDocument>(Path.Combine(manifestDirectory, AssetIndexFileName));
        foreach (AssetIndexEntryDocument entry in assetIndex.Assets)
        {
            if (!AssetGuid.TryParse(entry.AssetGuid, out AssetGuid guid))
            {
                continue;
            }

            SourceGuid sourceGuid = SourceGuid.TryParse(entry.SourceGuid, out SourceGuid parsedSourceGuid) ? parsedSourceGuid : SourceGuid.Empty;
            manifest.AddAsset(guid, entry.Name, entry.Path, entry.AssetType, sourceGuid, entry.SubAssetKey, dependencies.TryGetValue(guid, out IReadOnlyList<AssetGuid>? values) ? values : []);
        }

        return manifest;
    }

    private void EnsureIndexes()
    {
        if (_assetsBySource != null && _referencers != null)
        {
            return;
        }

        Dictionary<SourceGuid, List<AssetGuid>> assetsBySource = [];
        foreach (AssetManifestRecord record in _assets.Values)
        {
            if (record.SourceGuid.IsEmpty)
            {
                continue;
            }

            if (!assetsBySource.TryGetValue(record.SourceGuid, out List<AssetGuid>? values))
            {
                values = [];
                assetsBySource[record.SourceGuid] = values;
            }

            values.Add(record.Guid);
        }

        Dictionary<AssetGuid, HashSet<AssetGuid>> referencers = _assets.Keys.ToDictionary(static guid => guid, static _ => new HashSet<AssetGuid>());
        foreach ((AssetGuid owner, IReadOnlyList<AssetGuid> dependencies) in _dependencies)
        {
            foreach (AssetGuid dependency in dependencies)
            {
                if (!referencers.TryGetValue(dependency, out HashSet<AssetGuid>? values))
                {
                    values = [];
                    referencers[dependency] = values;
                }

                values.Add(owner);
            }
        }

        _assetsBySource = assetsBySource.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<AssetGuid>)pair.Value.OrderBy(static guid => guid.ToString(), StringComparer.Ordinal).ToArray());
        _referencers = referencers.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<AssetGuid>)pair.Value.OrderBy(static guid => guid.ToString(), StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<AssetGuid> NormalizeDependencies(AssetGuid owner, IEnumerable<AssetGuid>? dependencies)
        => dependencies?
            .Where(dependency => !dependency.IsEmpty && dependency != owner)
            .Distinct()
            .OrderBy(static dependency => dependency.ToString(), StringComparer.Ordinal)
            .ToArray() ?? [];

    private static TDocument ReadDocument<TDocument>(string path)
        where TDocument : new()
        => File.Exists(path)
            ? JsonSerializer.Deserialize<TDocument>(File.ReadAllText(path), AssetIoHelpers.JsonOptions) ?? new TDocument()
            : new TDocument();

    private sealed class SourceIndexDocument
    {
        public List<SourceIndexEntryDocument> Sources { get; set; } = [];
    }

    private sealed class SourceIndexEntryDocument
    {
        public string SourceGuid { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private sealed class AssetIndexDocument
    {
        public List<AssetIndexEntryDocument> Assets { get; set; } = [];
    }

    private sealed class AssetIndexEntryDocument
    {
        public string AssetGuid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string AssetType { get; set; } = string.Empty;
        public string SourceGuid { get; set; } = string.Empty;
        public string SubAssetKey { get; set; } = string.Empty;
    }

    private sealed class DependencyGraphDocument
    {
        public List<DependencyGraphEntryDocument> Assets { get; set; } = [];
    }

    private sealed class DependencyGraphEntryDocument
    {
        public string AssetGuid { get; set; } = string.Empty;
        public List<string> Dependencies { get; set; } = [];
    }
}
