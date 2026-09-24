using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi.Backends.Null;

internal struct NullSubmitState(NullDevice device)
{
    private CommandState _state;
    private InlineFlatCore<AliasingResource, bool> _aliasingStates;
    private InlineFlatCore<SubmitQueryKey, bool> _queryActive;
    private InlineFlatCore<SubmitQueryKey, bool> _queryWritten;

    public NullDevice Device { get; } = device;

    public void RequireTextureState(TextureHandle texture, TextureDesc desc, SubresourceRange range, ResourceState expected, string label)
    {
        RhiCommandValidation.ValidateSubresource(desc, range);
        uint mipCount = MipCount(desc, range);
        uint sliceCount = SliceCount(desc, range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + sliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + mipCount; mip++)
            {
                var key = new SubresourceKey(texture, mip, slice);
                ResourceState actual = _state.GetTexture(key, CurrentTextureState(key));
                if (!ResourceStateCompatibility.Satisfies(actual, expected))
                {
                    string name = string.IsNullOrWhiteSpace(desc.Name) ? "<unnamed>" : desc.Name;
                    throw new RhiException(
                        ErrorCode.ValidationFailure,
                        $"{label} requires texture '{name}' {texture} state {expected}, actual submit state is {actual}.");
                }
                _state.ValidateTexture(key, actual, expected, label, "submit");
            }
        }
    }

    public void RequireTextureState(TextureHandle texture, TextureDesc desc, TextureCopyRegion region, ResourceState expected, string label)
        => RequireTextureState(texture, desc, new SubresourceRange(region.MipLevel, 1, region.ArraySlice, 1), expected, label);

    public void SetTextureState(TextureHandle texture, TextureDesc desc, SubresourceRange range, ResourceState state)
    {
        RhiCommandValidation.ValidateSubresource(desc, range);
        uint mipCount = MipCount(desc, range);
        uint sliceCount = SliceCount(desc, range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + sliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + mipCount; mip++)
            {
                var key = new SubresourceKey(texture, mip, slice);
                ResourceState before = _state.GetTexture(key, CurrentTextureState(key));
                _state.SetTexture(key, before, state);
            }
        }
    }

    public void RequireBufferState(BufferHandle buffer, ResourceState expected, string label)
    {
        _state.ValidateBuffer(buffer, CurrentBufferState(buffer), expected, label, "submit");
    }

    public void SetBufferState(BufferHandle buffer, ResourceState state)
    {
        Device.Buffers.Get(buffer, "Buffer");
        _state.SetBuffer(buffer, CurrentBufferState(buffer), state);
    }

    public void RequireAliasingActive(AliasingResource resource, string label)
    {
        var placed = Device.FindPlacedAllocation(resource);
        if (placed == null)
            return;
        var heap = Device.MemoryHeaps.Get(placed.Allocation.Heap, "AliasingMemoryHeap");
        if (!heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
            return;

        bool targetActive = GetAliasingState(placed);
        if (!targetActive)
            throw new RhiException(ErrorCode.ValidationFailure, $"{label} uses an aliased resource that is not active. Submit an aliasing barrier before using it.");

        foreach (var other in heap.PlacedAllocations)
        {
            if (other == placed)
                continue;
            if (!NullDevice.RangesOverlap(placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes, other.Allocation.HeapOffset, other.Allocation.SizeInBytes))
                continue;
            if (GetAliasingState(other))
                throw new RhiException(ErrorCode.ValidationFailure, $"{label} overlaps another active aliased resource. Submit an aliasing barrier before using it.");
        }
    }

    public void ApplyAliasingBarrier(AliasingBarrier barrier)
    {
        if (barrier.Before.Kind != AliasingResourceKind.None)
        {
            var before = Device.FindPlacedAllocation(barrier.Before)
                ?? throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier before endpoint must be placed.");
            if (!GetAliasingState(before))
                throw new RhiException(ErrorCode.ValidationFailure, "Aliasing barrier before endpoint is not the active aliased resource.");
            _aliasingStates.Set(barrier.Before, false);
        }
        if (barrier.After.Kind != AliasingResourceKind.None)
        {
            var after = Device.FindPlacedAllocation(barrier.After)
                ?? throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier after endpoint must be placed.");
            var heap = Device.MemoryHeaps.Get(after.Allocation.Heap, "AliasingMemoryHeap");
            foreach (var placed in heap.PlacedAllocations)
            {
                if (placed.Resource != barrier.After
                    && NullDevice.RangesOverlap(after.Allocation.HeapOffset, after.Allocation.SizeInBytes, placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes))
                {
                    _aliasingStates.Set(placed.Resource, false);
                }
            }

            _aliasingStates.Set(barrier.After, true);
        }
    }

    public void RequireTextureState(TextureHandle texture, SubresourceRange range, ResourceState expected, string label)
    {
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
            {
                var key = new SubresourceKey(texture, mip, slice);
                _state.ValidateTexture(key, CurrentTextureState(key), expected, label, "submit");
            }
        }
    }

    public void WriteTimestamp(QueryPoolHandle queryPool, uint queryIndex)
    {
        var record = Device.QueryPools.Get(queryPool, "QueryPool");
        if (record.Desc.Type != QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "WriteTimestamp requires a timestamp query pool.");
        if (queryIndex >= record.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp query index is outside the query pool.");
        _queryWritten.Set(new SubmitQueryKey(queryPool, queryIndex), true);
    }

    public void BeginQuery(QueryPoolHandle queryPool, uint queryIndex)
    {
        var record = ValidateQuery(queryPool, queryIndex);
        var key = new SubmitQueryKey(queryPool, queryIndex);
        if (GetQueryActive(record, key))
            throw new RhiException(ErrorCode.ValidationFailure, "Query is already active.");
        _queryActive.Set(key, true);
    }

    public void EndQuery(QueryPoolHandle queryPool, uint queryIndex)
    {
        var record = ValidateQuery(queryPool, queryIndex);
        var key = new SubmitQueryKey(queryPool, queryIndex);
        if (!GetQueryActive(record, key))
            throw new RhiException(ErrorCode.ValidationFailure, "Query is not active.");
        _queryActive.Set(key, false);
        _queryWritten.Set(key, true);
    }

    public void RequireResolvedQueries(QueryPoolHandle queryPool, uint firstQuery, uint queryCount)
    {
        var record = Device.QueryPools.Get(queryPool, "QueryPool");
        ValidateQueryRange(record, firstQuery, queryCount);
        for (uint index = 0; index < queryCount; index++)
        {
            uint queryIndex = firstQuery + index;
            var key = new SubmitQueryKey(queryPool, queryIndex);
            if (GetQueryActive(record, key))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot resolve an active query.");
            if (!GetQueryWritten(record, key))
                throw new RhiException(ErrorCode.ValidationFailure, $"Query {queryIndex} has not been written.");
        }
    }

    private ResourceState CurrentTextureState(SubresourceKey key)
        => Device.Textures.Get(key.Texture, "Texture").GetState(key.MipLevel, key.ArraySlice);

    private ResourceState CurrentBufferState(BufferHandle buffer)
        => Device.Buffers.Get(buffer, "Buffer").State;

    private bool GetAliasingState(PlacedAllocationRecord allocation)
    {
        if (_aliasingStates.TryGetValue(allocation.Resource, out bool active))
            return active;
        _aliasingStates.Add(allocation.Resource, allocation.Active);
        return allocation.Active;
    }

    private QueryPoolRecord ValidateQuery(QueryPoolHandle queryPool, uint queryIndex)
    {
        var record = Device.QueryPools.Get(queryPool, "QueryPool");
        if (record.Desc.Type == QueryType.Timestamp)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Timestamp queries do not use BeginQuery/EndQuery.");
        if (queryIndex >= record.Desc.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query index is outside the query pool.");
        if (record.Desc.Type == QueryType.Occlusion && !Device.Features.OcclusionQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Occlusion queries are not supported by this device.");
        if (record.Desc.Type == QueryType.PipelineStatistics && !Device.Features.PipelineStatisticsQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Pipeline statistics queries are not supported by this device.");
        return record;
    }

    private static void ValidateQueryRange(QueryPoolRecord record, uint firstQuery, uint queryCount)
    {
        if (queryCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve count must be greater than zero.");
        if (firstQuery >= record.Desc.Count || queryCount > record.Desc.Count - firstQuery)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Query resolve range is outside the query pool.");
    }

    private bool GetQueryActive(QueryPoolRecord record, SubmitQueryKey key)
    {
        if (_queryActive.TryGetValue(key, out bool active))
            return active;
        active = record.Active[checked((int)key.Index)];
        _queryActive.Add(key, active);
        return active;
    }

    private bool GetQueryWritten(QueryPoolRecord record, SubmitQueryKey key)
    {
        if (_queryWritten.TryGetValue(key, out bool written))
            return written;
        written = record.Values[checked((int)key.Index)].HasValue;
        _queryWritten.Add(key, written);
        return written;
    }

    private static uint MipCount(TextureDesc desc, SubresourceRange range)
        => range.MipCount == uint.MaxValue ? desc.MipLevels - range.FirstMip : range.MipCount;

    private static uint SliceCount(TextureDesc desc, SubresourceRange range)
        => range.SliceCount == uint.MaxValue ? desc.ArraySize - range.FirstSlice : range.SliceCount;

    public void Dispose()
    {
        _state.Dispose();
        _aliasingStates.Dispose();
        _queryActive.Dispose();
        _queryWritten.Dispose();
    }
}

internal readonly record struct SubmitQueryKey(QueryPoolHandle Pool, uint Index);
