using Friflo.Engine.ECS;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Pipelines;

public static class ClusterRenderAuthoring
{
    public static bool TryApply(Entity entity, AttributeReflection attribute, int variantIndex, ShaderAsset shader)
    {
        return attribute.Name switch
        {
            "ClusterRaster" => ApplyClusterRaster(entity, attribute, variantIndex, shader),
            "ClusterShade" => ApplyClusterShade(entity, variantIndex, shader),
            "StencilConfig" => ApplyStencil(entity, attribute, variantIndex, shader),
            _ => false,
        };
    }

    public static bool TryApply(Entity entity, string attributeName, IList<string>? args, int variantIndex, ShaderAsset shader)
    {
        return attributeName switch
        {
            "ClusterRaster" => ApplyClusterRaster(entity, args, variantIndex, shader),
            "ClusterShade" => ApplyClusterShade(entity, variantIndex, shader),
            "StencilConfig" => ApplyStencil(entity, args, variantIndex, shader),
            _ => false,
        };
    }

    private static bool ApplyClusterRaster(Entity entity, AttributeReflection attribute, int variantIndex, ShaderAsset shader)
    {
        ClusterRasterAuthoring.Apply(entity, GetArgs(attribute), variantIndex, shader);
        return true;
    }

    private static bool ApplyClusterRaster(Entity entity, IList<string>? args, int variantIndex, ShaderAsset shader)
    {
        ClusterRasterAuthoring.Apply(entity, AsSpan(args), variantIndex, shader);
        return true;
    }

    private static bool ApplyClusterShade(Entity entity, int variantIndex, ShaderAsset shader)
    {
        ClusterShadeAuthoring.Apply(entity, [], variantIndex, shader);
        return true;
    }

    private static bool ApplyStencil(Entity entity, AttributeReflection attribute, int variantIndex, ShaderAsset shader)
    {
        StencilConfigAuthoring.Apply(entity, GetArgs(attribute), variantIndex, shader);
        return true;
    }

    private static bool ApplyStencil(Entity entity, IList<string>? args, int variantIndex, ShaderAsset shader)
    {
        StencilConfigAuthoring.Apply(entity, AsSpan(args), variantIndex, shader);
        return true;
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

    private static ReadOnlySpan<string> AsSpan(IList<string>? args)
    {
        if (args == null || args.Count == 0)
        {
            return [];
        }

        if (args is string[] array)
        {
            return array;
        }

        var copy = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            copy[i] = args[i];
        }

        return copy;
    }
}
