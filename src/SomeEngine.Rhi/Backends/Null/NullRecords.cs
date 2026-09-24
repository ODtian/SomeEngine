namespace SomeEngine.Rhi.Backends.Null;

internal sealed class BufferRecord(
    BufferDesc desc,
    byte[] data,
    ResourceAllocationInfo allocation)
{
    public BufferDesc Desc { get; } = desc;
    public byte[] Data { get; } = data;
    public ResourceAllocationInfo Allocation { get; } = allocation;
    public ResourceState State { get; set; } = desc.InitialState;
    public bool Mapped { get; set; }
    public ulong MappedOffset { get; set; }
    public ulong MappedSize { get; set; }
}

internal sealed class TextureRecord
{
    private readonly TextureSubresourceData?[] _subresources;
    private readonly ResourceState[] _states;

    public TextureRecord(TextureDesc desc, ResourceAllocationInfo allocation)
    {
        Desc = desc;
        Allocation = allocation;
        int subresourceCount = checked((int)((ulong)desc.MipLevels * desc.ArraySize));
        _subresources = new TextureSubresourceData?[subresourceCount];
        _states = new ResourceState[subresourceCount];
        Array.Fill(_states, desc.InitialState);
    }

    public TextureDesc Desc { get; }
    public ResourceAllocationInfo Allocation { get; }

    public ResourceState GetState(uint mipLevel, uint arraySlice)
    {
        ValidateSubresource(mipLevel, arraySlice);
        return _states[SubresourceIndex(mipLevel, arraySlice)];
    }

