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

    /// <summary>Shader加载回调。name → ShaderAsset?</summary>
    public delegate ShaderAsset? ShaderLoadFunc(string name);

    /// <summary>
    /// 从 FlatBuffer 字节加载 Material。
    /// </summary>
    public static Material Load(
        byte[] data,
        MaterialRegistry registry,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        var asset = MaterialAssetSerializer.Parse(data);
        return LoadFromAsset(asset, registry, textureLoader, shaderLoader);
    }

    /// <summary>
    /// 从文件路径加载 Material。
    /// </summary>
    public static Material LoadFromFile(
        string path,
        MaterialRegistry registry,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        var asset = MaterialAssetSerializer.Load(path);
        return LoadFromAsset(asset, registry, textureLoader, shaderLoader);
    }

    /// <summary>
    /// 从已解析的 FlatBuffer 对象加载 Material。
    /// </summary>
    public static Material LoadFromAsset(
        MaterialAsset asset,
        MaterialRegistry registry,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        var material = new Material
        {
            Name = asset.Name ?? "",
            ShaderAssetName = GetShaderName(asset),
        };

        // 1. 加载 Shader
        if (asset.Passes is { Count: > 0 } && shaderLoader != null)
        {
            // Primary pass shader → Material.ShaderAsset（Resolve() 用它初始化 pass.Shader）
            var primaryShaderName = asset.Passes[0].Shader;
            if (!string.IsNullOrEmpty(primaryShaderName))
            {
                material.ShaderAsset = shaderLoader(primaryShaderName);
            }

            // 额外 pass（multi-pass 材质）
            // Force resolve so AddPass works
            _ = material.Passes;
            for (int i = 1; i < asset.Passes.Count; i++)
            {
                var passEntry = asset.Passes[i];
                var pass = material.AddPass();
                if (!string.IsNullOrEmpty(passEntry.Shader))
                {
                    pass.Shader = shaderLoader(passEntry.Shader);
                }
            }
        }

        // 2. 加载贴图绑定
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

        // 3. 加载标量参数
        if (asset.Scalars != null)
        {
            foreach (var scalar in asset.Scalars)
            {
                if (scalar.Name == null || scalar.Value == null) continue;
                ApplyScalarParam(material.Params, scalar.Name, scalar.Value.Value);
            }
        }

        // 4. 注册到 registry
        registry.Register(material);

        // 5. 反序列化 Tag
        if (asset.Passes != null)
        {
            for (int i = 0; i < asset.Passes.Count && i < material.Passes.Length; i++)
            {
                var passEntry = asset.Passes[i];
                var pass = material.Passes[i];
                
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

        return material;
    }

    /// <summary>将 ParamValue union 值写入 ShaderParamBag。</summary>
    public static void ApplyScalarParam(ShaderParamBag bag, string name, SomeEngine.Assets.Schema.ParamValue value)
    {
        switch (value.Kind)
        {
            case SomeEngine.Assets.Schema.ParamValue.ItemKind.FloatVal:
                bag.SetScalar(name, value.FloatVal!.V);
                break;
            case SomeEngine.Assets.Schema.ParamValue.ItemKind.IntVal:
                bag.SetScalar(name, value.IntVal!.V);
                break;
            case SomeEngine.Assets.Schema.ParamValue.ItemKind.BoolVal:
                bag.SetScalar(name, value.BoolVal!.V ? 1 : 0);
                break;
            case SomeEngine.Assets.Schema.ParamValue.ItemKind.Vec2Val:
                var v2 = value.Vec2Val!;
                bag.SetScalar(name, new System.Numerics.Vector4(v2.X, v2.Y, 0, 0));
                break;
            case SomeEngine.Assets.Schema.ParamValue.ItemKind.Vec3Val:
                var v3 = value.Vec3Val!;
                bag.SetScalar(name, new System.Numerics.Vector4(v3.X, v3.Y, v3.Z, 0));
                break;
            case SomeEngine.Assets.Schema.ParamValue.ItemKind.Vec4Val:
                var v4 = value.Vec4Val!;
                bag.SetScalar(name, new System.Numerics.Vector4(v4.X, v4.Y, v4.Z, v4.W));
                break;
        }
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
