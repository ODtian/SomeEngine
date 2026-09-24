using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using Vortice.Direct3D12;

namespace SomeEngine.Rhi.D3D12;

internal sealed class D3D12Queue(D3D12Device device, QueueType type, CommandQueueKind kind, ID3D12CommandQueue nativeQueue) : IQueue, ID3D12QueueInterop, IDisposable
{
    private readonly ID3D12Fence _idleFence = device.NativeDevice.CreateFence(0, FenceFlags.None);
    private readonly List<CommandListLease> _availableCommandLists = [];
    private readonly System.Threading.Lock _commandListGate = new();
    private ulong _idleFenceValue;

    public QueueType Type { get; } = type;
    public IDevice Device { get; } = device;
    public CommandQueueKind Kind { get; } = kind;
    public ID3D12CommandQueue NativeQueue { get; } = nativeQueue;
    public CommandListType NativeType => D3D12Mappings.ToListType(Type);

    public T? Get<T>() where T : class
    {
        if (this is T self)
            return self;
        if (NativeQueue is T native)
            return native;
        return null;
    }

    public void Submit(
        ReadOnlySpan<CommandBufferHandle> commandBuffers,
        ReadOnlySpan<QueueWait> waits = default,
        ReadOnlySpan<QueueSignal> signals = default)
    {
        Profiler.QueueSubmit("D3D12", QueueTypeName(Type), commandBuffers.Length);
        using var scope = Profiler.BeginScope(Type switch
        {
            QueueType.Graphics => "D3D12Queue.Submit.Graphics",
            QueueType.Compute => "D3D12Queue.Submit.Compute",
            QueueType.Copy => "D3D12Queue.Submit.Copy",
            _ => "D3D12Queue.Submit",
        });
        device.ThrowIfDisposed();
        if (commandBuffers.IsEmpty)
        {
            if (waits.IsEmpty && signals.IsEmpty)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Queue submit requires work, waits, or signals.");

            ValidateSignals(signals, out var firstSignalFence, out var secondSignalFence);
            using (Profiler.BeginScope("D3D12Queue.Submit.Waits"))
            {
                foreach (var wait in waits)
                {
                    var fence = device.Fences.Get(wait.Fence, "Fence");
                    if (wait.Value > fence.LastSignaledValue)
                        throw new RhiException(ErrorCode.ValidationFailure, $"Fence wait value {wait.Value} has not been signaled. Last signaled value is {fence.LastSignaledValue}.");
                    NativeQueue.Wait(fence.Fence, wait.Value).CheckError();
                }
            }

            using (Profiler.BeginScope("D3D12Queue.Submit.SignalUserFences"))
            {
                SignalUserFences(signals, firstSignalFence, secondSignalFence);
            }

            return;
        }

        if (commandBuffers.Length == 1)
        {
            CommandBufferRecord record;
            using (Profiler.BeginScope("D3D12Queue.Submit.ValidateRecords"))
            {
                record = device.CommandBuffers.Get(commandBuffers[0], "CommandBuffer");
                if (record.Submitted)
                    throw new RhiException(ErrorCode.ValidationFailure, "Command buffer has already been submitted.");
                if (record.QueueKind != Kind)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Command buffer queue kind {record.QueueKind} cannot be submitted to {Kind} queue.");
                if (device.ValidationEnabled)
                {
                    var singleState = default(CommandState);
                    var singleAliasingStates = default(InlineFlatCore<AliasingResource, bool>);
                    var singleWrittenQueries = default(InlineFlatCore<QueryKey, bool>);
                    try
                    {
                        ValidateStateTransitions(record, ref singleState);
                        ValidateAliasingOperations(record, ref singleAliasingStates);
                        ValidateQueryOperations(record, ref singleWrittenQueries);
                    }
                    finally
                    {
                        singleState.Dispose();
                        singleAliasingStates.Dispose();
                        singleWrittenQueries.Dispose();
                    }
                }
            }

            ValidateSignals(signals, out var firstSignalFence, out var secondSignalFence);

            using (Profiler.BeginScope("D3D12Queue.Submit.Waits"))
            {
                foreach (var wait in waits)
                {
                    var fence = device.Fences.Get(wait.Fence, "Fence");
                    if (wait.Value > fence.LastSignaledValue)
                        throw new RhiException(ErrorCode.ValidationFailure, $"Fence wait value {wait.Value} has not been signaled. Last signaled value is {fence.LastSignaledValue}.");
                    NativeQueue.Wait(fence.Fence, wait.Value).CheckError();
                }
            }

            using (Profiler.BeginScope("D3D12Queue.Submit.ExecuteCommandLists"))
            {
                ID3D12CommandList nativeList = record.List;
                NativeQueue.ExecuteCommandLists(MemoryMarshal.CreateReadOnlySpan(ref nativeList, 1));
            }

            using (Profiler.BeginScope("D3D12Queue.Submit.CommitRecords"))
            {
                CommitStateTransitions(record);
                CommitAliasingOperations(record);
                device.MarkQueriesWritten(record.WrittenQueries);
            }

            ID3D12Fence? retireFence;
            ulong retireValue;

            using (Profiler.BeginScope("D3D12Queue.Submit.SignalUserFences"))
            {
                (retireFence, retireValue) = SignalUserFences(signals, firstSignalFence, secondSignalFence);
            }

            if (retireFence == null)
            {
                retireValue = ++_idleFenceValue;
                using (Profiler.BeginScope("D3D12Queue.Submit.SignalIdleFence"))
                {
                    NativeQueue.Signal(_idleFence, retireValue).CheckError();
                }
                retireFence = _idleFence;
            }

            using (Profiler.BeginScope("D3D12Queue.Submit.MarkSubmitted"))
            {
                record.Submitted = true;
                record.RetirementFence = retireFence;
                record.RetirementValue = retireValue;
            }

            return;
        }

        var nativeLists = ArrayPool<ID3D12CommandList>.Shared.Rent(commandBuffers.Length);
        var records = ArrayPool<CommandBufferRecord>.Shared.Rent(commandBuffers.Length);
        var state = default(CommandState);
        var aliasingStates = default(InlineFlatCore<AliasingResource, bool>);
        var writtenQueries = default(InlineFlatCore<QueryKey, bool>);
        try
        {
            using (Profiler.BeginScope("D3D12Queue.Submit.ValidateRecords"))
            {
                for (int index = 0; index < commandBuffers.Length; index++)
                {
                    for (int previous = 0; previous < index; previous++)
                    {
                        if (commandBuffers[previous] == commandBuffers[index])
                            throw new RhiException(ErrorCode.ValidationFailure, "Queue submit cannot contain the same command buffer more than once.");
                    }

                    var record = device.CommandBuffers.Get(commandBuffers[index], "CommandBuffer");
                    if (record.Submitted)
                        throw new RhiException(ErrorCode.ValidationFailure, "Command buffer has already been submitted.");
                    if (record.QueueKind != Kind)
                        throw new RhiException(ErrorCode.InvalidDescriptor, $"Command buffer queue kind {record.QueueKind} cannot be submitted to {Kind} queue.");
                    if (device.ValidationEnabled)
                    {
                        ValidateStateTransitions(record, ref state);
                        ValidateAliasingOperations(record, ref aliasingStates);
                        ValidateQueryOperations(record, ref writtenQueries);
                    }

                    records[index] = record;
                    nativeLists[index] = record.List;
                }
            }

            ValidateSignals(signals, out var firstSignalFence, out var secondSignalFence);

            using (Profiler.BeginScope("D3D12Queue.Submit.Waits"))
            {
                foreach (var wait in waits)
                {
                    var fence = device.Fences.Get(wait.Fence, "Fence");
                    if (wait.Value > fence.LastSignaledValue)
                        throw new RhiException(ErrorCode.ValidationFailure, $"Fence wait value {wait.Value} has not been signaled. Last signaled value is {fence.LastSignaledValue}.");
                    NativeQueue.Wait(fence.Fence, wait.Value).CheckError();
                }
            }

            using (Profiler.BeginScope("D3D12Queue.Submit.ExecuteCommandLists"))
            {
                NativeQueue.ExecuteCommandLists(nativeLists.AsSpan(0, commandBuffers.Length));
            }

            using (Profiler.BeginScope("D3D12Queue.Submit.CommitRecords"))
            {
                for (int index = 0; index < commandBuffers.Length; index++)
                {
                    var record = records[index];
                    CommitStateTransitions(record);
                    CommitAliasingOperations(record);
                    device.MarkQueriesWritten(record.WrittenQueries);
                }
            }

            ID3D12Fence? retireFence;
            ulong retireValue;

            using (Profiler.BeginScope("D3D12Queue.Submit.SignalUserFences"))
            {
                (retireFence, retireValue) = SignalUserFences(signals, firstSignalFence, secondSignalFence);
            }

            if (retireFence == null)
            {
                retireValue = ++_idleFenceValue;
                using (Profiler.BeginScope("D3D12Queue.Submit.SignalIdleFence"))
                {
                    NativeQueue.Signal(_idleFence, retireValue).CheckError();
                }
                retireFence = _idleFence;
            }

            using (Profiler.BeginScope("D3D12Queue.Submit.MarkSubmitted"))
            {
                for (int index = 0; index < commandBuffers.Length; index++)
                {
                    var record = records[index];
                    record.Submitted = true;
                    record.RetirementFence = retireFence;
                    record.RetirementValue = retireValue;
                }
            }
        }
        finally
        {
            state.Dispose();
            aliasingStates.Dispose();
            writtenQueries.Dispose();
            Array.Clear(nativeLists, 0, commandBuffers.Length);
            Array.Clear(records, 0, commandBuffers.Length);
            ArrayPool<ID3D12CommandList>.Shared.Return(nativeLists);
            ArrayPool<CommandBufferRecord>.Shared.Return(records);
        }
    }

