using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Assets;

/// <summary>
/// 从 FlatBuffer MaterialInstanceAsset 加载 MaterialInstance。
/// 基于 parent Material 的 Instantiate()，然后覆盖指定贴图参数。
/// </summary>
public static class MaterialInstanceLoader
{
    /// <summary>
    /// 从 FlatBuffer 字节加载 MaterialInstance。
    /// </summary>
    public static Material Load(
        byte[] data,
        Material parent,
        MaterialRegistry registry,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Parse(data);
        return LoadFromAsset(asset, parent, registry, textureLoader);
    }

    /// <summary>
    /// 从文件路径加载 MaterialInstance。
    /// </summary>
    public static Material LoadFromFile(
        string path,
        Material parent,
        MaterialRegistry registry,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialInstanceAssetSerializer.Load(path);
        return LoadFromAsset(asset, parent, registry, textureLoader);
    }

    /// <summary>
    /// 从已解析的 FlatBuffer 对象加载 MaterialInstance。
    /// </summary>
    public static Material LoadFromAsset(
        MaterialInstanceAsset asset,
        Material parent,
        MaterialRegistry registry,
        MaterialAssetLoader.TextureLoadFunc? textureLoader = null)
    {
        // 1. Clone parent
        var instance = parent.Instantiate();

        // 2. Apply overrides
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

        // 3. Register
        registry.Register(instance);

        // 4. Copy parent's tags to instance passes
        foreach (var pass in instance.Passes)
        {
            foreach (var parentPass in parent.Passes)
            {
                // Copy opaque/masked/etc tags from parent
                CopyTagIfPresent<OpaqueTag>(registry, parentPass, pass);
                CopyTagIfPresent<MaskedTag>(registry, parentPass, pass);
                CopyTagIfPresent<TranslucentTag>(registry, parentPass, pass);
                CopyTagIfPresent<TwoSidedTag>(registry, parentPass, pass);
                CopyTagIfPresent<ShadowCasterTag>(registry, parentPass, pass);
                CopyTagIfPresent<ClusterShaderTag>(registry, parentPass, pass);
                break; // Single pass per material for now
            }
        }

        return instance;
    }

    private static void CopyTagIfPresent<T>(MaterialRegistry registry, MaterialPass from, MaterialPass to)
        where T : struct, IMaterialTag
    {
        var tag = registry.GetTag<T>(from);
        if (tag.HasValue)
            registry.SetTag(to, tag.Value);
    }
}
