namespace SomeEngine.Rhi;

public readonly record struct Viewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth = 0,
    float MaxDepth = 1);

public readonly record struct Rect(int X, int Y, int Width, int Height);

public readonly record struct Color(float R, float G, float B, float A)
{
    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color Black = new(0, 0, 0, 1);
}

public readonly record struct ClearDepthStencil(float Depth, byte Stencil = 0);

public readonly record struct ClearValue(Format Format, Color Color, ClearDepthStencil DepthStencil)
{
    public static ClearValue FromColor(Format format, Color color) => new(format, color, default);
    public static ClearValue FromDepthStencil(Format format, ClearDepthStencil depthStencil) => new(format, default, depthStencil);
}

public readonly record struct SubresourceRange(
    uint FirstMip,
    uint MipCount,
    uint FirstSlice = 0,
    uint SliceCount = uint.MaxValue)
{
    public static readonly SubresourceRange All = new(0, uint.MaxValue, 0, uint.MaxValue);

    public bool CoversAll(TextureDesc texture)
        => FirstMip == 0
            && (MipCount == uint.MaxValue || MipCount == texture.MipLevels)
            && FirstSlice == 0
            && (SliceCount == uint.MaxValue || SliceCount == texture.ArraySize);
}

public readonly record struct TextureCopyRegion(
    uint MipLevel,
    uint ArraySlice,
    uint X,
    uint Y,
    uint Z,
    uint Width,
    uint Height,
    uint Depth);

public readonly record struct BufferTextureCopy(
    ulong Offset,
    uint RowPitch,
    uint SlicePitch);

public readonly record struct ResourceMemoryRequirements(
    ulong SizeInBytes,
    ulong Alignment,
    MemoryHeapKind HeapKind,
    bool RequiresDedicatedAllocation,
    bool PrefersDedicatedAllocation);

public readonly record struct ResourceAllocationInfo(
    ResourceOwnership Ownership,
    MemoryClass Memory,
    MemoryHeapHandle Heap,
    ulong HeapOffset,
    ulong SizeInBytes);

public readonly record struct MemoryBudget(
    MemoryClass Memory,
    ulong BudgetInBytes,
    ulong CurrentUsageInBytes,
    bool IsExact);

public readonly record struct AliasingResource
{
    public AliasingResourceKind Kind { get; init; }
    public BufferHandle Buffer { get; init; }
    public TextureHandle Texture { get; init; }

    public static AliasingResource None => default;
    public static AliasingResource BufferResource(BufferHandle buffer)
        => new() { Kind = AliasingResourceKind.Buffer, Buffer = buffer };
    public static AliasingResource TextureResource(TextureHandle texture)
        => new() { Kind = AliasingResourceKind.Texture, Texture = texture };
}
