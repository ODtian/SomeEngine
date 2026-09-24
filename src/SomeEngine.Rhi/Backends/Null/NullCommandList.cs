using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Rhi.Backends.Null;

internal sealed class NullCommandList(NullDevice device, string name, QueueType queueType) : ICommandList
{
    private NullCommandOperation[]? _operations;
    private int _operationCount;
    private BindingResourceDesc[]? _bindingResources;
    private int _bindingResourceCount;
    private CommandState _state;
    private readonly HashSet<QueryScopeKey> _activeQueries = new(2);
    private readonly HashSet<BufferHandle> _activeBuffers = new(4);
    private readonly HashSet<TextureHandle> _activeTextures = new(4);
    private readonly HashSet<QueryPoolHandle> _activeQueryPools = new(2);
    private readonly HashSet<TextureViewHandle> _activeTextureViews = new(4);
    private readonly HashSet<BufferViewHandle> _activeBufferViews = new(4);
    private readonly HashSet<SamplerHandle> _activeSamplers = new(4);
    private readonly HashSet<AccelerationStructureHandle> _activeAccelerationStructures = new(2);
    private readonly HashSet<BindingLayoutHandle> _activeBindingLayouts = new(2);
    private readonly HashSet<BindingSetHandle> _activeBindingSets = new(2);
    private readonly HashSet<PipelineLayoutHandle> _activePipelineLayouts = new(2);
    private readonly HashSet<PipelineHandle> _activePipelines = new(2);
    private readonly List<Profiler.Scope> _debugScopes = [];
    private bool _finished;
    private bool _passOpen;
    private int _debugMarkerDepth;

    public bool DebugMarkersEnabled => false;

    public void Dispose()
    {
        if (_finished)
            return;

        AbortRecording();
    }

    public void Barrier(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)
    {
        CheckRecord();
        RecordBarriers(textureBarriers, bufferBarriers, aliasingBarriers);
    }

    internal void CheckOpenPass(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)
    {
        CheckPass();
        RecordBarriers(textureBarriers, bufferBarriers, aliasingBarriers);
    }

