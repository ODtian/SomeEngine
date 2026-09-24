using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace SomeEngine.Rhi.D3D12;

internal sealed class D3D12CommandList : ICommandList
{
    private readonly D3D12Device _device;
    private readonly CommandQueueKind _queueKind;
    private readonly CommandListLease _lease;
    private readonly ID3D12GraphicsCommandList _list;
    private ID3D12GraphicsCommandList4? _list4;
    private ID3D12GraphicsCommandList6? _list6;
    private readonly List<TrackedDescriptorAllocation> _transientDescriptors = [];
    private readonly List<AliasOperation> _aliasingOperations = [];
    private InlineFlatCore<AliasingResource, bool> _aliasingUsesSinceBarrier;
    private CommandState _state;
    private readonly List<QueryOperation> _queryOperations = [];
    private readonly List<Profiler.Scope> _debugScopes = [];
    private InlineFlatCore<QueryKey, bool> _writtenQueries;
    private InlineFlatCore<QueryKey, bool> _activeQueries;
    private readonly List<QueryResolve> _queryResolves = [];
    private InlineFlatCore<BufferHandle, bool> _referencedBuffers;
    private InlineFlatCore<TextureHandle, bool> _referencedTextures;
    private InlineFlatCore<QueryPoolHandle, bool> _referencedQueryPools;
    private InlineFlatCore<TextureViewHandle, bool> _referencedTextureViews;
    private InlineFlatCore<BufferViewHandle, bool> _referencedBufferViews;
    private InlineFlatCore<SamplerHandle, bool> _referencedSamplers;
    private InlineFlatCore<AccelerationStructureHandle, bool> _referencedAccelerationStructures;
    private InlineFlatCore<BindingLayoutHandle, bool> _referencedBindingLayouts;
    private InlineFlatCore<BindingSetHandle, bool> _referencedBindingSets;
    private InlineFlatCore<PipelineLayoutHandle, bool> _referencedPipelineLayouts;
    private InlineFlatCore<PipelineHandle, bool> _referencedPipelines;
    private BufferHandle _lastBufferHandle;
    private BufferRecord? _lastBufferRecord;
    private TextureHandle _lastTextureHandle;
    private TextureRecord? _lastTextureRecord;
    private bool _finished;
    private bool _stateStored;
    private bool _passOpen;
    private bool _shaderDescriptorHeapsSet;
    private PipelineBindingDomain _currentPipelineBindingDomain;
    private ID3D12PipelineState? _currentPipelineState;
    private ID3D12StateObject? _currentRayTracingStateObject;
    private ID3D12RootSignature? _currentGraphicsRootSignature;
    private ID3D12RootSignature? _currentComputeRootSignature;
    private InlineFlatCore<uint, GpuDescriptorHandle> _graphicsRootDescriptorTables;
    private InlineFlatCore<uint, GpuDescriptorHandle> _computeRootDescriptorTables;
    private InlineFlatCore<uint, BindingSetHandle> _graphicsBindingSets;
    private InlineFlatCore<uint, BindingSetHandle> _computeBindingSets;
    private bool _hasViewport;
    private ViewportState _currentViewport;
    private bool _hasScissorRect;
    private ScissorState _currentScissorRect;
    private bool _hasPrimitiveTopology;
    private Vortice.Direct3D.PrimitiveTopology _currentPrimitiveTopology;
    private readonly bool _emitDebugMarkers;
    private int _debugDepth;

    public D3D12CommandList(D3D12Device device, CommandListDesc desc)
    {
        _device = device;
        _queueKind = D3D12Mappings.ToQueueKind(desc.QueueType);
        var queue = device.GetQueue(_queueKind);
        var lease = queue.RentCommandList();
        _lease = lease;
        _list = lease.List;
        _state = lease.TakeState();
        _referencedBuffers = lease.ReferencedBuffers;
        lease.ReferencedBuffers = default;
        _referencedTextures = lease.ReferencedTextures;
        lease.ReferencedTextures = default;
        _referencedQueryPools = lease.ReferencedQueryPools;
        lease.ReferencedQueryPools = default;
        _referencedTextureViews = lease.ReferencedTextureViews;
        lease.ReferencedTextureViews = default;
        _referencedBufferViews = lease.ReferencedBufferViews;
        lease.ReferencedBufferViews = default;
        _referencedSamplers = lease.ReferencedSamplers;
        lease.ReferencedSamplers = default;
        _referencedAccelerationStructures = lease.ReferencedAccelerationStructures;
        lease.ReferencedAccelerationStructures = default;
        _referencedBindingLayouts = lease.ReferencedBindingLayouts;
        lease.ReferencedBindingLayouts = default;
        _referencedBindingSets = lease.ReferencedBindingSets;
        lease.ReferencedBindingSets = default;
        _referencedPipelineLayouts = lease.ReferencedPipelineLayouts;
        lease.ReferencedPipelineLayouts = default;
        _referencedPipelines = lease.ReferencedPipelines;
        lease.ReferencedPipelines = default;
        _emitDebugMarkers = PixEvents.IsEnabled && PixEvents.IsAvailable;
    }

    public void Dispose()
    {
        if (_finished)
            return;

        AbortRecording();
    }

    internal ID3D12GraphicsCommandList List => _list;
    internal CommandQueueKind QueueKind => _queueKind;
    public bool DebugMarkersEnabled => _emitDebugMarkers;

    internal void SetGraphicsState(ID3D12PipelineState state)
    {
        if (_currentPipelineBindingDomain == PipelineBindingDomain.Graphics
            && ReferenceEquals(_currentPipelineState, state))
        {
            return;
        }

        _list.SetPipelineState(state);
        _currentPipelineBindingDomain = PipelineBindingDomain.Graphics;
        _currentPipelineState = state;
    }

    internal void SetComputeState(ID3D12PipelineState state)
    {
        if (_currentPipelineBindingDomain == PipelineBindingDomain.Compute
            && ReferenceEquals(_currentPipelineState, state))
        {
            return;
        }

        _list.SetPipelineState(state);
        _currentPipelineBindingDomain = PipelineBindingDomain.Compute;
        _currentPipelineState = state;
    }

    internal void SetRayState(ID3D12GraphicsCommandList4 list, ID3D12StateObject state)
    {
        if (_currentPipelineBindingDomain == PipelineBindingDomain.RayTracing
            && ReferenceEquals(_currentRayTracingStateObject, state))
        {
            return;
        }

        list.SetPipelineState1(state);
        _currentPipelineBindingDomain = PipelineBindingDomain.RayTracing;
        _currentRayTracingStateObject = state;
    }

    internal void SetGraphicsRoot(ID3D12RootSignature rootSignature)
    {
        if (ReferenceEquals(_currentGraphicsRootSignature, rootSignature))
        {
            return;
        }

        _list.SetGraphicsRootSignature(rootSignature);
        _currentGraphicsRootSignature = rootSignature;
        _graphicsRootDescriptorTables.Clear();
        _graphicsBindingSets.Clear();
    }

    internal void SetComputeRoot(ID3D12RootSignature rootSignature)
    {
        if (ReferenceEquals(_currentComputeRootSignature, rootSignature))
        {
            return;
        }

        _list.SetComputeRootSignature(rootSignature);
        _currentComputeRootSignature = rootSignature;
        _computeRootDescriptorTables.Clear();
        _computeBindingSets.Clear();
    }

    internal bool SetGraphicsTable(uint rootParameterIndex, GpuDescriptorHandle descriptor)
    {
        if (_graphicsRootDescriptorTables.TryGetValue(rootParameterIndex, out var current)
            && current.Ptr == descriptor.Ptr)
        {
            return false;
        }

        _list.SetGraphicsRootDescriptorTable(rootParameterIndex, descriptor);
        _graphicsRootDescriptorTables.Set(rootParameterIndex, descriptor);
        return true;
    }

    internal bool SetComputeTable(uint rootParameterIndex, GpuDescriptorHandle descriptor)
    {
        if (_computeRootDescriptorTables.TryGetValue(rootParameterIndex, out var current)
            && current.Ptr == descriptor.Ptr)
        {
            return false;
        }

        _list.SetComputeRootDescriptorTable(rootParameterIndex, descriptor);
        _computeRootDescriptorTables.Set(rootParameterIndex, descriptor);
        return true;
    }

    internal bool IsBoundSet(bool graphics, uint setIndex, BindingSetHandle bindingSet)
    {
        if (graphics)
            return _graphicsBindingSets.TryGetValue(setIndex, out var currentGraphics) && currentGraphics == bindingSet;

        return _computeBindingSets.TryGetValue(setIndex, out var currentCompute) && currentCompute == bindingSet;
    }

