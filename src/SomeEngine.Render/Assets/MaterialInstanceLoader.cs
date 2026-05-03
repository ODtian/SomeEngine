using Friflo.Engine.ECS;
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
        EntityStore materialStore,
        ParentMaterialLoadFunc parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Parse(data);
        return LoadFromAsset(asset, materialStore, parentLoader, textureLoader);
    }

    /// <summary>
    /// 从文件路径加载 MaterialInstance。
    /// </summary>
    public static Material LoadFromFile(
        string path,
        EntityStore materialStore,
        ParentMaterialLoadFunc parentLoader,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Load(path);
        return LoadFromAsset(asset, materialStore, parentLoader, textureLoader);
    }

    public static Material LoadFromAsset(
        MaterialInstanceAsset asset,
        EntityStore materialStore,
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

        if (!ReferenceEquals(parent.PassStore, materialStore))
        {
            throw new InvalidOperationException("Parent material is attached to a different pass entity store.");
        }

        var instance = parent.Instantiate();
        instance.AssetGuid = AssetGuid.TryParse(asset.AssetGuid, out var instanceGuid)
            ? instanceGuid
            : AssetGuid.Empty;

        if (asset.Overrides != null)
        {
            foreach (var ovr in asset.Overrides)
            {
                if (ovr.Name == null || ovr.TextureGuid == null) continue;
                if (!AssetGuid.TryParse(ovr.TextureGuid, out var texGuid) || texGuid.IsEmpty) continue;
                var view = textureLoader?.Invoke(texGuid);
                if (view != null)
                    instance.SetTexture(ovr.Name, view);
            }
        }

        if (asset.ScalarOverrides != null)
        {
            foreach (var ovr in asset.ScalarOverrides)
            {
                if (ovr.Name == null || ovr.Value == null) continue;
                MaterialAssetLoader.ApplyScalarParam(instance.Params, ovr.Name, ovr.Value.Value);
            }

            instance.Touch();
        }

        return instance;
    }
}