    public void SetState(SubresourceRange range, ResourceState state)
    {
        RhiCommandValidation.ValidateSubresource(Desc, range);
        uint mipCount = MipCount(range);
        uint sliceCount = SliceCount(range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + sliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + mipCount; mip++)
                _states[SubresourceIndex(mip, slice)] = state;
        }
    }

    public bool RangeMatches(SubresourceRange range, ResourceState state)
    {
        RhiCommandValidation.ValidateSubresource(Desc, range);
        uint mipCount = MipCount(range);
        uint sliceCount = SliceCount(range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + sliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + mipCount; mip++)
            {
                if (_states[SubresourceIndex(mip, slice)] != state)
                    return false;
            }
        }

        return true;
    }

    public bool ViewMatches(TextureViewDesc view, ResourceState state)
    {
        var range = new SubresourceRange(view.FirstMip, view.MipCount, view.FirstSlice, view.SliceCount);
        return RangeMatches(range, state);
    }

    public void CopyFromBuffer(byte[] source, BufferTextureCopy sourceLayout, TextureCopyRegion destination)
    {
        int bytesPerPixel = BytesPerPixel();
        int rowBytes = RowByteCount(destination.Width, bytesPerPixel);
        byte[] segmentData = AllocateRegionData(destination, rowBytes);

        for (uint z = 0; z < destination.Depth; z++)
        {
            ulong sourceSlice = sourceLayout.Offset + checked((ulong)z * sourceLayout.SlicePitch);
            for (uint y = 0; y < destination.Height; y++)
            {
                ulong sourceRow = sourceSlice + checked((ulong)y * sourceLayout.RowPitch);
                int segmentRow = SegmentRowOffset(z, y, destination.Height, rowBytes);
                Buffer.BlockCopy(source, checked((int)sourceRow), segmentData, segmentRow, rowBytes);
            }
        }

        EnsureSubresourceData(destination.MipLevel, destination.ArraySlice)
            .AddSegment(destination, segmentData);
    }

    public void CopyToBuffer(TextureCopyRegion sourceRegion, byte[] destination, BufferTextureCopy destinationLayout)
    {
        var data = GetSubresourceData(sourceRegion.MipLevel, sourceRegion.ArraySlice);
        int bytesPerPixel = BytesPerPixel();
        int rowBytes = RowByteCount(sourceRegion.Width, bytesPerPixel);

        for (uint z = 0; z < sourceRegion.Depth; z++)
        {
            ulong destinationSlice = destinationLayout.Offset + checked((ulong)z * destinationLayout.SlicePitch);
            for (uint y = 0; y < sourceRegion.Height; y++)
            {
                ulong destinationRow = destinationSlice + checked((ulong)y * destinationLayout.RowPitch);
                int destinationRowOffset = checked((int)destinationRow);
                Array.Clear(destination, destinationRowOffset, rowBytes);
                data?.CopyToRow(
                    sourceRegion.X,
                    checked(sourceRegion.Y + y),
                    checked(sourceRegion.Z + z),
                    sourceRegion.Width,
                    bytesPerPixel,
                    destination,
                    destinationRowOffset);
            }
        }
    }

    public void CopyToTexture(TextureCopyRegion sourceRegion, TextureRecord destinationTexture, TextureCopyRegion destinationRegion)
    {
        var sourceData = GetSubresourceData(sourceRegion.MipLevel, sourceRegion.ArraySlice);
        int bytesPerPixel = BytesPerPixel();
        int rowBytes = RowByteCount(sourceRegion.Width, bytesPerPixel);
        byte[] segmentData = AllocateRegionData(destinationRegion, rowBytes);

        for (uint z = 0; z < sourceRegion.Depth; z++)
        {
            for (uint y = 0; y < sourceRegion.Height; y++)
            {
                int segmentRow = SegmentRowOffset(z, y, destinationRegion.Height, rowBytes);
                sourceData?.CopyToRow(
                    sourceRegion.X,
                    checked(sourceRegion.Y + y),
                    checked(sourceRegion.Z + z),
                    sourceRegion.Width,
                    bytesPerPixel,
                    segmentData,
                    segmentRow);
            }
        }

        destinationTexture.EnsureSubresourceData(destinationRegion.MipLevel, destinationRegion.ArraySlice)
            .AddSegment(destinationRegion, segmentData);
    }

    private void ValidateSubresource(uint mipLevel, uint arraySlice)
    {
        if (mipLevel >= Desc.MipLevels || arraySlice >= Desc.ArraySize)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture subresource is outside the texture.");
    }

    private int SubresourceIndex(uint mipLevel, uint arraySlice)
        => checked((int)(arraySlice * Desc.MipLevels + mipLevel));

    private static uint MipExtent(uint size, uint mipLevel)
        => Math.Max(1u, size >> checked((int)mipLevel));

    private TextureSubresourceData? GetSubresourceData(uint mipLevel, uint arraySlice)
    {
        ValidateSubresource(mipLevel, arraySlice);
        return _subresources[SubresourceIndex(mipLevel, arraySlice)];
    }

    private TextureSubresourceData EnsureSubresourceData(uint mipLevel, uint arraySlice)
    {
        ValidateSubresource(mipLevel, arraySlice);
        int index = SubresourceIndex(mipLevel, arraySlice);
        return _subresources[index] ??= new TextureSubresourceData();
    }

    private int BytesPerPixel()
        => checked((int)RhiCommandValidation.FormatByteSize(Desc.Format));

    private static int RowByteCount(uint width, int bytesPerPixel)
        => checked((int)width * bytesPerPixel);

    private static byte[] AllocateRegionData(TextureCopyRegion region, int rowBytes)
    {
        int sliceBytes = checked(rowBytes * (int)region.Height);
        int totalBytes = checked(sliceBytes * (int)region.Depth);
        return new byte[totalBytes];
    }

    private static int SegmentRowOffset(uint z, uint y, uint height, int rowBytes)
        => checked(((int)z * (int)height + (int)y) * rowBytes);

    private uint MipCount(SubresourceRange range)
        => range.MipCount == uint.MaxValue ? Desc.MipLevels - range.FirstMip : range.MipCount;

    private uint SliceCount(SubresourceRange range)
        => range.SliceCount == uint.MaxValue ? Desc.ArraySize - range.FirstSlice : range.SliceCount;
}

internal sealed class TextureSubresourceData
{
    private TextureWriteSegment? _first;
    private TextureWriteSegment? _last;

    public void AddSegment(TextureCopyRegion region, byte[] data)
    {
        var segment = new TextureWriteSegment(
            checked((int)region.X),
            checked((int)region.Y),
            checked((int)region.Z),
            checked((int)region.Width),
            checked((int)region.Height),
            checked((int)region.Depth),
            data);
        RemoveCovered(segment);
        if (_last == null)
        {
            _first = segment;
            _last = segment;
            return;
        }

        _last.Next = segment;
        _last = segment;
    }