    internal void TrackBoundSet(bool graphics, uint setIndex, BindingSetHandle bindingSet)
    {
        if (graphics)
            _graphicsBindingSets.Set(setIndex, bindingSet);
        else
            _computeBindingSets.Set(setIndex, bindingSet);
    }

    internal void ClearBoundSet(bool graphics, uint setIndex)
    {
        if (graphics)
            _graphicsBindingSets.Set(setIndex, BindingSetHandle.Invalid);
        else
            _computeBindingSets.Set(setIndex, BindingSetHandle.Invalid);
    }

    internal void SetViewport(float x, float y, float width, float height, float minDepth, float maxDepth)
    {
        if (_hasViewport
            && _currentViewport.X == x
            && _currentViewport.Y == y
            && _currentViewport.Width == width
            && _currentViewport.Height == height
            && _currentViewport.MinDepth == minDepth
            && _currentViewport.MaxDepth == maxDepth)
        {
            return;
        }

        _list.RSSetViewport(x, y, width, height, minDepth, maxDepth);
        _currentViewport = new ViewportState(x, y, width, height, minDepth, maxDepth);
        _hasViewport = true;
    }

    internal void SetScissorRect(int left, int top, int right, int bottom)
    {
        if (_hasScissorRect
            && _currentScissorRect.Left == left
            && _currentScissorRect.Top == top
            && _currentScissorRect.Right == right
            && _currentScissorRect.Bottom == bottom)
        {
            return;
        }

        _list.RSSetScissorRect(new Vortice.RawRect(left, top, right, bottom));
        _currentScissorRect = new ScissorState(left, top, right, bottom);
        _hasScissorRect = true;
    }

