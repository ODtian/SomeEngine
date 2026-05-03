using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public sealed class ShaderAssetProvider : AssetProvider<ShaderAsset>
{
    public override string AssetType => nameof(ShaderAsset);

    public override bool Matches(string assetPath)
        => assetPath.EndsWith(".shader.asset", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".slang.asset", StringComparison.OrdinalIgnoreCase);

    public override ShaderAsset Create(AssetGuid guid, string filePath)
        => ShaderAssetSerializer.Load(filePath);
}