    public void CopyToRow(
        uint x,
        uint y,
        uint z,
        uint width,
        int bytesPerPixel,
        byte[] destination,
        int destinationRowOffset)
    {
        int rowX = checked((int)x);
        int rowY = checked((int)y);
        int rowZ = checked((int)z);
        int rowEndX = checked(rowX + (int)width);
        for (var segment = _first; segment != null; segment = segment.Next)
        {
            if (rowY < segment.Y || rowY >= segment.Y + segment.Height)
                continue;
            if (rowZ < segment.Z || rowZ >= segment.Z + segment.Depth)
                continue;

            int overlapStartX = Math.Max(rowX, segment.X);
            int overlapEndX = Math.Min(rowEndX, checked(segment.X + segment.Width));
            if (overlapStartX >= overlapEndX)
                continue;

            int segmentRow = checked((rowZ - segment.Z) * segment.Height + rowY - segment.Y);
            int sourceOffset = checked((segmentRow * segment.Width + overlapStartX - segment.X) * bytesPerPixel);
            int destinationOffset = checked(destinationRowOffset + (overlapStartX - rowX) * bytesPerPixel);
            int byteCount = checked((overlapEndX - overlapStartX) * bytesPerPixel);
            Buffer.BlockCopy(segment.Data, sourceOffset, destination, destinationOffset, byteCount);
        }
    }

    private void RemoveCovered(TextureWriteSegment covering)
    {
        TextureWriteSegment? previous = null;
        var current = _first;
        while (current != null)
        {
            var next = current.Next;
            if (Covers(covering, current))
            {
                if (previous == null)
                    _first = next;
                else
                    previous.Next = next;

                if (_last == current)
                    _last = previous;
            }
            else
            {
                previous = current;
            }

            current = next;
        }
    }

    private static bool Covers(TextureWriteSegment covering, TextureWriteSegment covered)
        => covered.X >= covering.X
            && covered.Y >= covering.Y
            && covered.Z >= covering.Z
            && covered.X + covered.Width <= covering.X + covering.Width
            && covered.Y + covered.Height <= covering.Y + covering.Height
            && covered.Z + covered.Depth <= covering.Z + covering.Depth;

    private sealed class TextureWriteSegment(
        int x,
        int y,
        int z,
        int width,
        int height,
        int depth,
        byte[] data)
    {
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Z { get; } = z;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Depth { get; } = depth;
        public byte[] Data { get; } = data;
        public TextureWriteSegment? Next { get; set; }
    }
}

internal sealed record TextureViewRecord(TextureHandle Texture, TextureViewDesc Desc, ResourceOwnership Ownership = ResourceOwnership.Committed);

internal sealed record BufferViewRecord(BufferHandle Buffer, BufferViewDesc Desc);

internal sealed record SamplerRecord(SamplerDesc Desc);

internal sealed record ShaderModuleRecord(ShaderModuleDesc Desc);

internal sealed record BindingLayoutRecord(BindingLayoutDesc Desc, BindingLayoutSignature Signature);

internal sealed record PipelineLayoutRecord(PipelineLayoutDesc Desc);

internal sealed record BindingSetRecord(
    BindingLayoutHandle Layout,
    BindingLayoutSignature LayoutSignature,
    BindingSetDesc Desc,
    BindState[] BindStates);

internal sealed record PipelineRecord(
    PipelineKind Kind,
    PipelineLayoutHandle Layout,
    string Name,
    GraphicsPipelineDesc? GraphicsDesc = null,
    MeshPipelineDesc? MeshDesc = null,
    RtPipelineDesc? RayTracingDesc = null);

internal sealed record PipelineCacheRecord(PipelineCacheDesc Desc, byte[] Data);

internal sealed record AccelerationStructureRecord(AccelerationStructureDesc Desc);