    private void RecordBarriers(
        ReadOnlySpan<TextureBarrier> textureBarriers,
        ReadOnlySpan<BufferBarrier> bufferBarriers,
        ReadOnlySpan<AliasingBarrier> aliasingBarriers)
    {
        if (textureBarriers.IsEmpty && bufferBarriers.IsEmpty && aliasingBarriers.IsEmpty)
            return;

        device.ReportBarriers(textureBarriers.Length, bufferBarriers.Length, aliasingBarriers.Length);
        foreach (var barrier in aliasingBarriers)
        {
            ValidateAliasingResource(barrier.Before, allowNone: true);
            ValidateAliasingResource(barrier.After, allowNone: true);
            if (barrier.Before.Kind == AliasingResourceKind.None && barrier.After.Kind == AliasingResourceKind.None)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier cannot have both endpoints empty.");
            AddOperation(NullCommandOperation.AliasingBarrier(barrier));
        }

        foreach (var barrier in textureBarriers)
        {
            var texture = device.Textures.Get(barrier.Texture, "Texture");
            RhiCommandValidation.ValidateSubresource(texture.Desc, barrier.Range);
            bool allowPresent = texture.Allocation.Ownership == ResourceOwnership.Swapchain;
            Validation.TextureResourceState(texture.Desc, barrier.Before, "Texture barrier before state", allowPresent);
            Validation.TextureResourceState(texture.Desc, barrier.After, "Texture barrier after state", allowPresent);
            ExpectTextureState(barrier.Texture, texture.Desc, barrier.Range, barrier.Before);
            SetTextureState(barrier.Texture, texture.Desc, barrier.Range, barrier.After);
            AddOperation(NullCommandOperation.TextureBarrier(barrier));
        }

        foreach (var barrier in bufferBarriers)
        {
            var buffer = device.Buffers.Get(barrier.Buffer, "Buffer");
            Validation.BufferResourceState(buffer.Desc, barrier.Before, "Buffer barrier before state");
            Validation.BufferResourceState(buffer.Desc, barrier.After, "Buffer barrier after state");
            if (barrier.Before == ResourceState.UnorderedAccess
                && barrier.After == ResourceState.UnorderedAccess
                && !buffer.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
                throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer UAV dependency barrier requires UnorderedAccess bind flag.");
            ExpectBufferState(barrier.Buffer, barrier.Before);
            _state.SetBufferFrom(barrier.Buffer, barrier.Before, barrier.After);
            AddOperation(NullCommandOperation.BufferBarrier(barrier));
        }
    }

    public void UavBarrier(TextureHandle texture)
    {
        CheckRecord();
        var record = device.Textures.Get(texture, "Texture");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture UAV barrier requires UnorderedAccess bind flag.");
        RequireTextureState(texture, ResourceState.UnorderedAccess, "Texture UAV barrier");
        device.ReportDependencies(1);
        AddOperation(NullCommandOperation.TextureDependency(texture, ResourceState.UnorderedAccess, "Texture UAV barrier"));
    }

    public void UavBarrier(BufferHandle buffer)
    {
        CheckRecord();
        var record = device.Buffers.Get(buffer, "Buffer");
        if (!record.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer UAV barrier requires UnorderedAccess bind flag.");
        RequireBufferState(buffer, ResourceState.UnorderedAccess, "Buffer UAV barrier");
        device.ReportDependencies(1);
        AddOperation(NullCommandOperation.BufferDependency(buffer, ResourceState.UnorderedAccess, "Buffer UAV barrier"));
    }

    public IRenderPass BeginRenderPass(in RenderPassDesc desc)
    {
        CheckRecord();
        if (queueType != QueueType.Graphics)
            throw new RhiException(ErrorCode.ValidationFailure, "Render passes require a graphics command list.");
        Validation.RenderPassDesc(desc, device.Limits);
        var compatibility = CreatePassCompatibility(desc);
        foreach (var attachment in desc.ColorAttachments)
        {
            var view = device.TextureViews.Get(attachment.View, "ColorAttachment");
            if (view.Desc.Kind != ViewKind.RenderTarget)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass color attachment must be a render-target view.");
            var texture = device.Textures.Get(view.Texture, "ColorAttachmentTexture");
            RequireTextureState(view.Texture, view.Desc, ResourceState.RenderTarget, "Render pass color attachment");
            ValidateRenderArea(texture.Desc, view.Desc, desc.RenderArea);

            if (attachment.ResolveTarget.IsValid)
            {
                var resolve = device.TextureViews.Get(attachment.ResolveTarget, "ResolveTarget");
                if (resolve.Desc.Kind != ViewKind.RenderTarget)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target must be a render-target view.");
                var resolveTexture = device.Textures.Get(resolve.Texture, "ResolveTargetTexture");
                RequireTextureState(resolve.Texture, resolve.Desc, ResourceState.RenderTarget, "Render pass resolve target");
                ValidateRenderArea(resolveTexture.Desc, resolve.Desc, desc.RenderArea);
                ValidateResolve(texture.Desc, view.Desc, resolveTexture.Desc, resolve.Desc, desc.RenderArea);
            }
        }

        if (desc.DepthStencilAttachment != null)
        {
            var attachment = desc.DepthStencilAttachment.Value;
            var view = device.TextureViews.Get(attachment.View, "DepthStencilAttachment");
            if (view.Desc.Kind != ViewKind.DepthStencil)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass depth attachment must be a depth-stencil view.");
            var texture = device.Textures.Get(view.Texture, "DepthStencilTexture");
            ResourceState requiredState = attachment.DepthReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite;
            RequireTextureState(view.Texture, view.Desc, requiredState, "Render pass depth attachment");
            ValidateRenderArea(texture.Desc, view.Desc, desc.RenderArea);
        }

        foreach (var attachment in desc.ColorAttachments)
        {
            AddOperation(NullCommandOperation.ViewDependency(attachment.View, ResourceState.RenderTarget, "Render pass color attachment"));
            if (attachment.ResolveTarget.IsValid)
                AddOperation(NullCommandOperation.ViewDependency(attachment.ResolveTarget, ResourceState.RenderTarget, "Render pass resolve target"));
        }

        if (desc.DepthStencilAttachment != null)
        {
            var attachment = desc.DepthStencilAttachment.Value;
            var requiredState = attachment.DepthReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite;
            AddOperation(NullCommandOperation.ViewDependency(attachment.View, requiredState, "Render pass depth attachment"));
        }

        _passOpen = true;
        return new RenderPass(this, device, compatibility);
    }

    public IComputePass BeginComputePass(ComputePassDesc desc)
    {
        CheckRecord();
        if (queueType == QueueType.Copy)
            throw new RhiException(ErrorCode.ValidationFailure, "Compute passes require a graphics or compute command list.");
        _passOpen = true;
        return new ComputePass(this, device);
    }

    public IRtPass BeginRtPass(RtPassDesc desc)
    {
        CheckRecord();
        if (!device.Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing passes require ray tracing support.");
        if (queueType == QueueType.Copy)
            throw new RhiException(ErrorCode.ValidationFailure, "Ray tracing passes require a graphics or compute command list.");
        _ = desc;
        _passOpen = true;
        return new RtPass(this, device);
    }

    public void CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong byteCount)
    {
        CheckRecord();
        var src = device.Buffers.Get(source, "SourceBuffer");
        var dst = device.Buffers.Get(destination, "DestinationBuffer");
        RequireBufferCopy(src.Desc, BindFlags.CopySource, "CopyBuffer source");
        RequireBufferCopy(dst.Desc, BindFlags.CopyDestination, "CopyBuffer destination");
        RequireBufferState(source, ResourceState.CopySource, "CopyBuffer source");
        RequireBufferState(destination, ResourceState.CopyDestination, "CopyBuffer destination");
        RhiCommandValidation.ValidateBufferRange(src.Desc, sourceOffset, byteCount, "CopyBuffer source");
        RhiCommandValidation.ValidateBufferRange(dst.Desc, destinationOffset, byteCount, "CopyBuffer destination");
        AddOperation(NullCommandOperation.CopyBuffer(source, sourceOffset, destination, destinationOffset, byteCount));
    }

    public void CopyToTexture(BufferHandle source, BufferTextureCopy sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
    {
        CheckRecord();
        var src = device.Buffers.Get(source, "SourceBuffer");
        var dst = device.Textures.Get(destination, "DestinationTexture");
        RequireBufferCopy(src.Desc, BindFlags.CopySource, "CopyToTexture source");
        RequireTextureCopy(dst.Desc, BindFlags.CopyDestination, "CopyToTexture destination");
        RequireBufferState(source, ResourceState.CopySource, "CopyToTexture source");
        RequireTextureState(destination, destinationRegion, ResourceState.CopyDestination, "CopyToTexture destination");
        RhiCommandValidation.ValidateBufferCopy(dst.Desc);
        RhiCommandValidation.ValidateTextureRegion(dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateFootprint(src.Desc, sourceRegion, destinationRegion, dst.Desc.Format, device.Limits.TextureRowPitchAlignment);
        AddOperation(NullCommandOperation.CopyToTexture(source, sourceRegion, destination, destinationRegion));
    }

    public void CopyToBuffer(TextureHandle source, TextureCopyRegion sourceRegion, BufferHandle destination, BufferTextureCopy destinationRegion)
    {
        CheckRecord();
        var src = device.Textures.Get(source, "SourceTexture");
        var dst = device.Buffers.Get(destination, "DestinationBuffer");
        RequireTextureCopy(src.Desc, BindFlags.CopySource, "CopyToBuffer source");
        RequireBufferCopy(dst.Desc, BindFlags.CopyDestination, "CopyToBuffer destination");
        RequireTextureState(source, sourceRegion, ResourceState.CopySource, "CopyToBuffer source");
        RequireBufferState(destination, ResourceState.CopyDestination, "CopyToBuffer destination");
        RhiCommandValidation.ValidateBufferCopy(src.Desc);
        RhiCommandValidation.ValidateTextureRegion(src.Desc, sourceRegion);
        RhiCommandValidation.ValidateFootprint(dst.Desc, destinationRegion, sourceRegion, src.Desc.Format, device.Limits.TextureRowPitchAlignment);
        AddOperation(NullCommandOperation.CopyToBuffer(source, sourceRegion, destination, destinationRegion));
    }

    public void CopyTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
    {
        CheckRecord();
        var src = device.Textures.Get(source, "SourceTexture");
        var dst = device.Textures.Get(destination, "DestinationTexture");
        RequireTextureCopy(src.Desc, BindFlags.CopySource, "CopyTexture source");
        RequireTextureCopy(dst.Desc, BindFlags.CopyDestination, "CopyTexture destination");
        RequireTextureState(source, sourceRegion, ResourceState.CopySource, "CopyTexture source");
        RequireTextureState(destination, destinationRegion, ResourceState.CopyDestination, "CopyTexture destination");
        RhiCommandValidation.ValidateTextureRegion(src.Desc, sourceRegion);
        RhiCommandValidation.ValidateTextureRegion(dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateCopyCompat(src.Desc, sourceRegion, dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateCopyOverlap(source, sourceRegion, destination, destinationRegion);
        AddOperation(NullCommandOperation.CopyTexture(source, sourceRegion, destination, destinationRegion));
    }


    public void ResolveTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
    {
        CheckRecord();
        var src = device.Textures.Get(source, "ResolveSource");
        var dst = device.Textures.Get(destination, "ResolveDestination");
        if (src.Desc.SampleCount <= 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture source must be multisampled.");
        if (dst.Desc.SampleCount != 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture destination must be single-sampled.");
        if (src.Desc.Format != dst.Desc.Format)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture requires matching formats.");
        RequireTextureState(source, sourceRegion, ResourceState.ResolveSource, "ResolveTexture source");
        RequireTextureState(destination, destinationRegion, ResourceState.ResolveDestination, "ResolveTexture destination");
        RhiCommandValidation.ValidateTextureRegion(src.Desc, sourceRegion);
        RhiCommandValidation.ValidateTextureRegion(dst.Desc, destinationRegion);
        RhiCommandValidation.ValidateResolveCompat(src.Desc, sourceRegion, dst.Desc, destinationRegion);
        AddOperation(NullCommandOperation.ResolveTexture(source, sourceRegion, destination, destinationRegion));
    }

    public void BuildAccelerationStructure(AccelBuildDesc desc)
    {
        CheckRecord();
        if (!device.Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structure builds require ray tracing support.");
        Validation.AccelBuildDesc(desc);
        var destination = device.AccelerationStructures.Get(desc.Destination, "DestinationAccelerationStructure");
        if (destination.Desc.Kind != desc.Kind)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure build kind {desc.Kind} does not match destination kind {destination.Desc.Kind}.");

        var scratch = device.Buffers.Get(desc.ScratchBuffer, "AccelerationStructureScratchBuffer");
        if (!scratch.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure scratch buffer requires UnorderedAccess bind flag.");
        RequireBufferState(desc.ScratchBuffer, ResourceState.UnorderedAccess, "Acceleration structure scratch buffer");
        if (desc.ScratchOffset % device.Limits.AccelerationStructureAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure scratch offset must be aligned to {device.Limits.AccelerationStructureAlignment} bytes.");
        var sizes = device.GetAccelSizes(desc);
        if (destination.Desc.SizeInBytes < sizes.AccelerationStructureSizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure destination is smaller than the required build result size.");
        ulong scratchSize = sizes.BuildScratchSizeInBytes;
        RhiCommandValidation.ValidateBufferRange(scratch.Desc, desc.ScratchOffset, scratchSize, "Acceleration structure scratch buffer");
        AddOperation(NullCommandOperation.BufferDependency(desc.ScratchBuffer, ResourceState.UnorderedAccess, "Acceleration structure scratch buffer"));
        AddOperation(NullCommandOperation.AccelerationDependency(desc.Source, desc.Destination));

        foreach (var geometry in desc.Geometries)
            AddGeomDeps(geometry);
    }

    public void CopyAccelerationStructure(AccelerationStructureHandle source, AccelerationStructureHandle destination, AccelCopyMode mode)
    {
        CheckRecord();
        if (!Enum.IsDefined(mode))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure copy mode value {mode} is not defined.");
        if (!device.Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structure copies require ray tracing support.");
        var sourceRecord = device.AccelerationStructures.Get(source, "SourceAccelerationStructure");
        var destinationRecord = device.AccelerationStructures.Get(destination, "DestinationAccelerationStructure");
        if (sourceRecord.Desc.Kind != destinationRecord.Desc.Kind)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure copy requires matching source and destination kinds.");
        if (mode == AccelCopyMode.Clone && destinationRecord.Desc.SizeInBytes < sourceRecord.Desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Acceleration structure clone destination is smaller than the source.");
        AddOperation(NullCommandOperation.AccelerationDependency(source, destination));
    }

    public void WriteTimestamp(QueryPoolHandle queryPool, uint queryIndex)
    {
        CheckRecord();
        var record = device.QueryPools.Get(queryPool, "QueryPool");
        if (record.Desc.Type != QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "WriteTimestamp requires a timestamp query pool.");
        if (queryIndex >= record.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp query index is outside the query pool.");
        EnsureQueryQueue(record);
        AddOperation(NullCommandOperation.WriteTimestamp(queryPool, queryIndex));
    }

    public void BeginQuery(QueryPoolHandle queryPool, uint queryIndex)
    {
        CheckRecord();
        var record = device.QueryPools.Get(queryPool, "QueryPool");
        if (record.Desc.Type == QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp queries do not use BeginQuery.");
        if (queryIndex >= record.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query index is outside the query pool.");
        if (record.Desc.Type == QueryType.Occlusion && !device.Features.OcclusionQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Occlusion queries are not supported by this device.");
        if (record.Desc.Type == QueryType.PipelineStatistics && !device.Features.PipelineStatisticsQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Pipeline statistics queries are not supported by this device.");
        EnsureQueryQueue(record);
        var key = new QueryScopeKey(queryPool, queryIndex);
        if (!_activeQueries.Add(key))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot begin an already active query in the same command buffer.");
        AddOperation(NullCommandOperation.BeginQuery(queryPool, queryIndex));
    }

    public void EndQuery(QueryPoolHandle queryPool, uint queryIndex)
    {
        CheckRecord();
        var record = device.QueryPools.Get(queryPool, "QueryPool");
        if (record.Desc.Type == QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp queries do not use EndQuery.");
        if (queryIndex >= record.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query index is outside the query pool.");
        if (record.Desc.Type == QueryType.Occlusion && !device.Features.OcclusionQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Occlusion queries are not supported by this device.");
        if (record.Desc.Type == QueryType.PipelineStatistics && !device.Features.PipelineStatisticsQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Pipeline statistics queries are not supported by this device.");
        EnsureQueryQueue(record);
        var key = new QueryScopeKey(queryPool, queryIndex);
        if (!_activeQueries.Remove(key))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot end a query that was not begun in the same command buffer.");
        AddOperation(NullCommandOperation.EndQuery(queryPool, queryIndex));
    }

    public void ResolveQueryData(QueryPoolHandle queryPool, uint firstQuery, uint queryCount, BufferHandle destination, ulong destinationOffset)
    {
        CheckRecord();
        var record = device.QueryPools.Get(queryPool, "QueryPool");
        if (queryCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve count must be greater than zero.");
        if (firstQuery >= record.Desc.Count || queryCount > record.Desc.Count - firstQuery)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve range is outside the query pool.");
        var buffer = device.Buffers.Get(destination, "QueryResolveDestination");
        if (!buffer.Desc.BindFlags.HasFlag(BindFlags.CopyDestination))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve destination requires CopyDestination bind flag.");
        if (destinationOffset % sizeof(ulong) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve destination offset must be 8-byte aligned.");
        EnsureQueryQueue(record);
        RequireBufferState(destination, ResourceState.QueryResolve, "Query resolve destination");
        RhiCommandValidation.ValidateBufferRange(
            buffer.Desc,
            destinationOffset,
            checked((ulong)queryCount * RhiCommandValidation.QueryBytes(record.Desc.Type)),
            "Query resolve destination");
        AddOperation(NullCommandOperation.ResolveQuery(queryPool, firstQuery, queryCount, destination, destinationOffset));
    }

    public void PushDebugGroup(string name)
    {
        CheckRecord();
        if (string.IsNullOrWhiteSpace(name))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Debug marker name must not be empty.");
        Profiler.Scope scope = Profiler.BeginMarker(name, "Null");
        if (scope.IsActive)
            _debugScopes.Add(scope);
        _debugMarkerDepth++;
    }

    public void PopDebugGroup()
    {
        CheckRecord();
        if (_debugMarkerDepth == 0)
            throw new RhiException(ErrorCode.ValidationFailure, "No debug marker is currently open.");
        if (_debugScopes.Count != 0)
        {
            int scopeIndex = _debugScopes.Count - 1;
            _debugScopes[scopeIndex].Dispose();
            _debugScopes.RemoveAt(scopeIndex);
        }
        _debugMarkerDepth--;
    }

    public void InsertDebugMarker(string name)
    {
        CheckRecord();
        if (string.IsNullOrWhiteSpace(name))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Debug marker name must not be empty.");
    }

    public CommandBufferHandle Finish()
    {
        device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (_passOpen)
        {
            AbortRecording();
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot finish command list while a pass is open.");
        }
        if (_debugMarkerDepth != 0)
        {
            AbortRecording();
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot finish command list while a debug marker is open.");
        }
        if (_activeQueries.Count != 0)
        {
            AbortRecording();
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot finish command list with active queries. End each non-timestamp query in the same command buffer.");
        }

        _finished = true;
        var operations = _operations ?? [];
        bool operationsArePooled = _operations != null;
        _operations = null;
        var bindingResources = _bindingResources ?? [];
        bool bindingResourcesArePooled = _bindingResources != null;
        _bindingResources = null;
        bool commandBufferOwnsArrays = false;
        try
        {
            var commandBuffer = device.AddCommandBuffer(
                name,
                queueType,
                operations,
                _operationCount,
                operationsArePooled,
                bindingResources,
                _bindingResourceCount,
                bindingResourcesArePooled);
            commandBufferOwnsArrays = true;
            return commandBuffer;
        }
        finally
        {
            if (!commandBufferOwnsArrays)
            {
                if (operationsArePooled)
                    device.ReturnCommandOperations(operations);
                if (bindingResourcesArePooled)
                    device.ReturnBindingResources(bindingResources);
            }

            UnpinAll();
            DisposeStateTracking();
        }
    }

    private void AbortRecording()
    {
        _finished = true;
        _passOpen = false;
        ClearDebugScopes();
        _debugMarkerDepth = 0;
        var operations = _operations;
        _operations = null;
        _operationCount = 0;
        var bindingResources = _bindingResources;
        _bindingResources = null;
        _bindingResourceCount = 0;
        if (operations != null)
            device.ReturnCommandOperations(operations);
        if (bindingResources != null)
            device.ReturnBindingResources(bindingResources);
        UnpinAll();
        DisposeStateTracking();
    }

    private void ClearDebugScopes()
    {
        for (int index = _debugScopes.Count - 1; index >= 0; index--)
            _debugScopes[index].Dispose();
        _debugScopes.Clear();
    }

    private void UnpinAll()
    {
        device.Unpin(_activeBuffers);
        device.Unpin(_activeTextures);
        device.Unpin(_activeQueryPools);
        device.Unpin(_activeTextureViews);
        device.Unpin(_activeBufferViews);
        device.Unpin(_activeSamplers);
        device.Unpin(_activeAccelerationStructures);
        device.Unpin(_activeBindingLayouts);
        device.Unpin(_activeBindingSets);
        device.Unpin(_activePipelineLayouts);
        device.Unpin(_activePipelines);
    }

    private void DisposeStateTracking()
    {
        _state.Dispose();
    }

    internal ResourceState GetTextureState(TextureHandle texture, uint mipLevel, uint arraySlice)
    {
        device.ThrowIfDisposed();
        var record = device.Textures.Get(texture, "Texture");
        return _state.GetTexture(new SubresourceKey(texture, mipLevel, arraySlice), record.GetState(mipLevel, arraySlice));
    }

    internal ResourceState GetBufferState(BufferHandle buffer)
    {
        device.ThrowIfDisposed();
        var record = device.Buffers.Get(buffer, "Buffer");
        return _state.GetBuffer(buffer, record.State);
    }

    internal void RequireTextureState(TextureHandle texture, ResourceState expected, string label)
    {
        RequireTextureState(texture, SubresourceRange.All, expected, label);
    }

    internal void RequireTextureState(TextureHandle texture, TextureViewDesc view, ResourceState expected, string label)
    {
        var textureRecord = device.Textures.Get(texture, "Texture");
        var range = new SubresourceRange(view.FirstMip, view.MipCount, view.FirstSlice, view.SliceCount);
        RequireTextureState(texture, textureRecord.Desc, range, expected, label);
    }

    private void RequireTextureState(TextureHandle texture, TextureCopyRegion region, ResourceState expected, string label)
    {
        var textureRecord = device.Textures.Get(texture, "Texture");
        var range = new SubresourceRange(region.MipLevel, 1, region.ArraySlice, 1);
        RequireTextureState(texture, textureRecord.Desc, range, expected, label);
    }

    internal void RequireTextureState(TextureHandle texture, SubresourceRange range, ResourceState expected, string label)
    {
        var textureRecord = device.Textures.Get(texture, "Texture");
        RequireTextureState(texture, textureRecord.Desc, range, expected, label);
    }

    private void RequireTextureState(TextureHandle texture, TextureDesc desc, SubresourceRange range, ResourceState expected, string label)
    {
        RhiCommandValidation.ValidateSubresource(desc, range);
        uint mipCount = MipCount(desc, range);
        uint sliceCount = SliceCount(desc, range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + sliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + mipCount; mip++)
            {
                _state.RequireTexture(new SubresourceKey(texture, mip, slice), expected, label, "command list");
            }
        }
    }

    private void ExpectTextureState(TextureHandle texture, TextureDesc desc, SubresourceRange range, ResourceState expected)
    {
        RhiCommandValidation.ValidateSubresource(desc, range);
        uint mipCount = MipCount(desc, range);
        uint sliceCount = SliceCount(desc, range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + sliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + mipCount; mip++)
            {
                _state.ExpectTexture(new SubresourceKey(texture, mip, slice), expected, "Texture barrier", "command list");
            }
        }
    }

    private void SetTextureState(TextureHandle texture, TextureDesc desc, SubresourceRange range, ResourceState state)
    {
        RhiCommandValidation.ValidateSubresource(desc, range);
        uint mipCount = MipCount(desc, range);
        uint sliceCount = SliceCount(desc, range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + sliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + mipCount; mip++)
                _state.SetTexture(new SubresourceKey(texture, mip, slice), state, state);
        }
    }

    internal void RequireBufferState(BufferHandle buffer, ResourceState expected, string label)
    {
        var record = device.Buffers.Get(buffer, "Buffer");
        _state.RequireBuffer(buffer, record.State, expected, label, "command list");
    }

    private void ExpectBufferState(BufferHandle buffer, ResourceState expected)
    {
        _state.ExpectBuffer(buffer, expected, "Buffer barrier", "command list");
    }

    internal void ClosePass()
    {
        if (!_passOpen)
            throw new RhiException(ErrorCode.ValidationFailure, "No pass is currently open.");
        _passOpen = false;
    }

    internal void CheckPass()
    {
        device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (!_passOpen)
            throw new RhiException(ErrorCode.ValidationFailure, "No pass is currently open.");
    }

    internal void AddOperation(NullCommandOperation operation)
    {
        EnsureOperationCapacity(_operationCount + 1);

        TrackOperationReferences(operation);
        _operations![_operationCount++] = operation;
    }

    internal void AddTransientDep(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)
    {
        int offset = _bindingResourceCount;
        EnsureResourceCap(checked(offset + resources.Length));
        resources.CopyTo(_bindingResources.AsSpan(offset, resources.Length));
        _bindingResourceCount += resources.Length;
        TrackBindingLayout(layout);
        TrackBindingResources(resources);
        AddOperation(NullCommandOperation.TransientBindingDependency(layout, offset, resources.Length));
    }

    private void TrackOperationReferences(NullCommandOperation operation)
    {
        switch (operation.Kind)
        {
            case NullOperationKind.TextureBarrier:
                TrackTexture(operation.TextureBarrierValue.Texture);
                break;
            case NullOperationKind.BufferBarrier:
                TrackBuffer(operation.BufferBarrierValue.Buffer);
                break;
            case NullOperationKind.CopyBuffer:
                TrackBuffer(operation.SourceBuffer);
                TrackBuffer(operation.DestinationBuffer);
                break;
            case NullOperationKind.CopyToTexture:
                TrackBuffer(operation.SourceBuffer);
                TrackTexture(operation.DestinationTexture);
                break;
            case NullOperationKind.CopyToBuffer:
                TrackTexture(operation.SourceTexture);
                TrackBuffer(operation.DestinationBuffer);
                break;
            case NullOperationKind.CopyTexture:
            case NullOperationKind.ResolveTexture:
                TrackTexture(operation.SourceTexture);
                TrackTexture(operation.DestinationTexture);
                break;
            case NullOperationKind.WriteTimestamp:
            case NullOperationKind.BeginQuery:
            case NullOperationKind.EndQuery:
                TrackQueryPool(operation.QueryPool);
                break;
            case NullOperationKind.ResolveQueryData:
                TrackQueryPool(operation.QueryPool);
                TrackBuffer(operation.DestinationBuffer);
                break;
            case NullOperationKind.AliasingBarrier:
                TrackAliasingEndpoint(operation.AliasingBarrierValue.Before);
                TrackAliasingEndpoint(operation.AliasingBarrierValue.After);
                break;
            case NullOperationKind.PipelineDependency:
            case NullOperationKind.RenderPipelineDependency:
                TrackPipeline(operation.Pipeline);
                TrackPipelineLayout(device.Pipelines.Get(operation.Pipeline, "Pipeline").Layout);
                break;
            case NullOperationKind.TextureDependency:
                TrackTexture(operation.Texture);
                break;
            case NullOperationKind.TextureViewDependency:
            {
                var view = device.TextureViews.Get(operation.TextureView, "TextureView");
                TrackTextureView(operation.TextureView);
                TrackTexture(view.Texture);
                break;
            }
            case NullOperationKind.BufferDependency:
                TrackBuffer(operation.Buffer);
                break;
            case NullOperationKind.AccelerationStructureDependency:
                if (operation.SourceAccelerationStructure.IsValid)
                    TrackAccelerationStructure(operation.SourceAccelerationStructure);
                if (operation.DestinationAccelerationStructure.IsValid)
                    TrackAccelerationStructure(operation.DestinationAccelerationStructure);
                break;
            case NullOperationKind.BindingSetDependency:
            {
                var set = device.BindingSets.Get(operation.BindingSet, "BindingSet");
                TrackBindingSet(operation.BindingSet);
                TrackBindingLayout(set.Layout);
                TrackBindingResources(set.Desc.Resources);
                break;
            }
            case NullOperationKind.TransientBindingDependency:
                TrackBindingLayout(operation.BindingLayout);
                break;
            default:
                throw new RhiException(ErrorCode.ValidationFailure, $"Command operation kind {operation.Kind} is not defined.");
        }
    }

    private void TrackBindingResources(ReadOnlySpan<BindingResourceDesc> resources)
    {
        foreach (var resource in resources)
            TrackBindingResource(resource);
    }

    private void TrackBindingResource(BindingResourceDesc resource)
    {
        switch (resource.ResourceType)
        {
            case BindingType.ConstantBuffer:
            case BindingType.StorageBufferRead:
            case BindingType.StorageBufferReadWrite:
            case BindingType.RawBufferRead:
            case BindingType.RawBufferReadWrite:
            {
                var view = device.BufferViews.Get(resource.BufferView, "BindingResourceBufferView");
                TrackBufferView(resource.BufferView);
                TrackBuffer(view.Buffer);
                break;
            }
            case BindingType.TextureRead:
            case BindingType.TextureReadWrite:
            {
                var view = device.TextureViews.Get(resource.TextureView, "BindingResourceTextureView");
                TrackTextureView(resource.TextureView);
                TrackTexture(view.Texture);
                break;
            }
            case BindingType.Sampler:
                device.Samplers.Get(resource.SamplerHandle, "BindingResourceSampler");
                TrackSampler(resource.SamplerHandle);
                break;
            case BindingType.AccelerationStructure:
                device.AccelerationStructures.Get(resource.AccelerationStructure, "BindingResourceAccelerationStructure");
                TrackAccelerationStructure(resource.AccelerationStructure);
                break;
            case BindingType.None:
                break;
            default:
                throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {resource.ResourceType} is not supported by Null backend.");
        }
    }

    private void TrackAccelerationStructure(AccelerationStructureHandle accelerationStructure)
    {
        if (_activeAccelerationStructures.Add(accelerationStructure))
            device.Pin(accelerationStructure);
    }

    private void TrackAliasingEndpoint(AliasingResource resource)
    {
        switch (resource.Kind)
        {
            case AliasingResourceKind.None:
                break;
            case AliasingResourceKind.Buffer:
                TrackBuffer(resource.Buffer);
                break;
            case AliasingResourceKind.Texture:
                TrackTexture(resource.Texture);
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined.");
        }
    }

    private void TrackBuffer(BufferHandle buffer)
    {
        if (_activeBuffers.Add(buffer))
            device.Pin(buffer);
    }

    private void TrackTexture(TextureHandle texture)
    {
        if (_activeTextures.Add(texture))
            device.Pin(texture);
    }

    private void TrackQueryPool(QueryPoolHandle queryPool)
    {
        if (_activeQueryPools.Add(queryPool))
            device.Pin(queryPool);
    }

    private void TrackTextureView(TextureViewHandle view)
    {
        if (_activeTextureViews.Add(view))
            device.Pin(view);
    }

    private void TrackBufferView(BufferViewHandle view)
    {
        if (_activeBufferViews.Add(view))
            device.Pin(view);
    }

    private void TrackSampler(SamplerHandle sampler)
    {
        if (_activeSamplers.Add(sampler))
            device.Pin(sampler);
    }

    private void TrackBindingLayout(BindingLayoutHandle layout)
    {
        if (_activeBindingLayouts.Add(layout))
            device.Pin(layout);
    }

    private void TrackBindingSet(BindingSetHandle bindingSet)
    {
        if (_activeBindingSets.Add(bindingSet))
            device.Pin(bindingSet);
    }

    private void TrackPipelineLayout(PipelineLayoutHandle pipelineLayout)
    {
        if (_activePipelineLayouts.Add(pipelineLayout))
            device.Pin(pipelineLayout);
    }

    private void TrackPipeline(PipelineHandle pipeline)
    {
        if (_activePipelines.Add(pipeline))
            device.Pin(pipeline);
    }

    private void EnsureOperationCapacity(int capacity)
    {
        if (_operations != null && capacity <= _operations.Length)
            return;

        int nextSize = _operations == null ? 8 : checked(_operations.Length * 2);
        while (nextSize < capacity)
            nextSize = checked(nextSize * 2);

        var nextOperations = device.RentCommandOperations(nextSize);
        if (_operations != null)
        {
            Array.Copy(_operations, nextOperations, _operationCount);
            device.ReturnCommandOperations(_operations);
        }

        _operations = nextOperations;
    }

    private void EnsureResourceCap(int capacity)
    {
        if (_bindingResources != null && capacity <= _bindingResources.Length)
            return;

        int nextSize = _bindingResources == null ? 8 : checked(_bindingResources.Length * 2);
        while (nextSize < capacity)
            nextSize = checked(nextSize * 2);

        var nextResources = device.RentBindingResources(nextSize);
        if (_bindingResources != null)
        {
            _bindingResources.AsSpan(0, _bindingResourceCount).CopyTo(nextResources);
            device.ReturnBindingResources(_bindingResources);
        }

        _bindingResources = nextResources;
    }

    private void CheckRecord()
    {
        device.ThrowIfDisposed();
        if (_finished)
            throw new RhiException(ErrorCode.ValidationFailure, "Command list is already finished.");
        if (_passOpen)
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot record command-list commands while a pass is open.");
    }

    private void EnsureQueryQueue(QueryPoolRecord record)
    {
        if (record.Desc.Type != QueryType.Timestamp && queueType != QueueType.Graphics)
            throw new RhiException(ErrorCode.ValidationFailure, $"{record.Desc.Type} queries require a graphics command list.");
    }

    private void ValidateAliasingResource(AliasingResource resource, bool allowNone)
    {
        switch (resource.Kind)
        {
            case AliasingResourceKind.None:
                if (!allowNone)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing resource must not be empty.");
                break;
            case AliasingResourceKind.Buffer:
                var buffer = device.Buffers.Get(resource.Buffer, "AliasingBuffer");
                if (buffer.Allocation.Ownership != ResourceOwnership.Placed)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing buffer must be a placed resource.");
                break;
            case AliasingResourceKind.Texture:
                var texture = device.Textures.Get(resource.Texture, "AliasingTexture");
                if (texture.Allocation.Ownership != ResourceOwnership.Placed)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing texture must be a placed resource.");
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined.");
        }
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

    private void AddGeomDeps(AccelGeomDesc geometry)
    {
        switch (geometry.Kind)
        {
            case AccelGeomKind.Triangles:
                RequireInputBuffer(geometry.VertexBuffer, geometry.VertexOffset, TriangleVertexBytes(geometry), "Acceleration structure vertex buffer");
                if (geometry.IndexCount != 0)
                    RequireInputBuffer(geometry.IndexBuffer, geometry.IndexOffset, checked((ulong)geometry.IndexCount * IndexBytes(geometry.IndexFormat)), "Acceleration structure index buffer");
                if (geometry.TransformBuffer.IsValid)
                    RequireInputBuffer(geometry.TransformBuffer, geometry.TransformOffset, 48, "Acceleration structure transform buffer");
                break;
            case AccelGeomKind.Aabbs:
                RequireInputBuffer(geometry.AabbBuffer, geometry.AabbOffset, AabbBytes(geometry), "Acceleration structure AABB buffer");
                break;
            case AccelGeomKind.Instances:
                RequireInputBuffer(geometry.InstanceBuffer, geometry.InstanceOffset, checked((ulong)geometry.InstanceCount * 64), "Acceleration structure instance buffer");
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure geometry kind {geometry.Kind} is not defined.");
        }
    }

    private void RequireInputBuffer(BufferHandle buffer, ulong offset, ulong byteCount, string label)
    {
        var record = device.Buffers.Get(buffer, label);
        if (!record.Desc.BindFlags.HasFlag(BindFlags.ShaderResource))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} requires ShaderResource bind flag.");
        RequireBufferState(buffer, ResourceState.ShaderResource, label);
        RhiCommandValidation.ValidateBufferRange(record.Desc, offset, byteCount, label);
        AddOperation(NullCommandOperation.BufferDependency(buffer, ResourceState.ShaderResource, label));
    }

    private static ulong TriangleVertexBytes(AccelGeomDesc geometry)
        => checked((ulong)(geometry.VertexCount - 1) * geometry.VertexStrideInBytes + RhiCommandValidation.FormatByteSize(geometry.VertexFormat));

    private static ulong AabbBytes(AccelGeomDesc geometry)
        => checked((ulong)(geometry.AabbCount - 1) * geometry.AabbStrideInBytes + 24);

    private static uint IndexBytes(IndexFormat format)
        => format switch
        {
            IndexFormat.UInt16 => 2,
            IndexFormat.UInt32 => 4,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Index format value {format} is not defined."),
        };

    private static void ValidateRenderArea(TextureDesc texture, TextureViewDesc view, Rect area)
    {
        uint width = Math.Max(1u, texture.Width >> checked((int)view.FirstMip));
        uint height = Math.Max(1u, texture.Height >> checked((int)view.FirstMip));
        if (area.X < 0 || area.Y < 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass area origin must be non-negative.");
        if ((uint)area.X > width || (uint)area.Width > width - (uint)area.X)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass area exceeds attachment width.");
        if ((uint)area.Y > height || (uint)area.Height > height - (uint)area.Y)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass area exceeds attachment height.");
    }

    private static void ValidateResolve(TextureDesc source, TextureViewDesc sourceView, TextureDesc destination, TextureViewDesc destinationView, Rect area)
    {
        if (sourceView.MipCount != 1 || sourceView.SliceCount != 1 || destinationView.MipCount != 1 || destinationView.SliceCount != 1)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Render-pass resolve supports single-subresource views.");
        if (source.Dimension != destination.Dimension)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve requires matching source and target dimensions.");
        uint sourceWidth = Math.Max(1u, source.Width >> checked((int)sourceView.FirstMip));
        uint sourceHeight = Math.Max(1u, source.Height >> checked((int)sourceView.FirstMip));
        uint sourceDepth = source.Dimension == ResourceDimension.Texture3D ? Math.Max(1u, source.Depth >> checked((int)sourceView.FirstMip)) : 1u;
        uint destinationWidth = Math.Max(1u, destination.Width >> checked((int)destinationView.FirstMip));
        uint destinationHeight = Math.Max(1u, destination.Height >> checked((int)destinationView.FirstMip));
        uint destinationDepth = destination.Dimension == ResourceDimension.Texture3D ? Math.Max(1u, destination.Depth >> checked((int)destinationView.FirstMip)) : 1u;
        if (sourceWidth != destinationWidth || sourceHeight != destinationHeight || sourceDepth != destinationDepth)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve source and target extents must match.");
        if (area.X != 0 || area.Y != 0 || area.Width < 0 || area.Height < 0 || (uint)area.Width != sourceWidth || (uint)area.Height != sourceHeight)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Render pass resolve requires a full-subresource render area.");
    }

    private RenderPassCompatibility CreatePassCompatibility(in RenderPassDesc desc)
    {
        Format color0 = Format.Unknown;
        Format color1 = Format.Unknown;
        Format color2 = Format.Unknown;
        Format color3 = Format.Unknown;
        Format color4 = Format.Unknown;
        Format color5 = Format.Unknown;
        Format color6 = Format.Unknown;
        Format color7 = Format.Unknown;
        int colorCount = 0;
        uint? sampleCount = null;
        foreach (var attachment in desc.ColorAttachments)
        {
            var view = device.TextureViews.Get(attachment.View, "ColorAttachment");
            var texture = device.Textures.Get(view.Texture, "ColorAttachmentTexture");
            switch (colorCount)
            {
                case 0:
                    color0 = view.Desc.Format;
                    break;
                case 1:
                    color1 = view.Desc.Format;
                    break;
                case 2:
                    color2 = view.Desc.Format;
                    break;
                case 3:
                    color3 = view.Desc.Format;
                    break;
                case 4:
                    color4 = view.Desc.Format;
                    break;
                case 5:
                    color5 = view.Desc.Format;
                    break;
                case 6:
                    color6 = view.Desc.Format;
                    break;
                case 7:
                    color7 = view.Desc.Format;
                    break;
                default:
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Null backend supports at most 8 color attachments.");
            }

            colorCount++;
            sampleCount = ValidateSamples(sampleCount, texture.Desc.SampleCount);

            if (attachment.ResolveTarget.IsValid)
            {
                var resolveView = device.TextureViews.Get(attachment.ResolveTarget, "ResolveTarget");
                var resolveTexture = device.Textures.Get(resolveView.Texture, "ResolveTargetTexture");
                if (texture.Desc.SampleCount == 1)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve requires a multisampled color attachment.");
                if (resolveTexture.Desc.SampleCount != 1)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target must be single-sampled.");
                if (resolveView.Desc.Format != view.Desc.Format)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Render pass resolve target format must match the color attachment format.");
                ValidateResolve(texture.Desc, view.Desc, resolveTexture.Desc, resolveView.Desc, desc.RenderArea);
            }
        }

        Format depthStencilFormat = Format.Unknown;
        bool depthReadOnly = false;
        if (desc.DepthStencilAttachment != null)
        {
            var attachment = desc.DepthStencilAttachment.Value;
            var view = device.TextureViews.Get(attachment.View, "DepthStencilAttachment");
            var texture = device.Textures.Get(view.Texture, "DepthStencilTexture");
            depthStencilFormat = view.Desc.Format;
            depthReadOnly = attachment.DepthReadOnly;
            sampleCount = ValidateSamples(sampleCount, texture.Desc.SampleCount);
        }

        return new RenderPassCompatibility(
            color0,
            color1,
            color2,
            color3,
            color4,
            color5,
            color6,
            color7,
            colorCount,
            depthStencilFormat,
            sampleCount ?? 1,
            depthReadOnly);
    }

    private static uint ValidateSamples(uint? current, uint next)
    {
        if (current.HasValue && current.Value != next)
            throw new RhiException(ErrorCode.InvalidDescriptor, "All render pass attachments must have the same sample count.");
        return next;
    }

    private static uint MipCount(TextureDesc desc, SubresourceRange range)
        => range.MipCount == uint.MaxValue ? desc.MipLevels - range.FirstMip : range.MipCount;

    private static uint SliceCount(TextureDesc desc, SubresourceRange range)
        => range.SliceCount == uint.MaxValue ? desc.ArraySize - range.FirstSlice : range.SliceCount;

    private readonly record struct QueryScopeKey(QueryPoolHandle Pool, uint Index);
}
