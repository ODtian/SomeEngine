using System.Collections.Generic;

namespace SomeEngine.Assets;

public interface IAsset
{
    AssetGuid AssetGuid { get; }
    string Name { get; }
}

public interface IMutableAsset : IAsset
{
    void SetAssetGuid(AssetGuid guid);
}

public delegate void AssetSaveHandler<in TAsset>(TAsset asset, string path)
    where TAsset : class, IMutableAsset;

public readonly record struct ImportedAsset(IAsset Asset, string SubAssetKey, string OutputPath);

public enum AssetDiagnosticKind
{
    OrphanSourceMeta,
    MissingAssetFile,
    DanglingReference,
}

public enum AssetDiagnosticSeverity
{
    Warning,
    Error,
}

public sealed class AssetDiagnostic
{
    public required AssetDiagnosticKind Kind { get; init; }
    public required AssetDiagnosticSeverity Severity { get; init; }
    public required string Path { get; init; }
    public required string Message { get; init; }
    public AssetGuid AssetGuid { get; init; }
    public SourceGuid SourceGuid { get; init; }
    public AssetGuid RelatedAssetGuid { get; init; }
    public bool HasAsset => !AssetGuid.IsEmpty;
    public bool HasRelatedAsset => !RelatedAssetGuid.IsEmpty;
}

public sealed class DependencyEntryData
{
    public required string RelativePath { get; init; }
    public required string ContentHash { get; init; }
}
