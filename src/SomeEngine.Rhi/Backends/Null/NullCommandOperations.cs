namespace SomeEngine.Rhi.Backends.Null;

internal enum NullOperationKind
{
    TextureBarrier,
    BufferBarrier,
    CopyBuffer,
    CopyToTexture,
    CopyToBuffer,
    CopyTexture,
    ResolveTexture,
    WriteTimestamp,
    BeginQuery,
    EndQuery,
    ResolveQueryData,
    AliasingBarrier,
    PipelineDependency,
    RenderPipelineDependency,
    TextureDependency,
    TextureViewDependency,
    BufferDependency,
    AccelerationStructureDependency,
    BindingSetDependency,
    TransientBindingDependency,
}

internal readonly record struct NullCommandOperation
{
    public NullOperationKind Kind { get; init; }
    public TextureBarrier TextureBarrierValue { get; init; }
    public BufferBarrier BufferBarrierValue { get; init; }
    public AliasingBarrier AliasingBarrierValue { get; init; }
    public BufferHandle SourceBuffer { get; init; }
    public BufferHandle DestinationBuffer { get; init; }
    public TextureHandle SourceTexture { get; init; }
    public TextureHandle DestinationTexture { get; init; }
    public TextureHandle Texture { get; init; }
    public TextureViewHandle TextureView { get; init; }
    public BufferHandle Buffer { get; init; }
    public AccelerationStructureHandle SourceAccelerationStructure { get; init; }
    public AccelerationStructureHandle DestinationAccelerationStructure { get; init; }
    public BufferTextureCopy BufferRegion { get; init; }
    public TextureCopyRegion SourceTextureRegion { get; init; }
    public TextureCopyRegion DestinationTextureRegion { get; init; }
    public ulong SourceOffset { get; init; }
    public ulong DestinationOffset { get; init; }
    public ulong ByteCount { get; init; }
    public QueryPoolHandle QueryPool { get; init; }
    public uint QueryIndex { get; init; }
    public uint FirstQuery { get; init; }
    public uint QueryCount { get; init; }
    public PipelineHandle Pipeline { get; init; }
    public RenderPassCompatibility Compatibility { get; init; }
    public ResourceState RequiredState { get; init; }
    public string? Label { get; init; }
    public BindingSetHandle BindingSet { get; init; }
    public BindingLayoutHandle BindingLayout { get; init; }
    public int BindingResourceOffset { get; init; }
    public int BindingResourceCount { get; init; }

    public static NullCommandOperation TextureBarrier(TextureBarrier barrier)
        => new() { Kind = NullOperationKind.TextureBarrier, TextureBarrierValue = barrier };

    public static NullCommandOperation BufferBarrier(BufferBarrier barrier)
        => new() { Kind = NullOperationKind.BufferBarrier, BufferBarrierValue = barrier };

    public static NullCommandOperation CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong byteCount)
        => new()
        {
            Kind = NullOperationKind.CopyBuffer,
            SourceBuffer = source,
            SourceOffset = sourceOffset,
            DestinationBuffer = destination,
            DestinationOffset = destinationOffset,
            ByteCount = byteCount,
        };

    public static NullCommandOperation CopyToTexture(BufferHandle source, BufferTextureCopy sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
        => new()
        {
            Kind = NullOperationKind.CopyToTexture,
            SourceBuffer = source,
            BufferRegion = sourceRegion,
            DestinationTexture = destination,
            DestinationTextureRegion = destinationRegion,
        };

    public static NullCommandOperation CopyToBuffer(TextureHandle source, TextureCopyRegion sourceRegion, BufferHandle destination, BufferTextureCopy destinationRegion)
        => new()
        {
            Kind = NullOperationKind.CopyToBuffer,
            SourceTexture = source,
            SourceTextureRegion = sourceRegion,
            DestinationBuffer = destination,
            BufferRegion = destinationRegion,
        };

    public static NullCommandOperation CopyTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
        => new()
        {
            Kind = NullOperationKind.CopyTexture,
            SourceTexture = source,
            SourceTextureRegion = sourceRegion,
            DestinationTexture = destination,
            DestinationTextureRegion = destinationRegion,
        };

    public static NullCommandOperation ResolveTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)
        => new()
        {
            Kind = NullOperationKind.ResolveTexture,
            SourceTexture = source,
            SourceTextureRegion = sourceRegion,
            DestinationTexture = destination,
            DestinationTextureRegion = destinationRegion,
        };

    public static NullCommandOperation WriteTimestamp(QueryPoolHandle queryPool, uint queryIndex)
        => new() { Kind = NullOperationKind.WriteTimestamp, QueryPool = queryPool, QueryIndex = queryIndex };

    public static NullCommandOperation BeginQuery(QueryPoolHandle queryPool, uint queryIndex)
        => new() { Kind = NullOperationKind.BeginQuery, QueryPool = queryPool, QueryIndex = queryIndex };

    public static NullCommandOperation EndQuery(QueryPoolHandle queryPool, uint queryIndex)
        => new() { Kind = NullOperationKind.EndQuery, QueryPool = queryPool, QueryIndex = queryIndex };

    public static NullCommandOperation ResolveQuery(QueryPoolHandle queryPool, uint firstQuery, uint queryCount, BufferHandle destination, ulong destinationOffset)
        => new()
        {
            Kind = NullOperationKind.ResolveQueryData,
            QueryPool = queryPool,
            FirstQuery = firstQuery,
            QueryCount = queryCount,
            DestinationBuffer = destination,
            DestinationOffset = destinationOffset,
        };

    public static NullCommandOperation AliasingBarrier(AliasingBarrier barrier)
        => new() { Kind = NullOperationKind.AliasingBarrier, AliasingBarrierValue = barrier };

    public static NullCommandOperation PipelineDependency(PipelineHandle pipeline)
        => new() { Kind = NullOperationKind.PipelineDependency, Pipeline = pipeline };

    public static NullCommandOperation RenderPipelineDependency(PipelineHandle pipeline, RenderPassCompatibility compatibility)
        => new()
        {
            Kind = NullOperationKind.RenderPipelineDependency,
            Pipeline = pipeline,
            Compatibility = compatibility,
        };

    public static NullCommandOperation TextureDependency(TextureHandle texture, ResourceState requiredState, string label)
        => new()
        {
            Kind = NullOperationKind.TextureDependency,
            Texture = texture,
            RequiredState = requiredState,
            Label = label,
        };

    public static NullCommandOperation ViewDependency(TextureViewHandle view, ResourceState requiredState, string label)
        => new()
        {
            Kind = NullOperationKind.TextureViewDependency,
            TextureView = view,
            RequiredState = requiredState,
            Label = label,
        };

    public static NullCommandOperation BufferDependency(BufferHandle buffer, ResourceState requiredState, string label)
        => new()
        {
            Kind = NullOperationKind.BufferDependency,
            Buffer = buffer,
            RequiredState = requiredState,
            Label = label,
        };

    public static NullCommandOperation AccelerationDependency(
        AccelerationStructureHandle source,
        AccelerationStructureHandle destination)
        => new()
        {
            Kind = NullOperationKind.AccelerationStructureDependency,
            SourceAccelerationStructure = source,
            DestinationAccelerationStructure = destination,
        };

    public static NullCommandOperation BindingSetDependency(BindingSetHandle bindingSet)
        => new() { Kind = NullOperationKind.BindingSetDependency, BindingSet = bindingSet };

    public static NullCommandOperation TransientBindingDependency(BindingLayoutHandle layout, int resourceOffset, int resourceCount)
        => new()
        {
            Kind = NullOperationKind.TransientBindingDependency,
            BindingLayout = layout,
            BindingResourceOffset = resourceOffset,
            BindingResourceCount = resourceCount,
        };

    public void ValidateSubmit(ref NullSubmitState state, ReadOnlySpan<BindingResourceDesc> transientBindingResources = default)
    {
        switch (Kind)
        {
            case NullOperationKind.TextureBarrier:
                ValidateTextureBarrier(ref state);
                break;
            case NullOperationKind.BufferBarrier:
                ValidateBufferBarrier(ref state);
                break;
            case NullOperationKind.CopyBuffer:
                ValidateCopyBuffer(ref state);
                break;
            case NullOperationKind.CopyToTexture:
                ValidateBufferCopy(ref state);
                break;
            case NullOperationKind.CopyToBuffer:
                ValidateTextureCopy(ref state);
                break;
            case NullOperationKind.CopyTexture:
                ValidateCopyTexture(ref state);
                break;
            case NullOperationKind.ResolveTexture:
                ValidateResolveTexture(ref state);
                break;
            case NullOperationKind.WriteTimestamp:
                state.WriteTimestamp(QueryPool, QueryIndex);
                break;
            case NullOperationKind.BeginQuery:
                state.BeginQuery(QueryPool, QueryIndex);
                break;
            case NullOperationKind.EndQuery:
                state.EndQuery(QueryPool, QueryIndex);
                break;
            case NullOperationKind.ResolveQueryData:
                ValidateQueryResolve(ref state);
                break;
            case NullOperationKind.AliasingBarrier:
                ValidateAliasingBarrier(ref state);
                break;
            case NullOperationKind.PipelineDependency:
                ValidatePipelineDependency(ref state);
                break;
            case NullOperationKind.RenderPipelineDependency:
                ValidatePassPipeline(ref state);
                break;
            case NullOperationKind.TextureDependency:
                ValidateTextureDependency(ref state);
                break;
            case NullOperationKind.TextureViewDependency:
                ValidateViewDependency(ref state);
                break;
            case NullOperationKind.BufferDependency:
                ValidateBufferDependency(ref state);
                break;
            case NullOperationKind.AccelerationStructureDependency:
                ValidateAccelerationDependency(ref state);
                break;
            case NullOperationKind.BindingSetDependency:
                ValidateSetDependency(ref state);
                break;
            case NullOperationKind.TransientBindingDependency:
                ValidateTransientDependency(ref state, transientBindingResources);
                break;
            default:
                throw new RhiException(ErrorCode.ValidationFailure, $"Unknown Null command operation kind {Kind}.");
        }
    }

    public void Execute(NullDevice device)
    {
        switch (Kind)
        {
            case NullOperationKind.TextureBarrier:
                device.SetTextureState(TextureBarrierValue.Texture, TextureBarrierValue.Range, TextureBarrierValue.After);
                break;
            case NullOperationKind.BufferBarrier:
                device.SetBufferState(BufferBarrierValue.Buffer, BufferBarrierValue.After);
                break;
            case NullOperationKind.CopyBuffer:
                ExecuteCopyBuffer(device);
                break;
            case NullOperationKind.CopyToTexture:
                ExecBufferCopy(device);
                break;
            case NullOperationKind.CopyToBuffer:
                ExecTextureCopy(device);
                break;
            case NullOperationKind.CopyTexture:
                ExecuteCopyTexture(device);
                break;
            case NullOperationKind.ResolveTexture:
                ExecuteResolveTexture(device);
                break;
            case NullOperationKind.WriteTimestamp:
                ExecuteWriteTimestamp(device);
                break;
            case NullOperationKind.BeginQuery:
                ExecuteBeginQuery(device);
                break;
            case NullOperationKind.EndQuery:
                ExecuteEndQuery(device);
                break;
            case NullOperationKind.ResolveQueryData:
                ExecQueryResolve(device);
                break;
            case NullOperationKind.AliasingBarrier:
                device.ApplyAliasingBarrier(AliasingBarrierValue);
                break;
            case NullOperationKind.PipelineDependency:
            case NullOperationKind.RenderPipelineDependency:
            case NullOperationKind.TextureDependency:
            case NullOperationKind.TextureViewDependency:
            case NullOperationKind.BufferDependency:
            case NullOperationKind.AccelerationStructureDependency:
            case NullOperationKind.BindingSetDependency:
            case NullOperationKind.TransientBindingDependency:
                break;
            default:
                throw new RhiException(ErrorCode.ValidationFailure, $"Unknown Null command operation kind {Kind}.");
        }
    }

    private void ValidateTextureBarrier(ref NullSubmitState state)
    {
        var texture = state.Device.Textures.Get(TextureBarrierValue.Texture, "Texture");
        state.RequireAliasingActive(AliasingResource.TextureResource(TextureBarrierValue.Texture), "Texture barrier");
        RhiCommandValidation.ValidateSubresource(texture.Desc, TextureBarrierValue.Range);
        state.RequireTextureState(TextureBarrierValue.Texture, texture.Desc, TextureBarrierValue.Range, TextureBarrierValue.Before, "Texture barrier");
        state.SetTextureState(TextureBarrierValue.Texture, texture.Desc, TextureBarrierValue.Range, TextureBarrierValue.After);
    }

    private void ValidateBufferBarrier(ref NullSubmitState state)
    {
        var buffer = state.Device.Buffers.Get(BufferBarrierValue.Buffer, "Buffer");
        state.RequireAliasingActive(AliasingResource.BufferResource(BufferBarrierValue.Buffer), "Buffer barrier");
        if (BufferBarrierValue.Before == ResourceState.UnorderedAccess
            && BufferBarrierValue.After == ResourceState.UnorderedAccess
            && !buffer.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer UAV dependency barrier requires UnorderedAccess bind flag.");
        state.RequireBufferState(BufferBarrierValue.Buffer, BufferBarrierValue.Before, "Buffer barrier");
        state.SetBufferState(BufferBarrierValue.Buffer, BufferBarrierValue.After);
    }

    private void ValidateCopyBuffer(ref NullSubmitState state)
    {
        var source = state.Device.Buffers.Get(SourceBuffer, "SourceBuffer");
        var destination = state.Device.Buffers.Get(DestinationBuffer, "DestinationBuffer");
        state.RequireAliasingActive(AliasingResource.BufferResource(SourceBuffer), "CopyBuffer source");
        state.RequireAliasingActive(AliasingResource.BufferResource(DestinationBuffer), "CopyBuffer destination");
        state.RequireBufferState(SourceBuffer, ResourceState.CopySource, "CopyBuffer source");
        state.RequireBufferState(DestinationBuffer, ResourceState.CopyDestination, "CopyBuffer destination");
        RhiCommandValidation.ValidateBufferRange(source.Desc, SourceOffset, ByteCount, "CopyBuffer source");
        RhiCommandValidation.ValidateBufferRange(destination.Desc, DestinationOffset, ByteCount, "CopyBuffer destination");
    }

    private void ExecuteCopyBuffer(NullDevice device)
    {
        var source = device.Buffers.Get(SourceBuffer, "SourceBuffer");
        var destination = device.Buffers.Get(DestinationBuffer, "DestinationBuffer");
        System.Buffer.BlockCopy(
            source.Data,
            checked((int)SourceOffset),
            destination.Data,
            checked((int)DestinationOffset),
            checked((int)ByteCount));
    }

    private void ValidateBufferCopy(ref NullSubmitState state)
    {
        var source = state.Device.Buffers.Get(SourceBuffer, "SourceBuffer");
        var destination = state.Device.Textures.Get(DestinationTexture, "DestinationTexture");
        state.RequireAliasingActive(AliasingResource.BufferResource(SourceBuffer), "CopyToTexture source");
        state.RequireAliasingActive(AliasingResource.TextureResource(DestinationTexture), "CopyToTexture destination");
        state.RequireBufferState(SourceBuffer, ResourceState.CopySource, "CopyToTexture source");
        state.RequireTextureState(DestinationTexture, destination.Desc, DestinationTextureRegion, ResourceState.CopyDestination, "CopyToTexture destination");
        RhiCommandValidation.ValidateBufferCopy(destination.Desc);
        RhiCommandValidation.ValidateTextureRegion(destination.Desc, DestinationTextureRegion);
        RhiCommandValidation.ValidateFootprint(source.Desc, BufferRegion, DestinationTextureRegion, destination.Desc.Format, state.Device.Limits.TextureRowPitchAlignment);
    }

    private void ExecBufferCopy(NullDevice device)
    {
        var source = device.Buffers.Get(SourceBuffer, "SourceBuffer");
        var destination = device.Textures.Get(DestinationTexture, "DestinationTexture");
        destination.CopyFromBuffer(source.Data, BufferRegion, DestinationTextureRegion);
    }

    private void ValidateTextureCopy(ref NullSubmitState state)
    {
        var source = state.Device.Textures.Get(SourceTexture, "SourceTexture");
        var destination = state.Device.Buffers.Get(DestinationBuffer, "DestinationBuffer");
        state.RequireAliasingActive(AliasingResource.TextureResource(SourceTexture), "CopyToBuffer source");
        state.RequireAliasingActive(AliasingResource.BufferResource(DestinationBuffer), "CopyToBuffer destination");
        state.RequireTextureState(SourceTexture, source.Desc, SourceTextureRegion, ResourceState.CopySource, "CopyToBuffer source");
        state.RequireBufferState(DestinationBuffer, ResourceState.CopyDestination, "CopyToBuffer destination");
        RhiCommandValidation.ValidateBufferCopy(source.Desc);
        RhiCommandValidation.ValidateTextureRegion(source.Desc, SourceTextureRegion);
        RhiCommandValidation.ValidateFootprint(destination.Desc, BufferRegion, SourceTextureRegion, source.Desc.Format, state.Device.Limits.TextureRowPitchAlignment);
    }

    private void ExecTextureCopy(NullDevice device)
    {
        var source = device.Textures.Get(SourceTexture, "SourceTexture");
        var destination = device.Buffers.Get(DestinationBuffer, "DestinationBuffer");
        source.CopyToBuffer(SourceTextureRegion, destination.Data, BufferRegion);
    }

    private void ValidateCopyTexture(ref NullSubmitState state)
    {
        var source = state.Device.Textures.Get(SourceTexture, "SourceTexture");
        var destination = state.Device.Textures.Get(DestinationTexture, "DestinationTexture");
        state.RequireAliasingActive(AliasingResource.TextureResource(SourceTexture), "CopyTexture source");
        state.RequireAliasingActive(AliasingResource.TextureResource(DestinationTexture), "CopyTexture destination");
        state.RequireTextureState(SourceTexture, source.Desc, SourceTextureRegion, ResourceState.CopySource, "CopyTexture source");
        state.RequireTextureState(DestinationTexture, destination.Desc, DestinationTextureRegion, ResourceState.CopyDestination, "CopyTexture destination");
        RhiCommandValidation.ValidateTextureRegion(source.Desc, SourceTextureRegion);
        RhiCommandValidation.ValidateTextureRegion(destination.Desc, DestinationTextureRegion);
        RhiCommandValidation.ValidateCopyCompat(source.Desc, SourceTextureRegion, destination.Desc, DestinationTextureRegion);
        RhiCommandValidation.ValidateCopyOverlap(SourceTexture, SourceTextureRegion, DestinationTexture, DestinationTextureRegion);
    }

    private void ValidateResolveTexture(ref NullSubmitState state)
    {
        var source = state.Device.Textures.Get(SourceTexture, "ResolveSource");
        var destination = state.Device.Textures.Get(DestinationTexture, "ResolveDestination");
        state.RequireAliasingActive(AliasingResource.TextureResource(SourceTexture), "ResolveTexture source");
        state.RequireAliasingActive(AliasingResource.TextureResource(DestinationTexture), "ResolveTexture destination");
        if (source.Desc.SampleCount <= 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture source must be multisampled.");
        if (destination.Desc.SampleCount != 1)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture destination must be single-sampled.");
        if (source.Desc.Format != destination.Desc.Format)
            throw new RhiException(ErrorCode.InvalidDescriptor, "ResolveTexture requires matching formats.");
        state.RequireTextureState(SourceTexture, source.Desc, SourceTextureRegion, ResourceState.ResolveSource, "ResolveTexture source");
        state.RequireTextureState(DestinationTexture, destination.Desc, DestinationTextureRegion, ResourceState.ResolveDestination, "ResolveTexture destination");
        RhiCommandValidation.ValidateTextureRegion(source.Desc, SourceTextureRegion);
        RhiCommandValidation.ValidateTextureRegion(destination.Desc, DestinationTextureRegion);
        RhiCommandValidation.ValidateResolveCompat(source.Desc, SourceTextureRegion, destination.Desc, DestinationTextureRegion);
    }

    private void ExecuteResolveTexture(NullDevice device)
    {
        var source = device.Textures.Get(SourceTexture, "ResolveSource");
        var destination = device.Textures.Get(DestinationTexture, "ResolveDestination");
        source.CopyToTexture(SourceTextureRegion, destination, DestinationTextureRegion);
    }

    private void ExecuteCopyTexture(NullDevice device)
    {
        var source = device.Textures.Get(SourceTexture, "SourceTexture");
        var destination = device.Textures.Get(DestinationTexture, "DestinationTexture");
        source.CopyToTexture(SourceTextureRegion, destination, DestinationTextureRegion);
    }

    private QueryPoolRecord ValidateTimestampQuery(NullDevice device)
    {
        var queryPool = device.QueryPools.Get(QueryPool, "QueryPool");
        if (queryPool.Desc.Type != QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "WriteTimestamp requires a timestamp query pool.");
        if (QueryIndex >= queryPool.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp query index is outside the query pool.");
        return queryPool;
    }

    private void ExecuteWriteTimestamp(NullDevice device)
    {
        var queryPool = ValidateTimestampQuery(device);
        queryPool.Values[checked((int)QueryIndex)] = device.NextTimestamp();
    }

    private QueryPoolRecord ValidateQuery(NullDevice device)
    {
        var queryPool = device.QueryPools.Get(QueryPool, "QueryPool");
        if (queryPool.Desc.Type == QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp queries do not use BeginQuery/EndQuery.");
        if (QueryIndex >= queryPool.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query index is outside the query pool.");
        if (queryPool.Desc.Type == QueryType.Occlusion && !device.Features.OcclusionQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Occlusion queries are not supported by this device.");
        if (queryPool.Desc.Type == QueryType.PipelineStatistics && !device.Features.PipelineStatisticsQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Pipeline statistics queries are not supported by this device.");
        return queryPool;
    }

    private void ValidateBeginQuery(NullDevice device)
    {
        var queryPool = ValidateQuery(device);
        if (queryPool.Active[checked((int)QueryIndex)])
            throw new RhiException(ErrorCode.ValidationFailure, "Query is already active.");
    }

    private void ExecuteBeginQuery(NullDevice device)
    {
        var queryPool = ValidateQuery(device);
        queryPool.Active[checked((int)QueryIndex)] = true;
    }

    private void ValidateEndQuery(NullDevice device)
    {
        var queryPool = ValidateQuery(device);
        if (!queryPool.Active[checked((int)QueryIndex)])
            throw new RhiException(ErrorCode.ValidationFailure, "Query is not active.");
    }

    private void ExecuteEndQuery(NullDevice device)
    {
        var queryPool = ValidateQuery(device);
        queryPool.Active[checked((int)QueryIndex)] = false;
        queryPool.Values[checked((int)QueryIndex)] = device.NextTimestamp();
    }

    private void ValidateQueryResolve(ref NullSubmitState state)
    {
        ValidateQueryRange(state.Device);
        state.RequireResolvedQueries(QueryPool, FirstQuery, QueryCount);
        var destination = state.Device.Buffers.Get(DestinationBuffer, "QueryResolveDestination");
        state.RequireAliasingActive(AliasingResource.BufferResource(DestinationBuffer), "Query resolve destination");
        if (!destination.Desc.BindFlags.HasFlag(BindFlags.CopyDestination))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve destination requires CopyDestination bind flag.");
        state.RequireBufferState(DestinationBuffer, ResourceState.QueryResolve, "Query resolve destination");
        var queryPool = state.Device.QueryPools.Get(QueryPool, "QueryPool");
        if (DestinationOffset % sizeof(ulong) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve destination offset must be 8-byte aligned.");
        RhiCommandValidation.ValidateBufferRange(
            destination.Desc,
            DestinationOffset,
            checked((ulong)QueryCount * RhiCommandValidation.QueryBytes(queryPool.Desc.Type)),
            "Query resolve destination");
    }

    private void ExecQueryResolve(NullDevice device)
    {
        var queryPool = ValidateQueryRange(device);
        var destination = device.Buffers.Get(DestinationBuffer, "QueryResolveDestination");
        int writeOffset = checked((int)DestinationOffset);
        int queryResultBytes = checked((int)RhiCommandValidation.QueryBytes(queryPool.Desc.Type));
        for (uint index = 0; index < QueryCount; index++)
        {
            ulong value = queryPool.Values[checked((int)(FirstQuery + index))]!.Value;
            var destinationSpan = destination.Data.AsSpan(writeOffset, queryResultBytes);
            destinationSpan.Clear();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destinationSpan[..sizeof(ulong)], value);
            writeOffset += queryResultBytes;
        }
    }

    private QueryPoolRecord ValidateQueryRange(NullDevice device)
    {
        var queryPool = device.QueryPools.Get(QueryPool, "QueryPool");
        if (QueryCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve count must be greater than zero.");
        if (FirstQuery >= queryPool.Desc.Count || QueryCount > queryPool.Desc.Count - FirstQuery)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve range is outside the query pool.");
        return queryPool;
    }

    private void ValidateAliasingBarrier(ref NullSubmitState state)
    {
        var device = state.Device;
        var before = AllocationFor(device, AliasingBarrierValue.Before);
        var after = AllocationFor(device, AliasingBarrierValue.After);
        if (before == null && after == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier cannot have both endpoints empty.");
        if (before != null && before.Value.Ownership != ResourceOwnership.Placed)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier before endpoint must be placed.");
        if (after != null && after.Value.Ownership != ResourceOwnership.Placed)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier after endpoint must be placed.");
        if (before != null)
            ValidateAliasingHeap(device, before.Value);
        if (after != null && (before == null || after.Value.Heap != before.Value.Heap))
            ValidateAliasingHeap(device, after.Value);
        if (before != null && after != null)
        {
            if (before.Value.Heap != after.Value.Heap)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier endpoints must be placed in the same heap.");
            if (!RangesOverlap(before.Value.HeapOffset, before.Value.SizeInBytes, after.Value.HeapOffset, after.Value.SizeInBytes))
                throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier endpoints must overlap in heap memory.");
        }
        state.ApplyAliasingBarrier(AliasingBarrierValue);
    }

    private static void ValidateAliasingHeap(NullDevice device, ResourceAllocationInfo allocation)
    {
        var heap = device.MemoryHeaps.Get(allocation.Heap, "AliasingMemoryHeap");
        if (!heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier requires a memory heap created with AllowAliasing.");
    }

    private static ResourceAllocationInfo? AllocationFor(NullDevice device, AliasingResource resource)
        => resource.Kind switch
        {
            AliasingResourceKind.None => null,
            AliasingResourceKind.Buffer => device.Buffers.Get(resource.Buffer, "AliasingBuffer").Allocation,
            AliasingResourceKind.Texture => device.Textures.Get(resource.Texture, "AliasingTexture").Allocation,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined."),
        };

    private static bool RangesOverlap(ulong firstOffset, ulong firstSize, ulong secondOffset, ulong secondSize)
        => firstOffset < secondOffset + secondSize && secondOffset < firstOffset + firstSize;

    private void ValidatePipelineDependency(ref NullSubmitState state)
    {
        var pipeline = state.Device.Pipelines.Get(Pipeline, "Pipeline");
        state.Device.PipelineLayouts.Get(pipeline.Layout, "PipelineLayout");
    }

    private void ValidatePassPipeline(ref NullSubmitState state)
    {
        var pipeline = state.Device.Pipelines.Get(Pipeline, "Pipeline");
        state.Device.PipelineLayouts.Get(pipeline.Layout, "PipelineLayout");
        NullPassValidation.ValidatePassCompatibility(pipeline, Compatibility);
    }

    private void ValidateTextureDependency(ref NullSubmitState state)
    {
        var texture = state.Device.Textures.Get(Texture, "Texture");
        state.RequireAliasingActive(AliasingResource.TextureResource(Texture), Label ?? "Texture dependency");
        if (!texture.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Texture UAV dependency requires UnorderedAccess bind flag.");
        state.RequireTextureState(Texture, texture.Desc, SubresourceRange.All, RequiredState, Label ?? string.Empty);
    }

    private void ValidateViewDependency(ref NullSubmitState state)
    {
        var view = state.Device.TextureViews.Get(TextureView, "TextureView");
        var texture = state.Device.Textures.Get(view.Texture, "Texture");
        state.RequireAliasingActive(AliasingResource.TextureResource(view.Texture), Label ?? "Texture view dependency");
        var range = new SubresourceRange(view.Desc.FirstMip, view.Desc.MipCount, view.Desc.FirstSlice, view.Desc.SliceCount);
        state.RequireTextureState(view.Texture, texture.Desc, range, RequiredState, Label ?? string.Empty);
    }

    private void ValidateBufferDependency(ref NullSubmitState state)
    {
        var buffer = state.Device.Buffers.Get(Buffer, "Buffer");
        state.RequireAliasingActive(AliasingResource.BufferResource(Buffer), Label ?? "Buffer dependency");
        if (RequiredState == ResourceState.UnorderedAccess && !buffer.Desc.BindFlags.HasFlag(BindFlags.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Buffer UAV dependency requires UnorderedAccess bind flag.");
        state.RequireBufferState(Buffer, RequiredState, Label ?? string.Empty);
    }

    private void ValidateAccelerationDependency(ref NullSubmitState state)
    {
        if (SourceAccelerationStructure.IsValid)
            state.Device.AccelerationStructures.Get(SourceAccelerationStructure, "SourceAccelerationStructure");
        if (DestinationAccelerationStructure.IsValid)
            state.Device.AccelerationStructures.Get(DestinationAccelerationStructure, "DestinationAccelerationStructure");
    }

    private void ValidateSetDependency(ref NullSubmitState state)
    {
        var bindingSet = state.Device.BindingSets.Get(BindingSet, "BindingSet");
        foreach (var bindState in bindingSet.BindStates)
        {
            switch (bindState.Kind)
            {
                case BindStateKind.Buffer:
                    state.RequireAliasingActive(AliasingResource.BufferResource(bindState.Buffer), bindState.Label);
                    state.RequireBufferState(bindState.Buffer, bindState.State, bindState.Label);
                    break;
                case BindStateKind.Texture:
                    state.RequireAliasingActive(AliasingResource.TextureResource(bindState.Texture), bindState.Label);
                    state.RequireTextureState(bindState.Texture, bindState.TextureRange, bindState.State, bindState.Label);
                    break;
                default:
                    throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding state kind {bindState.Kind} is not supported by Null backend.");
            }
        }

        foreach (var resource in bindingSet.Desc.Resources)
        {
            switch (resource.ResourceType)
            {
                case BindingType.Sampler:
                    state.Device.Samplers.Get(resource.SamplerHandle, "Sampler");
                    break;
                case BindingType.AccelerationStructure:
                    state.Device.AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                    break;
            }
        }
    }

    private void ValidateTransientDependency(ref NullSubmitState state, ReadOnlySpan<BindingResourceDesc> resources)
    {
        state.Device.BindingLayouts.Get(BindingLayout, "BindingLayout");
        if (resources.Length != BindingResourceCount)
            throw new RhiException(ErrorCode.ValidationFailure, "Transient binding resource storage is corrupt.");
        state.Device.ValidateTransientBindings(BindingLayout, resources);
        foreach (var resource in resources)
        {
            var bindState = BindRules.Resolve(resource.ResourceType);
            switch (bindState.Target)
            {
                case BindTarget.BufferView:
                    RequireBufferView(ref state, resource.BufferView, bindState.State, bindState.Label);
                    break;
                case BindTarget.TextureView:
                    RequireTextureView(ref state, resource.TextureView, bindState.State, bindState.Label);
                    break;
                case BindTarget.Sampler:
                    state.Device.Samplers.Get(resource.SamplerHandle, "Sampler");
                    break;
                case BindTarget.AccelerationStructure:
                    state.Device.AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                    break;
                case BindTarget.None:
                    break;
                default:
                    throw new RhiException(
                        ErrorCode.UnsupportedFeature,
                        $"Binding state target {bindState.Target} is not supported by Null backend.");
            }
        }
    }

    private static void RequireBufferView(ref NullSubmitState state, BufferViewHandle view, ResourceState required, string label)
    {
        var record = state.Device.BufferViews.Get(view, "BufferView");
        state.Device.Buffers.Get(record.Buffer, "Buffer");
        state.RequireAliasingActive(AliasingResource.BufferResource(record.Buffer), label);
        state.RequireBufferState(record.Buffer, required, label);
    }

    private static void RequireTextureView(ref NullSubmitState state, TextureViewHandle view, ResourceState required, string label)
    {
        var record = state.Device.TextureViews.Get(view, "TextureView");
        var texture = state.Device.Textures.Get(record.Texture, "Texture");
        state.RequireAliasingActive(AliasingResource.TextureResource(record.Texture), label);
        var range = new SubresourceRange(record.Desc.FirstMip, record.Desc.MipCount, record.Desc.FirstSlice, record.Desc.SliceCount);
        state.RequireTextureState(record.Texture, texture.Desc, range, required, label);
    }
}