    internal void SetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology topology)
    {
        if (_hasPrimitiveTopology && _currentPrimitiveTopology == topology)
            return;

        _list.IASetPrimitiveTopology(topology);
        _currentPrimitiveTopology = topology;
        _hasPrimitiveTopology = true;
    }

    public void Barrier(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)
    {
        CheckCanRecord();
        RecordBarriers(textureBarriers, bufferBarriers, aliasingBarriers);
    }

    internal void CheckOpenPass(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)
    {
        CheckPassRecord();
        RecordBarriers(textureBarriers, bufferBarriers, aliasingBarriers);
    }

    [SkipLocalsInit]
    private void RecordBarriers(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers)
    {
        if (textureBarriers.IsEmpty && bufferBarriers.IsEmpty && aliasingBarriers.IsEmpty)
            return;

        _device.ReportBarriers(textureBarriers.Length, bufferBarriers.Length, aliasingBarriers.Length);
        int additionalAliasOperations = aliasingBarriers.Length;
        if (_device.ValidationEnabled && _device.HasAliasingUseValidation)
            additionalAliasOperations = checked(additionalAliasOperations + textureBarriers.Length + bufferBarriers.Length);
        if (additionalAliasOperations > 0)
            _aliasingOperations.EnsureCapacity(checked(_aliasingOperations.Count + additionalAliasOperations));
        using var scope = Profiler.BeginScope("D3D12.Barrier");
        {
            int nativeBarrierCount = 0;
            int uavDependencyCount = 0;
            ResourceBarrier singleUavNativeBarrier = default;
            Span<ResourceBarrier> stackNativeBarriers = stackalloc ResourceBarrier[64];
            List<ResourceBarrier>? heapNativeBarriers = null;
            foreach (var barrier in aliasingBarriers)
            {
                if (_device.ValidationEnabled)
                    _device.ValidateAliasingBarrier(barrier);
                var before = ResourceFor(barrier.Before);
                var after = ResourceFor(barrier.After);
                if (before == null && after == null)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier cannot have both endpoints empty.");
                TrackAliasingEndpoint(barrier.Before);
                TrackAliasingEndpoint(barrier.After);
                AddNativeBarrier(
                    ResourceBarrier.BarrierAliasing(before, after),
                    stackNativeBarriers,
                    ref heapNativeBarriers,
                    ref nativeBarrierCount);
                _aliasingOperations.Add(new AliasOperation(AliasOperationKind.Barrier, default, barrier));
                _aliasingUsesSinceBarrier.Clear();
            }

            foreach (var barrier in textureBarriers)
            {
                var texture = GetTexture(barrier.Texture, "Texture");
                var range = texture.ActualRange(barrier.Range);
                if (_device.ValidationEnabled)
                {
                    bool allowPresent = texture.Allocation.Ownership == ResourceOwnership.Swapchain;
                    Validation.TextureResourceState(texture.Desc, barrier.Before, "Texture barrier before state", allowPresent);
                    Validation.TextureResourceState(texture.Desc, barrier.After, "Texture barrier after state", allowPresent);
                }
                bool uavDependency = barrier.Before == ResourceState.UnorderedAccess
                    && barrier.After == ResourceState.UnorderedAccess;
                if (uavDependency)
                    RegisterUavBarrier(
                        ResourceBarrier.BarrierUnorderedAccessView(texture.Resource),
                        ref singleUavNativeBarrier,
                        ref uavDependencyCount);
                var before = NativeState(barrier.Before);
                var after = NativeState(barrier.After);
                bool wholeTextureTransition = !uavDependency
                    && before != after
                    && range.CoversAll(texture.Desc);
                if (wholeTextureTransition)
                {
                    AddNativeBarrier(
                        ResourceBarrier.BarrierTransition(texture.Resource, before, after, Vortice.Direct3D12.D3D12.ResourceBarrierAllSubResources),
                        stackNativeBarriers,
                        ref heapNativeBarriers,
                        ref nativeBarrierCount);
                }

                if (_device.ValidationEnabled)
                    CheckTextureState(barrier.Texture, range, barrier.Before, "Texture barrier");
                else
                    TrackTexture(barrier.Texture);
                if (!wholeTextureTransition && !uavDependency && before != after)
                {
                    for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
                    {
                        for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
                        {
                            uint subresource = texture.SubresourceIndex(mip, slice);
                            AddNativeBarrier(
                                ResourceBarrier.BarrierTransition(texture.Resource, before, after, subresource),
                                stackNativeBarriers,
                                ref heapNativeBarriers,
                                ref nativeBarrierCount);
                        }
                    }
                }
                SetTextureState(barrier.Texture, range, barrier.Before, barrier.After);
            }

            _state.EnsureAdditionalBufferCapacity(bufferBarriers.Length);
            foreach (var barrier in bufferBarriers)
            {
                var buffer = GetBuffer(barrier.Buffer, "Buffer");
                if (_device.ValidationEnabled)
                {
                    Validation.BufferResourceState(buffer.Desc, barrier.Before, "Buffer barrier before state");
                    Validation.BufferResourceState(buffer.Desc, barrier.After, "Buffer barrier after state");
                }
                bool uavDependency = barrier.Before == ResourceState.UnorderedAccess
                    && barrier.After == ResourceState.UnorderedAccess;
                if (uavDependency && !buffer.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer UAV dependency barrier requires UnorderedAccess bind flag.");
                if (_device.ValidationEnabled)
                    RequireBufferState(barrier.Buffer, buffer, barrier.Before, "Buffer barrier");
                else
                    TrackBuffer(barrier.Buffer);
                var before = NativeState(barrier.Before);
                var after = NativeState(barrier.After);
                if (uavDependency)
                {
                    RegisterUavBarrier(
                        ResourceBarrier.BarrierUnorderedAccessView(buffer.Resource),
                        ref singleUavNativeBarrier,
                        ref uavDependencyCount);
                }
                if (before != after)
                {
                    AddNativeBarrier(
                        ResourceBarrier.BarrierTransition(buffer.Resource, before, after, Vortice.Direct3D12.D3D12.ResourceBarrierAllSubResources),
                        stackNativeBarriers,
                        ref heapNativeBarriers,
                        ref nativeBarrierCount);
                }
                if (_device.ValidationEnabled)
                    SetBufferState(barrier.Buffer, barrier.After);
                else
                    SetBufferState(barrier.Buffer, barrier.Before, barrier.After);
            }

            if (uavDependencyCount == 1)
            {
                AddNativeBarrier(
                    singleUavNativeBarrier,
                    stackNativeBarriers,
                    ref heapNativeBarriers,
                    ref nativeBarrierCount);
            }
            else if (uavDependencyCount > 1)
            {
                AddNativeBarrier(
                    ResourceBarrier.BarrierUnorderedAccessView(null!),
                    stackNativeBarriers,
                    ref heapNativeBarriers,
                    ref nativeBarrierCount);
            }

            using (Profiler.BeginScope("D3D12.Barrier.FlushNative"))
            {
                _device.ReportNative(nativeBarrierCount);
                FlushNativeBarriers(stackNativeBarriers, heapNativeBarriers, nativeBarrierCount);
            }
        }
    }

    public void UavBarrier(TextureHandle texture)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.UavBarrier");
        var record = _device.Textures.Get(texture, "Texture");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture UAV barrier requires UnorderedAccess bind flag.");
        var range = new SubresourceRange(0, record.Desc.MipLevels, 0, record.Desc.ArraySize);
        if (_device.ValidationEnabled)
            CheckTextureState(texture, range, ResourceState.UnorderedAccess, "Texture UAV barrier");
        else
            TrackTexture(texture);
        SetTextureState(texture, range, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess);

        _device.ReportDependencies(1);
        _device.ReportNative(1);
        _list.ResourceBarrierUnorderedAccessView(record.Resource);
    }

    public void UavBarrier(BufferHandle buffer)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.UavBarrier");
        var record = _device.Buffers.Get(buffer, "Buffer");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer UAV barrier requires UnorderedAccess bind flag.");
        RequireBufferState(buffer, ResourceState.UnorderedAccess, "Buffer UAV barrier");
        _device.ReportDependencies(1);
        _device.ReportNative(1);
        _list.ResourceBarrierUnorderedAccessView(record.Resource);
    }

    private static void AddNativeBarrier(
        ResourceBarrier barrier,
        Span<ResourceBarrier> stackBarriers,
        ref List<ResourceBarrier>? heapBarriers,
        ref int count)
    {
        if (heapBarriers == null && count < stackBarriers.Length)
        {
            stackBarriers[count++] = barrier;
            return;
        }

        heapBarriers ??= new List<ResourceBarrier>(stackBarriers.Length * 2);
        if (count == stackBarriers.Length)
        {
            for (int index = 0; index < stackBarriers.Length; index++)
                heapBarriers.Add(stackBarriers[index]);
        }

        heapBarriers.Add(barrier);
        count++;
    }

    private static void RegisterUavBarrier(
        ResourceBarrier barrier,
        ref ResourceBarrier singleBarrier,
        ref int count)
    {
        if (count == 0)
            singleBarrier = barrier;
        count++;
    }

    private void FlushNativeBarriers(
        Span<ResourceBarrier> stackBarriers,
        List<ResourceBarrier>? heapBarriers,
        int count)
    {
        if (count == 0)
            return;

        if (heapBarriers == null)
        {
            _list.ResourceBarrier(stackBarriers[..count]);
            return;
        }

        _list.ResourceBarrier(CollectionsMarshal.AsSpan(heapBarriers));
    }

    private ResourceStates NativeState(ResourceState state)
    {
        if (_queueKind != CommandQueueKind.Compute)
            return D3D12Mappings.ToState(state);

        return state switch
        {
            ResourceState.GenericRead => ResourceStates.GenericRead,
            ResourceState.ShaderResource => ResourceStates.NonPixelShaderResource,
            ResourceState.IndirectArgument => ResourceStates.NonPixelShaderResource | ResourceStates.IndirectArgument,
            _ => D3D12Mappings.ToState(state),
        };
    }

    public IRenderPass BeginRenderPass(in RenderPassDesc desc)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.BeginPass");
        if (_queueKind != CommandQueueKind.Direct)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass requires a graphics queue command list.");
        RenderPass.ValidateForBegin(this, _device, desc);
        var pass = new RenderPass(this, _device, desc, validated: true);
        _passOpen = true;
        return pass;
    }

    public IComputePass BeginComputePass(ComputePassDesc desc)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.BeginPass");
        if (_queueKind == CommandQueueKind.Copy)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Compute pass cannot be recorded on a copy queue.");
        _passOpen = true;
        return new ComputePass(this, _device, desc);
    }

    public IRtPass BeginRtPass(RtPassDesc desc)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.BeginPass");
        if (!_device.Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing passes require ray tracing support.");
        if (_queueKind == CommandQueueKind.Copy)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing pass cannot be recorded on a copy queue.");
        _ = desc;
        _passOpen = true;
        return new RtPass(this, _device);
    }

    public void CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong byteCount)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.Copy");
        var src = _device.Buffers.Get(source, "SourceBuffer");
        var dst = _device.Buffers.Get(destination, "DestinationBuffer");
        RequireBufferCopy(src.Desc, BindFlags.CopySource, "CopyBuffer source");
        RequireBufferCopy(dst.Desc, BindFlags.CopyDestination, "CopyBuffer destination");
        RequireBufferState(source, ResourceState.CopySource, "Copy source buffer");
        RequireBufferState(destination, ResourceState.CopyDestination, "Copy destination buffer");
        RhiCommandValidation.ValidateBufferRange(src.Desc, sourceOffset, byteCount, "CopyBuffer source");
        RhiCommandValidation.ValidateBufferRange(dst.Desc, destinationOffset, byteCount, "CopyBuffer destination");
        _list.CopyBufferRegion(dst.Resource, destinationOffset, src.Resource, sourceOffset, byteCount);
    }

    public void CopyToTexture(BufferHandle source, BufferTextureCopy sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.Copy");
        var src = _device.Buffers.Get(source, "SourceBuffer");
        var dst = _device.Textures.Get(destination, "DestinationTexture");
        RequireBufferCopy(src.Desc, BindFlags.CopySource, "CopyToTexture source");
        RequireTextureCopy(dst.Desc, BindFlags.CopyDestination, "CopyToTexture destination");
        RequireBufferState(source, ResourceState.CopySource, "Copy source buffer");
        RequireTextureState(destination, destinationRegion.MipLevel, destinationRegion.ArraySlice, ResourceState.CopyDestination, "Copy destination texture");
        RhiCommandValidation.ValidateBufferCopy(dst.Desc);
        RhiCommandValidation.ValidateTextureRegion(dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateFootprint(src.Desc, sourceRegion, destinationRegion, dst.Desc.Format, _device.Limits.TextureRowPitchAlignment);
        ValidatePlacement(sourceRegion.Offset, "CopyToTexture source");
        var footprint = CreateFootprint(dst.Desc.Format, sourceRegion, destinationRegion);
        _list.CopyTextureRegion(new TextureCopyLocation(dst.Resource, dst.SubresourceIndex(destinationRegion.MipLevel, destinationRegion.ArraySlice)), destinationRegion.X, destinationRegion.Y, destinationRegion.Z, new TextureCopyLocation(src.Resource, footprint), null);
    }

    public void CopyToBuffer(TextureHandle source, TextureCopyRegion sourceRegion, BufferHandle destination, BufferTextureCopy destinationRegion)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.Copy");
        var src = _device.Textures.Get(source, "SourceTexture");
        var dst = _device.Buffers.Get(destination, "DestinationBuffer");
        RequireTextureCopy(src.Desc, BindFlags.CopySource, "CopyToBuffer source");
        RequireBufferCopy(dst.Desc, BindFlags.CopyDestination, "CopyToBuffer destination");
        RequireTextureState(source, sourceRegion.MipLevel, sourceRegion.ArraySlice, ResourceState.CopySource, "Copy source texture");
        RequireBufferState(destination, ResourceState.CopyDestination, "Copy destination buffer");
        RhiCommandValidation.ValidateBufferCopy(src.Desc);
        RhiCommandValidation.ValidateTextureRegion(src.Desc, sourceRegion);
        RhiCommandValidation.ValidateFootprint(dst.Desc, destinationRegion, sourceRegion, src.Desc.Format, _device.Limits.TextureRowPitchAlignment);
        ValidatePlacement(destinationRegion.Offset, "CopyToBuffer destination");
        var footprint = CreateFootprint(src.Desc.Format, destinationRegion, sourceRegion);
        _list.CopyTextureRegion(
            new TextureCopyLocation(dst.Resource, footprint),
            0,
            0,
            0,
            new TextureCopyLocation(src.Resource, src.SubresourceIndex(sourceRegion.MipLevel, sourceRegion.ArraySlice)),
            CreateSourceBox(sourceRegion));
    }

    public void CopyTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.Copy");
        var src = _device.Textures.Get(source, "SourceTexture");
        var dst = _device.Textures.Get(destination, "DestinationTexture");
        RequireTextureCopy(src.Desc, BindFlags.CopySource, "CopyTexture source");
        RequireTextureCopy(dst.Desc, BindFlags.CopyDestination, "CopyTexture destination");
        RequireTextureState(source, sourceRegion.MipLevel, sourceRegion.ArraySlice, ResourceState.CopySource, "Copy source texture");
        RequireTextureState(destination, destinationRegion.MipLevel, destinationRegion.ArraySlice, ResourceState.CopyDestination, "Copy destination texture");
        RhiCommandValidation.ValidateTextureRegion(src.Desc, sourceRegion);
        RhiCommandValidation.ValidateTextureRegion(dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateCopyCompat(src.Desc, sourceRegion, dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateCopyOverlap(source, sourceRegion, destination, destinationRegion);
        _list.CopyTextureRegion(
            new TextureCopyLocation(dst.Resource, dst.SubresourceIndex(destinationRegion.MipLevel, destinationRegion.ArraySlice)),
            destinationRegion.X,
            destinationRegion.Y,
            destinationRegion.Z,
            new TextureCopyLocation(src.Resource, src.SubresourceIndex(sourceRegion.MipLevel, sourceRegion.ArraySlice)),
            CreateSourceBox(sourceRegion));
    }


    public void ResolveTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
    {
        CheckCanRecord();
        using var scope = Profiler.BeginScope("D3D12.Resolve");
        var src = _device.Textures.Get(source, "SourceTexture");
        var dst = _device.Textures.Get(destination, "DestinationTexture");
        if (src.Desc.SampleCount <= 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture source must be multisampled.");
        if (dst.Desc.SampleCount != 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture destination must be single-sampled.");
        if (src.Desc.Format != dst.Desc.Format)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture requires matching formats.");
        RequireTextureState(source, sourceRegion.MipLevel, sourceRegion.ArraySlice, ResourceState.ResolveSource, "Resolve source texture");
        RequireTextureState(destination, destinationRegion.MipLevel, destinationRegion.ArraySlice, ResourceState.ResolveDestination, "Resolve destination texture");
        RhiCommandValidation.ValidateTextureRegion(src.Desc, sourceRegion);
        RhiCommandValidation.ValidateTextureRegion(dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateResolveCompat(src.Desc, sourceRegion, dst.Desc, destinationRegion);
        _list.ResolveSubresource(dst.Resource, dst.SubresourceIndex(destinationRegion.MipLevel, destinationRegion.ArraySlice), src.Resource, src.SubresourceIndex(sourceRegion.MipLevel, sourceRegion.ArraySlice), D3D12Mappings.ToDxgi(src.Desc.Format));
    }

    public void BuildAccelerationStructure(AccelBuildDesc desc)
    {
        CheckCanRecord();
        if (!_device.Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structure builds require ray tracing support.");
        if (_queueKind == CommandQueueKind.Copy)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure builds cannot be recorded on a copy queue.");
        Validation.AccelBuildDesc(desc);
        var destination = _device.AccelerationStructures.Get(desc.Destination, "DestinationAccelerationStructure");
        if (destination.Desc.Kind != desc.Kind)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure build kind {desc.Kind} does not match destination kind {destination.Desc.Kind}.");

        var scratch = _device.Buffers.Get(desc.ScratchBuffer, "AccelerationStructureScratchBuffer");
        if (!scratch.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure scratch buffer requires UnorderedAccess bind flag.");
        RequireBufferState(desc.ScratchBuffer, ResourceState.UnorderedAccess, "Acceleration structure scratch buffer");
        if (desc.ScratchOffset % _device.Limits.AccelerationStructureAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure scratch offset must be aligned to {_device.Limits.AccelerationStructureAlignment} bytes.");
        var sizes = _device.GetAccelSizes(desc);
        if (destination.Desc.SizeInBytes < sizes.AccelerationStructureSizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure destination is smaller than the required build result size.");
        ulong scratchSize = sizes.BuildScratchSizeInBytes;
        RhiCommandValidation.ValidateBufferRange(scratch.Desc, desc.ScratchOffset, scratchSize, "Acceleration structure scratch buffer");
        ValidateGeometry(desc);

        var list4 = RequireList4("Acceleration structure builds");
        var inputs = _device.CreateRtInputs(desc, requireResourceHandles: true);
        list4.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(
            destination.Resource.GPUVirtualAddress,
            inputs,
            0,
            scratch.Resource.GPUVirtualAddress + desc.ScratchOffset));
        _list.ResourceBarrierUnorderedAccessView(destination.Resource);
        TrackAccelerationStructure(desc.Destination);
        if (desc.Source.IsValid)
            TrackAccelerationStructure(desc.Source);
        TrackBuffer(desc.ScratchBuffer);
        TrackGeometry(desc);
    }

    public void CopyAccelerationStructure(AccelerationStructureHandle source, AccelerationStructureHandle destination, AccelCopyMode mode)
    {
        CheckCanRecord();
        if (!Enum.IsDefined(mode))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure copy mode value {mode} is not defined.");
        if (!_device.Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structure copies require ray tracing support.");
        if (_queueKind == CommandQueueKind.Copy)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure copies cannot be recorded on a copy queue.");
        var sourceRecord = _device.AccelerationStructures.Get(source, "SourceAccelerationStructure");
        var destinationRecord = _device.AccelerationStructures.Get(destination, "DestinationAccelerationStructure");
        if (sourceRecord.Desc.Kind != destinationRecord.Desc.Kind)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure copy requires matching source and destination kinds.");
        if (mode == AccelCopyMode.Clone && destinationRecord.Desc.SizeInBytes < sourceRecord.Desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure clone destination is smaller than the source.");
        var list4 = RequireList4("Acceleration structure copies");
        list4.CopyRaytracingAccelerationStructure(
            destinationRecord.Resource.GPUVirtualAddress,
            sourceRecord.Resource.GPUVirtualAddress,
            D3D12Mappings.ToRtCopy(mode));
        _list.ResourceBarrierUnorderedAccessView(destinationRecord.Resource);
        TrackAccelerationStructure(source);
        TrackAccelerationStructure(destination);
    }

    public void WriteTimestamp(QueryPoolHandle queryPool, uint queryIndex)
    {
        CheckCanRecord();
        var pool = _device.QueryPools.Get(queryPool, "QueryPool");
        if (pool.Desc.Type != QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "WriteTimestamp requires a timestamp query pool.");
        if (queryIndex >= pool.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp query index is outside the query pool.");
        EnsureQueryQueue(pool);
        TrackQueryPool(queryPool);
        _writtenQueries.Set(new QueryKey(queryPool, queryIndex), true);
        _queryOperations.Add(new QueryOperation(QueryOperationKind.Write, queryPool, queryIndex, 1));
        _list.EndQuery(pool.Heap, Vortice.Direct3D12.QueryType.Timestamp, queryIndex);
    }

    public void BeginQuery(QueryPoolHandle queryPool, uint queryIndex)
    {
        CheckCanRecord();
        var pool = _device.QueryPools.Get(queryPool, "QueryPool");
        if (pool.Desc.Type == QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp queries do not use BeginQuery.");
        if (queryIndex >= pool.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query index is outside the query pool.");
        EnsureQueryQueue(pool);
        var key = new QueryKey(queryPool, queryIndex);
        if (!_activeQueries.TryAdd(key, true))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot begin an already active query in the same command buffer.");
        TrackQueryPool(queryPool);
        _queryOperations.Add(new QueryOperation(QueryOperationKind.Begin, queryPool, queryIndex, 1));
        _list.BeginQuery(pool.Heap, ToQueryType(pool.Desc.Type), queryIndex);
    }

    public void EndQuery(QueryPoolHandle queryPool, uint queryIndex)
    {
        CheckCanRecord();
        var pool = _device.QueryPools.Get(queryPool, "QueryPool");
        if (pool.Desc.Type == QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp queries do not use EndQuery.");
        if (queryIndex >= pool.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query index is outside the query pool.");
        EnsureQueryQueue(pool);
        var key = new QueryKey(queryPool, queryIndex);
        if (!_activeQueries.Remove(key))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot end a query that was not begun in the same command buffer.");
        TrackQueryPool(queryPool);
        _writtenQueries.Set(key, true);
        _queryOperations.Add(new QueryOperation(QueryOperationKind.End, queryPool, queryIndex, 1));
        _list.EndQuery(pool.Heap, ToQueryType(pool.Desc.Type), queryIndex);
    }

    public void ResolveQueryData(QueryPoolHandle queryPool, uint firstQuery, uint queryCount, BufferHandle destination, ulong destinationOffset)
    {
        CheckCanRecord();
        var pool = _device.QueryPools.Get(queryPool, "QueryPool");
        var buffer = _device.Buffers.Get(destination, "QueryResolveDestination");
        if (queryCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve count must be greater than zero.");
        if (firstQuery >= pool.Desc.Count || queryCount > pool.Desc.Count - firstQuery)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve range is outside the query pool.");
        if (!buffer.Desc.BindFlags.HasFlag(BindFlags.CopyDestination))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve destination buffer requires CopyDestination bind flag.");
        ulong byteCount = checked((ulong)queryCount * RhiCommandValidation.QueryBytes(pool.Desc.Type));
        if (destinationOffset % sizeof(ulong) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve destination offset must be 8-byte aligned.");
        if (destinationOffset > buffer.Desc.SizeInBytes || byteCount > buffer.Desc.SizeInBytes - destinationOffset)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve destination range is outside the buffer.");
        EnsureQueryQueue(pool);
        TrackQueryPool(queryPool);
        RequireBufferState(destination, ResourceState.QueryResolve, "Query resolve destination");
        _queryOperations.Add(new QueryOperation(QueryOperationKind.Resolve, queryPool, firstQuery, queryCount));
        _queryResolves.Add(new QueryResolve(queryPool, firstQuery, queryCount));
        _list.ResolveQueryData(pool.Heap, ToQueryType(pool.Desc.Type), firstQuery, queryCount, buffer.Resource, destinationOffset);
    }

    public void PushDebugGroup(string name)
    {
        _device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (string.IsNullOrWhiteSpace(name))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Debug marker name must not be empty.");
        Profiler.Scope scope = Profiler.BeginMarker(name, "D3D12");
        if (scope.IsActive)
            _debugScopes.Add(scope);
        if (_emitDebugMarkers)
            PixEvents.BeginEvent(_list.NativePointer, name);
        _debugDepth++;
    }

    public void PopDebugGroup()
    {
        _device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (_debugDepth == 0)
            throw new RhiException(ErrorCode.ValidationFailure, "Debug marker stack is empty.");
        if (_emitDebugMarkers)
            PixEvents.EndEvent(_list.NativePointer);
        if (_debugScopes.Count != 0)
        {
            int scopeIndex = _debugScopes.Count - 1;
            _debugScopes[scopeIndex].Dispose();
            _debugScopes.RemoveAt(scopeIndex);
        }
        _debugDepth--;
    }

    public void InsertDebugMarker(string name)
    {
        _device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (string.IsNullOrWhiteSpace(name))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Debug marker name must not be empty.");
        if (_emitDebugMarkers)
            PixEvents.SetMarker(_list.NativePointer, name);
    }

    public CommandBufferHandle Finish()
    {
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        using var scope = Profiler.BeginScope("D3D12.CommandList.Finish");
        if (_passOpen)
        {
            AbortRecording();
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot finish command list while a pass is open.");
        }
        if (_debugDepth != 0)
        {
            AbortRecording();
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot finish command list with unclosed debug markers.");
        }
        if (_activeQueries.Count != 0)
        {
            AbortRecording();
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot finish command list with active queries. End each non-timestamp query in the same command buffer.");
        }
        bool commandBufferOwnsStorage = false;
        bool closed = false;
        TrackedDescriptorAllocation[]? transientDescriptors = null;
        BufferTransition[]? bufferStates = null;
        TextureTransition[]? textureStates = null;
        AliasOperation[]? aliasingOperations = null;
        QueryOperation[]? queryOperations = null;
        QueryKey[]? writtenQueries = null;
        QueryResolve[]? queryResolves = null;
        BufferHandle[]? referencedBuffers = null;
        TextureHandle[]? referencedTextures = null;
        QueryPoolHandle[]? referencedQueryPools = null;
        TextureViewHandle[]? referencedTextureViews = null;
        BufferViewHandle[]? referencedBufferViews = null;
        SamplerHandle[]? referencedSamplers = null;
        AccelerationStructureHandle[]? referencedAccelerationStructures = null;
        BindingLayoutHandle[]? referencedBindingLayouts = null;
        BindingSetHandle[]? referencedBindingSets = null;
        PipelineLayoutHandle[]? referencedPipelineLayouts = null;
        PipelineHandle[]? referencedPipelines = null;
        bool referencesCopied = false;
        ulong debugMessageStart = _device.GetDebugCount();
        try
        {
            using (Profiler.BeginScope("D3D12.CommandList.Finish.Close"))
            {
                _list.Close();
            }
            closed = true;
            _finished = true;

            using (Profiler.BeginScope("D3D12.CommandList.Finish.CopyDescriptors"))
            {
                transientDescriptors = _transientDescriptors.ToArray();
            }
            using (Profiler.BeginScope("D3D12.CommandList.Finish.CopyState"))
            {
                bufferStates = _state.CopyBuffers();
                textureStates = _state.CopyTextures();
            }
            using (Profiler.BeginScope("D3D12.CommandList.Finish.CopyAliasing"))
            {
                aliasingOperations = _aliasingOperations.ToArray();
            }
            using (Profiler.BeginScope("D3D12.CommandList.Finish.CopyQueries"))
            {
                queryOperations = _queryOperations.ToArray();
                writtenQueries = CopyKeys(ref _writtenQueries);
                queryResolves = _queryResolves.ToArray();
            }
            using (Profiler.BeginScope("D3D12.CommandList.Finish.CopyReferences"))
            {
                referencedBuffers = CopyKeys(ref _referencedBuffers);
                referencedTextures = CopyKeys(ref _referencedTextures);
                referencedQueryPools = CopyKeys(ref _referencedQueryPools);
                referencedTextureViews = CopyKeys(ref _referencedTextureViews);
                referencedBufferViews = CopyKeys(ref _referencedBufferViews);
                referencedSamplers = CopyKeys(ref _referencedSamplers);
                referencedAccelerationStructures = CopyKeys(ref _referencedAccelerationStructures);
                referencedBindingLayouts = CopyKeys(ref _referencedBindingLayouts);
                referencedBindingSets = CopyKeys(ref _referencedBindingSets);
                referencedPipelineLayouts = CopyKeys(ref _referencedPipelineLayouts);
                referencedPipelines = CopyKeys(ref _referencedPipelines);
            }
            referencesCopied = true;

            CommandBufferHandle commandBuffer;
            using (Profiler.BeginScope("D3D12.CommandList.Finish.CreateRecord"))
            {
                commandBuffer = _device.AddCommandBuffer(
                    new CommandBufferRecord(
                        _queueKind,
                        StoreStateAndGetLease(),
                        transientDescriptors,
                        bufferStates,
                        textureStates,
                        aliasingOperations,
                        queryOperations,
                        writtenQueries,
                        queryResolves,
                        referencedBuffers,
                        referencedTextures,
                        referencedQueryPools,
                        referencedTextureViews,
                        referencedBufferViews,
                        referencedSamplers,
                        referencedAccelerationStructures,
                        referencedBindingLayouts,
                        referencedBindingSets,
                        referencedPipelineLayouts,
                        referencedPipelines));
            }
            commandBufferOwnsStorage = true;
            return commandBuffer;
        }
        catch (SharpGenException ex)
        {
            throw new RhiException(
                ErrorCode.BackendFailure,
                $"D3D12 failed to close command list: {ex.Message}{_device.FormatDebugMessages(debugMessageStart)}");
        }
        finally
        {
            DisposeCommandLists();
            if (referencesCopied)
            {
                using (Profiler.BeginScope("D3D12.CommandList.Finish.UnpinReferences"))
                {
                    _device.Unpin(referencedBuffers!);
                    _device.Unpin(referencedTextures!);
                    _device.Unpin(referencedQueryPools!);
                    _device.Unpin(referencedTextureViews!);
                    _device.Unpin(referencedBufferViews!);
                    _device.Unpin(referencedSamplers!);
                    _device.Unpin(referencedAccelerationStructures!);
                    _device.Unpin(referencedBindingLayouts!);
                    _device.Unpin(referencedBindingSets!);
                    _device.Unpin(referencedPipelineLayouts!);
                    _device.Unpin(referencedPipelines!);
                }
            }
            else
            {
                UnpinAll();
            }

            if (!commandBufferOwnsStorage)
            {
                var descriptorsToFree = transientDescriptors is not null
                    ? (IEnumerable<TrackedDescriptorAllocation>)transientDescriptors
                    : _transientDescriptors;
                using (Profiler.BeginScope("D3D12.CommandList.Finish.ReleaseDescriptors"))
                {
                    foreach (var allocation in descriptorsToFree)
                        _device.FreeTransientDescriptor(allocation.Allocation, allocation.Type);
                }
                if (closed)
                    ReturnLeaseToQueue();
                else
                {
                    DisposeLease();
                }
            }

            DisposeTracking();
        }
    }

    private void AbortRecording()
    {
        _finished = true;
        _passOpen = false;
        ClearDebugScopes();
        _debugDepth = 0;
        foreach (var allocation in _transientDescriptors)
            _device.FreeTransientDescriptor(allocation.Allocation, allocation.Type);

        UnpinAll();
        try
        {
            _list.Close();
            DisposeCommandLists();
            ReturnLeaseToQueue();
        }
        catch
        {
            DisposeCommandLists();
            DisposeLease();
        }

        DisposeTracking();
    }

    private CommandListLease StoreStateAndGetLease()
    {
        if (!_stateStored)
        {
            _lease.StoreState(ref _state);
            StoreResourceReferences();
            _stateStored = true;
        }

        return _lease;
    }

    private void StoreResourceReferences()
    {
        StoreReferenceCore(ref _referencedBuffers, ref _lease.ReferencedBuffers);
        StoreReferenceCore(ref _referencedTextures, ref _lease.ReferencedTextures);
        StoreReferenceCore(ref _referencedQueryPools, ref _lease.ReferencedQueryPools);
        StoreReferenceCore(ref _referencedTextureViews, ref _lease.ReferencedTextureViews);
        StoreReferenceCore(ref _referencedBufferViews, ref _lease.ReferencedBufferViews);
        StoreReferenceCore(ref _referencedSamplers, ref _lease.ReferencedSamplers);
        StoreReferenceCore(ref _referencedAccelerationStructures, ref _lease.ReferencedAccelerationStructures);
        StoreReferenceCore(ref _referencedBindingLayouts, ref _lease.ReferencedBindingLayouts);
        StoreReferenceCore(ref _referencedBindingSets, ref _lease.ReferencedBindingSets);
        StoreReferenceCore(ref _referencedPipelineLayouts, ref _lease.ReferencedPipelineLayouts);
        StoreReferenceCore(ref _referencedPipelines, ref _lease.ReferencedPipelines);
    }

    private static void StoreReferenceCore<TKey>(
        ref InlineFlatCore<TKey, bool> source,
        ref InlineFlatCore<TKey, bool> destination)
        where TKey : notnull
    {
        source.ClearNoResize();
        destination = source;
        source = default;
    }

    private void ReturnLeaseToQueue()
    {
        _device.GetQueue(_queueKind).ReturnCommandList(StoreStateAndGetLease());
    }

    private void DisposeLease()
    {
        StoreStateAndGetLease();
        _lease.Dispose();
    }

    private void ClearDebugScopes()
    {
        for (int index = _debugScopes.Count - 1; index >= 0; index--)
            _debugScopes[index].Dispose();
        _debugScopes.Clear();
    }

    private void UnpinAll()
    {
        _device.Unpin(CopyKeys(ref _referencedBuffers));
        _device.Unpin(CopyKeys(ref _referencedTextures));
        _device.Unpin(CopyKeys(ref _referencedQueryPools));
        _device.Unpin(CopyKeys(ref _referencedTextureViews));
        _device.Unpin(CopyKeys(ref _referencedBufferViews));
        _device.Unpin(CopyKeys(ref _referencedSamplers));
        _device.Unpin(CopyKeys(ref _referencedAccelerationStructures));
        _device.Unpin(CopyKeys(ref _referencedBindingLayouts));
        _device.Unpin(CopyKeys(ref _referencedBindingSets));
        _device.Unpin(CopyKeys(ref _referencedPipelineLayouts));
        _device.Unpin(CopyKeys(ref _referencedPipelines));
    }

    private void DisposeTracking()
    {
        _aliasingUsesSinceBarrier.Dispose();
        _writtenQueries.Dispose();
        _activeQueries.Dispose();
        _graphicsRootDescriptorTables.Dispose();
        _computeRootDescriptorTables.Dispose();
        _graphicsBindingSets.Dispose();
        _computeBindingSets.Dispose();
    }

    private void DisposeCommandLists()
    {
        _list6?.Dispose();
        _list6 = null;
        _list4?.Dispose();
        _list4 = null;
    }

    internal void EndPass()
    {
        if (!_passOpen)
            throw new RhiException(ErrorCode.ValidationFailure, "No pass is open.");
        _passOpen = false;
    }

    internal void CheckPassRecord()
    {
        _device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (!_passOpen)
            throw new RhiException(ErrorCode.ValidationFailure, "No pass is open.");
    }

    internal void RequireBufferState(BufferHandle buffer, ResourceState state, string label)
    {
        if (!_device.ValidationEnabled)
        {
            TrackBuffer(buffer);
            return;
        }

        var record = _device.Buffers.Get(buffer, "Buffer");
        RequireBufferState(buffer, record, state, label);
    }

    private void RequireBufferState(BufferHandle buffer, BufferRecord record, ResourceState state, string label)
    {
        TrackBuffer(buffer);
        if (!_device.ValidationEnabled)
            return;

        _state.RequireBuffer(buffer, record.State, state, label, "command list");
    }

    internal void ValidateBufferState(BufferHandle buffer, ResourceState state, string label)
    {
        if (!_device.ValidationEnabled)
        {
            TrackBuffer(buffer);
            return;
        }

        var record = _device.Buffers.Get(buffer, "Buffer");
        _state.ValidateBuffer(buffer, record.State, state, label, "command list");
    }

    internal void RequireTextureState(TextureHandle texture, uint mip, uint slice, ResourceState state, string label)
    {
        if (!_device.ValidationEnabled)
        {
            TrackTexture(texture);
            return;
        }

        var record = GetTexture(texture, "Texture");
        RequireTextureState(texture, record, mip, slice, state, label);
    }

    private void RequireTextureState(TextureHandle texture, TextureRecord record, uint mip, uint slice, ResourceState state, string label)
    {
        TrackTexture(texture);
        if (!_device.ValidationEnabled)
            return;

        record.SubresourceIndex(mip, slice);
        var key = new SubresourceKey(texture, mip, slice);
        _state.RequireTexture(key, state, label, "command list");
    }

    internal void RequireTextureState(TextureHandle texture, TextureDesc textureDesc, TextureViewDesc viewDesc, ResourceState state, string label)
    {
        if (!_device.ValidationEnabled)
        {
            TrackTexture(texture);
            return;
        }

        var range = ActualRange(textureDesc, viewDesc);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
                RequireTextureState(texture, mip, slice, state, label);
        }
    }

    internal void RequireTextureState(TextureHandle texture, SubresourceRange range, ResourceState state, string label)
    {
        if (!_device.ValidationEnabled)
        {
            TrackTexture(texture);
            return;
        }

        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
                RequireSubState(texture, mip, slice, state, label);
        }
    }

    private void RequireSubState(TextureHandle texture, uint mip, uint slice, ResourceState state, string label)
    {
        TrackTexture(texture);
        if (!_device.ValidationEnabled)
            return;

        var key = new SubresourceKey(texture, mip, slice);
        _state.RequireTexture(key, state, label, "command list");
    }

    private void CheckTextureState(TextureHandle texture, SubresourceRange range, ResourceState state, string label)
    {
        TrackTexture(texture);
        if (!_device.ValidationEnabled)
            return;

        _state.CheckTexture(texture, range, state, label, "command list");
    }

    internal void ValidateTextureState(
        TextureHandle texture,
        TextureDesc textureDesc,
        TextureViewDesc viewDesc,
        ResourceState state,
        string label,
        bool trackWhenValidationDisabled = true)
    {
        if (!_device.ValidationEnabled)
        {
            if (trackWhenValidationDisabled)
                TrackTexture(texture);
            return;
        }

        var range = ActualRange(textureDesc, viewDesc);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
                ValidateTextureState(texture, mip, slice, state, label);
        }
    }

    private void ValidateTextureState(TextureHandle texture, uint mip, uint slice, ResourceState state, string label)
    {
        if (!_device.ValidationEnabled)
        {
            TrackTexture(texture);
            return;
        }

        var record = _device.Textures.Get(texture, "Texture");
        record.SubresourceIndex(mip, slice);
        var key = new SubresourceKey(texture, mip, slice);
        _state.ValidateTexture(key, record.GetState(mip, slice), state, label, "command list");
    }

    internal ResourceState GetBufferState(BufferHandle buffer)
    {
        TrackBuffer(buffer);
        var record = GetBuffer(buffer, "Buffer");
        return _state.GetBuffer(buffer, record.State);
    }

    internal ResourceState GetTextureState(TextureHandle texture, uint mip, uint slice)
    {
        TrackTexture(texture);
        var key = new SubresourceKey(texture, mip, slice);
        var record = _device.Textures.Get(texture, "Texture");
        var state = record.GetState(mip, slice);
        return _state.GetTexture(key, state);
    }

    private void SetBufferState(BufferHandle buffer, ResourceState state)
    {
        var record = _device.Buffers.Get(buffer, "Buffer");
        _state.SetBuffer(buffer, record.State, state);
    }

    private void SetBufferState(BufferHandle buffer, ResourceState before, ResourceState after)
    {
        _state.SetBufferFrom(buffer, before, after);
    }

    private void SetTextureState(TextureHandle texture, uint mip, uint slice, ResourceState state)
    {
        var key = new SubresourceKey(texture, mip, slice);
        var record = _device.Textures.Get(texture, "Texture");
        _state.SetTexture(key, record.GetState(mip, slice), state);
    }

    private void SetTextureState(TextureHandle texture, uint mip, uint slice, ResourceState before, ResourceState after)
    {
        var key = new SubresourceKey(texture, mip, slice);
        _state.SetTextureFrom(key, before, after);
    }

    private void SetTextureState(TextureHandle texture, SubresourceRange range, ResourceState before, ResourceState after)
    {
        TrackTexture(texture);
        _state.SetTextureRange(texture, range, before, after);
    }

    internal void TrackTransientDescriptor(ShaderDescriptorAllocation? allocation, DescriptorHeapType type)
    {
        if (allocation is not { } value)
            return;

        if (_transientDescriptors.Count > 0)
        {
            int lastIndex = _transientDescriptors.Count - 1;
            var previousTracked = _transientDescriptors[lastIndex];
            var previous = previousTracked.Allocation;
            if (previousTracked.Type == type && checked(previous.Index + previous.Count) == value.Index)
            {
                _transientDescriptors[lastIndex] = previousTracked with
                {
                    Allocation = previous with { Count = checked(previous.Count + value.Count) },
                };
                return;
            }
        }

        _transientDescriptors.Add(new TrackedDescriptorAllocation(type, value));
    }

    internal void TrackBuffer(BufferHandle buffer)
    {
        if (_referencedBuffers.TryAdd(buffer, true))
        {
            _device.Pin(buffer);
        }
        TrackAliasingUse(AliasingResource.BufferResource(buffer));
    }

    internal void TrackTexture(TextureHandle texture)
    {
        if (_referencedTextures.TryAdd(texture, true))
        {
            _device.Pin(texture);
        }
        TrackAliasingUse(AliasingResource.TextureResource(texture));
    }

    private void TrackAliasingUse(AliasingResource resource)
    {
        if (!_device.ValidationEnabled)
            return;
        if (!_device.HasAliasingUseValidation)
            return;
        if (_aliasingUsesSinceBarrier.ContainsKey(resource))
            return;
        if (!_device.NeedsAliasValidation(resource))
        {
            return;
        }
        if (_aliasingUsesSinceBarrier.TryAdd(resource, true))
            _aliasingOperations.Add(new AliasOperation(AliasOperationKind.ResourceUse, resource, default));
    }

    internal void TrackTextureView(TextureViewHandle view)
    {
        if (_referencedTextureViews.TryAdd(view, true))
            _device.Pin(view);
    }

    internal void TrackBufferView(BufferViewHandle view)
    {
        if (_referencedBufferViews.TryAdd(view, true))
            _device.Pin(view);
    }

    internal void TrackSampler(SamplerHandle sampler)
    {
        if (_referencedSamplers.TryAdd(sampler, true))
            _device.Pin(sampler);
    }

    internal void TrackAccelerationStructure(AccelerationStructureHandle accelerationStructure)
    {
        if (_referencedAccelerationStructures.TryAdd(accelerationStructure, true))
            _device.Pin(accelerationStructure);
    }

    internal bool TrackBindingSet(BindingSetHandle bindingSet)
    {
        if (!_referencedBindingSets.TryAdd(bindingSet, true))
            return false;

        _device.Pin(bindingSet);
        return true;
    }

    internal void TrackBindingLayout(BindingLayoutHandle bindingLayout)
    {
        if (_referencedBindingLayouts.TryAdd(bindingLayout, true))
            _device.Pin(bindingLayout);
    }

    internal void TrackBindingResources(ReadOnlySpan<BindingResourceDesc> resources)
    {
        foreach (var resource in resources)
            TrackDescRef(resource);
    }

    internal void TrackSetUses(BindingSetRecord set)
    {
        Span<BufferHandle> newBuffers = set.Buffers.Length <= 64
            ? stackalloc BufferHandle[set.Buffers.Length]
            : new BufferHandle[set.Buffers.Length];
        int newBufferCount = 0;
        foreach (var buffer in set.Buffers)
        {
            if (_referencedBuffers.TryAdd(buffer, true))
                newBuffers[newBufferCount++] = buffer;
            TrackAliasingUse(AliasingResource.BufferResource(buffer));
        }

        Span<TextureHandle> newTextures = set.Textures.Length <= 64
            ? stackalloc TextureHandle[set.Textures.Length]
            : new TextureHandle[set.Textures.Length];
        int newTextureCount = 0;
        foreach (var texture in set.Textures)
        {
            if (_referencedTextures.TryAdd(texture, true))
                newTextures[newTextureCount++] = texture;
            TrackAliasingUse(AliasingResource.TextureResource(texture));
        }

        Span<AccelerationStructureHandle> newAccelerationStructures = set.AccelerationStructures.Length <= 64
            ? stackalloc AccelerationStructureHandle[set.AccelerationStructures.Length]
            : new AccelerationStructureHandle[set.AccelerationStructures.Length];
        int newAccelerationStructureCount = 0;
        foreach (var accelerationStructure in set.AccelerationStructures)
        {
            if (_referencedAccelerationStructures.TryAdd(accelerationStructure, true))
                newAccelerationStructures[newAccelerationStructureCount++] = accelerationStructure;
        }

        _device.Pin(newBuffers[..newBufferCount]);
        _device.Pin(newTextures[..newTextureCount]);
        _device.Pin(newAccelerationStructures[..newAccelerationStructureCount]);
    }

    internal void TrackSetAccel(BindingSetRecord set)
    {
        Span<AccelerationStructureHandle> newAccelerationStructures = set.AccelerationStructures.Length <= 64
            ? stackalloc AccelerationStructureHandle[set.AccelerationStructures.Length]
            : new AccelerationStructureHandle[set.AccelerationStructures.Length];
        int newAccelerationStructureCount = 0;
        foreach (var accelerationStructure in set.AccelerationStructures)
        {
            if (_referencedAccelerationStructures.TryAdd(accelerationStructure, true))
                newAccelerationStructures[newAccelerationStructureCount++] = accelerationStructure;
        }

        _device.Pin(newAccelerationStructures[..newAccelerationStructureCount]);
    }

    private void TrackDescRef(BindingResourceDesc resource)
    {
        switch (resource.ResourceType)
        {
            case BindingType.ConstantBuffer:
            case BindingType.StorageBufferRead:
            case BindingType.StorageBufferReadWrite:
            case BindingType.RawBufferRead:
            case BindingType.RawBufferReadWrite:
                TrackBufferView(resource.BufferView);
                break;
            case BindingType.TextureRead:
            case BindingType.TextureReadWrite:
                TrackTextureView(resource.TextureView);
                break;
            case BindingType.Sampler:
                TrackSampler(resource.SamplerHandle);
                break;
            case BindingType.AccelerationStructure:
                TrackAccelerationStructure(resource.AccelerationStructure);
                break;
            case BindingType.None:
                break;
            default:
                throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {resource.ResourceType} is not supported by D3D12 backend.");
        }
    }

    internal void EnsureDescHeaps()
    {
        if (_shaderDescriptorHeapsSet)
        {
            return;
        }
        _list.SetDescriptorHeaps(_device.ShaderResourceAndSamplerHeapList);
        _graphicsRootDescriptorTables.Clear();
        _computeRootDescriptorTables.Clear();
        _graphicsBindingSets.Clear();
        _computeBindingSets.Clear();
        _shaderDescriptorHeapsSet = true;
    }

    internal void TrackPipelineLayout(PipelineLayoutHandle pipelineLayout)
    {
        if (_referencedPipelineLayouts.TryAdd(pipelineLayout, true))
            _device.Pin(pipelineLayout);
    }

    internal void TrackPipeline(PipelineHandle pipeline)
    {
        if (_referencedPipelines.TryAdd(pipeline, true))
            _device.Pin(pipeline);
    }

    internal void TrackAliasingEndpoint(AliasingResource resource)
    {
        switch (resource.Kind)
        {
            case AliasingResourceKind.None:
                break;
            case AliasingResourceKind.Buffer:
                if (_referencedBuffers.TryAdd(resource.Buffer, true))
                    _device.Pin(resource.Buffer);
                break;
            case AliasingResourceKind.Texture:
                if (_referencedTextures.TryAdd(resource.Texture, true))
                    _device.Pin(resource.Texture);
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined.");
        }
    }

    private void TrackQueryPool(QueryPoolHandle queryPool)
    {
        if (_referencedQueryPools.TryAdd(queryPool, true))
            _device.Pin(queryPool);
    }

    private static SubresourceRange ActualRange(TextureDesc texture, TextureViewDesc view)
    {
        uint mipCount = view.MipCount == uint.MaxValue ? texture.MipLevels - view.FirstMip : view.MipCount;
        uint sliceCount = view.SliceCount == uint.MaxValue ? texture.ArraySize - view.FirstSlice : view.SliceCount;
        return new SubresourceRange(view.FirstMip, mipCount, view.FirstSlice, sliceCount);
    }

    private static void RequireBufferCopy(BufferDesc desc, BindFlags flag, string label)
    {
        if (!desc.BindFlags.HasFlag(flag))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} requires bind flag {flag}.");
    }

    private static void RequireTextureCopy(TextureDesc desc, BindFlags flag, string label)
    {
        if (!desc.BindFlags.HasFlag(flag))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} requires bind flag {flag}.");
    }

    private void CheckCanRecord()
    {
        _device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (_passOpen)
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot record command-list commands while a pass is open.");
    }

    private ID3D12Resource? ResourceFor(AliasingResource resource)
        => resource.Kind switch
        {
            AliasingResourceKind.None => null,
            AliasingResourceKind.Buffer => GetBuffer(resource.Buffer, "AliasingBuffer").Resource,
            AliasingResourceKind.Texture => GetTexture(resource.Texture, "AliasingTexture").Resource,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined."),
        };

    private static TKey[] CopyKeys<TKey>(ref InlineFlatCore<TKey, bool> set)
        where TKey : notnull
    {
        if (set.Count == 0)
            return [];
        var keys = new TKey[set.Count];
        int count = 0;
        for (int slot = 0; slot < set.SlotCount; slot++)
        {
            if (set.TryPairSlot(slot, out var pair))
                keys[count++] = pair.Key;
        }

        return keys;
    }

    private BufferRecord GetBuffer(BufferHandle buffer, string kind)
    {
        if (_lastBufferRecord != null && buffer == _lastBufferHandle)
            return _lastBufferRecord;

        BufferRecord record = _device.Buffers.Get(buffer, kind);
        _lastBufferHandle = buffer;
        _lastBufferRecord = record;
        return record;
    }

    private TextureRecord GetTexture(TextureHandle texture, string kind)
    {
        if (_lastTextureRecord != null && texture == _lastTextureHandle)
            return _lastTextureRecord;

        TextureRecord record = _device.Textures.Get(texture, kind);
        _lastTextureHandle = texture;
        _lastTextureRecord = record;
        return record;
    }

    private static PlacedSubresourceFootPrint CreateFootprint(Format format, BufferTextureCopy buffer, TextureCopyRegion texture)
        => new()
        {
            Offset = buffer.Offset,
            Footprint = new SubresourceFootPrint(D3D12Mappings.ToDxgi(format), texture.Width, texture.Height, texture.Depth, buffer.RowPitch),
        };

    private static Box CreateSourceBox(TextureCopyRegion region)
        => new(
            checked((int)region.X),
            checked((int)region.Y),
            checked((int)region.Z),
            checked((int)(region.X + region.Width)),
            checked((int)(region.Y + region.Height)),
            checked((int)(region.Z + region.Depth)));

    private static void ValidatePlacement(ulong offset, string label)
    {
        const ulong TextureDataPlacementAlignment = 512;
        if (offset % TextureDataPlacementAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} buffer offset must be aligned to {TextureDataPlacementAlignment} bytes on D3D12.");
    }

    internal ID3D12GraphicsCommandList4 RequireList4(string label)
    {
        _list4 ??= _list.QueryInterfaceOrNull<ID3D12GraphicsCommandList4>();
        if (_list4 == null)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"{label} require ID3D12GraphicsCommandList4.");
        return _list4;
    }

    internal ID3D12GraphicsCommandList6 RequireList6(string label)
    {
        _list6 ??= _list.QueryInterfaceOrNull<ID3D12GraphicsCommandList6>();
        if (_list6 == null)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"{label} require ID3D12GraphicsCommandList6.");
        return _list6;
    }

    private void ValidateGeometry(AccelBuildDesc desc)
    {
        foreach (var geometry in desc.Geometries)
        {
            switch (geometry.Kind)
            {
                case AccelGeomKind.Triangles:
                    ValidateGeomBuffer(geometry.VertexBuffer, geometry.VertexOffset, TriangleVertexBytes(geometry), "Acceleration structure vertex buffer");
                    if (geometry.IndexCount > 0)
                        ValidateGeomBuffer(geometry.IndexBuffer, geometry.IndexOffset, checked((ulong)geometry.IndexCount * IndexElementSize(geometry.IndexFormat)), "Acceleration structure index buffer");
                    if (geometry.TransformBuffer.IsValid)
                        ValidateGeomBuffer(geometry.TransformBuffer, geometry.TransformOffset, 48, "Acceleration structure transform buffer");
                    break;
                case AccelGeomKind.Aabbs:
                    ValidateGeomBuffer(geometry.AabbBuffer, geometry.AabbOffset, checked((ulong)geometry.AabbCount * geometry.AabbStrideInBytes), "Acceleration structure AABB buffer");
                    break;
                case AccelGeomKind.Instances:
                    ValidateGeomBuffer(geometry.InstanceBuffer, geometry.InstanceOffset, checked((ulong)geometry.InstanceCount * 64), "Acceleration structure instance buffer");
                    break;
            }
        }
    }

    private void TrackGeometry(AccelBuildDesc desc)
    {
        foreach (var geometry in desc.Geometries)
        {
            switch (geometry.Kind)
            {
                case AccelGeomKind.Triangles:
                    TrackBuffer(geometry.VertexBuffer);
                    if (geometry.IndexBuffer.IsValid)
                        TrackBuffer(geometry.IndexBuffer);
                    if (geometry.TransformBuffer.IsValid)
                        TrackBuffer(geometry.TransformBuffer);
                    break;
                case AccelGeomKind.Aabbs:
                    TrackBuffer(geometry.AabbBuffer);
                    break;
                case AccelGeomKind.Instances:
                    TrackBuffer(geometry.InstanceBuffer);
                    break;
            }
        }
    }

    private void ValidateGeomBuffer(BufferHandle buffer, ulong offset, ulong sizeInBytes, string label)
    {
        var record = _device.Buffers.Get(buffer, label);
        if (!record.Desc.BindFlags.HasFlag(BindFlags.ShaderResource))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} requires ShaderResource bind flag.");
        RequireBufferState(buffer, ResourceState.ShaderResource, label);
        RhiCommandValidation.ValidateBufferRange(record.Desc, offset, sizeInBytes, label);
    }

    private static ulong TriangleVertexBytes(AccelGeomDesc geometry)
    {
        ulong stride = geometry.VertexStrideInBytes;
        ulong element = RtVertexBytes(geometry.VertexFormat);
        return checked((ulong)(geometry.VertexCount - 1) * stride + element);
    }

    private static ulong RtVertexBytes(Format format)
        => format switch
        {
            Format.Rg16Float => 4,
            Format.Rg32Float => 8,
            Format.Rgb32Float => 12,
            Format.Rgba16Float => 8,
            Format.Rgba32Float => 16,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {format} is not valid for ray tracing vertex data."),
        };

    private static ulong IndexElementSize(IndexFormat format)
        => format switch
        {
            IndexFormat.UInt16 => 2,
            IndexFormat.UInt32 => 4,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Index format value {format} is not defined."),
        };

    private void EnsureQueryQueue(QueryPoolRecord pool)
    {
        var queueType = ToQueueType(_queueKind);
        if (pool.Desc.Type == QueryType.Timestamp && queueType == QueueType.Copy)
            throw new RhiException(ErrorCode.UnsupportedFeature, "D3D12 copy queue timestamp query pools are not part of the public RHI query API.");
        if (pool.Desc.Type != QueryType.Timestamp && queueType != QueueType.Graphics)
            throw new RhiException(ErrorCode.ValidationFailure, $"{pool.Desc.Type} queries require a graphics command list.");
    }

    private static QueueType ToQueueType(CommandQueueKind kind)
        => kind switch
        {
            CommandQueueKind.Direct => QueueType.Graphics,
            CommandQueueKind.Compute => QueueType.Compute,
            CommandQueueKind.Copy => QueueType.Copy,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Queue kind {kind} is not defined."),
        };

    private static Vortice.Direct3D12.QueryType ToQueryType(QueryType type)
        => type switch
        {
            QueryType.Timestamp => Vortice.Direct3D12.QueryType.Timestamp,
            QueryType.Occlusion => Vortice.Direct3D12.QueryType.Occlusion,
            QueryType.PipelineStatistics => Vortice.Direct3D12.QueryType.PipelineStatistics,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Query type {type} is not defined."),
        };

    private enum PipelineBindingDomain
    {
        None,
        Graphics,
        Compute,
        RayTracing,
    }

    private readonly struct ViewportState
    {
        public ViewportState(float x, float y, float width, float height, float minDepth, float maxDepth)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            MinDepth = minDepth;
            MaxDepth = maxDepth;
        }

        public readonly float X;
        public readonly float Y;
        public readonly float Width;
        public readonly float Height;
        public readonly float MinDepth;
        public readonly float MaxDepth;
    }

    private readonly struct ScissorState
    {
        public ScissorState(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
