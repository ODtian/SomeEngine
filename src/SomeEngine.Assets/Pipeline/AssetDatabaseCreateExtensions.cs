using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class AssetDatabaseCreateExtensions
{
    public static AssetGuid CreateAsset(this AssetDatabase database, string path, MaterialAsset asset)
        => database.CreateAsset(path, asset, MaterialAssetSerializer.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, MaterialInstanceAsset asset)
        => database.CreateAsset(path, asset, MaterialInstanceAssetSerializer.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, TextureAsset asset)
        => database.CreateAsset(path, asset, TextureAssetSerializer.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, ShaderAsset asset)
        => database.CreateAsset(path, asset, ShaderAssetSerializer.Save);

    public static AssetGuid CreateAsset(this AssetDatabase database, string path, MeshAsset asset)
        => database.CreateAsset(path, asset, MeshAssetSerializer.Save);
}

