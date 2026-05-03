using Diligent;
using Friflo.Engine.ECS;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Render.Materials;

internal sealed class MaterialTagBaker : IBaker<TagEntry>
{
    public void Bake(in TagEntry source, Entity entity)
    {
        switch (BakerName.Normalize(source.Name))
        {
            case "opaque":
                entity.AddTag<Opaque>();
                break;
            case "masked":
                entity.AddTag<Masked>();
                break;
            case "translucent":
                entity.AddTag<Translucent>();
                break;
            case "twosided":
                entity.AddTag<TwoSided>();
                break;
        }
    }
}

internal sealed class MaterialComponentBaker : IBaker<ComponentEntry>
{
    public void Bake(in ComponentEntry source, Entity entity)
    {
        if (string.IsNullOrWhiteSpace(source.Json))
        {
            return;
        }

        switch (BakerName.Normalize(source.TypeName))
        {
            case "overlayshade":
                entity.AddComponent(ReadJson<OverlayShade>(source.Json));
                break;
            case "stencilstate":
                entity.AddComponent(ReadJson<StencilState>(source.Json));
                break;
            case "clusterdeform":
                entity.AddComponent(ReadJson<ClusterDeform>(source.Json));
                break;
        }
    }

    private static T ReadJson<T>(string json)
        where T : struct, IComponent
        => System.Text.Json.JsonSerializer.Deserialize<T>(json, JsonOptions.Instance);
}

internal sealed class ShaderAttributeBaker : IBaker<ShaderAttributeBakeSource>
{
    public void Bake(in ShaderAttributeBakeSource source, Entity entity)
    {
        if (!TryResolveEntryPoint(in source, out string entryPoint))
        {
            return;
        }

        ShaderVariantRef variantRef = new(source.Shader, entryPoint);
        switch (source.Attribute.Name)
        {
            case "ClusterShade":
                entity.AddComponent(new ClusterShadeComponent { Default = variantRef });
                break;
            case "ClusterDeform":
                ClusterDeform deform = entity.TryGetComponent<ClusterDeform>(out ClusterDeform existingDeform)
                    ? existingDeform
                    : new ClusterDeform();
                deform.Default = variantRef;
                entity.AddComponent(deform);
                break;
            case "ClusterRaster":
                BakeRaster(in source, entity, variantRef);
                break;
            case "StencilConfig":
                BakeStencil(in source, entity);
                break;
        }
    }

    private static void BakeRaster(in ShaderAttributeBakeSource source, Entity entity, ShaderVariantRef variantRef)
    {
        if (source.Attribute.Args == null || source.Attribute.Args.Count == 0)
        {
            return;
        }

        ClusterRaster raster = entity.TryGetComponent<ClusterRaster>(out ClusterRaster existing)
            ? existing
            : new ClusterRaster();

        switch (BakerName.Normalize(source.Attribute.Args[0]))
        {
            case "swinline":
                raster.SWInline = variantRef;
                break;
            case "swcached":
                raster.SWCached = variantRef;
                break;
            case "hwvsinline":
                raster.HWVSInline = variantRef;
                break;
            case "hwvscached":
                raster.HWVSCached = variantRef;
                break;
            case "hwps":
                raster.HWPS = variantRef;
                break;
            default:
                return;
        }

        entity.AddComponent(raster);
    }

    private static void BakeStencil(in ShaderAttributeBakeSource source, Entity entity)
    {
        if (source.Attribute.Args == null || source.Attribute.Args.Count < 3)
        {
            return;
        }

        if (!byte.TryParse(source.Attribute.Args[0], out byte reference)
            || !TryParseComparisonFunction(source.Attribute.Args[1], out ComparisonFunction compare)
            || !TryParseStencilOp(source.Attribute.Args[2], out StencilOp passOp))
        {
            return;
        }

        entity.AddComponent(new StencilState
        {
            Ref = reference,
            Compare = compare,
            PassOp = passOp,
        });
    }

    private static bool TryResolveEntryPoint(in ShaderAttributeBakeSource source, out string entryPoint)
    {
        if (source.Attribute.VariantIndex < 0
            || source.Shader.Variants == null
            || source.Attribute.VariantIndex >= source.Shader.Variants.Count)
        {
            entryPoint = string.Empty;
            return false;
        }

        entryPoint = source.Shader.Variants[source.Attribute.VariantIndex].EntryPoint ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(source.PreferredEntryPoint)
            && !string.Equals(entryPoint, source.PreferredEntryPoint, StringComparison.Ordinal))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(entryPoint);
    }

    private static bool TryParseComparisonFunction(string? rawValue, out ComparisonFunction value)
        => TryParseGeneratedEnum(rawValue, MaterialEnumNames.ComparisonFunctionNames, MaterialEnumNames.ComparisonFunctionValues, out value);

    private static bool TryParseStencilOp(string? rawValue, out StencilOp value)
        => TryParseGeneratedEnum(rawValue, MaterialEnumNames.StencilOpNames, MaterialEnumNames.StencilOpValues, out value);

    private static bool TryParseGeneratedEnum<TEnum>(
        string? rawValue,
        IReadOnlyList<string> names,
        IReadOnlyList<TEnum> values,
        out TEnum value)
        where TEnum : struct
    {
        string normalized = BakerName.Normalize(rawValue);
        for (int i = 0; i < names.Count && i < values.Count; i++)
        {
            if (BakerName.Normalize(names[i]) == normalized)
            {
                value = values[i];
                return true;
            }
        }

        value = default;
        return false;
    }
}

internal static class MaterialEnumNames
{
    internal static readonly string[] ComparisonFunctionNames = ComparisonFunctionExtensions.GetNames();
    internal static readonly ComparisonFunction[] ComparisonFunctionValues = ComparisonFunctionExtensions.GetValues();
    internal static readonly string[] StencilOpNames = StencilOpExtensions.GetNames();
    internal static readonly StencilOp[] StencilOpValues = StencilOpExtensions.GetValues();
}

internal static class JsonOptions
{
    internal static readonly System.Text.Json.JsonSerializerOptions Instance = new()
    {
        IncludeFields = true,
    };
}

internal static class BakerName
{
    public static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
    }
}
