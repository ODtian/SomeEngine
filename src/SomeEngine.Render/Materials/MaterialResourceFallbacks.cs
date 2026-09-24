using SomeEngine.Rhi;

namespace SomeEngine.Render.Materials;

public sealed class MaterialResourceFallbacks
{
    public TextureViewHandle WhiteTexture { get; init; }
    public TextureViewHandle BlackTexture { get; init; }
    public TextureViewHandle FlatNormalTexture { get; init; }
    public TextureViewHandle LightCookieAtlas { get; init; }
    public BufferViewHandle DefaultBufferView { get; init; }
    public BufferViewHandle DefaultRawView { get; init; }
    public BufferViewHandle DefaultRawUnorderedAccessView { get; init; }
    public BufferViewHandle DefaultConstantBufferView { get; init; }
    public SamplerHandle DefaultSampler { get; init; }
    public SamplerHandle LightCookieSampler { get; init; }

    public TextureViewHandle ResolveTexture(string name)
    {
        switch (name)
        {
            case "NormalMap":
                return FlatNormalTexture.IsValid ? FlatNormalTexture : WhiteTexture;
            case "EmissiveMap":
                return BlackTexture.IsValid ? BlackTexture : WhiteTexture;
            default:
                return WhiteTexture;
        }
    }
}
