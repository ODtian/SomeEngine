using System;
using System.Collections.Generic;
using System.Linq;

namespace SomeEngine.Assets.Schema;

public partial class ShaderAsset : global::SomeEngine.Assets.IAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => Name ?? string.Empty;

    // Debug/tooling helper only. Scanner and manifest construction must not depend on this.
    internal ImportTraceData GetImportTraceData()
        => new()
        {
            SourceGuid = global::SomeEngine.Assets.SourceGuid.TryParse(ImportTrace?.SourceGuid, out global::SomeEngine.Assets.SourceGuid guid)
                ? guid
                : global::SomeEngine.Assets.SourceGuid.Empty,
            LastKnownSourcePath = ImportTrace?.SourcePath ?? string.Empty,
            SubAssetKey = ImportTrace?.SubAssetKey ?? string.Empty,
            ContentFingerprint = ImportTrace?.ContentFingerprint ?? string.Empty,
            Dependencies = ImportTrace?.Dependencies?.Select(static entry => new global::SomeEngine.Assets.DependencyEntryData
            {
                RelativePath = entry.Path ?? string.Empty,
                ContentHash = entry.ContentHash ?? string.Empty,
            }).ToArray() ?? [],
            ImporterVersion = ImportTrace?.ImporterVersion ?? 0,
        };
}

public partial class MaterialAsset : global::SomeEngine.Assets.IAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => Name ?? string.Empty;
}

public partial class MaterialInstanceAsset : global::SomeEngine.Assets.IAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name
        => !string.IsNullOrWhiteSpace(Parent) ? Parent! : ParentGuid ?? string.Empty;
}

public partial class MeshAsset : global::SomeEngine.Assets.IAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => Name ?? string.Empty;
}

internal sealed class ImportTraceData
{
    // Debug/tooling-only projection of embedded ImportTrace.
    public required global::SomeEngine.Assets.SourceGuid SourceGuid { get; init; }
    public required string LastKnownSourcePath { get; init; }
    public required string SubAssetKey { get; init; }
    public required string ContentFingerprint { get; init; }
    public required IReadOnlyList<global::SomeEngine.Assets.DependencyEntryData> Dependencies { get; init; }
    public required uint ImporterVersion { get; init; }
}
