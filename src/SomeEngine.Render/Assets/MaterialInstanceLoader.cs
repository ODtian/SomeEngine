using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Assets;

/// <summary>
/// 从 FlatBuffer MaterialInstanceAsset 加载 MaterialInstance。
/// 基于 parent Material 的 Instantiate()，然后覆盖指定贴图/标量参数和 Tag。
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

        // 4. Register
        registry.Register(instance);

        // 5. 通用 tag 继承：复制 parent 的所有 tag (per-pass 配对)
        int passCount = Math.Min(parent.Passes.Length, instance.Passes.Length);
        for (int i = 0; i < passCount; i++)
        {
            registry.CopyAllTags(parent.Passes[i], instance.Passes[i]);
        }

        // 6. Apply tag overrides
        if (asset.TagOverrides != null)
        {
            foreach (var ovr in asset.TagOverrides)
            {
                if (ovr.Name == null) continue;

                // Apply to primary pass (index 0)
                if (instance.Passes.Length > 0)
                {
                    var pass = instance.Passes[0];
                    if (ovr.Remove)
                    {
                        // 通过名称移除 tag（利用源生成的 resolver 反推类型不太可行，
                        // 改为 RemoveAllTags + 重新 ApplyTag 除了被移除的）
                        // 简化方案：单独用 ApplyTag 覆盖，remove=true 时不处理
                        // 由于 TagStore 只有泛型 RemoveTag<T>，这里暂不支持按名称移除
                        // TODO: 后续可通过扩展 MaterialTagResolver 添加 RemoveTag(name) 方法
                    }
                    else
                    {
                        MaterialTagResolver.ApplyTag(registry, pass, ovr.Name, ovr.Value);
                    }
                }
            }
        }

        return instance;
    }
}
