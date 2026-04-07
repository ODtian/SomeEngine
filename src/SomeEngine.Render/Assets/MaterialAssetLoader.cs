using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Render.Assets;

/// <summary>
/// 从 FlatBuffer MaterialAsset 加载 Material 实例。
/// 创建 Material + 设置 ShaderParamBag 参数 + 反序列化 Tag。
/// </summary>
public static class MaterialAssetLoader
{
    /// <summary>贴图加载回调。path → ITextureView?</summary>
    public delegate Diligent.ITextureView? TextureLoadFunc(string path);

    /// <summary>Shader 加载回调。guid → ShaderAsset?</summary>
    public delegate ShaderAsset? ShaderLoadFunc(AssetGuid guid);

    public static Material Load(
        byte[] data,
        MaterialSystem materialSystem,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        MaterialAsset asset = MaterialAssetSerializer.Parse(data);
        return LoadFromAsset(asset, materialSystem, textureLoader, shaderLoader);
    }

    public static Material LoadFromFile(
        string path,
        MaterialSystem materialSystem,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        MaterialAsset asset = MaterialAssetSerializer.Load(path);
        return LoadFromAsset(asset, materialSystem, textureLoader, shaderLoader);
    }

    public static Material LoadFromAsset(
        MaterialAsset asset,
        MaterialSystem materialSystem,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        AssetGuid materialAssetGuid = AssetGuid.TryParse(asset.AssetGuid, out AssetGuid parsedMaterialGuid)
            ? parsedMaterialGuid
            : AssetGuid.Empty;
        Material material = new()
        {
            AssetGuid = materialAssetGuid,
            Name = asset.Name ?? string.Empty,
        };
        Friflo.Engine.ECS.Entity entity = materialSystem.Store.CreateEntity();
        entity.AddComponent(new MaterialRef { Owner = material });
        material.Entity = entity;
        material.System = materialSystem;

        if (asset.Passes is { Count: > 0 } && shaderLoader != null)
        {
            for (int i = 0; i < asset.Passes.Count; i++)
            {
                PassEntry passEntry = asset.Passes[i];
                if (!AssetGuid.TryParse(passEntry.ShaderGuid, out AssetGuid shaderGuid) || shaderGuid.IsEmpty)
                {
                    continue;
                }

                ShaderAsset? loadedShader = shaderLoader(shaderGuid);
                if (loadedShader == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(loadedShader.AssetGuid))
                {
                    loadedShader.AssetGuid = shaderGuid.ToFlatString();
                }

                ApplyShaderAuthoring(material.Entity, passEntry, loadedShader);
            }
        }

        if (asset.Textures != null)
        {
            foreach (TextureBinding binding in asset.Textures)
            {
                if (binding.Name == null || binding.Path == null)
                {
                    continue;
                }

                Diligent.ITextureView? view = textureLoader?.Invoke(binding.Path);
                if (view != null)
                {
                    material.SetTexture(binding.Name, view);
                }
            }
        }

        if (asset.Scalars != null)
        {
            foreach (ScalarParam scalar in asset.Scalars)
            {
                if (scalar.Name == null || scalar.Value == null)
                {
                    continue;
                }

                ApplyScalarParam(material.Params, scalar.Name, scalar.Value.Value);
            }
        }

        if (asset.Passes != null)
        {
            foreach (PassEntry passEntry in asset.Passes)
            {
                if (passEntry.Tags == null)
                {
                    continue;
                }

                foreach (TagEntry tagEntry in passEntry.Tags)
                {
                    if (!string.IsNullOrEmpty(tagEntry.Name))
                    {
                        MaterialEntityTags.Apply(material.Entity, tagEntry.Name);
                    }
                }
            }
        }

        return material;
    }

    /// <summary>将 ParamValue union 值写入 ShaderParamBag。</summary>
    public static void ApplyScalarParam(ShaderParamBag bag, string name, ParamValue value)
    {
        switch (value.Kind)
        {
            case ParamValue.ItemKind.FloatVal:
                bag.SetScalar(name, value.FloatVal!.V);
                break;
            case ParamValue.ItemKind.IntVal:
                bag.SetScalar(name, value.IntVal!.V);
                break;
            case ParamValue.ItemKind.BoolVal:
                bag.SetScalar(name, value.BoolVal!.V ? 1 : 0);
                break;
            case ParamValue.ItemKind.Vec2Val:
                Vec2Val v2 = value.Vec2Val!;
                bag.SetScalar(name, new System.Numerics.Vector4(v2.X, v2.Y, 0, 0));
                break;
            case ParamValue.ItemKind.Vec3Val:
                Vec3Val v3 = value.Vec3Val!;
                bag.SetScalar(name, new System.Numerics.Vector4(v3.X, v3.Y, v3.Z, 0));
                break;
            case ParamValue.ItemKind.Vec4Val:
                Vec4Val v4 = value.Vec4Val!;
                bag.SetScalar(name, new System.Numerics.Vector4(v4.X, v4.Y, v4.Z, v4.W));
                break;
        }
    }