    public void WaitIdle()
    {
        device.ThrowIfDisposed();
        ulong value = ++_idleFenceValue;
        NativeQueue.Signal(_idleFence, value).CheckError();
        WaitFence(_idleFence, value);
    }

    private static string QueueTypeName(QueueType type)
        => type switch
        {
            QueueType.Graphics => "Graphics",
            QueueType.Compute => "Compute",
            QueueType.Copy => "Copy",
            _ => "Unknown",
        };

    internal CommandListLease RentCommandList()
    {
        lock (_commandListGate)
        {
            if (_availableCommandLists.Count != 0)
            {
                int index = _availableCommandLists.Count - 1;
                var lease = _availableCommandLists[index];
                _availableCommandLists.RemoveAt(index);
                lease.Allocator.Reset();
                lease.List.Reset(lease.Allocator, null);
                return lease;
            }
        }

        var allocator = device.NativeDevice.CreateCommandAllocator(NativeType);
        var list = device.NativeDevice.CreateCommandList<ID3D12GraphicsCommandList>(NativeType, allocator, null);
        return new CommandListLease(allocator, list);
    }

    internal void ReturnCommandList(CommandListLease lease)
    {
        lock (_commandListGate)
        {
            if (_availableCommandLists.Count < device.Policy.MaxCachedCommandListsPerQueue)
            {
                _availableCommandLists.Add(lease);
                return;
            }
        }

        lease.Dispose();
    }

