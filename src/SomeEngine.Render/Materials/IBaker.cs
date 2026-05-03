using Friflo.Engine.ECS;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Materials;

public interface IBaker<TSource>
{
    void Bake(in TSource source, Entity entity);
}

public readonly record struct ShaderAttributeBakeSource(
    ShaderEntryPointAttribute Attribute,
    ShaderAsset Shader,
    string? PreferredEntryPoint);

internal static partial class Baker
{
    private static readonly MaterialTagBaker s_tagBaker = new();
    private static readonly MaterialComponentBaker s_componentBaker = new();
    private static readonly ShaderAttributeBaker s_shaderAttributeBaker = new();

    public static void Apply(in TagEntry source, Entity entity)
    {
        s_tagBaker.Bake(in source, entity);
    }

    public static void Apply(in ComponentEntry source, Entity entity)
    {
        s_componentBaker.Bake(in source, entity);
    }

    public static void Apply(in ShaderAttributeBakeSource source, Entity entity)
    {
        s_shaderAttributeBaker.Bake(in source, entity);
    }
}
