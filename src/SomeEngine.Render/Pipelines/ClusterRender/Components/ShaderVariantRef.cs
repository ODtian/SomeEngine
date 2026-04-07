using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Pipelines;

public readonly record struct ShaderVariantRef(ShaderAsset? Shader, string? EntryPoint)
{
    public bool IsEmpty => Shader == null || string.IsNullOrEmpty(EntryPoint);
}
