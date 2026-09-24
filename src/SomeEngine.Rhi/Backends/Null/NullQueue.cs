using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Rhi.Backends.Null;

internal sealed class NullQueue(NullDevice device, QueueType type) : IQueue
{
    public QueueType Type { get; } = type;
    public IDevice Device { get; } = device;

    public T? Get<T>() where T : class
        => this is T self ? self : null;

    public void Submit(
        ReadOnlySpan<CommandBufferHandle> commandBuffers,
        ReadOnlySpan<QueueWait> waits = default,
        ReadOnlySpan<QueueSignal> signals = default)
    {
        Profiler.QueueSubmit("Null", QueueTypeName(Type), commandBuffers.Length);
        using var scope = Profiler.BeginScope(Type switch
        {
            QueueType.Graphics => "NullQueue.Submit.Graphics",
            QueueType.Compute => "NullQueue.Submit.Compute",
            QueueType.Copy => "NullQueue.Submit.Copy",
            _ => "NullQueue.Submit",
        });
        device.ThrowIfDisposed();
        if (commandBuffers.IsEmpty && waits.IsEmpty && signals.IsEmpty)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Queue submit requires work, waits, or signals.");

        foreach (var wait in waits)
        {
            var fence = device.Fences.Get(wait.Fence, "Fence");
            if (fence.CompletedValue < wait.Value)
                throw new RhiException(ErrorCode.ValidationFailure, $"Fence '{fence.Name}' has completed value {fence.CompletedValue}, cannot wait for {wait.Value} in Null backend.");
        }

        for (int index = 0; index < commandBuffers.Length; index++)
        {
            var commandBuffer = commandBuffers[index];
            for (int previous = 0; previous < index; previous++)
            {
                if (commandBuffers[previous] == commandBuffer)
                    throw new RhiException(ErrorCode.ValidationFailure, "Queue submit contains the same command buffer more than once.");
            }

            var record = device.CommandBuffers.Get(commandBuffer, "CommandBuffer");
            if (record.Submitted)
                throw new RhiException(ErrorCode.ValidationFailure, $"Command buffer '{record.Name}' was already submitted. Command buffers are one-shot.");
            if (record.QueueType != Type)
                throw new RhiException(ErrorCode.ValidationFailure, $"Command buffer '{record.Name}' was recorded for {record.QueueType} queue, but submitted to {Type} queue.");
        }

        for (int index = 0; index < signals.Length; index++)
        {
            var signal = signals[index];
            var fence = device.Fences.Get(signal.Fence, "Fence");
            ulong currentValue = fence.CompletedValue;
            for (int previous = 0; previous < index; previous++)
            {
                if (signals[previous].Fence == signal.Fence)
                    currentValue = signals[previous].Value;
            }

            if (signal.Value <= currentValue)
                throw new RhiException(ErrorCode.ValidationFailure, $"Fence signal value {signal.Value} must be greater than current timeline value {currentValue}.");
        }

        ValidateQueryDependencies(commandBuffers);
        var submitState = new NullSubmitState(device);
        try
        {
            foreach (var commandBuffer in commandBuffers)
            {
                var command = device.CommandBuffers.Get(commandBuffer, "CommandBuffer");
                for (int index = 0; index < command.OperationCount; index++)
                {
                    var operation = command.Operations[index];
                    operation.ValidateSubmit(ref submitState, command.BindingResourcesFor(operation));
                }
            }
        }
        finally
        {
            submitState.Dispose();
        }

        foreach (var commandBuffer in commandBuffers)
        {
            var command = device.CommandBuffers.Get(commandBuffer, "CommandBuffer");
            for (int index = 0; index < command.OperationCount; index++)
            {
                var operation = command.Operations[index];
                operation.Execute(device);
            }
            command.Submitted = true;
        }

        foreach (var signal in signals)
            device.Fences.Get(signal.Fence, "Fence").CompletedValue = signal.Value;
    }

    public void WaitIdle()
    {
        device.ThrowIfDisposed();
    }

    private static string QueueTypeName(QueueType type)
        => type switch
        {
            QueueType.Graphics => "Graphics",
            QueueType.Compute => "Compute",
            QueueType.Copy => "Copy",
            _ => "Unknown",
        };

    private void ValidateQueryDependencies(ReadOnlySpan<CommandBufferHandle> commandBuffers)
    {
        for (int commandIndex = 0; commandIndex < commandBuffers.Length; commandIndex++)
        {
            var command = device.CommandBuffers.Get(commandBuffers[commandIndex], "CommandBuffer");
            for (int operationIndex = 0; operationIndex < command.OperationCount; operationIndex++)
            {
                var operation = command.Operations[operationIndex];
                switch (operation.Kind)
                {
                    case NullOperationKind.WriteTimestamp:
                    case NullOperationKind.EndQuery:
                        break;
                    case NullOperationKind.ResolveQueryData:
                        ValidateQueryDeps(commandBuffers, commandIndex, operationIndex, operation);
                        break;
                }
            }
        }
    }

    private void ValidateQueryDeps(
        ReadOnlySpan<CommandBufferHandle> commandBuffers,
        int commandIndex,
        int operationIndex,
        NullCommandOperation resolve)
    {
        var queryPool = device.QueryPools.Get(resolve.QueryPool, "QueryPool");
        for (uint index = 0; index < resolve.QueryCount; index++)
        {
            uint queryIndex = resolve.FirstQuery + index;
            if (queryPool.Values[checked((int)queryIndex)].HasValue)
                continue;
            if (WasQueryWritten(commandBuffers, commandIndex, operationIndex, resolve.QueryPool, queryIndex))
                continue;
            throw new RhiException(ErrorCode.ValidationFailure, $"Query {queryIndex} has not been written.");
        }
    }

    private bool WasQueryWritten(
        ReadOnlySpan<CommandBufferHandle> commandBuffers,
        int commandIndex,
        int operationIndex,
        QueryPoolHandle queryPool,
        uint queryIndex)
    {
        for (int currentCommandIndex = 0; currentCommandIndex <= commandIndex; currentCommandIndex++)
        {
            var command = device.CommandBuffers.Get(commandBuffers[currentCommandIndex], "CommandBuffer");
            int end = currentCommandIndex == commandIndex ? operationIndex : command.OperationCount;
            for (int currentOperationIndex = 0; currentOperationIndex < end; currentOperationIndex++)
            {
                var operation = command.Operations[currentOperationIndex];
                if ((operation.Kind == NullOperationKind.WriteTimestamp
                        || operation.Kind == NullOperationKind.EndQuery)
                    && operation.QueryPool == queryPool
                    && operation.QueryIndex == queryIndex)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
