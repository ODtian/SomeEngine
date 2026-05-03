using Diligent;

namespace SomeEngine.Render.Materials;

public sealed class MaterialResourceFallbacks
{
    public ITextureView? WhiteTexture { get; init; }
    public ITextureView? BlackTexture { get; init; }
    public ITextureView? FlatNormalTexture { get; init; }
    public IBufferView? DefaultBufferView { get; init; }
    public IBuffer? DefaultConstantBuffer { get; init; }
    public ISampler? DefaultSampler { get; init; }

    public ITextureView? ResolveTexture(string name)
    {
        if (name.Contains("normal", StringComparison.OrdinalIgnoreCase))
            return FlatNormalTexture ?? WhiteTexture;

        if (
            name.Contains("emissive", StringComparison.OrdinalIgnoreCase)
            || name.Contains("opacity", StringComparison.OrdinalIgnoreCase)
        )
        {
            return BlackTexture ?? WhiteTexture;
        }

        return WhiteTexture;
    }
}