internal readonly record struct RenderPassCompatibility(
    Format Color0,
    Format Color1,
    Format Color2,
    Format Color3,
    Format Color4,
    Format Color5,
    Format Color6,
    Format Color7,
    int ColorCount,
    Format DepthStencilFormat,
    uint SampleCount,
    bool DepthReadOnly)
{
    public Format ColorFormatAt(int index)
        => index switch
        {
            0 => Color0,
            1 => Color1,
            2 => Color2,
            3 => Color3,
            4 => Color4,
            5 => Color5,
            6 => Color6,
            7 => Color7,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
}

internal readonly record struct BindingLayoutSignature(ulong Hash, int SlotCount);

internal sealed class CommandBufferRecord(
    NullDevice device,
    string name,
    QueueType queueType,
    NullCommandOperation[] operations,
    int operationCount,
    bool operationsArePooled,
    BindingResourceDesc[] bindingResources,
    int bindingResourceCount,
    bool bindingResourcesArePooled,
    BufferHandle[] referencedBuffers,
    TextureHandle[] referencedTextures,
    QueryPoolHandle[] referencedQueryPools,
    TextureViewHandle[] referencedTextureViews,
    BufferViewHandle[] referencedBufferViews,
    SamplerHandle[] referencedSamplers,
    AccelerationStructureHandle[] referencedAccelerationStructures,
    BindingSetHandle[] referencedBindingSets,
    BindingLayoutHandle[] referencedBindingLayouts,
    PipelineLayoutHandle[] referencedPipelineLayouts,
    PipelineHandle[] referencedPipelines) : IDisposable
{
    private NullCommandOperation[]? _operations = operations;
    private BindingResourceDesc[]? _bindingResources = bindingResources;

    public string Name { get; } = name;
    public QueueType QueueType { get; } = queueType;
    public NullCommandOperation[] Operations
        => _operations ?? [];
    public int OperationCount { get; } = operationCount;
    public int BindingResourceCount { get; } = bindingResourceCount;
    public bool Submitted { get; set; }
    public BufferHandle[] ReferencedBuffers { get; } = referencedBuffers;
    public TextureHandle[] ReferencedTextures { get; } = referencedTextures;
    public QueryPoolHandle[] ReferencedQueryPools { get; } = referencedQueryPools;
    public TextureViewHandle[] ReferencedTextureViews { get; } = referencedTextureViews;
    public BufferViewHandle[] ReferencedBufferViews { get; } = referencedBufferViews;
    public SamplerHandle[] ReferencedSamplers { get; } = referencedSamplers;
    public AccelerationStructureHandle[] ReferencedAccelerationStructures { get; } = referencedAccelerationStructures;
    public BindingSetHandle[] ReferencedBindingSets { get; } = referencedBindingSets;
    public BindingLayoutHandle[] ReferencedBindingLayouts { get; } = referencedBindingLayouts;
    public PipelineLayoutHandle[] ReferencedPipelineLayouts { get; } = referencedPipelineLayouts;
    public PipelineHandle[] ReferencedPipelines { get; } = referencedPipelines;

    public ReadOnlySpan<BindingResourceDesc> BindingResourcesFor(NullCommandOperation operation)
    {
        if (operation.Kind != NullOperationKind.TransientBindingDependency)
            return default;
        var resources = _bindingResources
            ?? throw new RhiException(ErrorCode.ValidationFailure, "Transient binding resource storage has already been released.");
        if (operation.BindingResourceOffset < 0
            || operation.BindingResourceCount < 0
            || operation.BindingResourceOffset > BindingResourceCount
            || operation.BindingResourceCount > BindingResourceCount - operation.BindingResourceOffset)
        {
            throw new RhiException(ErrorCode.ValidationFailure, "Transient binding resource range is corrupt.");
        }

        return resources.AsSpan(operation.BindingResourceOffset, operation.BindingResourceCount);
    }

    public void Dispose()
    {
        var operations = _operations;
        _operations = null;
        var resources = _bindingResources;
        _bindingResources = null;
        if (bindingResourcesArePooled && resources != null)
            device.ReturnBindingResources(resources);

        if (operationsArePooled && operations != null)
            device.ReturnCommandOperations(operations);
    }
}

internal sealed class FenceRecord(string name, ulong value)
{
    public string Name { get; } = name;
    public ulong CompletedValue { get; set; } = value;
}

internal sealed class QueryPoolRecord(QueryPoolDesc desc)
{
    public QueryPoolDesc Desc { get; } = desc;
    public ulong?[] Values { get; } = new ulong?[checked((int)desc.Count)];
    public bool[] Active { get; } = new bool[checked((int)desc.Count)];
}

internal sealed class SwapchainRecord(SwapchainDesc desc, TextureHandle[] textures, TextureViewHandle[] rtvs, TextureDesc textureDesc)
{
    public SwapchainDesc Desc { get; set; } = desc;
    public TextureHandle[] Textures { get; set; } = textures;
    public TextureViewHandle[] RenderTargetViews { get; set; } = rtvs;
    public TextureDesc TextureDesc { get; set; } = textureDesc;
    public uint CurrentIndex { get; set; }

    public TextureHandle Texture => Textures[checked((int)CurrentIndex)];
    public TextureViewHandle RenderTargetView => RenderTargetViews[checked((int)CurrentIndex)];
}

internal sealed class MemoryHeapRecord(MemoryHeapDesc desc)
{
    public MemoryHeapDesc Desc { get; } = desc;
    public int LiveResourceCount { get; set; }
    public List<PlacedAllocationRecord> PlacedAllocations { get; } = [];
}

internal sealed class PlacedAllocationRecord(AliasingResource resource, ResourceAllocationInfo allocation)
{
    public AliasingResource Resource { get; } = resource;
    public ResourceAllocationInfo Allocation { get; } = allocation;
    public bool Active { get; set; } = true;
}
