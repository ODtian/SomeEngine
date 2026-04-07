using Friflo.Engine.ECS;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

public sealed class ClusterShadeAuthoring : IMaterialAuthoring
{
    public string AttributeName => "ClusterShade";

    public void Apply(Entity entity, AttributeReflection attribute, int variantIndex, ShaderAsset shader)
    {
        Apply(entity, [], variantIndex, shader);
    }

    internal static void Apply(Entity entity, ReadOnlySpan<string> args, int variantIndex, ShaderAsset shader)
    {
        ref var shade = ref GetOrAddComponent<ClusterShadeComponent>(entity);
        shade.Default = new ShaderVariantRef(shader, GetEntryPoint(shader, variantIndex));
    }

    private static ref T GetOrAddComponent<T>(Entity entity) where T : struct, IComponent
    {
        if (!entity.TryGetComponent<T>(out _))
        {
            entity.AddComponent(new T());
        }

        return ref entity.GetComponent<T>();
    }

    private static string? GetEntryPoint(ShaderAsset shader, int variantIndex)
    {
        return shader.Variants != null && variantIndex >= 0 && variantIndex < shader.Variants.Count
            ? shader.Variants[variantIndex].EntryPoint
            : null;
    }
}
