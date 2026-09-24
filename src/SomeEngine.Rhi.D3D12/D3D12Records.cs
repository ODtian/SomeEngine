using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using Vortice.Direct3D12;
using Vortice.DXGI;
using RhiPrimitiveTopology = SomeEngine.Rhi.PrimitiveTopology;

namespace SomeEngine.Rhi.D3D12;

internal sealed class BufferRecord(BufferDesc desc, ID3D12Resource resource, ResourceAllocationInfo allocation)
{
    public BufferDesc Desc { get; } = desc;
    public ID3D12Resource Resource { get; } = resource;
    public ResourceAllocationInfo Allocation { get; } = allocation;
    public ResourceState State { get; set; } = desc.InitialState;
    public MappedMemory? PersistentMappedMemory { get; set; }
    public bool PersistentMapActive { get; set; }
    public MappedMemory? MappedMemory { get; set; }
    public MapMode MappedMode { get; set; }
    public ulong MappedOffset { get; set; }
    public ulong MappedSize { get; set; }
}

internal sealed class TextureRecord(TextureDesc desc, ID3D12Resource resource, ResourceAllocationInfo allocation)
{
    private readonly ResourceState[] _states = CreateStates(desc);

    public TextureDesc Desc { get; } = desc;
    public ID3D12Resource Resource { get; } = resource;
    public ResourceAllocationInfo Allocation { get; } = allocation;

    public ResourceState GetState(uint mipLevel, uint arraySlice)
        => _states[SubresourceIndex(mipLevel, arraySlice)];

    public void SetState(SubresourceRange range, ResourceState state)
    {
        var actual = ActualRange(range);
        for (uint slice = actual.FirstSlice; slice < actual.FirstSlice + actual.SliceCount; slice++)
        {
            for (uint mip = actual.FirstMip; mip < actual.FirstMip + actual.MipCount; mip++)
                _states[SubresourceIndex(mip, slice)] = state;
        }
    }

    public uint SubresourceIndex(uint mipLevel, uint arraySlice)
    {
        if (mipLevel >= Desc.MipLevels || arraySlice >= Desc.ArraySize)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture subresource is outside the texture.");
        return mipLevel + arraySlice * Desc.MipLevels;
    }

    public SubresourceRange ActualRange(SubresourceRange range)
    {
        uint firstMip = range.FirstMip;
        uint mipCount = range.MipCount == uint.MaxValue ? Desc.MipLevels - firstMip : range.MipCount;
        uint firstSlice = range.FirstSlice;
        uint sliceCount = range.SliceCount == uint.MaxValue ? Desc.ArraySize - firstSlice : range.SliceCount;
        if (firstMip >= Desc.MipLevels || mipCount == 0 || mipCount > Desc.MipLevels - firstMip)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture barrier mip range is outside the texture.");
        if (firstSlice >= Desc.ArraySize || sliceCount == 0 || sliceCount > Desc.ArraySize - firstSlice)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture barrier array range is outside the texture.");
        return new SubresourceRange(firstMip, mipCount, firstSlice, sliceCount);
    }

    private static ResourceState[] CreateStates(TextureDesc desc)
    {
        var states = new ResourceState[checked((int)((ulong)desc.MipLevels * desc.ArraySize))];
        Array.Fill(states, desc.InitialState);
        return states;
    }
}

internal sealed record TextureViewRecord(
    TextureHandle Texture,
    TextureViewDesc Desc,
    CpuDescriptorHandle Descriptor,
    DescriptorAllocation Allocation,
    ViewKind Kind,
    ResourceOwnership Ownership = ResourceOwnership.Committed,
    CpuDescriptorHandle ReadOnlyDepthDescriptor = default,
    DescriptorAllocation? ReadOnlyDepthAllocation = null);

internal sealed record BufferViewRecord(BufferHandle Buffer, BufferViewDesc Desc, CpuDescriptorHandle Descriptor, DescriptorAllocation Allocation, ViewKind Kind);

