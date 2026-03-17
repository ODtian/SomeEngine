using System;
using System.Collections.Concurrent;
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
    internal static IPipelineState? TraversePSO;
    internal static IPipelineState? UpdateArgsPSO;
    internal static IPipelineState? ClearArgsPSO;
    internal static IPipelineState? InitQueuePSO;

    internal static readonly ConcurrentBag<IShaderResourceBinding> TraverseSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> UpdateArgsSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> ClearArgsSRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> InitQueueSRBPool = [];

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

            string shaderPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_bvh_traverse.slang"));
            var shaderAsset = SlangShaderImporter.Import(shaderPath);

            using var csTraverse = shaderAsset.CreateShader(context, "main");
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

            using var csUpdateArgs = shaderAsset.CreateShader(context, "UpdateArgs");
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

            using var csClearArgs = shaderAsset.CreateShader(context, "ClearArgs");
            ClearArgsPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "BVH Clear Args PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Dynamic },
                },
                Cs = csClearArgs,
            });

            using var csInitQueue = shaderAsset.CreateShader(context, "InitQueue");
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

            s_initialized = true;
        }
    }

    internal static IShaderResourceBinding RentSRB(IPipelineState pso, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(true); // Must be true for Traverse/Update/Clear/Init ? Wait, original code used 'true' (initStaticResources)

    internal static void ReturnSRB(IShaderResourceBinding srb, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.Add(srb);
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
    private uint _readbackOffset;
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

    public void Setup(RenderGraphBuilder builder) { }

    public void Execute(RenderGraphContext rgCtx) { }

    public void SetupReadbackPass(RenderGraphBuilder builder)
    {
        builder.Write(HReadbackBuffer, ResourceState.CopyDest);
        builder.Read(HCandidateCount, ResourceState.CopySource);
        builder.Read(HArgsA, ResourceState.CopySource);
        builder.Read(HArgsB, ResourceState.CopySource);
        if (HPageFaultReadbackBuffer.IsValid)
            builder.Read(HPageFaultReadbackBuffer, ResourceState.CopyDest);
    }

    public void ExecuteReadbackPass(RenderContext renderContext, RenderGraphContext rgCtx)
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var readback = rgCtx.GetBuffer(HReadbackBuffer);
        var candCount = rgCtx.GetBuffer(HCandidateCount);
        var argsA = rgCtx.GetBuffer(HArgsA);
        var argsB = rgCtx.GetBuffer(HArgsB);

        ProcessReadbacks(ctx, readback);
        var pageFaultReadbackBuf = HPageFaultReadbackBuffer.IsValid ? rgCtx.GetBuffer(HPageFaultReadbackBuffer) : null;
        ProcessPageFaultReadback(ctx, pageFaultReadbackBuf);
    }

    public void SetupClearArgsPass(RenderGraphBuilder builder, bool clearArgsA)
    {
        builder.Write(clearArgsA ? HArgsA : HArgsB, ResourceState.UnorderedAccess);
    }

    public void ExecuteClearArgsPass(RenderContext renderContext, RenderGraphContext rgCtx, bool clearArgsA)
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null || ClusterBVHTraversePSOs.ClearArgsPSO == null) return;

        var args = rgCtx.GetBuffer(clearArgsA ? HArgsA : HArgsB);
        if (args == null) return;

        var srb = ClusterBVHTraversePSOs.RentSRB(ClusterBVHTraversePSOs.ClearArgsPSO, ClusterBVHTraversePSOs.ClearArgsSRBPool);

        srb.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")
            ?.Set(args.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterBVHTraversePSOs.ClearArgsPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

        ClusterBVHTraversePSOs.ReturnSRB(srb, ClusterBVHTraversePSOs.ClearArgsSRBPool);
    }

    public void SetupInitQueuePass(RenderGraphBuilder builder)
    {
        builder.Read(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.Read(HGlobalInstanceHeaderBuffer, ResourceState.ShaderResource);
        builder.Write(HQueueA, ResourceState.UnorderedAccess);
        builder.Write(HArgsA, ResourceState.UnorderedAccess);
    }

    public void ExecuteInitQueuePass(RenderContext renderContext, RenderGraphContext rgCtx)
    {
        if (transformSystem.Count == 0 || ClusterBVHTraversePSOs.InitQueuePSO == null) return;

        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var queueA = rgCtx.GetBuffer(HQueueA);
        var argsA = rgCtx.GetBuffer(HArgsA);
        var cullingUB = rgCtx.GetBuffer(HCullingUniforms);
        var headers = rgCtx.GetBuffer(HGlobalInstanceHeaderBuffer);

        if (queueA == null || argsA == null || cullingUB == null || headers == null) return;

        uint groups = ((uint)transformSystem.Count + 63) / 64;
        var srb = ClusterBVHTraversePSOs.RentSRB(ClusterBVHTraversePSOs.InitQueuePSO, ClusterBVHTraversePSOs.InitQueueSRBPool);

        srb.GetVariableByName(ShaderType.Compute, "Uniforms")?.Set(cullingUB, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "InstanceHeaders")?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Queue_Next")?.Set(queueA.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")?.Set(argsA.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterBVHTraversePSOs.InitQueuePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = groups, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

        ClusterBVHTraversePSOs.ReturnSRB(srb, ClusterBVHTraversePSOs.InitQueueSRBPool);
    }

    public void SetupTraversePass(RenderGraphBuilder builder, bool currentIsA)
    {
        RenderGraphHandle currentQueue = currentIsA ? HQueueA : HQueueB;
        RenderGraphHandle nextQueue = currentIsA ? HQueueB : HQueueA;
        RenderGraphHandle currentArgs = currentIsA ? HArgsA : HArgsB;
        RenderGraphHandle nextArgs = currentIsA ? HArgsB : HArgsA;

        builder.Read(currentQueue, ResourceState.ShaderResource);
        builder.Read(currentArgs, ResourceState.ShaderResource | ResourceState.IndirectArgument);
        builder.Write(nextQueue, ResourceState.UnorderedAccess);
        builder.Write(nextArgs, ResourceState.UnorderedAccess);
        builder.Write(HCandidateClusters, ResourceState.UnorderedAccess);
        builder.Write(HCandidateCount, ResourceState.UnorderedAccess);
        builder.Write(HPageFaultBuffer, ResourceState.UnorderedAccess);

        builder.Read(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HGlobalInstanceHeaderBuffer, ResourceState.ShaderResource);
        builder.Read(HGlobalBVHBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
    }

    public void ExecuteTraversePass(RenderContext renderContext, RenderGraphContext rgCtx, bool currentIsA, int depth)
    {
        if (ClusterBVHTraversePSOs.TraversePSO == null) return;
        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var currentQueue = rgCtx.GetBuffer(currentIsA ? HQueueA : HQueueB);
        var nextQueue = rgCtx.GetBuffer(currentIsA ? HQueueB : HQueueA);
        var currentArgs = rgCtx.GetBuffer(currentIsA ? HArgsA : HArgsB);
        var nextArgs = rgCtx.GetBuffer(currentIsA ? HArgsB : HArgsA);

        var candidates = rgCtx.GetBuffer(HCandidateClusters);
        var candidateCount = rgCtx.GetBuffer(HCandidateCount);
        var pageFault = rgCtx.GetBuffer(HPageFaultBuffer);

        var cullingUB = rgCtx.GetBuffer(HCullingUniforms);
        var globalBVH = rgCtx.GetBuffer(HGlobalBVHBuffer);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var instances = rgCtx.GetBuffer(HGlobalTransformBuffer);
        var headers = rgCtx.GetBuffer(HGlobalInstanceHeaderBuffer);

        if (currentQueue == null || nextQueue == null || currentArgs == null || nextArgs == null || candidates == null ||
            candidateCount == null || pageFault == null || cullingUB == null || globalBVH == null || pageHeap == null ||
            instances == null || headers == null) return;

        var srb = ClusterBVHTraversePSOs.RentSRB(ClusterBVHTraversePSOs.TraversePSO, ClusterBVHTraversePSOs.TraverseSRBPool);

        BindTransientResources(srb, cullingUB, globalBVH, pageHeap, instances, headers, candidates,
            candidateCount, pageFault, currentQueue, nextQueue, currentArgs, nextArgs);

        var depthVar = srb.GetVariableByName(ShaderType.Compute, "DepthIndexCB");
        unsafe
        {
            uint d = (uint)depth;
            depthVar?.SetInlineConstants(new IntPtr(&d), 0, 1);
        }

        ctx.SetPipelineState(ClusterBVHTraversePSOs.TraversePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
        {
            AttribsBuffer = currentArgs,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
        });

        ClusterBVHTraversePSOs.ReturnSRB(srb, ClusterBVHTraversePSOs.TraverseSRBPool);
    }

    public void SetupUpdateArgsPass(RenderGraphBuilder builder, bool targetIsA)
    {
        builder.Write(targetIsA ? HArgsA : HArgsB, ResourceState.UnorderedAccess);
    }

    public void ExecuteUpdateArgsPass(RenderContext renderContext, RenderGraphContext rgCtx, bool targetIsA)
    {
        if (ClusterBVHTraversePSOs.UpdateArgsPSO == null) return;
        var ctx = renderContext.ImmediateContext;
        if (ctx == null) return;

        var targetArgs = rgCtx.GetBuffer(targetIsA ? HArgsA : HArgsB);
        if (targetArgs == null) return;

        var srb = ClusterBVHTraversePSOs.RentSRB(ClusterBVHTraversePSOs.UpdateArgsPSO, ClusterBVHTraversePSOs.UpdateArgsSRBPool);

        srb.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")
            ?.Set(targetArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterBVHTraversePSOs.UpdateArgsPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

        ClusterBVHTraversePSOs.ReturnSRB(srb, ClusterBVHTraversePSOs.UpdateArgsSRBPool);
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

        ctx.CopyBuffer(pageFaultBuffer, 0, ResourceStateTransitionMode.Verify,
            readbackBuffer, 0, clusterManager.PageFaultBufferSize, ResourceStateTransitionMode.Verify);

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

    private void BindTransientResources(
        IShaderResourceBinding srb, IBuffer cullingUB, IBuffer globalBVH, IBuffer pageHeap, IBuffer instances,
        IBuffer headers, IBuffer candidates, IBuffer candCount, IBuffer pageFaultBuffer, IBuffer queueCurrent,
        IBuffer queueNext, IBuffer argsCurrent, IBuffer argsNext)
    {
        srb.GetVariableByName(ShaderType.Compute, "GlobalBVH")?.Set(globalBVH.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageHeap")?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Uniforms")?.Set(cullingUB, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CandidateClusters")?.Set(candidates.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CandidateCount")?.Set(candCount.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageFaultBuffer")?.Set(pageFaultBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Queue_Current")?.Set(queueCurrent.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Queue_Next")?.Set(queueNext.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CurrentDispatchArgs")?.Set(argsCurrent.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")?.Set(argsNext.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Instances")?.Set(instances.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "InstanceHeaders")?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
    }

    private void ProcessReadbacks(IDeviceContext ctx, IBuffer? readbackBuffer)
    {
        if (readbackBuffer == null || _pendingReadbacks.Count == 0) return;
        var map = ctx.MapBuffer<uint>(readbackBuffer, MapType.Read, MapFlags.DoNotWait);
        if (map.Length == 0) return;
        try
        {
            while (_pendingReadbacks.Count > 0)
            {
                var (off, size, cb) = _pendingReadbacks.Peek();
                int idx = (int)(off / 4);
                int words = (int)(size / 4);
                if (idx + words <= map.Length) cb(map.Slice(idx, words).ToArray());
                _pendingReadbacks.Dequeue();
            }
        }
        finally
        {
            ctx.UnmapBuffer(readbackBuffer, MapType.Read);
        }
        if (_pendingReadbacks.Count == 0) _readbackOffset = 0;
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
