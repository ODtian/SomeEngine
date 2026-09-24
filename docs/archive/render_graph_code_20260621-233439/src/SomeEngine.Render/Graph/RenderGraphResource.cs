using SomeEngine.Rhi;

namespace SomeEngine.Render.Graph;

public readonly struct RenderGraphHandle : IEquatable<RenderGraphHandle>
{
    private readonly int _id;
    private readonly int _owner;
    private readonly int _generation;

    internal RenderGraphHandle(int index, int owner, int generation)
    {
        _id = index + 1;
        _owner = owner;
        _generation = generation;
    }

    public bool IsValid => _id > 0 && _owner > 0 && _generation > 0;

    internal bool TryGetIndex(int owner, int generation, int count, out int index)
    {
        index = _id - 1;
        return _id > 0
            && _owner == owner
            && _generation == generation
            && index >= 0
            && index < count;
    }

    public static readonly RenderGraphHandle Invalid = default;

    public bool Equals(RenderGraphHandle other)
        => _id == other._id && _owner == other._owner && _generation == other._generation;

    public override bool Equals(object? obj)
        => obj is RenderGraphHandle other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(_id, _owner, _generation);

    public static bool operator ==(RenderGraphHandle left, RenderGraphHandle right)
        => left.Equals(right);

    public static bool operator !=(RenderGraphHandle left, RenderGraphHandle right)
        => !left.Equals(right);
}

public enum ResourceKind
{
    Texture,
    Buffer,
}

public readonly record struct ImportDesc(ResourceState InitialState)
{
    public ResourceState? FinalState { get; init; }
    public bool AllowWrite { get; init; }
}

internal readonly record struct TextureViewKey(
    ViewKind Kind,
    TextureViewDimension Dimension,
    Format Format,
    uint FirstMip,
    uint MipCount,
    uint FirstSlice,
    uint SliceCount)
{
    public static TextureViewKey From(TextureViewDesc desc)
        => new(desc.Kind, desc.Dimension, desc.Format, desc.FirstMip, desc.MipCount, desc.FirstSlice, desc.SliceCount);
}

internal readonly record struct BufferViewKey(
    ViewKind Kind,
    ulong Offset,
    ulong SizeInBytes,
    Format Format,
    uint StrideInBytes,
    bool Raw)
{
    public static BufferViewKey From(BufferViewDesc desc)
        => new(desc.Kind, desc.Offset, desc.SizeInBytes, desc.Format, desc.StrideInBytes, desc.Raw);
}

public readonly struct SubResourceRange(
    uint firstMipLevel,
    uint mipLevelCount,
    uint firstArraySlice = 0,
    uint arraySliceCount = uint.MaxValue)
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

    internal SubresourceRange ToRange() => new(FirstMipLevel, MipLevelCount, FirstArraySlice, ArraySliceCount);
}