internal sealed record SamplerRecord(SamplerDesc Desc, CpuDescriptorHandle Descriptor, DescriptorAllocation Allocation);

internal sealed record ShaderRecord(ShaderModuleDesc Desc);

internal sealed record LayoutRecord(
    BindingLayoutDesc Desc,
    LayoutSignature Signature,
    ValidationSlot[] ValidationSlots,
    SlotLookup[] ValidationSlotLookup,
    int RequiredBindingResourceCount,
    SlotLayout[] ResourceSlots,
    SlotLookup[] ResourceSlotLookup,
    int ResourceDescriptorCount,
    bool ResourceSlotsNeedNullDescriptors,
    SlotLayout[] DynamicResourceSlots,
    SlotLookup[] DynamicResourceSlotLookup,
    int DynamicResourceDescriptorCount,
    bool DynamicResourceSlotsNeedNullDescriptors,
    SlotLayout[] SamplerSlots,
    SlotLookup[] SamplerSlotLookup,
    int SamplerDescriptorCount,
    bool SamplerSlotsNeedNullDescriptors,
    bool SupportsTransientBindings,
    bool HasDynamicResourceSlots);

internal sealed record PipeLayoutRecord(
    PipelineLayoutDesc Desc,
    ID3D12RootSignature RootSignature,
    RootSignatureRecord SharedRootSignature,
    ulong RootSignatureFingerprint,
    RootBinding[] Sets,
    LayoutSignature[] SetSignatures,
    PushRoot[] PushConstants);

internal sealed class RootSignatureRecord(
    ID3D12RootSignature rootSignature,
    ulong fingerprint,
    SlotLayout[][] resourceSlots,
    SlotLayout[][] dynamicResourceSlots,
    SlotLayout[][] samplerSlots,
    PushRangeDesc[] pushConstants,
    StaticSamplerDesc[] staticSamplers)
{
    public ID3D12RootSignature RootSignature { get; } = rootSignature;
    public ulong Fingerprint { get; } = fingerprint;
    public SlotLayout[][] ResourceSlots { get; } = resourceSlots;
    public SlotLayout[][] DynamicResourceSlots { get; } = dynamicResourceSlots;
    public SlotLayout[][] SamplerSlots { get; } = samplerSlots;
    public PushRangeDesc[] PushConstants { get; } = pushConstants;
    public StaticSamplerDesc[] StaticSamplers { get; } = staticSamplers;
    public int RefCount { get; set; } = 1;
}

internal sealed record BindingSetRecord(
    BindingLayoutHandle Layout,
    LayoutSignature LayoutSignature,
    bool HasDynamicResourceSlots,
    BindingSetFlags Flags,
    ShaderDescriptorAllocation? ResourceDescriptors,
    ShaderDescriptorAllocation? DynamicResourceDescriptors,
    ShaderDescriptorAllocation? SamplerDescriptors,
    BindingResourceDesc[] Resources,
    BindState[] BindStates,
    BufferHandle[] Buffers,
    TextureHandle[] Textures,
    AccelerationStructureHandle[] AccelerationStructures);

internal sealed record PipelineRecord(
    PipelineKind Kind,
    PipelineLayoutHandle Layout,
    PipeLayoutRecord LayoutRecord,
    ID3D12PipelineState? State = null,
    RhiPrimitiveTopology Topology = RhiPrimitiveTopology.TriangleList,
    GraphicsPipelineDesc? GraphicsDesc = null,
    MeshPipelineDesc? MeshDesc = null,
    ID3D12StateObject? StateObject = null,
    ID3D12StateObjectProperties? StateObjectProperties = null,
    RtPipelineDesc? RayTracingDesc = null,
    PipelinePassCompatibility RenderPassCompatibility = default);

internal sealed record PipelineCacheRecord(PipelineCacheDesc Desc, ID3D12PipelineLibrary Library, GCHandle InitialDataPin)
{
    public void Dispose()
    {
        Library.Dispose();
        if (InitialDataPin.IsAllocated)
            InitialDataPin.Free();
    }
}