    private static void ApplyShaderAuthoring(Friflo.Engine.ECS.Entity entity, PassEntry passEntry, ShaderAsset shader)
    {
        bool usedSerializedAttributes = false;
        if (shader.EntryPointAttributes != null)
        {
            for (int i = 0; i < shader.EntryPointAttributes.Count; i++)
            {
                ShaderEntryPointAttribute attribute = shader.EntryPointAttributes[i];
                if (attribute.Name == null)
                {
                    continue;
                }

                usedSerializedAttributes = ClusterRenderAuthoring.TryApply(
                    entity,
                    attribute.Name,
                    attribute.Args,
                    attribute.VariantIndex,
                    shader) || usedSerializedAttributes;
            }
        }

        if (!usedSerializedAttributes)
        {
            ApplyLegacyPassRole(entity, passEntry, shader);
        }
    }

    private static void ApplyLegacyPassRole(Friflo.Engine.ECS.Entity entity, PassEntry passEntry, ShaderAsset shader)
    {
        if (passEntry.Tags == null)
        {
            return;
        }

        for (int i = 0; i < passEntry.Tags.Count; i++)
        {
            string? name = passEntry.Tags[i].Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            switch (name)
            {
                case "ClusterShader":
                    int shadeVariant = FindVariantIndex(shader, passEntry.EntryPoint);
                    if (shadeVariant >= 0)
                    {
                        ClusterShadeAuthoring.Apply(entity, [], shadeVariant, shader);
                    }
                    break;
                case "ClusterRaster":
                    ApplyLegacyRasterAuthoring(entity, shader);
                    break;
                case "VertexDeform":
                    int deformVariant = FindVariantIndex(shader, passEntry.EntryPoint);
                    if (deformVariant >= 0)
                    {
                        SetClusterDeform(entity, shader, deformVariant);
                    }
                    break;
            }
        }
    }

    private static void ApplyLegacyRasterAuthoring(Friflo.Engine.ECS.Entity entity, ShaderAsset shader)
    {
        if (shader.Variants == null)
        {
            return;
        }

        for (int i = 0; i < shader.Variants.Count; i++)
        {
            ShaderBytecode variant = shader.Variants[i];
            if (variant.Stage != ShaderStage.Compute)
            {
                continue;
            }

            if (string.Equals(variant.EntryPoint, "CSSWRaster", StringComparison.Ordinal))
            {
                ClusterRasterAuthoring.Apply(entity, ["sw_inline"], i, shader);
            }
            else if (string.Equals(variant.EntryPoint, "CSSWRasterCached", StringComparison.Ordinal))
            {
                ClusterRasterAuthoring.Apply(entity, ["sw_cached"], i, shader);
            }
        }
    }

    private static void SetClusterDeform(Friflo.Engine.ECS.Entity entity, ShaderAsset shader, int variantIndex)
    {
        string? entryPoint = shader.Variants != null && variantIndex >= 0 && variantIndex < shader.Variants.Count
            ? shader.Variants[variantIndex].EntryPoint
            : null;

        if (entity.TryGetComponent<ClusterDeform>(out _))
        {
            ref ClusterDeform deform = ref entity.GetComponent<ClusterDeform>();
            deform.Default = new ShaderVariantRef(shader, entryPoint);
        }
        else
        {
            entity.AddComponent(new ClusterDeform { Default = new ShaderVariantRef(shader, entryPoint) });
        }
    }

    private static int FindVariantIndex(ShaderAsset shader, string? preferredEntryPoint)
    {
        if (shader.Variants == null)
        {
            return -1;
        }

        if (!string.IsNullOrEmpty(preferredEntryPoint))
        {
            for (int i = 0; i < shader.Variants.Count; i++)
            {
                if (string.Equals(shader.Variants[i].EntryPoint, preferredEntryPoint, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        for (int i = 0; i < shader.Variants.Count; i++)
        {
            if (shader.Variants[i].Stage == ShaderStage.Compute)
            {
                return i;
            }
        }

        return shader.Variants.Count > 0 ? 0 : -1;
    }
}
