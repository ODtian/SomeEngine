using Friflo.Engine.ECS;
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
    /// <summary>贴图加载回调。guid → ITextureView?</summary>
    public delegate Diligent.ITextureView? TextureLoadFunc(AssetGuid textureGuid);
    public delegate ShaderAsset? ShaderLoadFunc(AssetGuid guid);

    public static Material Load(
        byte[] data,
        EntityStore materialStore,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        MaterialAsset asset = MaterialAssetSerializer.Parse(data);
        return LoadFromAsset(asset, materialStore, textureLoader, shaderLoader);
    }

    public static Material LoadFromFile(
        string path,
        EntityStore materialStore,
        TextureLoadFunc? textureLoader = null,
        ShaderLoadFunc? shaderLoader = null)
    {
        MaterialAsset asset = MaterialAssetSerializer.Load(path);
        return LoadFromAsset(asset, materialStore, textureLoader, shaderLoader);
    }

    public static Material LoadFromAsset(
        MaterialAsset asset,
        EntityStore materialStore,
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
        material.PassStore = materialStore;

        var passEntities = new List<Friflo.Engine.ECS.Entity>();
        ShaderMaterialScalarLayout? scalarLayout = null;

        if (asset.Passes is { Count: > 0 })
        {
            for (int i = 0; i < asset.Passes.Count; i++)
            {
                PassEntry passEntry = asset.Passes[i];
                Friflo.Engine.ECS.Entity entity = materialStore.CreateEntity();
                entity.AddComponent(new MaterialRef { Owner = material });

                if (passEntry.Tags != null)
                {
                    foreach (TagEntry tagEntry in passEntry.Tags)
                    {
                        Baker.Apply(in tagEntry, entity);
                    }
                }

                if (passEntry.Components != null)
                {
                    foreach (ComponentEntry componentEntry in passEntry.Components)
                    {
                        Baker.Apply(in componentEntry, entity);
                    }
                }

                if (shaderLoader == null
                    || !AssetGuid.TryParse(passEntry.ShaderGuid, out AssetGuid shaderGuid)
                    || shaderGuid.IsEmpty)
                {
                    passEntities.Add(entity);
                    continue;
                }

                ShaderAsset? shader = shaderLoader(shaderGuid);
                scalarLayout ??= SelectMaterialScalarLayout(shader);
                if (shader?.EntryPointAttributes == null)
                {
                    passEntities.Add(entity);
                    continue;
                }

                for (int attrIndex = 0; attrIndex < shader.EntryPointAttributes.Count; attrIndex++)
                {
                    ShaderEntryPointAttribute attribute = shader.EntryPointAttributes[attrIndex];
                    ShaderAttributeBakeSource bakeSource = new(attribute, shader, passEntry.EntryPoint);
                    Baker.Apply(in bakeSource, entity);
                }

                if (TryMergeClusterRasterPass(passEntities, entity))
                {
                    entity.DeleteEntity();
                    continue;
                }

                passEntities.Add(entity);
            }
        }

        if (passEntities.Count == 0)
        {
            Friflo.Engine.ECS.Entity entity = materialStore.CreateEntity();
            entity.AddComponent(new MaterialRef { Owner = material });
            passEntities.Add(entity);
        }

        material.PassEntities = [.. passEntities];

        if (asset.Textures != null)
        {
            foreach (TextureBinding binding in asset.Textures)
            {
                if (binding.Name == null || binding.TextureGuid == null)
                {
                    continue;
                }

                if (!AssetGuid.TryParse(binding.TextureGuid, out AssetGuid textureGuid) || textureGuid.IsEmpty)
                {
                    continue;
                }

                Diligent.ITextureView? view = textureLoader?.Invoke(textureGuid);
                material.SetTexture(binding.Name, view);
            }
        }

        MaterialScalarRegionLayout scalarRegionLayout = MaterialScalarRegionLayout.FromShaderLayout(scalarLayout);
        if (asset.Scalars is { Count: > 0 } && scalarRegionLayout.PayloadByteSize == 0)
        {
            throw new InvalidOperationException(
                $"Material '{material.Name}' declares scalar parameters, but no shader material scalar layout was found.");
        }

        material.SetScalarRegionLayout(scalarRegionLayout);

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

        return material;
    }

    /// <summary>将 ParamValue union 值写入 ShaderParamBag。</summary>
    private static ShaderMaterialScalarLayout? SelectMaterialScalarLayout(ShaderAsset? shader)
    {
        if (shader?.Metadata?.MaterialScalarLayouts == null)
        {
            return null;
        }

        foreach (ShaderMaterialScalarLayout layout in shader.Metadata.MaterialScalarLayouts)
        {
            if (!string.IsNullOrWhiteSpace(layout.Name))
            {
                return layout;
            }
        }

        return null;
    }

    private static bool TryMergeClusterRasterPass(
        List<Friflo.Engine.ECS.Entity> passEntities,
        Friflo.Engine.ECS.Entity source)
    {
        if (!source.TryGetComponent<ClusterRaster>(out ClusterRaster sourceRaster))
        {
            return false;
        }

        foreach (Friflo.Engine.ECS.Entity target in passEntities)
        {
            if (!target.TryGetComponent<ClusterRaster>(out _)
                || !HaveSameMaterialTags(target, source))
            {
                continue;
            }

            ref ClusterRaster targetRaster = ref target.GetComponent<ClusterRaster>();
            MergeVariant(ref targetRaster.SWInline, sourceRaster.SWInline);
            MergeVariant(ref targetRaster.SWCached, sourceRaster.SWCached);
            MergeVariant(ref targetRaster.HWVSInline, sourceRaster.HWVSInline);
            MergeVariant(ref targetRaster.HWVSCached, sourceRaster.HWVSCached);
            MergeVariant(ref targetRaster.HWPS, sourceRaster.HWPS);
            return true;
        }

        return false;
    }

    private static void MergeVariant(ref ShaderVariantRef target, ShaderVariantRef source)
    {
        if (target.IsEmpty && !source.IsEmpty)
        {
            target = source;
        }
    }

    private static bool HaveSameMaterialTags(
        Friflo.Engine.ECS.Entity left,
        Friflo.Engine.ECS.Entity right)
    {
        return left.Tags.Has<Opaque>() == right.Tags.Has<Opaque>()
            && left.Tags.Has<Masked>() == right.Tags.Has<Masked>()
            && left.Tags.Has<Translucent>() == right.Tags.Has<Translucent>()
            && left.Tags.Has<TwoSided>() == right.Tags.Has<TwoSided>();
    }

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
}
