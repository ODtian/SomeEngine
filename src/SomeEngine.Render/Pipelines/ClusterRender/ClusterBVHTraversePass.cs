using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal static class ClusterBVHTraversePSOs
{
    internal const string ShaderFile = "cluster_bvh_traverse.slang";
    internal const string TraverseEntryPoint = "main";
    internal const string UpdateArgsEntryPoint = "UpdateArgs";
    internal const string InitArgsEntryPoint = "InitArgs";
    internal const string InitQueueEntryPoint = "InitQueue";

    internal static IPipelineState? TraversePSO;
    internal static IPipelineState? UpdateArgsPSO;
    internal static IPipelineState? InitArgsPSO;
    internal static IPipelineState? InitQueuePSO;
    internal static ShaderAsset? ShaderAsset;

    internal static IShaderResourceBinding? TraverseSRB;
    internal static IShaderResourceBinding? UpdateArgsSRB;
    internal static IShaderResourceBinding? InitArgsSRB;
    internal static IShaderResourceBinding? InitQueueSRB;

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    internal static void EnsureInitialized(RenderContext context)
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;

            var device = context.Device;
            if (device == null) return;

            string shaderPath = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(shaderPath);
            ShaderAsset = shaderAsset;

            var csTraverse = shaderAsset.CreateShader(context, TraverseEntryPoint);
            TraversePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "BVH Traverse PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                        Variables = [
                            new ShaderResourceVariableDesc
                            {
                                ShaderStages = ShaderType.Compute,
                                Name = "DepthIndexCB",
                                Type = ShaderResourceVariableType.Mutable,
                                Flags = ShaderVariableFlags.InlineConstants,
                            }
                        ],
                    },
                },
                Cs = csTraverse,
            });

            var csUpdateArgs = shaderAsset.CreateShader(context, UpdateArgsEntryPoint);
            UpdateArgsPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "BVH Update Args PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Dynamic },
                },
                Cs = csUpdateArgs,
            });

            var csInitArgs = shaderAsset.CreateShader(context, InitArgsEntryPoint);
            InitArgsPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "BVH Init Args PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Dynamic },
                },
                Cs = csInitArgs,
            });

            var csInitQueue = shaderAsset.CreateShader(context, InitQueueEntryPoint);
            InitQueuePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "BVH Init Queue PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Dynamic },
                },
                Cs = csInitQueue,
            });

            TraverseSRB = TraversePSO?.CreateShaderResourceBinding(false);
            UpdateArgsSRB = UpdateArgsPSO?.CreateShaderResourceBinding(false);
            InitArgsSRB = InitArgsPSO?.CreateShaderResourceBinding(false);
            InitQueueSRB = InitQueuePSO?.CreateShaderResourceBinding(false);

            s_initialized = true;
        }
    }
}