    public void Dispose()
    {
        foreach (var lease in _availableCommandLists)
        {
            lease.Dispose();
        }

        _availableCommandLists.Clear();
        _idleFence.Dispose();
        NativeQueue.Dispose();
    }

    private static void WaitFence(ID3D12Fence fence, ulong value)
    {
        if (fence.CompletedValue >= value)
            return;
        using var wait = new ManualResetEvent(false);
        fence.SetEventOnCompletion(value, wait.SafeWaitHandle.DangerousGetHandle()).CheckError();
        wait.WaitOne();
    }

    private void ValidateStateTransitions(
        CommandBufferRecord record,
        ref CommandState state)
    {
        foreach (var transition in record.BufferStates)
        {
            var buffer = device.Buffers.Get(transition.Buffer, "Buffer");
            state.ApplyBuffer(transition, buffer.State, buffer.Desc.Name);
        }

        foreach (var transition in record.TextureStates)
        {
            var texture = device.Textures.Get(transition.Texture, "Texture");
            ApplyTexture(transition, texture, ref state);
        }
    }

    private void CommitStateTransitions(CommandBufferRecord record)
    {
        foreach (var transition in record.BufferStates)
        {
            if (!transition.Commit)
                continue;
            device.SetBufferState(transition.Buffer, transition.FinalState);
        }

        foreach (var transition in record.TextureStates)
        {
            if (!transition.Commit)
                continue;
            device.SetTextureState(transition.Texture, transition.Range, transition.FinalState);
        }
    }

