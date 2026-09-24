using System.Buffers;
using System.Threading.Tasks;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using RhiComputePass = SomeEngine.Rhi.IComputePass;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph
{
    internal void Execute(IDevice device, IQueue queue, ISwapchain? swapchain = null, uint syncInterval = 1)
        => _executor.Execute(device, queue, swapchain, syncInterval);

    public void Execute(GraphQueues queues, ISwapchain? swapchain = null, uint syncInterval = 1)
        => _executor.Execute(queues, swapchain, syncInterval);

    internal void Execute(IDevice device, GraphQueues queues, ISwapchain? swapchain = null, uint syncInterval = 1)
        => _executor.Execute(device, queues, swapchain, syncInterval);

    internal void ExecuteCore(
        IDevice device,
        GraphQueues queues,
        ISwapchain? swapchain,
        uint syncInterval)
    {
        using var scope = Profiler.BeginScope("RenderGraph.Execute");
        ThrowIfDisposed();
        ThrowIfActive(nameof(Execute));
        ArgumentNullException.ThrowIfNull(device);
        queues.Require();
        if (_executor.CurrentFrameSubmitted)
        {
            throw new InvalidOperationException(
                "RenderGraph Execute cannot be called again for the current frame. Call BeginFrame before recording and executing another frame.");
        }

        if (!ReferenceEquals(_lastDevice, device))
        {
            if (_lastDevice != null)
            {
                throw new InvalidOperationException(
                    "RenderGraph instances are device-affine after first execution. Create a new RenderGraph for a different device.");
            }
            _aliasCache.Clear();
            DestroyTimestamps();
        }
        _lastDevice = device;
        CompiledGraph compile;
        using (Profiler.BeginScope("RenderGraph.CompileGraph"))
        {
            compile = CompileGraph(queues.HasCompute, queues.HasCopy);
        }
        ExecuteGraph(device, queues, swapchain, syncInterval, compile);
    }

    private void ExecuteGraph(
        IDevice device,
        GraphQueues queues,
        ISwapchain? swapchain,
        uint syncInterval,
        CompiledGraph compile)
    {
        using (Profiler.BeginScope("RenderGraph.RetirePendingFrames"))
        {
            RetirePendingFrames(wait: false);
        }
        using (Profiler.BeginScope("RenderGraph.BuildAliases"))
        {
            BuildAliases(device, compile);
        }
        using (Profiler.BeginScope("RenderGraph.BuildFrameSync"))
        {
            _compiler.BuildFrameSync(compile);
        }
        using (Profiler.BeginScope("RenderGraph.ValidateImportedResources"))
        {
            ValidateImportedResources(device, compile);
        }
        using (Profiler.BeginScope("RenderGraph.FinalizeResources"))
        {
            FinalizeResources(device, compile);
        }
        BeginCommandBarriers(compile);
        using (Profiler.BeginScope("RenderGraph.CreatePassBarriers"))
        {
            CreatePassBarriers(device, compile);
        }
        _activeCompile = compile;

        try
        {
            bool finalCommandWork = compile.FinalWork;
            bool mergeFinalInBatch = compile.FinalWork && CanRecordFinalInBatch(compile);
            bool finalSubmitWork = compile.FinalWork && HasBarriers(_finalBarriers);

            if (compile.Count == 0 && !finalSubmitWork)
            {
                CommitResourceStates(compile);
                PublishExports();
                ClearFrameUsage();
                _executor.CurrentFrameSubmitted = true;
                return;
            }

            FenceHandle fence = default;
            ulong fenceValue = 0;
            bool signalFrameInBatch = false;
            var commandBuffers = _executor.CommandBuffers;
            commandBuffers.Clear();
            int commandBufferCount = compile.QueueBatches.Count + (mergeFinalInBatch ? 0 : compile.FinalWork ? 1 : 0);
            DeviceTimestamps? timestamps = RentTimestamps(device, compile.Count, finalCommandWork);
            if (commandBuffers.Capacity < commandBufferCount)
                commandBuffers.Capacity = commandBufferCount;
            Span<FencePoint> queuePoints = stackalloc FencePoint[QueueCount];
            FencePoint[]? rentedBatchPoints = compile.QueueBatches.Count == 0
                ? null
                : ArrayPool<FencePoint>.Shared.Rent(compile.QueueBatches.Count);
            CommandBufferHandle[]? rentedRecordedBatches = compile.QueueBatches.Count == 0
                ? null
                : ArrayPool<CommandBufferHandle>.Shared.Rent(compile.QueueBatches.Count);
            Span<FencePoint> batchPoints = rentedBatchPoints == null
                ? default
                : rentedBatchPoints.AsSpan(0, compile.QueueBatches.Count);
            batchPoints.Clear();
            bool submitted = false;
            bool queuedForRetirement = false;
            try
            {
                if (rentedRecordedBatches != null)
                {
                    RecordBatches(
                        device,
                        compile,
                        timestamps,
                        rentedRecordedBatches,
                        mergeFinalInBatch ? compile.QueueBatches.Count - 1 : -1);
                    for (int batchIndex = 0; batchIndex < compile.QueueBatches.Count; batchIndex++)
                        commandBuffers.Add(rentedRecordedBatches[batchIndex]);
                }

                signalFrameInBatch = mergeFinalInBatch || CanBatchSignal(compile, finalSubmitWork);
                if (signalFrameInBatch)
                {
                    fence = GetFrameFence(device);
                    fenceValue = checked(++_executor.FrameFenceValue);
                }

                ReportQueues(compile, finalSubmitWork);
                SubmitBatches(
                    device,
                    queues,
                    compile,
                    commandBuffers,
                    queuePoints,
                    batchPoints,
                    signalFrameInBatch,
                    mergeFinalInBatch ? compile.QueueBatches.Count - 1 : -1,
                    fence,
                    fenceValue,
                    out submitted);

                if (!signalFrameInBatch && finalSubmitWork)
                {
                    QueueType finalQueue = compile.FrameQueue;
                    CommandBufferHandle finalBuffer = RecordFinal(device, timestamps, finalQueue);
                    commandBuffers.Add(finalBuffer);

                    using (Profiler.BeginScope("RenderGraph.QueueSubmit"))
                    {
                        SubmitFinal(device, queues, compile, finalBuffer, queuePoints, finalQueue, out fence, out fenceValue);
                    }
                    submitted = true;
                }
                else if (!signalFrameInBatch)
                {
                    using (Profiler.BeginScope("RenderGraph.QueueSubmit"))
                    {
                        SubmitFrame(device, queues, compile, queuePoints, out fence, out fenceValue);
                    }
                    submitted = true;
                }

                CommitResourceStates(compile);
                using (Profiler.BeginScope("RenderGraph.QueueFrameRetirement"))
                {
                    QueueFrame(compile, fence, fenceValue, commandBuffers, timestamps);
                    timestamps = null;
                }
                queuedForRetirement = true;

                if (swapchain != null)
                {
                    using (Profiler.BeginScope("RenderGraph.Present"))
                    {
                        swapchain.Present(new PresentDesc
                        {
                            SyncInterval = syncInterval,
                            AllowTearing = syncInterval == 0,
                        });
                    }

                    Profiler.FrameMark();
                }

            }
            catch
            {
                if (!queuedForRetirement)
                    DestroyFrameTransientBindingSets(device);
                ClearFrameUsage();
                if (submitted && !queuedForRetirement)
                    device.WaitIdle();
                if (!queuedForRetirement)
                {
                    ReturnTimestamps(timestamps);
                    for (int i = commandBuffers.Count - 1; i >= 0; i--)
                    {
                        if (commandBuffers[i].IsValid)
                            device.Destroy(commandBuffers[i]);
                    }
                }

                throw;
            }
            finally
            {
                if (rentedBatchPoints != null)
                {
                    batchPoints.Clear();
                    ArrayPool<FencePoint>.Shared.Return(rentedBatchPoints);
                }
                if (rentedRecordedBatches != null)
                {
                    Array.Clear(rentedRecordedBatches, 0, compile.QueueBatches.Count);
                    ArrayPool<CommandBufferHandle>.Shared.Return(rentedRecordedBatches);
                }

                commandBuffers.Clear();
            }
        }
        finally
        {
            _activeCompile = null;
        }
    }

    private void RecordBatches(
        IDevice device,
        CompiledGraph compile,
        DeviceTimestamps? timestamps,
        CommandBufferHandle[] buffers,
        int finalBatchIndex)
    {
        int count = compile.QueueBatches.Count;
        if (!CanRecordBatchesInParallel(device, compile, timestamps))
        {
            try
            {
                for (int batchIndex = 0; batchIndex < count; batchIndex++)
                    buffers[batchIndex] = RecordBatch(
                        device,
                        compile,
                        batchIndex,
                        timestamps,
                        batchIndex == finalBatchIndex);
            }
            catch
            {
                for (int batchIndex = 0; batchIndex < count; batchIndex++)
                {
                    if (buffers[batchIndex].IsValid)
                    {
                        device.Destroy(buffers[batchIndex]);
                        buffers[batchIndex] = default;
                    }
                }

                throw;
            }

            return;
        }

        try
        {
            Parallel.For(
                0,
                count,
                batchIndex => buffers[batchIndex] = RecordBatch(
                    device,
                    compile,
                    batchIndex,
                    timestamps: null,
                    batchIndex == finalBatchIndex));
        }
        catch
        {
            for (int batchIndex = 0; batchIndex < count; batchIndex++)
            {
                if (buffers[batchIndex].IsValid)
                {
                    device.Destroy(buffers[batchIndex]);
                    buffers[batchIndex] = default;
                }
            }

            throw;
        }
    }

    private static bool CanRecordBatchesInParallel(IDevice device, CompiledGraph compile, DeviceTimestamps? timestamps)
        => false
            && device.Features.ParallelCommandRecording
            && timestamps == null
            && compile.QueueBatches.Count > 1;

    private CommandBufferHandle RecordBatch(
        IDevice device,
        CompiledGraph compile,
        int batchIndex,
        DeviceTimestamps? timestamps,
        bool recordFinal)
    {
        var batch = compile.QueueBatches[batchIndex];
        using var list = device.CreateCommandList(new CommandListDesc
        {
            Name = $"RenderGraph {batch.Queue} Batch {batchIndex}",
            QueueType = batch.Queue,
        });
        uint timingStart = timestamps?.Cursor ?? 0;
        var execution = new GraphFrameExecution(this, device, list);
        var graphContext = new RenderGraphContext(this, execution);
        bool emitDebugMarkers = list.DebugMarkersEnabled;
        bool writeNames = Profiler.NeedsNames(emitDebugMarkers);

        if (batch.Kind == QueueBatchKind.Compute)
        {
            if (emitDebugMarkers || (timestamps?.Supports(batch.Queue) ?? false))
            {
                int endSlot = checked(batch.StartSlot + batch.Count);
                for (int passSlot = batch.StartSlot; passSlot < endSlot; passSlot++)
                {
                    ExecuteComputePass(
                        compile,
                        list,
                        execution,
                        graphContext,
                        passSlot,
                        emitDebugMarkers,
                        writeNames,
                        timestamps,
                        batch.Queue);
                }
            }
            else
            {
                ExecuteComputeBatch(
                    compile,
                    list,
                    execution,
                    graphContext,
                    batch,
                    emitDebugMarkers,
                    writeNames);
            }
        }
        else
        {
            int endSlot = checked(batch.StartSlot + batch.Count);
            for (int passSlot = batch.StartSlot; passSlot < endSlot;)
            {
                int passIndex = compile.Passes[passSlot];
                if (IsComputeMode(_passes[passIndex].Mode))
                {
                    if (timestamps == null)
                    {
                        int runStartSlot = passSlot;
                        do
                        {
                            passSlot++;
                        }
                        while (passSlot < endSlot && IsComputeMode(_passes[compile.Passes[passSlot]].Mode));

                        ExecuteComputeBatch(
                            compile,
                            list,
                            execution,
                            graphContext,
                            new QueueBatch(
                                runStartSlot,
                                passSlot - runStartSlot,
                                QueueBatchKind.Compute,
                                batch.Queue,
                                0,
                                0,
                                QueueMask.None,
                                QueueMask.None),
                            emitDebugMarkers,
                            writeNames);
                    }
                    else
                    {
                        ExecuteComputePass(
                            compile,
                            list,
                            execution,
                            graphContext,
                            passSlot,
                            emitDebugMarkers,
                            writeNames,
                            timestamps,
                            batch.Queue);
                        passSlot++;
                    }
                }
                else
                {
                    ExecuteCommandPass(
                        compile,
                        list,
                        graphContext,
                        passSlot,
                        emitDebugMarkers,
                        writeNames,
                        timestamps,
                        batch.Queue);
                    passSlot++;
                }
            }
        }

        if (recordFinal)
            RecordFinalTransitions(list, timestamps, batch.Queue, emitDebugMarkers);

        timestamps?.Resolve(list, batch.Queue, timingStart);
        using (Profiler.BeginScope("RenderGraph.FinishCommandBuffer"))
        {
            return list.Finish();
        }
    }

    private CommandBufferHandle RecordFinal(
        IDevice device,
        DeviceTimestamps? timestamps,
        QueueType queue)
    {
        using var list = device.CreateCommandList(new CommandListDesc
        {
            Name = "RenderGraph Final",
            QueueType = queue,
        });
        uint timingStart = timestamps?.Cursor ?? 0;
        bool emitDebugMarkers = list.DebugMarkersEnabled;

        RecordFinalTransitions(list, timestamps, queue, emitDebugMarkers);
        timestamps?.Resolve(list, queue, timingStart);

        using (Profiler.BeginScope("RenderGraph.FinishCommandBuffer"))
            return list.Finish();
    }

    private void RecordFinalTransitions(
        ICommandList list,
        DeviceTimestamps? timestamps,
        QueueType queue,
        bool emitDebugMarkers)
    {
        TimestampSpan timestampSpan = timestamps?.Start(list, queue, "Final Transitions", -1, "FinalTransitions") ?? default;
        using (Profiler.BeginGraphPass("Final Transitions", -1, "FinalTransitions"))
        {
            if (emitDebugMarkers)
                list.PushDebugGroup("RenderGraph Final Transitions");
            try
            {
                EmitBarriers(list, _finalBarriers);
            }
            finally
            {
                if (emitDebugMarkers)
                list.PopDebugGroup();
            }
        }
        timestamps?.Stop(list, timestampSpan);
    }

    private void ExecuteCommandPass(
        CompiledGraph compile,
        ICommandList list,
        RenderGraphContext graphContext,
        int passSlot,
        bool emitDebugMarkers,
        bool writeNames,
        DeviceTimestamps? timestamps,
        QueueType queue)
    {
        int passIndex = compile.Passes[passSlot];
        Pass pass = _passes[passIndex];
        var execute = pass.CommandExecute
            ?? throw new InvalidOperationException($"RenderGraph pass '{pass.Name}' has no compiled command execute delegate.");
        string passName = writeNames ? MarkerName(pass.Name, passSlot) : string.Empty;
        TimestampSpan timestampSpan = timestamps?.Start(list, queue, passName, passIndex, "Pass") ?? default;
        using var passScope = Profiler.BeginGraphPass(passName, passSlot, "Pass");
        if (emitDebugMarkers)
            list.PushDebugGroup($"RenderGraph Pass: {passName}");
        try
        {
            CommandBarriers barriers = _passBarriers[passIndex];
            if (HasBarriers(barriers))
            {
                using (Profiler.BeginGraphPass(passName, passSlot, "Barriers"))
                {
                    if (emitDebugMarkers)
                        list.PushDebugGroup($"RenderGraph Barriers: {passName}");
                    try
                    {
                        EmitBarriers(list, barriers);
                    }
                    finally
                    {
                        if (emitDebugMarkers)
                            list.PopDebugGroup();
                    }
                }
            }

            using (Profiler.BeginGraphPass(passName, passSlot, "Execute"))
            {
                if (emitDebugMarkers)
                    list.PushDebugGroup($"RenderGraph Execute: {passName}");
                graphContext.BeginPass(passIndex, pass.Mode);
                try
                {
                    BeginExecutePass(passIndex);
                    try
                    {
                        execute(graphContext);
                    }
                    finally
                    {
                        EndExecutePass();
                    }
                }
                finally
                {
                    graphContext.EndPass();
                    if (emitDebugMarkers)
                        list.PopDebugGroup();
                }
            }

            timestamps?.Stop(list, timestampSpan);
        }
        finally
        {
            if (emitDebugMarkers)
                list.PopDebugGroup();
        }
    }

    private void ExecuteComputePass(
        CompiledGraph compile,
        ICommandList list,
        GraphFrameExecution execution,
        RenderGraphContext graphContext,
        int passSlot,
        bool emitDebugMarkers,
        bool writeNames,
        DeviceTimestamps? timestamps,
        QueueType queue)
    {
        int passIndex = compile.Passes[passSlot];
        Pass pass = _passes[passIndex];
        var execute = pass.ComputeExecute
            ?? throw new InvalidOperationException($"RenderGraph pass '{pass.Name}' has no compiled compute execute delegate.");
        string passName = writeNames ? MarkerName(pass.Name, passSlot) : string.Empty;
        TimestampSpan timestampSpan = timestamps?.Start(list, queue, passName, passIndex, "Pass") ?? default;
        using var passScope = Profiler.BeginGraphPass(passName, passSlot, "Pass");
        if (emitDebugMarkers)
            list.PushDebugGroup($"RenderGraph Pass: {passName}");
        try
        {
            CommandBarriers barriers = _passBarriers[passIndex];
            if (HasBarriers(barriers))
            {
                using (Profiler.BeginGraphPass(passName, passSlot, "Barriers"))
                {
                    if (emitDebugMarkers)
                        list.PushDebugGroup($"RenderGraph Barriers: {passName}");
                    try
                    {
                        EmitBarriers(list, barriers);
                    }
                    finally
                    {
                        if (emitDebugMarkers)
                            list.PopDebugGroup();
                    }
                }
            }

            using (Profiler.BeginGraphPass(passName, passSlot, "Execute"))
            {
                if (emitDebugMarkers)
                    list.PushDebugGroup($"RenderGraph Execute: {passName}");
                graphContext.BeginPass(passIndex, pass.Mode);
                RhiComputePass? computePass = null;
                ComputeCommands? commands = null;
                try
                {
                    computePass = list.BeginComputePass(new ComputePassDesc
                    {
                        Name = passName,
                    });
                    commands = new ComputeCommands(graphContext, execution, computePass, passName);
                    BeginExecutePass(passIndex);
                    try
                    {
                        execute(graphContext, commands);
                    }
                    finally
                    {
                        EndExecutePass();
                    }
                }
                finally
                {
                    commands?.Invalidate();
                    graphContext.EndPass();
                    if (computePass != null)
                        computePass.End();
                    if (emitDebugMarkers)
                        list.PopDebugGroup();
                }
            }

            timestamps?.Stop(list, timestampSpan);
        }
        finally
        {
            if (emitDebugMarkers)
                list.PopDebugGroup();
        }
    }

    private static bool IsComputeMode(PassMode mode)
        => mode is PassMode.Compute or PassMode.AsyncCompute;

    private void ExecuteComputeBatch(
        CompiledGraph compile,
        ICommandList list,
        GraphFrameExecution execution,
        RenderGraphContext graphContext,
        QueueBatch batch,
        bool emitDebugMarkers,
        bool writeNames)
    {
        int startPassSlot = batch.StartSlot;
        int endPassSlot = checked(batch.StartSlot + batch.Count);

        RhiComputePass? computePass = null;
        try
        {
            for (int passSlot = startPassSlot; passSlot < endPassSlot; passSlot++)
            {
                int passIndex = compile.Passes[passSlot];
                Pass pass = _passes[passIndex];
                var execute = pass.ComputeExecute
                    ?? throw new InvalidOperationException($"RenderGraph pass '{pass.Name}' has no compiled compute execute delegate.");
                string passName = writeNames ? MarkerName(pass.Name, passSlot) : string.Empty;
                using var passScope = Profiler.BeginGraphPass(passName, passSlot, "Pass");
                if (emitDebugMarkers)
                    list.PushDebugGroup($"RenderGraph Pass: {passName}");

                try
                {
                    CommandBarriers barriers = _passBarriers[passIndex];
                    if (HasBarriers(barriers))
                    {
                        using (Profiler.BeginGraphPass(passName, passSlot, "Barriers"))
                        {
                            if (emitDebugMarkers)
                                list.PushDebugGroup($"RenderGraph Barriers: {passName}");
                            try
                            {
                                if (computePass == null)
                                    EmitBarriers(list, barriers);
                                else
                                    EmitBarriers(computePass, barriers);
                            }
                            finally
                            {
                                if (emitDebugMarkers)
                                    list.PopDebugGroup();
                            }
                        }
                    }

                    computePass ??= list.BeginComputePass(new ComputePassDesc
                    {
                        Name = BatchName(compile, startPassSlot, endPassSlot, writeNames),
                    });

                    using (Profiler.BeginGraphPass(passName, passSlot, "Execute"))
                    {
                        if (emitDebugMarkers)
                            list.PushDebugGroup($"RenderGraph Execute: {passName}");
                        graphContext.BeginPass(passIndex, pass.Mode);
                        ComputeCommands? commands = null;
                        try
                        {
                            commands = new ComputeCommands(graphContext, execution, computePass, passName);
                            BeginExecutePass(passIndex);
                            try
                            {
                                execute(graphContext, commands);
                            }
                            finally
                            {
                                EndExecutePass();
                            }
                        }
                        finally
                        {
                            commands?.Invalidate();
                            graphContext.EndPass();
                            if (emitDebugMarkers)
                                list.PopDebugGroup();
                        }
                    }
                }
                finally
                {
                    if (emitDebugMarkers)
                        list.PopDebugGroup();
                }
            }
        }
        finally
        {
            if (computePass != null)
                computePass.End();
        }
    }

    private string BatchName(CompiledGraph compile, int startPassSlot, int endPassSlot, bool writeNames)
    {
        if (!writeNames)
            return string.Empty;

        int firstPassIndex = compile.Passes[startPassSlot];
        string firstName = MarkerName(_passes[firstPassIndex].Name, startPassSlot);
        if (endPassSlot == startPassSlot + 1)
            return firstName;

        int lastPassIndex = compile.Passes[endPassSlot - 1];
        string lastName = MarkerName(_passes[lastPassIndex].Name, endPassSlot - 1);
        return $"RenderGraph Compute: {firstName} -> {lastName}";
    }

    private void SubmitBatches(
        IDevice device,
        GraphQueues queues,
        CompiledGraph compile,
        IReadOnlyList<CommandBufferHandle> commandBuffers,
        Span<FencePoint> queuePoints,
        Span<FencePoint> batchPoints,
        bool signalFrameInBatch,
        int finalBatchIndex,
        FenceHandle frameFence,
        ulong frameValue,
        out bool submitted)
    {
        int count = compile.QueueBatches.Count;
        submitted = false;
        if (count == 0)
            return;

        byte[]? rentedSubmitted = null;
        int[]? rentedNext = null;
        Span<byte> submittedBatches = count <= StackBatchScratchLimit
            ? stackalloc byte[count]
            : (rentedSubmitted = ArrayPool<byte>.Shared.Rent(count)).AsSpan(0, count);
        Span<int> queueHeads = stackalloc int[QueueCount];
        Span<int> nextSameQueue = count <= StackBatchScratchLimit
            ? stackalloc int[count]
            : (rentedNext = ArrayPool<int>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            submittedBatches.Clear();
            queueHeads.Fill(-1);
            nextSameQueue.Fill(-1);
            for (int batchIndex = count - 1; batchIndex >= 0; batchIndex--)
            {
                int queueSlot = QueueSlot(compile.QueueBatches[batchIndex].Queue);
                nextSameQueue[batchIndex] = queueHeads[queueSlot];
                queueHeads[queueSlot] = batchIndex;
            }

            int submittedCount = 0;
            while (submittedCount < count)
            {
                bool progress = false;
                for (int batchIndex = 0; batchIndex < count; batchIndex++)
                {
                    if (!CanSubmit(compile, batchIndex, submittedBatches, queueHeads, finalBatchIndex, submittedCount))
                        continue;

                    CommandBufferHandle buffer = commandBuffers[batchIndex];
                    bool signalFrame = signalFrameInBatch && submittedCount == count - 1;
                    using (Profiler.BeginScope("RenderGraph.QueueSubmit"))
                    {
                        SubmitBatch(
                            device,
                            queues,
                            compile,
                            batchIndex,
                            buffer,
                            queuePoints,
                            batchPoints,
                            signalFrame,
                            signalFrame && batchIndex == finalBatchIndex,
                            frameFence,
                            frameValue);
                    }

                    submittedBatches[batchIndex] = 1;
                    int queueSlot = QueueSlot(compile.QueueBatches[batchIndex].Queue);
                    queueHeads[queueSlot] = nextSameQueue[batchIndex];
                    submittedCount++;
                    progress = true;
                    submitted = true;
                }

                if (!progress)
                    throw new InvalidOperationException("RenderGraph queue batches contain an unsatisfied dependency.");
            }
        }
        finally
        {
            if (rentedSubmitted != null)
                ArrayPool<byte>.Shared.Return(rentedSubmitted);
            if (rentedNext != null)
                ArrayPool<int>.Shared.Return(rentedNext);
        }
    }

    private static bool CanSubmit(
        CompiledGraph compile,
        int batchIndex,
        ReadOnlySpan<byte> submittedBatches,
        ReadOnlySpan<int> queueHeads,
        int finalBatchIndex,
        int submittedCount)
    {
        if (submittedBatches[batchIndex] != 0)
            return false;
        if (batchIndex == finalBatchIndex && submittedCount != compile.QueueBatches.Count - 1)
            return false;

        QueueBatch batch = compile.QueueBatches[batchIndex];
        if (queueHeads[QueueSlot(batch.Queue)] != batchIndex)
            return false;

        var links = compile.QueueWaits[batchIndex];
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
        {
            QueueLink link = compile.QueueLinks[links[linkIndex]];
            if (submittedBatches[link.SourceBatch] == 0)
                return false;
        }

        return true;
    }

    private void SubmitBatch(
        IDevice device,
        GraphQueues queues,
        CompiledGraph compile,
        int batchIndex,
        CommandBufferHandle buffer,
        Span<FencePoint> queuePoints,
        Span<FencePoint> batchPoints,
        bool signalFrame,
        bool waitFrame,
        FenceHandle frameFence,
        ulong frameValue)
    {
        QueueBatch batch = compile.QueueBatches[batchIndex];
        QueueType queue = batch.Queue;
        bool needQueueSignal = !signalFrame || batch.OutputLinks != 0;
        FencePoint signal = needQueueSignal
            ? NextQueuePoint(device, queue)
            : new FencePoint(frameFence, frameValue);
        Span<CommandBufferHandle> buffers = stackalloc CommandBufferHandle[1];
        Span<QueueWait> waits = stackalloc QueueWait[QueueCount * 2];
        Span<QueueSignal> signals = stackalloc QueueSignal[2];
        buffers[0] = buffer;
        int signalCount = 0;
        if (needQueueSignal)
            signals[signalCount++] = new QueueSignal(signal.Fence, signal.Value);
        if (signalFrame)
            signals[signalCount++] = new QueueSignal(frameFence, frameValue);

        int waitCount = AddBatchWaits(compile, batchIndex, batchPoints, waits);
        if (waitFrame)
            waitCount = AddFrameWaits(queuePoints, waits, queue, compile.FrameWaits, waitCount);
        if (Profiler.NeedsCounters)
        {
            string queueName = QueueTypeName(queue);
            Profiler.QueueWait(queueName, waitCount);
            Profiler.QueueSignal(queueName, signalCount);
        }

        queues.Get(queue).Submit(
            buffers,
            waits[..waitCount],
            signals[..signalCount]);
        queuePoints[QueueSlot(queue)] = signal;
        batchPoints[batchIndex] = signal;
    }

    private static bool CanBatchSignal(CompiledGraph compile, bool finalWork)
    {
        if (compile.QueueBatches.Count == 0 || finalWork)
            return false;

        QueueType queue = compile.QueueBatches[0].Queue;
        for (int batchIndex = 1; batchIndex < compile.QueueBatches.Count; batchIndex++)
        {
            if (compile.QueueBatches[batchIndex].Queue != queue)
                return false;
        }

        return true;
    }

    private static bool CanRecordFinalInBatch(CompiledGraph compile)
    {
        if (compile.QueueBatches.Count == 0)
            return false;

        return FinalQueue(compile) == compile.QueueBatches[^1].Queue;
    }

    private static QueueType FinalQueue(CompiledGraph compile)
    {
        if (compile.QueueBatches.Count == 0)
            return QueueType.Graphics;

        QueueType queue = compile.QueueBatches[^1].Queue;
        if (queue == QueueType.Compute && CanComputeFinish(compile))
            return QueueType.Compute;
        if (queue == QueueType.Copy && CanCopyFinish(compile))
            return queue;

        return QueueType.Graphics;
    }

    private static bool CanComputeFinish(CompiledGraph compile)
    {
        if (compile.FinalTransitions.HasChecks)
            return false;

        IReadOnlyList<ResourceTransition> requiredTransitions = compile.FinalTransitions.RequiredTransitionSource;
        for (int transitionIndex = 0; transitionIndex < requiredTransitions.Count; transitionIndex++)
        {
            ResourceTransition transition = requiredTransitions[transitionIndex];
            if (!IsComputeState(transition.Before) || !IsComputeState(transition.After))
                return false;
        }

        return true;
    }

    private static bool CanCopyFinish(CompiledGraph compile)
    {
        if (compile.FinalTransitions.HasAliases)
            return false;
        if (compile.FinalTransitions.HasChecks)
            return false;

        IReadOnlyList<ResourceTransition> requiredTransitions = compile.FinalTransitions.RequiredTransitionSource;
        for (int transitionIndex = 0; transitionIndex < requiredTransitions.Count; transitionIndex++)
        {
            ResourceTransition transition = requiredTransitions[transitionIndex];
            if (!IsCopyState(transition.Before) || !IsCopyState(transition.After))
                return false;
        }

        return true;
    }

    private static bool IsComputeState(ResourceState state)
        => state is ResourceState.Undefined
            or ResourceState.Common
            or ResourceState.GenericRead
            or ResourceState.ShaderResource
            or ResourceState.UnorderedAccess
            or ResourceState.CopySource
            or ResourceState.CopyDestination
            or ResourceState.IndirectArgument
            or ResourceState.QueryResolve;

    private static bool IsCopyState(ResourceState state)
        => state is ResourceState.Undefined
            or ResourceState.Common
            or ResourceState.GenericRead
            or ResourceState.CopySource
            or ResourceState.CopyDestination;

    private void SubmitFinal(
        IDevice device,
        GraphQueues queues,
        CompiledGraph compile,
        CommandBufferHandle buffer,
        ReadOnlySpan<FencePoint> points,
        QueueType queue,
        out FenceHandle frameFence,
        out ulong frameValue)
    {
        frameFence = GetFrameFence(device);
        frameValue = checked(++_executor.FrameFenceValue);
        Span<CommandBufferHandle> buffers = stackalloc CommandBufferHandle[1];
        Span<QueueWait> waits = stackalloc QueueWait[2];
        Span<QueueSignal> signals = stackalloc QueueSignal[1];
        buffers[0] = buffer;
        signals[0] = new QueueSignal(frameFence, frameValue);
        int waitCount = AddFrameWaits(points, waits, queue, compile.FrameWaits);
        if (Profiler.NeedsCounters)
        {
            string queueName = QueueTypeName(queue);
            Profiler.QueueWait(queueName, waitCount);
            Profiler.QueueSignal(queueName);
        }

        queues.Get(queue).Submit(
            buffers,
            waits[..waitCount],
            signals);
    }

    private void SubmitFrame(
        IDevice device,
        GraphQueues queues,
        CompiledGraph compile,
        ReadOnlySpan<FencePoint> points,
        out FenceHandle frameFence,
        out ulong frameValue)
    {
        QueueType queue = compile.FrameQueue;
        frameFence = GetFrameFence(device);
        frameValue = checked(++_executor.FrameFenceValue);
        Span<QueueWait> waits = stackalloc QueueWait[2];
        Span<QueueSignal> signals = stackalloc QueueSignal[1];
        signals[0] = new QueueSignal(frameFence, frameValue);
        int waitCount = AddFrameWaits(points, waits, queue, compile.FrameWaits);
        if (Profiler.NeedsCounters)
        {
            string queueName = QueueTypeName(queue);
            Profiler.QueueWait(queueName, waitCount);
            Profiler.QueueSignal(queueName);
        }

        queues.Get(queue).Submit(
            default,
            waits[..waitCount],
            signals);
    }

    private int AddBatchWaits(
        CompiledGraph compile,
        int batchIndex,
        ReadOnlySpan<FencePoint> batchPoints,
        Span<QueueWait> waits)
    {
        QueueBatch batch = compile.QueueBatches[batchIndex];
        if (batch.WaitQueues == QueueMask.None)
            return 0;

        int count = 0;
        var links = compile.QueueWaits[batchIndex];
        for (int linkIndex = 0; linkIndex < links.Count; linkIndex++)
        {
            var link = compile.QueueLinks[links[linkIndex]];
            var point = batchPoints[link.SourceBatch];
            if (!point.Fence.IsValid || point.Value == 0)
                throw new InvalidOperationException("RenderGraph queue link references a source batch that has not been submitted.");

            bool found = false;
            for (int waitIndex = 0; waitIndex < count; waitIndex++)
            {
                if (waits[waitIndex].Fence == point.Fence)
                {
                    if (point.Value > waits[waitIndex].Value)
                        waits[waitIndex] = new QueueWait(point.Fence, point.Value);
                    found = true;
                    break;
                }
            }

            if (!found)
                waits[count++] = new QueueWait(point.Fence, point.Value);
        }

        return count;
    }

    private static int AddFrameWaits(
        ReadOnlySpan<FencePoint> points,
        Span<QueueWait> waits,
        QueueType queue,
        QueueMask waitQueues,
        int count = 0)
    {
        AddFrameWait(points, waits, QueueType.Graphics, queue, waitQueues, ref count);
        AddFrameWait(points, waits, QueueType.Compute, queue, waitQueues, ref count);
        AddFrameWait(points, waits, QueueType.Copy, queue, waitQueues, ref count);
        return count;
    }

    private static void AddFrameWait(
        ReadOnlySpan<FencePoint> points,
        Span<QueueWait> waits,
        QueueType queue,
        QueueType target,
        QueueMask waitQueues,
        ref int count)
    {
        if (queue == target)
            return;
        if ((waitQueues & QueueBit(queue)) == 0)
            return;

        var point = points[QueueSlot(queue)];
        if (!point.Fence.IsValid || point.Value == 0)
        {
            throw new InvalidOperationException(
                $"RenderGraph frame wait for {queue} queue has no submitted signal point.");
        }

        for (int waitIndex = 0; waitIndex < count; waitIndex++)
        {
            if (waits[waitIndex].Fence != point.Fence)
                continue;

            if (point.Value > waits[waitIndex].Value)
                waits[waitIndex] = new QueueWait(point.Fence, point.Value);
            return;
        }

        waits[count++] = new QueueWait(point.Fence, point.Value);
    }

    private FencePoint NextQueuePoint(IDevice device, QueueType queue)
    {
        int index = QueueSlot(queue);
        FenceHandle fence = _executor.QueueFences[index];
        if (!fence.IsValid)
        {
            fence = device.CreateFence($"RenderGraph {queue} Queue");
            _executor.QueueFences[index] = fence;
            _executor.QueueFenceValues[index] = 0;
        }

        ulong value = checked(++_executor.QueueFenceValues[index]);
        return new FencePoint(fence, value);
    }

    private static int QueueSlot(QueueType queue)
        => queue switch
        {
            QueueType.Graphics => 0,
            QueueType.Compute => 1,
            QueueType.Copy => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(queue), queue, "RenderGraph does not support this queue type."),
        };

    private static string QueueTypeName(QueueType queue)
        => queue switch
        {
            QueueType.Graphics => "Graphics",
            QueueType.Compute => "Compute",
            QueueType.Copy => "Copy",
            _ => "Unknown",
        };

    private void ReportQueues(CompiledGraph compile, bool finalWork)
    {
        if (!Profiler.NeedsCounters)
            return;

        for (int batchIndex = 0; batchIndex < compile.QueueBatches.Count; batchIndex++)
        {
            QueueBatch batch = compile.QueueBatches[batchIndex];
            Profiler.QueueBatch(QueueTypeName(batch.Queue));
        }

        for (int linkIndex = 0; linkIndex < compile.QueueLinks.Count; linkIndex++)
        {
            QueueLink link = compile.QueueLinks[linkIndex];
            Profiler.QueueLink(QueueTypeName(link.SourceQueue), QueueTypeName(link.TargetQueue));
        }

        if (finalWork)
            Profiler.QueueFinal(QueueTypeName(FinalQueue(compile)));
    }

    private static int TextureStateIndex(TextureDesc desc, uint mip, uint slice)
        => checked((int)(slice * desc.MipLevels + mip));
}
