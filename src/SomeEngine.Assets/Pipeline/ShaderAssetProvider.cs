using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public sealed class ShaderAssetProvider : AssetProvider<ShaderAsset>
{
    public override string AssetType => nameof(ShaderAsset);

    public override bool Matches(string assetPath)
        => assetPath.EndsWith(".shader.asset", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".slang.asset", StringComparison.OrdinalIgnoreCase);

    public override ShaderAsset Create(AssetGuid guid, string filePath)
    {
        ShaderAsset asset = ShaderAssetCodec.Load(filePath);
        if (asset.EntryPointReflections?.Count > 0)
            return asset;

        throw new InvalidOperationException(
            $"Shader asset '{asset.Name ?? Path.GetFileName(filePath)}' has no serialized entry-point reflection. Runtime shader loading does not compile or reflect source files; reimport '{filePath}' with the current Slang importer.");
    }
}
