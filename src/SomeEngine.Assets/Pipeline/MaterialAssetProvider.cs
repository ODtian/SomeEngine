using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public sealed class MaterialAssetProvider : AssetProvider<MaterialAsset>
{
    public override string AssetType => nameof(MaterialAsset);

    public override bool Matches(string assetPath)
        => assetPath.EndsWith(".material.asset", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".mat.asset", StringComparison.OrdinalIgnoreCase);

    public override MaterialAsset Create(AssetGuid guid, string filePath)
        => MaterialAssetCodec.Load(filePath);

    public override IReadOnlyList<AssetGuid> GetDependencies(string filePath)
    {
        MaterialAsset material = MaterialAssetCodec.Load(filePath);
        var deps = new HashSet<AssetGuid>();

        if (material.Passes != null)
        {
            foreach (PassEntry pass in material.Passes)
            {
                if (AssetGuid.TryParse(pass.ShaderGuid, out AssetGuid guid) && !guid.IsEmpty)
                    deps.Add(guid);
            }
        }

        if (material.Textures != null)
        {
            foreach (TextureBinding tex in material.Textures)
            {
                if (AssetGuid.TryParse(tex.TextureGuid, out AssetGuid guid) && !guid.IsEmpty)
                    deps.Add(guid);
            }
        }

        return deps.ToArray();
    }
}
