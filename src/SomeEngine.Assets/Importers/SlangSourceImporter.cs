using System;
using System.Collections.Generic;
using System.IO;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public sealed class SlangSourceImporter : IAssetImporter
{
    private static readonly string[] Extensions = [".slang", ".hlsl"];

    public string ImporterName => nameof(SlangShaderImporter);
    public IReadOnlyList<string> SourceExtensions => Extensions;

    public bool MatchesSourcePath(string sourcePath)
        => SourceExtensions.Any(extension => sourcePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ImportedAsset> Import(string projectRoot, string sourcePath)
    {
        string fullPath = Path.IsPathRooted(sourcePath)
            ? Path.GetFullPath(sourcePath)
            : Path.GetFullPath(Path.Combine(projectRoot, sourcePath));
        SourceMeta sourceMeta = SourceMetaManager.GetOrCreate(fullPath, ImporterName);
        string outputPath = Path.ChangeExtension(fullPath, ".shader.asset");
        ShaderAsset asset = SlangShaderImporter.Import(fullPath, sourceMeta, AssetMetaManager.TryLoad(outputPath));
        return AssetGuid.TryParse(asset.AssetGuid, out AssetGuid guid) && !guid.IsEmpty
            ? [new ImportedAsset(asset, "shader:main", outputPath)]
            : [];
    }
}