    private static void ApplyTexture(TextureTransition transition, TextureRecord texture, ref CommandState state)
    {
        var range = texture.ActualRange(transition.Range);
        for (uint slice = range.FirstSlice; slice < range.FirstSlice + range.SliceCount; slice++)
        {
            for (uint mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
            {
                var key = new SubresourceKey(transition.Texture, mip, slice);
                state.ApplyTexture(key, transition.OriginalState, transition.FinalState, texture.GetState(mip, slice), texture.Desc.Name);
            }
        }
    }

    private void ValidateAliasingOperations(CommandBufferRecord record, ref InlineFlatCore<AliasingResource, bool> aliasingStates)
    {
        foreach (var operation in record.AliasingOperations)
        {
            if (operation.Kind == AliasOperationKind.Barrier)
            {
                ValidateAliasState(operation.Barrier, ref aliasingStates);
                ApplyAliasingBarrier(operation.Barrier, ref aliasingStates);
                continue;
            }

            ValidateAliasingUse(operation.Resource, ref aliasingStates);
        }
    }

    private void ValidateAliasingUse(AliasingResource resource, ref InlineFlatCore<AliasingResource, bool> aliasingStates)
    {
        var placed = device.FindPlacedAllocation(resource);
        if (placed == null || !device.UsesAliasingHeap(placed))
            return;
        if (!GetAliasingState(placed, ref aliasingStates))
            throw new RhiException(ErrorCode.ValidationFailure, "Command buffer uses an aliased resource that is not active. Record an aliasing barrier before using it.");
        var heap = device.MemoryHeaps.Get(placed.Allocation.Heap, "AliasingMemoryHeap");
        foreach (var other in heap.PlacedAllocations)
        {
            if (other == placed)
                continue;
            if (!D3D12Device.RangesOverlap(placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes, other.Allocation.HeapOffset, other.Allocation.SizeInBytes))
                continue;
            if (GetAliasingState(other, ref aliasingStates))
                throw new RhiException(ErrorCode.ValidationFailure, "Command buffer uses an aliased resource that overlaps another active resource. Record an aliasing barrier before using it.");
        }
    }

    private void ValidateAliasState(AliasingBarrier barrier, ref InlineFlatCore<AliasingResource, bool> aliasingStates)
    {
        if (barrier.Before.Kind == AliasingResourceKind.None)
            return;
        var before = device.FindPlacedAllocation(barrier.Before)
            ?? throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier before endpoint must be placed.");
        if (device.UsesAliasingHeap(before) && !GetAliasingState(before, ref aliasingStates))
            throw new RhiException(ErrorCode.ValidationFailure, "Aliasing barrier before endpoint is not the active aliased resource.");
    }

    private void ApplyAliasingBarrier(AliasingBarrier barrier, ref InlineFlatCore<AliasingResource, bool> aliasingStates)
    {
        if (barrier.Before.Kind != AliasingResourceKind.None)
            aliasingStates.Set(barrier.Before, false);
        if (barrier.After.Kind == AliasingResourceKind.None)
            return;

        var after = device.FindPlacedAllocation(barrier.After)
            ?? throw new RhiException(ErrorCode.InvalidDescriptor, "Aliasing barrier after endpoint must be placed.");
        var heap = device.MemoryHeaps.Get(after.Allocation.Heap, "AliasingMemoryHeap");
        foreach (var placed in heap.PlacedAllocations)
        {
            if (placed.Resource != barrier.After
                && D3D12Device.RangesOverlap(after.Allocation.HeapOffset, after.Allocation.SizeInBytes, placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes))
            {
                aliasingStates.Set(placed.Resource, false);
            }
        }

        aliasingStates.Set(barrier.After, true);
    }

    private static bool GetAliasingState(PlacedRecord placed, ref InlineFlatCore<AliasingResource, bool> aliasingStates)
    {
        if (aliasingStates.TryGetValue(placed.Resource, out bool active))
            return active;
        aliasingStates.Add(placed.Resource, placed.Active);
        return placed.Active;
    }

    private void CommitAliasingOperations(CommandBufferRecord record)
    {
        foreach (var operation in record.AliasingOperations)
        {
            if (operation.Kind == AliasOperationKind.Barrier)
                device.ApplyAliasingBarrier(operation.Barrier);
        }
    }

    private void ValidateQueryOperations(
        CommandBufferRecord record,
        ref InlineFlatCore<QueryKey, bool> writtenQueries)
    {
        foreach (var operation in record.QueryOperations)
        {
            var pool = device.QueryPools.Get(operation.Pool, "QueryPool");
            if (operation.Kind == QueryOperationKind.Begin)
                continue;

            if (operation.Kind == QueryOperationKind.End)
            {
                var key = new QueryKey(operation.Pool, operation.FirstQuery);
                writtenQueries.Set(key, true);
                continue;
            }

            if (operation.Kind == QueryOperationKind.Write)
            {
                writtenQueries.Set(new QueryKey(operation.Pool, operation.FirstQuery), true);
                continue;
            }

            for (uint index = 0; index < operation.QueryCount; index++)
            {
                uint queryIndex = operation.FirstQuery + index;
                var key = new QueryKey(operation.Pool, queryIndex);
                if ((!writtenQueries.TryGetValue(key, out bool written) || !written) && !pool.Written[checked((int)queryIndex)])
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot resolve an unwritten D3D12 query.");
            }
        }
    }

    private void ValidateSignals(
        ReadOnlySpan<QueueSignal> signals,
        out FenceRecord? firstSignalFence,
        out FenceRecord? secondSignalFence)
    {
        firstSignalFence = null;
        secondSignalFence = null;
        for (int index = 0; index < signals.Length; index++)
        {
            var signal = signals[index];
            var fence = device.Fences.Get(signal.Fence, "Fence");
            if (index == 0)
                firstSignalFence = fence;
            else if (index == 1)
                secondSignalFence = fence;
            ulong currentValue = fence.LastSignaledValue;
            for (int previous = 0; previous < index; previous++)
            {
                if (signals[previous].Fence == signal.Fence)
                    currentValue = signals[previous].Value;
            }

            if (signal.Value <= currentValue)
                throw new RhiException(ErrorCode.ValidationFailure, $"Fence signal value {signal.Value} must be greater than current timeline value {currentValue}.");
        }
    }

    private (ID3D12Fence? Fence, ulong Value) SignalUserFences(
        ReadOnlySpan<QueueSignal> signals,
        FenceRecord? firstSignalFence,
        FenceRecord? secondSignalFence)
    {
        ID3D12Fence? retirementFence = null;
        ulong retirementValue = 0;
        for (int index = 0; index < signals.Length; index++)
        {
            var signal = signals[index];
            var fence = index switch
            {
                0 when firstSignalFence != null => firstSignalFence,
                1 when secondSignalFence != null => secondSignalFence,
                _ => device.Fences.Get(signal.Fence, "Fence"),
            };
            NativeQueue.Signal(fence.Fence, signal.Value).CheckError();
            fence.LastSignaledValue = signal.Value;
            if (retirementFence == null)
            {
                retirementFence = fence.Fence;
                retirementValue = signal.Value;
            }
        }

        return (retirementFence, retirementValue);
    }
}
