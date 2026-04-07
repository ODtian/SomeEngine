using System.Linq;
using Friflo.Engine.ECS;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Render.Materials;

internal static class MaterialEntityUtility
{
    internal static readonly ComponentTypes OverlayShadeComponent = ComponentTypes.Get<OverlayShade>();
    internal static readonly Tags MaskedTag = Tags.Get<Masked>();

    public static ulong ComputeMaterialSignature(Entity entity, ShaderVariantRef variantRef)
    {
        ulong hash = HashVariantRef(variantRef);
        hash = HashValue(hash, ComputeParamSignature(entity));
        return hash;
    }

    public static ulong ComputeSlotSignature(Entity entity)
    {
        ulong hash = 14695981039346656037UL;
        hash = HashVariant(hash, entity, static e => e.TryGetComponent<ClusterRaster>(out var value) ? value.SWInline : default);
        hash = HashVariant(hash, entity, static e => e.TryGetComponent<ClusterRaster>(out var value) ? value.SWCached : default);
        hash = HashVariant(hash, entity, static e => e.TryGetComponent<ClusterRaster>(out var value) ? value.HWVSInline : default);
        hash = HashVariant(hash, entity, static e => e.TryGetComponent<ClusterRaster>(out var value) ? value.HWVSCached : default);
        hash = HashVariant(hash, entity, static e => e.TryGetComponent<ClusterRaster>(out var value) ? value.HWPS : default);
        hash = HashVariant(hash, entity, static e => e.TryGetComponent<ClusterShadeComponent>(out var value) ? value.Default : default);
        hash = HashVariant(hash, entity, static e => e.TryGetComponent<ClusterDeform>(out var value) ? value.Default : default);

        if (entity.TryGetComponent<OverlayShade>(out var overlay))
        {
            hash = HashValue(hash, overlay.Layer);
        }

        if (entity.TryGetComponent<StencilState>(out var stencil))
        {
            hash = HashValue(hash, stencil.Ref);
            hash = HashValue(hash, (int)stencil.PassOp);
            hash = HashValue(hash, (int)stencil.Compare);
        }

        hash = HashValue(hash, entity.Tags.Has<Opaque>());
        hash = HashValue(hash, entity.Tags.Has<Masked>());
        hash = HashValue(hash, entity.Tags.Has<Translucent>());
        hash = HashValue(hash, entity.Tags.Has<TwoSided>());
        hash = HashValue(hash, ComputeParamSignature(entity));
        return hash;
    }

    public static IEnumerable<(string Name, Diligent.ShaderResourceType Type)> EnumerateResolvedResources(Entity entity, ShaderVariantRef variantRef)
    {
        if (!TryGetMaterial(entity, out var material))
        {
            yield break;
        }

        var bindings = GetOrderedMaterialBindings(variantRef.Shader);
        if (bindings.Length == 0)
        {
            foreach (var resource in material.Params.EnumerateResources())
            {
                yield return resource;
            }

            yield break;
        }

        foreach (var binding in bindings)
        {
            var type = binding.ResourceType switch
            {
                0 => Diligent.ShaderResourceType.TextureSrv,
                1 => Diligent.ShaderResourceType.Sampler,
                2 => Diligent.ShaderResourceType.ConstantBuffer,
                _ => (Diligent.ShaderResourceType?)null,
            };

            if (type.HasValue && !string.IsNullOrEmpty(binding.Name))
            {
                yield return (binding.Name!, type.Value);
            }
        }
    }

    public static ulong ComputeResolvedResourceLayoutHash(Entity entity, ShaderVariantRef variantRef)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var (name, type) in EnumerateResolvedResources(entity, variantRef))
        {
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            hash ^= (ulong)type;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    public static ulong ComputeResolvedParamSignature(Entity entity, ShaderVariantRef variantRef)
    {
        if (!TryGetMaterial(entity, out var material))
        {
            return 0UL;
        }

        var bindings = GetOrderedMaterialBindings(variantRef.Shader);
        if (bindings.Length == 0)
        {
            return material.Params.GetSignatureHash();
        }

        var names = new string[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            names[i] = bindings[i].Name ?? string.Empty;
        }

        return material.Params.GetFilteredSignatureHash(names, includeScalars: true);
    }

    public static void CloneMaterialIdentity(Material owner, Entity source, Entity target)
    {
        if (source.TryGetComponent<ClusterRaster>(out var raster))
        {
            target.AddComponent(raster);
        }

        if (source.TryGetComponent<ClusterShadeComponent>(out var shade))
        {
            target.AddComponent(shade);
        }

        if (source.TryGetComponent<ClusterDeform>(out var deform))
        {
            target.AddComponent(deform);
        }

        if (source.TryGetComponent<OverlayShade>(out var overlay))
        {
            target.AddComponent(overlay);
        }

        if (source.TryGetComponent<StencilState>(out var stencil))
        {
            target.AddComponent(stencil);
        }

        target.AddComponent(new MaterialRef { Owner = owner });

        if (source.Tags.Has<Opaque>())
        {
            target.AddTag<Opaque>();
        }

        if (source.Tags.Has<Masked>())
        {
            target.AddTag<Masked>();
        }

        if (source.Tags.Has<Translucent>())
        {
            target.AddTag<Translucent>();
        }

        if (source.Tags.Has<TwoSided>())
        {
            target.AddTag<TwoSided>();
        }
    }

    public static bool TryGetMaterial(Entity entity, out Material material)
    {
        if (entity.TryGetComponent<MaterialRef>(out var materialRef) && materialRef.Owner != null)
        {
            material = materialRef.Owner;
            return true;
        }

        material = null!;
        return false;
    }

    private static ulong ComputeParamSignature(Entity entity)
    {
        return TryGetMaterial(entity, out var material) ? material.Params.GetSignatureHash() : 0UL;
    }

    private static ShaderMaterialBinding[] GetOrderedMaterialBindings(ShaderAsset? shader)
    {
        if (shader?.Metadata?.MaterialBindings is not { Count: > 0 } bindings)
        {
            return [];
        }

        var ordered = bindings
            .Where(static binding => !string.IsNullOrEmpty(binding.Name))
            .ToArray();
        Array.Sort(ordered, static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return ordered;
    }

    private static ulong HashVariant(ulong hash, Entity entity, Func<Entity, ShaderVariantRef> getter)
    {
        return HashValue(hash, HashVariantRef(getter(entity)));
    }

    private static ulong HashVariantRef(ShaderVariantRef variantRef)
    {
        ulong hash = 14695981039346656037UL;
        hash = HashValue(hash, variantRef.Shader?.AssetGuid ?? variantRef.Shader?.Name ?? string.Empty);
        hash = HashValue(hash, variantRef.EntryPoint ?? string.Empty);
        return hash;
    }

    private static ulong HashValue<T>(ulong hash, T value)
    {
        int valueHash = value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(value);
        hash ^= unchecked((ulong)valueHash);
        hash *= 1099511628211UL;
        return hash;
    }
}
