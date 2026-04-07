using Friflo.Engine.ECS;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

public sealed class ClusterRasterAuthoring : IMaterialAuthoring
{
    public string AttributeName => "ClusterRaster";

    public void Apply(Entity entity, AttributeReflection attribute, int variantIndex, ShaderAsset shader)
    {
        Apply(entity, GetArgs(attribute), variantIndex, shader);
    }

    internal static void Apply(Entity entity, ReadOnlySpan<string> args, int variantIndex, ShaderAsset shader)
    {
        string key = args.Length > 0 ? args[0] : "sw_inline";
        ref var raster = ref GetOrAddComponent<ClusterRaster>(entity);
        var variantRef = new ShaderVariantRef(shader, GetEntryPoint(shader, variantIndex));

        switch (key)
        {
            case "sw_inline":
                raster.SWInline = variantRef;
                break;
            case "sw_cached":
                raster.SWCached = variantRef;
                break;
            case "hw_vs_inline":
                raster.HWVSInline = variantRef;
                break;
            case "hw_vs_cached":
                raster.HWVSCached = variantRef;
                break;
            case "hw_ps":
                raster.HWPS = variantRef;
                break;
        }
    }

    private static ref T GetOrAddComponent<T>(Entity entity) where T : struct, IComponent
    {
        if (!entity.TryGetComponent<T>(out _))
        {
            entity.AddComponent(new T());
        }

        return ref entity.GetComponent<T>();
    }

    private static string[] GetArgs(AttributeReflection attribute)
    {
        var args = new string[attribute.ArgumentCount];
        for (uint i = 0; i < attribute.ArgumentCount; i++)
        {
            args[i] = attribute.GetArgumentValueString(i);
        }

        return args;
    }

    private static string? GetEntryPoint(ShaderAsset shader, int variantIndex)
    {
        return shader.Variants != null && variantIndex >= 0 && variantIndex < shader.Variants.Count
            ? shader.Variants[variantIndex].EntryPoint
            : null;
    }
}
