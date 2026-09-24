using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class AssetCreate
{
    public static AssetGuid CreateAsset(this AssetDatabase database, string path, MaterialAsset asset)
        => database.CreateAsset(path, asset, MaterialAssetCodec.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, ClusterRenderAsset asset)
        => database.CreateAsset(path, asset, ClusterRenderCodec.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, MaterialInstanceAsset asset)
        => database.CreateAsset(path, asset, MaterialInstanceCodec.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, TextureAsset asset)
        => database.CreateAsset(path, asset, TextureAssetCodec.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, ShaderAsset asset)
        => database.CreateAsset(path, asset, ShaderAssetCodec.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, MeshAsset asset)
        => database.CreateAsset(path, asset, MeshAssetCodec.Save);
}
