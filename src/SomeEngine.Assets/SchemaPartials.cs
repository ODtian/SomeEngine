namespace SomeEngine.Assets.Schema;

public partial class ShaderAsset : global::SomeEngine.Assets.IMutableAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => Name ?? string.Empty;

    void global::SomeEngine.Assets.IMutableAsset.SetAssetGuid(global::SomeEngine.Assets.AssetGuid guid)
        => AssetGuid = guid.ToFlatString();

}

public partial class MaterialAsset : global::SomeEngine.Assets.IMutableAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => Name ?? string.Empty;

    void global::SomeEngine.Assets.IMutableAsset.SetAssetGuid(global::SomeEngine.Assets.AssetGuid guid)
        => AssetGuid = guid.ToFlatString();
}

public partial class MaterialInstanceAsset : global::SomeEngine.Assets.IMutableAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => ParentGuid ?? string.Empty;

    void global::SomeEngine.Assets.IMutableAsset.SetAssetGuid(global::SomeEngine.Assets.AssetGuid guid)
        => AssetGuid = guid.ToFlatString();
}

public partial class MeshAsset : global::SomeEngine.Assets.IMutableAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => Name ?? string.Empty;

    void global::SomeEngine.Assets.IMutableAsset.SetAssetGuid(global::SomeEngine.Assets.AssetGuid guid)
        => AssetGuid = guid.ToFlatString();
}

public partial class TextureAsset : global::SomeEngine.Assets.IMutableAsset
{
    global::SomeEngine.Assets.AssetGuid global::SomeEngine.Assets.IAsset.AssetGuid
        => global::SomeEngine.Assets.AssetGuid.TryParse(AssetGuid, out global::SomeEngine.Assets.AssetGuid guid)
            ? guid
            : global::SomeEngine.Assets.AssetGuid.Empty;

    string global::SomeEngine.Assets.IAsset.Name => Name ?? string.Empty;

    void global::SomeEngine.Assets.IMutableAsset.SetAssetGuid(global::SomeEngine.Assets.AssetGuid guid)
        => AssetGuid = guid.ToFlatString();
}