public class ClusterBVHTraversePass(
    RenderContext context,
    ClusterResourceManager clusterManager,
    InstanceDataManager transformSystem,
    Action<uint[]>? onPageFaultReadback = null
) : IRenderGraphPass, IDisposable
{
    public string Name => "BVH Traverse";

    public RenderGraphHandle HQueueA = RenderGraphHandle.Invalid,
        HQueueB = RenderGraphHandle.Invalid,
        HArgsA = RenderGraphHandle.Invalid,
        HArgsB = RenderGraphHandle.Invalid,
        HReadbackBuffer = RenderGraphHandle.Invalid;

    private readonly Queue<(uint Offset, uint Size, Action<uint[]> Callback)> _pendingReadbacks = new();
    private bool _pendingPageFaultReadback;

    public RenderGraphHandle HCandidateClusters = RenderGraphHandle.Invalid,
        HCandidateArgs = RenderGraphHandle.Invalid,
        HCandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HIndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCullingUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageFaultBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageFaultReadbackBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid,
        HGlobalInstanceHeaderBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalBVHBuffer = RenderGraphHandle.Invalid,
        HPageHeap = RenderGraphHandle.Invalid;

    // Frame data
    private Matrix4x4 _view, _proj;
    private Vector3 _cameraPos;
    private float _lodThreshold, _lodScale;
    private int _forcedLODLevel;
    private bool _bypassCulling, _hasPrevHistory;
    private Matrix4x4 _prevViewProjT = Matrix4x4.Identity;
    private uint _hizMipCount;
    private Vector2 _hizInvSize = Vector2.Zero;
    private int _instanceCount;
    private int _maxDepth;

    public void SetFrameData(
        Matrix4x4 view, Matrix4x4 proj, Vector3 camPos, float lodThreshold, float lodScale,
        int forcedLOD, bool bypass, Matrix4x4 prevViewProjT, bool hasPrevHistory,
        uint hizMipCount, Vector2 hizInvSize)
    {
        _view = view; _proj = proj; _cameraPos = camPos; _lodThreshold = lodThreshold;
        _lodScale = lodScale; _forcedLODLevel = forcedLOD; _bypassCulling = bypass;
        _prevViewProjT = prevViewProjT; _hasPrevHistory = hasPrevHistory;
        _hizMipCount = hizMipCount; _hizInvSize = hizInvSize;
    }

    public void Init() => ClusterBVHTraversePSOs.EnsureInitialized(context);

    public void SetExecutionConfig(int instanceCount, int maxDepth)
    {
        _instanceCount = instanceCount;
        _maxDepth = maxDepth;
    }

    public void Setup(RenderGraphBuilder builder)
    {
        bool hasInstances = _instanceCount > 0;
        var argsExitState = hasInstances ? ResourceState.CopySource : ResourceState.UnorderedAccess;

        builder.Use(
            HArgsA,
            ResourceState.UnorderedAccess,
            argsExitState,
            RenderGraphAccess.ReadWrite);
        builder.Use(
            HArgsB,
            ResourceState.UnorderedAccess,
            argsExitState,
            RenderGraphAccess.ReadWrite);

        if (!hasInstances)
            return;

        builder.Use(
            HQueueA,
            ResourceState.UnorderedAccess,
            ResourceState.UnorderedAccess,
            RenderGraphAccess.ReadWrite);
        builder.Use(
            HQueueB,
            ResourceState.UnorderedAccess,
            ResourceState.UnorderedAccess,
            RenderGraphAccess.ReadWrite);
        builder.Use(
            HCandidateClusters,
            ResourceState.UnorderedAccess,
            ResourceState.UnorderedAccess,
            RenderGraphAccess.Write);
        builder.Use(
            HCandidateCount,
            ResourceState.UnorderedAccess,
            ResourceState.CopySource,
            RenderGraphAccess.ReadWrite);
        builder.Use(
            HPageFaultBuffer,
            ResourceState.UnorderedAccess,
            ResourceState.UnorderedAccess,
            RenderGraphAccess.Write);

        builder.Read(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HGlobalInstanceHeaderBuffer, ResourceState.ShaderResource);
        builder.Read(HGlobalBVHBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);

        builder.Write(HReadbackBuffer, ResourceState.CopyDest);
        if (HPageFaultReadbackBuffer.IsValid)
            builder.ReadWrite(HPageFaultReadbackBuffer, ResourceState.CopyDest);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var renderContext = rgCtx.RenderContext;

        ResourceState queueAState = ResourceState.UnorderedAccess;
        ResourceState queueBState = ResourceState.UnorderedAccess;
        ResourceState argsAState = ResourceState.UnorderedAccess;
        ResourceState argsBState = ResourceState.UnorderedAccess;
        ResourceState candidateClustersState = ResourceState.UnorderedAccess;
        ResourceState candidateCountState = ResourceState.UnorderedAccess;
        ResourceState pageFaultState = ResourceState.UnorderedAccess;

        void QueueTransition(
            RenderGraphHandle handle,
            ref ResourceState currentState,
            ResourceState nextState)
        {
            rgCtx.QueueTransition(handle, currentState, nextState);
            currentState = nextState;
        }

        void QueueUav(RenderGraphHandle handle, ref ResourceState currentState)
        {
            if (currentState != ResourceState.UnorderedAccess)
            {
                QueueTransition(handle, ref currentState, ResourceState.UnorderedAccess);
                return;
            }

            rgCtx.QueueUavBarrier(handle);
        }

        QueueUav(HArgsA, ref argsAState);
        QueueUav(HArgsB, ref argsBState);
        rgCtx.FlushTransitions();
        ExecuteInitArgsPass(renderContext, rgCtx);

        if (_instanceCount <= 0)
            return;

        if (!BindFrameResources(rgCtx))
            return;

        QueueUav(HArgsA, ref argsAState);
        rgCtx.FlushTransitions();
        ExecuteInitQueuePass(renderContext, rgCtx);

        QueueUav(HArgsA, ref argsAState);
        QueueUav(HArgsB, ref argsBState);
        rgCtx.FlushTransitions();
        ExecuteUpdateArgsPass(renderContext, rgCtx, targetIsA: true, clearIsA: false);

        bool currentIsA = true;
        for (int depth = 0; depth < _maxDepth; depth++)
        {
            bool nextIsA = !currentIsA;

            var currentQueue = currentIsA ? HQueueA : HQueueB;
            var nextQueue = currentIsA ? HQueueB : HQueueA;
            var currentArgs = currentIsA ? HArgsA : HArgsB;
            var nextArgs = currentIsA ? HArgsB : HArgsA;

            ref ResourceState currentQueueState = ref currentIsA ? ref queueAState : ref queueBState;
            ref ResourceState nextQueueState = ref currentIsA ? ref queueBState : ref queueAState;
            ref ResourceState currentArgsState = ref currentIsA ? ref argsAState : ref argsBState;
            ref ResourceState nextArgsState = ref currentIsA ? ref argsBState : ref argsAState;

            QueueTransition(currentQueue, ref currentQueueState, ResourceState.ShaderResource);
            QueueTransition(
                currentArgs,
                ref currentArgsState,
                ResourceState.ShaderResource | ResourceState.IndirectArgument);
            QueueUav(nextQueue, ref nextQueueState);
            QueueUav(nextArgs, ref nextArgsState);
            QueueUav(HCandidateClusters, ref candidateClustersState);
            QueueUav(HCandidateCount, ref candidateCountState);
            QueueUav(HPageFaultBuffer, ref pageFaultState);
            rgCtx.FlushTransitions();

            ExecuteTraversePass(renderContext, rgCtx, currentIsA, depth);

            QueueUav(nextArgs, ref nextArgsState);
            QueueTransition(currentArgs, ref currentArgsState, ResourceState.UnorderedAccess);
            rgCtx.FlushTransitions();
            ExecuteUpdateArgsPass(
                renderContext,
                rgCtx,
                targetIsA: nextIsA,
                clearIsA: currentIsA);

            currentIsA = nextIsA;
        }

        QueueTransition(
            HCandidateCount,
            ref candidateCountState,
            ResourceState.CopySource);
        QueueTransition(HArgsA, ref argsAState, ResourceState.CopySource);
        QueueTransition(HArgsB, ref argsBState, ResourceState.CopySource);
        rgCtx.FlushTransitions();

        ExecuteReadbackPass(renderContext, rgCtx);
    }

    public void ExecuteReadbackPass(RenderContext renderContext, RenderGraphContext rgCtx)
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var readback = rgCtx.GetBuffer(HReadbackBuffer);
        var pageFaultReadbackBuf = HPageFaultReadbackBuffer.IsValid ? rgCtx.GetBuffer(HPageFaultReadbackBuffer) : null;
        var candCount = rgCtx.GetBuffer(HCandidateCount);
        var argsA = rgCtx.GetBuffer(HArgsA);
        var argsB = rgCtx.GetBuffer(HArgsB);

        ProcessReadbacks(ctx, readback);
        ProcessPageFaultReadback(ctx, pageFaultReadbackBuf);

        if (readback == null || candCount == null || argsA == null || argsB == null)
            return;

        ctx.CopyBuffer(candCount, 0, ResourceStateTransitionMode.None, readback, 0, 4, ResourceStateTransitionMode.None);
        ctx.CopyBuffer(argsA, 0, ResourceStateTransitionMode.None, readback, 4, 16, ResourceStateTransitionMode.None);
        ctx.CopyBuffer(argsB, 0, ResourceStateTransitionMode.None, readback, 20, 16, ResourceStateTransitionMode.None);
    }

    public void ExecuteInitArgsPass(RenderContext renderContext, RenderGraphContext rgCtx)
    {
        var ctx = renderContext.ImmediateContext;
        var srb = ClusterBVHTraversePSOs.InitArgsSRB;
        if (ctx == null || ClusterBVHTraversePSOs.InitArgsPSO == null || srb == null) return;

        var argsA = rgCtx.GetBuffer(HArgsA);
        var argsB = rgCtx.GetBuffer(HArgsB);
        if (argsA == null || argsB == null) return;

        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "NextDispatchArgs")
            ?.Set(argsA.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "ClearDispatchArgs")
            ?.Set(argsB.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterBVHTraversePSOs.InitArgsPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
    }

    public bool BindFrameResources(RenderGraphContext rgCtx)
    {
        var cullingUB = rgCtx.GetBuffer(HCullingUniforms);
        var globalBVH = rgCtx.GetBuffer(HGlobalBVHBuffer);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var instances = rgCtx.GetBuffer(HGlobalTransformBuffer);
        var headers = rgCtx.GetBuffer(HGlobalInstanceHeaderBuffer);
        var candidates = rgCtx.GetBuffer(HCandidateClusters);
        var candidateCount = rgCtx.GetBuffer(HCandidateCount);
        var pageFault = rgCtx.GetBuffer(HPageFaultBuffer);

        if (cullingUB == null || globalBVH == null || pageHeap == null || instances == null || headers == null ||
            candidates == null || candidateCount == null || pageFault == null)
            return false;

        ClusterBVHTraversePSOs.InitQueueSRB?.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(cullingUB, SetShaderResourceFlags.None);
        ClusterBVHTraversePSOs.InitQueueSRB?.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "InstanceHeaders")
            ?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        ClusterBVHTraversePSOs.UpdateArgsSRB?.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(cullingUB, SetShaderResourceFlags.None);

        var traverse = ClusterBVHTraversePSOs.TraverseSRB;
        if (traverse == null)
            return false;

        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "GlobalBVH")
            ?.Set(globalBVH.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "PageHeap")
            ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(cullingUB, SetShaderResourceFlags.None);
        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "CandidateClusters")
            ?.Set(candidates.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "CandidateCount")
            ?.Set(candidateCount.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "PageFaultBuffer")
            ?.Set(pageFault.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "Instances")
            ?.Set(instances.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        traverse.GetVariableByReflectedBinding(context, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "InstanceHeaders")
            ?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        return true;
    }

    public void ExecuteInitQueuePass(RenderContext renderContext, RenderGraphContext rgCtx)
    {
        var srb = ClusterBVHTraversePSOs.InitQueueSRB;
        if (transformSystem.Count == 0 || ClusterBVHTraversePSOs.InitQueuePSO == null || srb == null) return;

        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var queueA = rgCtx.GetBuffer(HQueueA);
        var argsA = rgCtx.GetBuffer(HArgsA);

        if (queueA == null || argsA == null) return;

        uint groups = ((uint)transformSystem.Count + 63) / 64;

        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "Queue_Next")
            ?.Set(queueA.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "NextDispatchArgs")
            ?.Set(argsA.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterBVHTraversePSOs.InitQueuePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = groups, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
    }

    public void ExecuteTraversePass(RenderContext renderContext, RenderGraphContext rgCtx, bool currentIsA, int depth)
    {
        var srb = ClusterBVHTraversePSOs.TraverseSRB;
        if (ClusterBVHTraversePSOs.TraversePSO == null || srb == null) return;
        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var currentQueue = rgCtx.GetBuffer(currentIsA ? HQueueA : HQueueB);
        var nextQueue = rgCtx.GetBuffer(currentIsA ? HQueueB : HQueueA);
        var currentArgs = rgCtx.GetBuffer(currentIsA ? HArgsA : HArgsB);
        var nextArgs = rgCtx.GetBuffer(currentIsA ? HArgsB : HArgsA);

        if (currentQueue == null || nextQueue == null || currentArgs == null || nextArgs == null) return;

        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "Queue_Current")
            ?.Set(currentQueue.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "Queue_Next")
            ?.Set(nextQueue.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "CurrentDispatchArgs")
            ?.Set(currentArgs.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "NextDispatchArgs")
            ?.Set(nextArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        unsafe
        {
            uint d = (uint)depth;
            srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "DepthIndexCB")
                ?.SetInlineConstants(new IntPtr(&d), 0, 1);
        }

        ctx.SetPipelineState(ClusterBVHTraversePSOs.TraversePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = currentArgs,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
        });
    }

    public void ExecuteUpdateArgsPass(RenderContext renderContext, RenderGraphContext rgCtx, bool targetIsA, bool clearIsA)
    {
        var srb = ClusterBVHTraversePSOs.UpdateArgsSRB;
        if (ClusterBVHTraversePSOs.UpdateArgsPSO == null || srb == null) return;
        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var targetArgs = rgCtx.GetBuffer(targetIsA ? HArgsA : HArgsB);
        var clearArgs = rgCtx.GetBuffer(clearIsA ? HArgsA : HArgsB);
        if (targetArgs == null || clearArgs == null) return;

        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "NextDispatchArgs")
            ?.Set(targetArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(renderContext, ClusterBVHTraversePSOs.ShaderAsset, ShaderType.Compute, "ClearDispatchArgs")
            ?.Set(clearArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterBVHTraversePSOs.UpdateArgsPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });
    }

    public void SetupPageFaultCopyPass(RenderGraphBuilder builder, RenderGraphHandle hPageFaultReadback)
    {
        builder.Read(HPageFaultBuffer, ResourceState.CopySource);
        if (hPageFaultReadback.IsValid)
            builder.Write(hPageFaultReadback, ResourceState.CopyDest);
        else
            builder.Write(HReadbackBuffer, ResourceState.CopyDest);
    }

    public void ExecutePageFaultCopyPass(RenderContext renderContext, RenderGraphContext rgCtx, RenderGraphHandle hPageFaultReadback)
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var pageFaultBuffer = rgCtx.GetBuffer(HPageFaultBuffer);
        if (pageFaultBuffer == null) return;

        var readbackBuffer = hPageFaultReadback.IsValid
            ? rgCtx.GetBuffer(hPageFaultReadback)
            : rgCtx.GetBuffer(HReadbackBuffer);

        if (readbackBuffer == null) return;

        ctx.CopyBuffer(pageFaultBuffer, 0, ResourceStateTransitionMode.None,
            readbackBuffer, 0, clusterManager.PageFaultBufferSize, ResourceStateTransitionMode.None);

        _pendingPageFaultReadback = hPageFaultReadback.IsValid;
    }

    private void DispatchPageFaults(uint[] data)
    {
        if (data.Length == 0)
        {
            onPageFaultReadback?.Invoke([]);
            return;
        }

        uint faultCount = data[0];
        if (faultCount > ClusterResourceManager.MaxPageFaults)
            faultCount = ClusterResourceManager.MaxPageFaults;

        uint maxReadable = (uint)Math.Max(data.Length - 1, 0);
        if (faultCount > maxReadable)
            faultCount = maxReadable;

        var faults = new uint[faultCount];
        for (int i = 0; i < faults.Length; i++)
        {
            faults[i] = data[i + 1];
        }

        onPageFaultReadback?.Invoke(faults);
    }

    private void ProcessReadbacks(IDeviceContext ctx, IBuffer? readbackBuffer)
    {
        if (readbackBuffer == null)
            return;

        var map = ctx.MapBuffer<uint>(readbackBuffer, MapType.Read, MapFlags.DoNotWait);
        if (map.Length == 0)
            return;

        try
        {
            if (map.Length >= 9)
            {
                // [0] candidateCount, [1..4] argsA, [5..8] argsB
            }
        }
        finally
        {
            ctx.UnmapBuffer(readbackBuffer, MapType.Read);
        }
    }

    private void ProcessPageFaultReadback(IDeviceContext ctx, IBuffer? pageFaultReadbackBuffer)
    {
        if (!_pendingPageFaultReadback || pageFaultReadbackBuffer == null) return;
        var map = ctx.MapBuffer<uint>(pageFaultReadbackBuffer, MapType.Read, MapFlags.DoNotWait);
        if (map.Length == 0) return;

        try
        {
            DispatchPageFaults(map.ToArray());
            _pendingPageFaultReadback = false;
        }
        finally
        {
            ctx.UnmapBuffer(pageFaultReadbackBuffer, MapType.Read);
        }
    }

    public void Dispose() { }
}
