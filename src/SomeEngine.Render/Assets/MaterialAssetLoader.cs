using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Assets;

/// <summary>
/// 从 FlatBuffer MaterialAsset 加载 Material 实例。
/// 创建 Material + 设置 ShaderParamBag 参数 + 反序列化 Tag。
/// <para>
/// 贴图加载委托给调用者提供的 textureLoader 回调。
/// </para>
/// </summary>
public static class MaterialAssetLoader
{
    /// <summary>贴图加载回调。path → ITextureView?</summary>
    public delegate Diligent.ITextureView? TextureLoadFunc(string path);

    /// <summary>
    /// 从 FlatBuffer 字节加载 Material。
    /// </summary>
    public static Material Load(
        byte[] data,
        MaterialRegistry registry,
        TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialAssetSerializer.Parse(data);
        return LoadFromAsset(asset, registry, textureLoader);
    }

    /// <summary>
    /// 从文件路径加载 Material。
    /// </summary>
    public static Material LoadFromFile(
        string path,
        MaterialRegistry registry,
        TextureLoadFunc? textureLoader = null)
    {
        var asset = MaterialAssetSerializer.Load(path);
        return LoadFromAsset(asset, registry, textureLoader);
    }

    /// <summary>
    /// 从已解析的 FlatBuffer 对象加载 Material。
    /// </summary>
    public static Material LoadFromAsset(
        MaterialAsset asset,
        MaterialRegistry registry,
        TextureLoadFunc? textureLoader = null)
    {
        var material = new Material
        {
            Name = asset.Name ?? "",
            ShaderAssetName = GetShaderName(asset),
        };

        // 1. 加载贴图绑定
        if (asset.Textures != null)
        {
            foreach (var binding in asset.Textures)
            {
                if (binding.Name == null || binding.Path == null)
                    continue;

                var view = textureLoader?.Invoke(binding.Path);
                if (view != null)
                    material.SetTexture(binding.Name, view);
            }
        }

        // 2. 注册到 registry
        registry.Register(material);

        // 3. 反序列化 Tag
        if (asset.Passes != null)
        {
            foreach (var pass in material.Passes)
            {
                // 从第一个 PassEntry 取 tag（Phase 3 单 pass）
                if (asset.Passes.Count > 0)
                {
                    var passEntry = asset.Passes[0];
                    if (passEntry.Tags != null)
                    {
                        foreach (var tagEntry in passEntry.Tags)
                        {
                            if (tagEntry.Name == null) continue;
                            MaterialTagResolver.ApplyTag(registry, pass, tagEntry.Name, tagEntry.Value);
                        }
                    }
                }
            }
        }

        return material;
    }

    private static string GetShaderName(MaterialAsset asset)
    {
        if (asset.Passes is { Count: > 0 })
        {
            var firstPass = asset.Passes[0];
            if (firstPass.Shader != null)
                return firstPass.Shader;
        }
        return "";
    }
}
