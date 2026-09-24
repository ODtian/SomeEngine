using SomeEngine.Rhi;

namespace SomeEngine.Render.Materials;

public sealed class Texture
{
    public string Name { get; init; } = string.Empty;
    internal TextureViewHandle View { get; init; }
}
