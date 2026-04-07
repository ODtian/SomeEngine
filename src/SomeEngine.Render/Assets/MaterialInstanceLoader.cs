using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Assets;

/// <summary>
/// 从 FlatBuffer MaterialInstanceAsset 加载 MaterialInstance。
/// 基于 parent Material 的 Instantiate()，然后覆盖指定贴图/标量参数和 Tag。
/// </summary>
public static class MaterialInstanceLoader
{
    public delegate Material? ParentMaterialLoadFunc(AssetGuid guid);

    /// <summary>
    /// 从 FlatBuffer 字节加载 MaterialInstance。
    /// </summary>
    public static Material Load(
        byte[] data,
        Material parent,
        MaterialSystem materialSystem,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Parse(data);
        return LoadFromAsset(asset, parent, materialSystem, textureLoader);
    }

    public static Material Load(
        byte[] data,
        MaterialSystem materialSystem,
        ParentMaterialLoadFunc parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Parse(data);
        return LoadFromAsset(asset, materialSystem, parentLoader, textureLoader);
    }

    /// <summary>
    /// 从文件路径加载 MaterialInstance。
    /// </summary>
    public static Material LoadFromFile(
        string path,
        Material parent,
        MaterialSystem materialSystem,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Load(path);
        return LoadFromAsset(asset, parent, materialSystem, textureLoader);
    }

    public static Material LoadFromFile(
        string path,
        MaterialSystem materialSystem,
        ParentMaterialLoadFunc parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Load(path);
        return LoadFromAsset(asset, materialSystem, parentLoader, textureLoader);
    }

    /// <summary>
    /// 从已解析的 FlatBuffer 对象加载 MaterialInstance。
    /// </summary>
    public static Material LoadFromAsset(
        MaterialInstanceAsset asset,
        Material parent,
        MaterialSystem materialSystem,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        if (!ReferenceEquals(parent.System, materialSystem))
        {
            throw new InvalidOperationException("Parent material is attached to a different MaterialSystem.");
        }

        var instance = parent.Instantiate();
        instance.AssetGuid = AssetGuid.TryParse(asset.AssetGuid, out var instanceGuid)
            ? instanceGuid
            : AssetGuid.Empty;

        // 2. Apply texture overrides
        if (asset.Overrides != null)
        {
            foreach (var ovr in asset.Overrides)
            {
                if (ovr.Name == null || ovr.Path == null) continue;
                var view = textureLoader?.Invoke(ovr.Path);
                if (view != null)
                    instance.SetTexture(ovr.Name, view);
            }
        }

        // 3. Apply scalar overrides
        if (asset.ScalarOverrides != null)
        {
            foreach (var ovr in asset.ScalarOverrides)
            {
                if (ovr.Name == null || ovr.Value == null) continue;
                MaterialAssetLoader.ApplyScalarParam(instance.Params, ovr.Name, ovr.Value.Value);
            }
        }

        if (asset.TagOverrides != null)
        {
            foreach (var ovr in asset.TagOverrides)
            {
                if (ovr.Name == null) continue;

                if (ovr.Remove)
                {
                    MaterialEntityTags.Remove(instance.Entity, ovr.Name);
                }
                else
                {
                    MaterialEntityTags.Apply(instance.Entity, ovr.Name);
                }
            }
        }

        return instance;
    }

    public static Material LoadFromAsset(
        MaterialInstanceAsset asset,
        MaterialSystem materialSystem,
        ParentMaterialLoadFunc parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var parentGuid = AssetGuid.TryParse(asset.ParentGuid, out var parsedParentGuid)
            ? parsedParentGuid
            : AssetGuid.Empty;

        if (parentGuid.IsEmpty)
        {
            throw new InvalidOperationException("MaterialInstanceAsset.ParentGuid is missing.");
        }

        var parent = parentLoader(parentGuid)
            ?? throw new InvalidOperationException($"Parent material not found for guid '{parentGuid}'.");

        return LoadFromAsset(asset, parent, materialSystem, textureLoader);
    }
}
