using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public sealed class MaterialInstanceAssetProvider : AssetProvider<MaterialInstanceAsset>
{
    public override string AssetType => nameof(MaterialInstanceAsset);

    public override bool Matches(string assetPath) =>
        assetPath.EndsWith(".materialinstance.asset", StringComparison.OrdinalIgnoreCase)
        || assetPath.EndsWith(".matinst.asset", StringComparison.OrdinalIgnoreCase);

    public override MaterialInstanceAsset Create(AssetGuid guid, string filePath) =>
        MaterialInstanceAssetSerializer.Load(filePath);

    public override IReadOnlyList<AssetGuid> GetDependencies(string filePath)
    {
        MaterialInstanceAsset instance = MaterialInstanceAssetSerializer.Load(filePath);
        List<AssetGuid> dependencies = [];
        if (AssetGuid.TryParse(instance.ParentGuid, out AssetGuid parentGuid) && !parentGuid.IsEmpty)
        {
            dependencies.Add(parentGuid);
        }

        if (instance.Overrides != null)
        {
            foreach (ParamOverride paramOverride in instance.Overrides)
            {
                if (AssetGuid.TryParse(paramOverride.TextureGuid, out AssetGuid textureGuid)
                    && !textureGuid.IsEmpty
                    && !dependencies.Contains(textureGuid))
                {
                    dependencies.Add(textureGuid);
                }
            }
        }

        return dependencies;
    }
}
