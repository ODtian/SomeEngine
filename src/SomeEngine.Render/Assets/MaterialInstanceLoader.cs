using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Assets;

public static class MaterialInstanceLoader
{
    public delegate Material? ParentLoader(AssetGuid guid);

    public static Material Load(
        byte[] data,
        ParentLoader parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        MaterialInstanceAsset asset = MaterialInstanceCodec.Parse(data);
        return LoadFromAsset(asset, parentLoader, textureLoader);
    }

    public static Material LoadFromFile(
        string path,
        ParentLoader parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        MaterialInstanceAsset asset = MaterialInstanceCodec.Load(path);
        return LoadFromAsset(asset, parentLoader, textureLoader);
    }

    public static Material LoadFromAsset(
        MaterialInstanceAsset asset,
        ParentLoader parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        ArgumentNullException.ThrowIfNull(parentLoader);

        AssetGuid parentGuid = AssetGuid.TryParse(asset.ParentGuid, out AssetGuid parsedParent)
            ? parsedParent
            : AssetGuid.Empty;
        if (parentGuid.IsEmpty)
            throw new InvalidOperationException("MaterialInstanceAsset.ParentGuid is missing.");

        Material parent = parentLoader(parentGuid)
            ?? throw new InvalidOperationException($"Parent material not found for guid '{parentGuid}'.");

        Material instance = parent.Clone();
        if (asset.Overrides != null)
        {
            foreach (ParamOverride ovr in asset.Overrides)
            {
                if (ovr.Name == null
                    || ovr.TextureGuid == null
                    || !AssetGuid.TryParse(ovr.TextureGuid, out AssetGuid textureGuid)
                    || textureGuid.IsEmpty)
                {
                    continue;
                }

                Handle<Texture> texture = textureLoader?.Invoke(textureGuid) ?? default;
                if (texture.IsValid)
                    MaterialAssetLoader.ApplyTexture(instance, ovr.Name, texture);
            }
        }

        if (asset.ScalarOverrides != null)
        {
            foreach (ScalarOverride ovr in asset.ScalarOverrides)
            {
                if (ovr.Name != null && ovr.Value != null)
                    MaterialAssetLoader.ApplyScalar(instance, ovr.Name, ovr.Value.Value);
            }
        }

        return instance;
    }
}
