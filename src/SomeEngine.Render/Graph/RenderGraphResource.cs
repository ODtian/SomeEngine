using Diligent;

namespace SomeEngine.Render.Graph;

public readonly struct RenderGraphHandle(int index)
{
    internal readonly int Index = index;
    public bool IsValid => Index >= 0;
    public static readonly RenderGraphHandle Invalid = new(-1);
}

public enum ResourceKind
{
    Texture,
    Buffer,
}

internal struct RenderGraphResource(string name, ResourceKind kind)
{
    public string Name = name;
    public ResourceKind Kind = kind;
    public ResourceState CurrentState;
}

internal class CachedTexture : IDisposable
{
    public TextureDesc Desc;
    public ITexture? Texture;
    public int IdleFrames;
    public ulong LastUsedFence;
    public ResourceState LastState;
    public Dictionary<string, ITextureView> Views { get; } = new();

    public void Dispose()
    {
        foreach (var (_, view) in Views)
            view.Dispose();
        Views.Clear();
        Texture?.Dispose();
        Texture = null;
    }
}

internal class CachedBuffer : IDisposable
{
    public BufferDesc Desc;
    public IBuffer? Buffer;
    public int IdleFrames;
    public ulong LastUsedFence;
    public ResourceState LastState;
    public Dictionary<string, IBufferView> Views { get; } = new();

    public void Dispose()
    {
        foreach (var (_, view) in Views)
            view.Dispose();
        Views.Clear();
        Buffer?.Dispose();
        Buffer = null;
    }
}

public readonly struct SubResourceRange(
    uint firstMipLevel,
    uint mipLevelCount,
    uint firstArraySlice = 0,
    uint arraySliceCount = uint.MaxValue
)
{
    public static readonly SubResourceRange All = new(0, uint.MaxValue, 0, uint.MaxValue);

    public uint FirstMipLevel { get; } = firstMipLevel;
    public uint MipLevelCount { get; } = mipLevelCount;
    public uint FirstArraySlice { get; } = firstArraySlice;
    public uint ArraySliceCount { get; } = arraySliceCount;

    public bool IsAll =>
        FirstMipLevel == 0
        && MipLevelCount == uint.MaxValue
        && FirstArraySlice == 0
        && ArraySliceCount == uint.MaxValue;

    public static SubResourceRange Mip(uint mip) => new(mip, 1, 0, uint.MaxValue);

    public static SubResourceRange MipRange(uint first, uint count) =>
        new(first, count, 0, uint.MaxValue);
}