internal sealed record AccelRecord(
    AccelerationStructureDesc Desc,
    ID3D12Resource Resource,
    CpuDescriptorHandle Descriptor,
    DescriptorAllocation Allocation);

internal sealed class CommandListLease(ID3D12CommandAllocator allocator, ID3D12GraphicsCommandList list) : IDisposable
{
    private CommandState _state;

    public ID3D12CommandAllocator Allocator { get; } = allocator;
    public ID3D12GraphicsCommandList List { get; } = list;
    internal InlineFlatCore<BufferHandle, bool> ReferencedBuffers;
    internal InlineFlatCore<TextureHandle, bool> ReferencedTextures;
    internal InlineFlatCore<QueryPoolHandle, bool> ReferencedQueryPools;
    internal InlineFlatCore<TextureViewHandle, bool> ReferencedTextureViews;
    internal InlineFlatCore<BufferViewHandle, bool> ReferencedBufferViews;
    internal InlineFlatCore<SamplerHandle, bool> ReferencedSamplers;
    internal InlineFlatCore<AccelerationStructureHandle, bool> ReferencedAccelerationStructures;
    internal InlineFlatCore<BindingLayoutHandle, bool> ReferencedBindingLayouts;
    internal InlineFlatCore<BindingSetHandle, bool> ReferencedBindingSets;
    internal InlineFlatCore<PipelineLayoutHandle, bool> ReferencedPipelineLayouts;
    internal InlineFlatCore<PipelineHandle, bool> ReferencedPipelines;

    public CommandState TakeState()
    {
        var state = _state;
        _state = default;
        return state;
    }

    public void StoreState(ref CommandState state)
    {
        state.ClearNoResize();
        _state = state;
        state = default;
    }

    public void Dispose()
    {
        _state.Dispose();
        ReferencedBuffers.Dispose();
        ReferencedTextures.Dispose();
        ReferencedQueryPools.Dispose();
        ReferencedTextureViews.Dispose();
        ReferencedBufferViews.Dispose();
        ReferencedSamplers.Dispose();
        ReferencedAccelerationStructures.Dispose();
        ReferencedBindingLayouts.Dispose();
        ReferencedBindingSets.Dispose();
        ReferencedPipelineLayouts.Dispose();
        ReferencedPipelines.Dispose();
        List.Dispose();
        Allocator.Dispose();
    }
}

internal sealed record CommandBufferRecord(
    CommandQueueKind QueueKind,
    CommandListLease Lease,
    TrackedDescriptorAllocation[] TransientDescriptors,
    BufferTransition[] BufferStates,
    TextureTransition[] TextureStates,
    AliasOperation[] AliasingOperations,
    QueryOperation[] QueryOperations,
    QueryKey[] WrittenQueries,
    QueryResolve[] QueryResolves,
    BufferHandle[] ReferencedBuffers,
    TextureHandle[] ReferencedTextures,
    QueryPoolHandle[] ReferencedQueryPools,
    TextureViewHandle[] ReferencedTextureViews,
    BufferViewHandle[] ReferencedBufferViews,
    SamplerHandle[] ReferencedSamplers,
    AccelerationStructureHandle[] ReferencedAccelerationStructures,
    BindingLayoutHandle[] ReferencedBindingLayouts,
    BindingSetHandle[] ReferencedBindingSets,
    PipelineLayoutHandle[] ReferencedPipelineLayouts,
    PipelineHandle[] ReferencedPipelines)
{
    public ID3D12CommandAllocator Allocator => Lease.Allocator;
    public ID3D12GraphicsCommandList List => Lease.List;
    public bool Submitted { get; set; }
    public ID3D12Fence? RetirementFence { get; set; }
    public ulong RetirementValue { get; set; }
}

internal sealed class FenceRecord(ID3D12Fence fence, ulong initialValue)
{
    public ID3D12Fence Fence { get; } = fence;
    public ulong LastSignaledValue { get; set; } = initialValue;
}

