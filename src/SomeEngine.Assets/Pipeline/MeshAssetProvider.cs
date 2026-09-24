using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public sealed class MeshAssetProvider : AssetProvider<MeshAsset>
{
    public override string AssetType => nameof(MeshAsset);

    public override bool Matches(string assetPath)
        => assetPath.EndsWith(".mesh.asset", StringComparison.OrdinalIgnoreCase);

    public override MeshAsset Create(AssetGuid guid, string filePath)
        => MeshAssetCodec.Load(filePath);
}