internal sealed record QueryPoolRecord(QueryPoolDesc Desc, ID3D12QueryHeap Heap)
{
    public bool[] Written { get; } = new bool[checked((int)Desc.Count)];
}

internal sealed record HeapRecord(MemoryHeapDesc Desc, ID3D12Heap Heap)
{
    public int LiveResourceCount { get; set; }
    public List<PlacedRecord> PlacedAllocations { get; } = [];
}

internal sealed class PlacedRecord(AliasingResource resource, ResourceAllocationInfo allocation)
{
    public AliasingResource Resource { get; } = resource;
    public ResourceAllocationInfo Allocation { get; } = allocation;
    public bool Active { get; set; } = true;
}

internal sealed class SwapchainRecord(SwapchainDesc desc, IDXGISwapChain3 swapchain, TextureHandle[] textures, TextureViewHandle[] renderTargetViews)
{
    public SwapchainDesc Desc { get; set; } = desc;
    public IDXGISwapChain3 Swapchain { get; } = swapchain;
    public TextureHandle[] Textures { get; } = textures;
    public TextureViewHandle[] RenderTargetViews { get; } = renderTargetViews;
}

internal readonly record struct LayoutSignature(ulong Hash, int SlotCount);

internal readonly record struct ValidationSlot(uint Binding, BindingType Type, uint Count, BindingFlags Flags, BindShapeDesc Shape, bool HasShapeRule);

internal readonly record struct SlotLayout(uint Binding, BindingType Type, uint Count, BindingFlags Flags, uint BaseDescriptor, BindShapeDesc Shape);

internal readonly record struct SlotLookup(uint Binding, RegisterClass RegisterClass, int SlotIndex);

internal enum RegisterClass
{
    None,
    ConstantBuffer,
    ShaderResource,
    UnorderedAccess,
    Sampler,
}

internal readonly record struct RootBinding(int ResourceTable, int DynamicResourceTable, int SamplerTable);

internal readonly record struct PushRoot(ShaderStageFlags Stages, uint Offset, uint SizeInBytes, int RootParameter);

internal readonly record struct DescriptorAllocation(CpuDescriptorHandle Cpu, int Index);

internal readonly record struct ShaderDescriptorAllocation(CpuDescriptorHandle Cpu, GpuDescriptorHandle Gpu, int Index, int Count);

internal readonly record struct SourceDescriptor(CpuDescriptorHandle Cpu, int Index);

internal readonly record struct TrackedDescriptorAllocation(DescriptorHeapType Type, ShaderDescriptorAllocation Allocation);

internal readonly record struct AliasOperation(AliasOperationKind Kind, AliasingResource Resource, AliasingBarrier Barrier);

internal readonly record struct QueryKey(QueryPoolHandle Pool, uint Index);

internal readonly record struct QueryResolve(QueryPoolHandle Pool, uint FirstQuery, uint QueryCount);

internal readonly record struct QueryOperation(QueryOperationKind Kind, QueryPoolHandle Pool, uint FirstQuery, uint QueryCount);

internal readonly record struct PassCompatibility(
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

internal readonly record struct PipelinePassCompatibility(
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
    bool DepthWriteEnable)
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

internal readonly record struct ColorResolve(
    TextureHandle Source,
    TextureViewDesc SourceView,
    TextureHandle Destination,
    TextureViewDesc DestinationView,
    Vortice.DXGI.Format Format);

internal enum AliasOperationKind
{
    ResourceUse,
    Barrier,
}

internal enum QueryOperationKind
{
    Begin,
    End,
    Write,
    Resolve,
}

internal enum PipelineKind
{
    Graphics,
    Compute,
    Mesh,
    RayTracing,
}

internal enum CommandQueueKind
{
    Direct,
    Compute,
    Copy,
}

internal sealed unsafe class MappedMemory : MemoryManager<byte>
{
    private readonly byte* _pointer;
    private readonly int _length;
    private bool _disposed;

    public MappedMemory(byte* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public byte* Pointer => _pointer;

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<byte>(_pointer, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)elementIndex > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
    }

    public void Close() => Dispose(true);

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
    }
}
