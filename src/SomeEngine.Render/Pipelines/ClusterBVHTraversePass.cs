using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterBVHTraversePass(
    RenderContext context,
    ClusterResourceManager clusterManager,
    InstanceSyncSystem transformSystem,
    Action<uint[]>? onPageFaultReadback = null
) : RenderPass("ClusterBVHTraverse"), IDisposable
{
    private ShaderAsset? _shaderAsset;
    private IPipelineState? _bvhTraversePSO;
    private IShaderResourceBinding? _bvhTraverseSRB_A;
    private IShaderResourceBinding? _bvhTraverseSRB_B;
    private IPipelineState? _bvhUpdateArgsPSO;
    private IShaderResourceBinding? _bvhUpdateArgsSRB_A;
    private IShaderResourceBinding? _bvhUpdateArgsSRB_B;
    private IPipelineState? _clearArgsPSO;
    private IShaderResourceBinding? _clearArgsSRB_A;
    private IShaderResourceBinding? _clearArgsSRB_B;
    private IPipelineState? _initQueuePSO;
    private IShaderResourceBinding? _initQueueSRB;

    public RGResourceHandle HQueueA = RGResourceHandle.Invalid,
        HQueueB = RGResourceHandle.Invalid,
        HArgsA = RGResourceHandle.Invalid,
        HArgsB = RGResourceHandle.Invalid,
        HReadbackBuffer = RGResourceHandle.Invalid;
    private bool _initialized;

    private readonly Queue<(uint Offset, uint Size, Action<uint[]> Callback)> _pendingReadbacks =
        new();
    private uint _readbackOffset;
    private bool _pendingPageFaultReadback;

    // RenderGraph handles set by orchestrator
    public RGResourceHandle HCandidateClusters = RGResourceHandle.Invalid,
        HCandidateArgs = RGResourceHandle.Invalid,
        HCandidateCount = RGResourceHandle.Invalid;
    public RGResourceHandle HIndirectDrawArgs = RGResourceHandle.Invalid,
        HBvhDebugBuffer = RGResourceHandle.Invalid,
        HBvhDebugCountBuffer = RGResourceHandle.Invalid;
    public RGResourceHandle HCullingUniforms = RGResourceHandle.Invalid;
    public RGResourceHandle HPageFaultBuffer = RGResourceHandle.Invalid;
    public RGResourceHandle HGlobalTransformBuffer = RGResourceHandle.Invalid,
        HGlobalInstanceHeaderBuffer = RGResourceHandle.Invalid;
    public RGResourceHandle HGlobalBVHBuffer = RGResourceHandle.Invalid,
        HPageHeap = RGResourceHandle.Invalid;

    public uint[] DebugBVHGroupCount { get; } = new uint[32];
    public uint[] DebugBVHItemCount { get; } = new uint[32];

    // Frame data
    private Matrix4x4 _view,
        _proj;
    private Vector3 _cameraPos;
    private float _lodThreshold,
        _lodScale;
    private int _forcedLODLevel,
        _debugBVHDepth;
    private bool _bypassCulling,
        _visualiseBVH,
        _hasPrevHistory;
    private Matrix4x4 _prevViewProjT = Matrix4x4.Identity;
    private uint _hizMipCount;
    private Vector2 _hizInvSize = Vector2.Zero;
    private IBuffer? _cullingUniformBuffer;

    public void SetFrameData(
        IBuffer cullingUB,
        Matrix4x4 view,
        Matrix4x4 proj,
        Vector3 camPos,
        float lodThreshold,
        float lodScale,
        int forcedLOD,
        bool bypass,
        bool visBVH,
        int debugDepth,
        Matrix4x4 prevViewProjT,
        bool hasPrevHistory,
        uint hizMipCount,
        Vector2 hizInvSize
    )
    {
        _cullingUniformBuffer = cullingUB;
        _view = view;
        _proj = proj;
        _cameraPos = camPos;
        _lodThreshold = lodThreshold;
        _lodScale = lodScale;
        _forcedLODLevel = forcedLOD;
        _bypassCulling = bypass;
        _visualiseBVH = visBVH;
        _debugBVHDepth = debugDepth;
        _prevViewProjT = prevViewProjT;
        _hasPrevHistory = hasPrevHistory;
        _hizMipCount = hizMipCount;
        _hizInvSize = hizInvSize;
    }

    public void Init()
    {
        if (_initialized)
            return;
        var device = context.Device;
        if (device == null)
            return;

        InitPSOs(device);
        _initialized = true;
    }

    private void InitPSOs(IRenderDevice device)
    {
        string shaderPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_bvh_traverse.slang"
            )
        );
        _shaderAsset = SlangShaderImporter.Import(shaderPath);

        using var cs = _shaderAsset.CreateShader(context, "main");

        var psoCi = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "BVH Traverse PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    Variables =
                    [
                        new ShaderResourceVariableDesc
                        {
                            ShaderStages = ShaderType.Compute,
                            Name = "DepthIndexCB",
                            Type = ShaderResourceVariableType.Mutable,
                            Flags = ShaderVariableFlags.InlineConstants,
                        },
                    ],
                },
            },
            Cs = cs,
        };

        _bvhTraversePSO = device.CreateComputePipelineState(psoCi);
        if (_bvhTraversePSO == null)
            return;

        _bvhTraverseSRB_A = _bvhTraversePSO.CreateShaderResourceBinding(true);
        _bvhTraverseSRB_B = _bvhTraversePSO.CreateShaderResourceBinding(true);

        // Update Args PSO
        using var upCs = _shaderAsset.CreateShader(context, "UpdateArgs");
        var upCi = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "BVH Update Args PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    Variables = [],
                },
            },
            Cs = upCs,
        };

        _bvhUpdateArgsPSO = device.CreateComputePipelineState(upCi);
        if (_bvhUpdateArgsPSO != null)
        {
            _bvhUpdateArgsSRB_A = _bvhUpdateArgsPSO.CreateShaderResourceBinding(true);
            _bvhUpdateArgsSRB_B = _bvhUpdateArgsPSO.CreateShaderResourceBinding(true);
        }

        // Clear Args PSO
        using var clearCs = _shaderAsset.CreateShader(context, "ClearArgs");
        var clearCi = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "BVH Clear Args PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    Variables = [],
                },
            },
            Cs = clearCs,
        };

        _clearArgsPSO = device.CreateComputePipelineState(clearCi);
        if (_clearArgsPSO != null)
        {
            _clearArgsSRB_A = _clearArgsPSO.CreateShaderResourceBinding(true);
            _clearArgsSRB_B = _clearArgsPSO.CreateShaderResourceBinding(true);
        }

        // Init Queue PSO
        using var initCs = _shaderAsset.CreateShader(context, "InitQueue");
        var initCi = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "BVH Init Queue PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    Variables = [],
                },
            },
            Cs = initCs,
        };

        _initQueuePSO = device.CreateComputePipelineState(initCi);
        if (_initQueuePSO != null)
        {
            _initQueueSRB = _initQueuePSO.CreateShaderResourceBinding(true);
        }
    }

    public override void Setup(RenderGraphBuilder builder) { }

    public override void Execute(RenderContext context, RenderGraphContext rgCtx) { }

    public void SetupReadbackPass(RenderGraphBuilder builder)
    {
        builder.WriteBuffer(HReadbackBuffer, ResourceState.CopyDest);
    }

    public void ExecuteReadbackPass(RenderContext renderContext, RenderGraphContext rgCtx)
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null)
            return;

        ProcessReadbacks(ctx, rgCtx.GetBuffer(HReadbackBuffer));
        ProcessPageFaultReadback(ctx);
    }

    public void SetupClearArgsPass(RenderGraphBuilder builder, bool clearArgsA)
    {
        builder.WriteBuffer(clearArgsA ? HArgsA : HArgsB, ResourceState.UnorderedAccess);
    }

    public void ExecuteClearArgsPass(RenderContext renderContext, RenderGraphContext rgCtx, bool clearArgsA)
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null || _clearArgsPSO == null)
            return;

        var args = rgCtx.GetBuffer(clearArgsA ? HArgsA : HArgsB);
        var srb = clearArgsA ? _clearArgsSRB_A : _clearArgsSRB_B;
        if (args == null || srb == null)
            return;

        srb.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")
            ?.Set(args.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(_clearArgsPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = 1,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void SetupInitQueuePass(RenderGraphBuilder builder)
    {
        builder.ReadBuffer(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.ReadBuffer(HGlobalInstanceHeaderBuffer, ResourceState.ShaderResource);
        builder.WriteBuffer(HQueueA, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HArgsA, ResourceState.UnorderedAccess);
    }

    public void ExecuteInitQueuePass(RenderContext renderContext, RenderGraphContext rgCtx)
    {
        if (transformSystem.Count == 0 || _initQueuePSO == null || _initQueueSRB == null)
            return;

        var ctx = renderContext.ImmediateContext;
        if (ctx == null)
            return;

        var queueA = rgCtx.GetBuffer(HQueueA);
        var argsA = rgCtx.GetBuffer(HArgsA);
        var cullingUB = rgCtx.GetBuffer(HCullingUniforms);
        var headers = rgCtx.GetBuffer(HGlobalInstanceHeaderBuffer);

        if (queueA == null || argsA == null || cullingUB == null || headers == null)
            return;

        uint groups = ((uint)transformSystem.Count + 63) / 64;

        _initQueueSRB
            .GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(cullingUB, SetShaderResourceFlags.None);
        _initQueueSRB
            .GetVariableByName(ShaderType.Compute, "InstanceHeaders")
            ?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        _initQueueSRB
            .GetVariableByName(ShaderType.Compute, "Queue_Next")
            ?.Set(queueA.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        _initQueueSRB
            .GetVariableByName(ShaderType.Compute, "NextDispatchArgs")
            ?.Set(argsA.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(_initQueuePSO);
        ctx.CommitShaderResources(_initQueueSRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = groups,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void SetupTraversePass(RenderGraphBuilder builder, bool currentIsA)
    {
        RGResourceHandle currentQueue = currentIsA ? HQueueA : HQueueB;
        RGResourceHandle nextQueue = currentIsA ? HQueueB : HQueueA;
        RGResourceHandle currentArgs = currentIsA ? HArgsA : HArgsB;
        RGResourceHandle nextArgs = currentIsA ? HArgsB : HArgsA;

        builder.ReadBuffer(currentQueue, ResourceState.ShaderResource);
        builder.ReadBuffer(currentArgs, ResourceState.ShaderResource | ResourceState.IndirectArgument);

        builder.WriteBuffer(nextQueue, ResourceState.UnorderedAccess);
        builder.WriteBuffer(nextArgs, ResourceState.UnorderedAccess);

        builder.WriteBuffer(HCandidateClusters, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HCandidateCount, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HBvhDebugBuffer, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HBvhDebugCountBuffer, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HPageFaultBuffer, ResourceState.UnorderedAccess);

        builder.ReadBuffer(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.ReadBuffer(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.ReadBuffer(HGlobalInstanceHeaderBuffer, ResourceState.ShaderResource);
        builder.ReadBuffer(HGlobalBVHBuffer, ResourceState.ShaderResource);
        builder.ReadBuffer(HPageHeap, ResourceState.ShaderResource);
    }

    public void ExecuteTraversePass(
        RenderContext renderContext,
        RenderGraphContext rgCtx,
        bool currentIsA,
        int depth
    )
    {
        if (_bvhTraversePSO == null)
            return;

        var ctx = renderContext.ImmediateContext;
        if (ctx == null)
            return;

        IShaderResourceBinding? srb = currentIsA ? _bvhTraverseSRB_A : _bvhTraverseSRB_B;
        if (srb == null)
            return;

        var currentQueue = rgCtx.GetBuffer(currentIsA ? HQueueA : HQueueB);
        var nextQueue = rgCtx.GetBuffer(currentIsA ? HQueueB : HQueueA);
        var currentArgs = rgCtx.GetBuffer(currentIsA ? HArgsA : HArgsB);
        var nextArgs = rgCtx.GetBuffer(currentIsA ? HArgsB : HArgsA);

        var candidates = rgCtx.GetBuffer(HCandidateClusters);
        var candidateCount = rgCtx.GetBuffer(HCandidateCount);
        var debug = rgCtx.GetBuffer(HBvhDebugBuffer);
        var debugCount = rgCtx.GetBuffer(HBvhDebugCountBuffer);
        var pageFault = rgCtx.GetBuffer(HPageFaultBuffer);

        var cullingUB = rgCtx.GetBuffer(HCullingUniforms);
        var globalBVH = rgCtx.GetBuffer(HGlobalBVHBuffer);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var instances = rgCtx.GetBuffer(HGlobalTransformBuffer);
        var headers = rgCtx.GetBuffer(HGlobalInstanceHeaderBuffer);

        if (
            currentQueue == null
            || nextQueue == null
            || currentArgs == null
            || nextArgs == null
            || candidates == null
            || candidateCount == null
            || pageFault == null
            || cullingUB == null
            || globalBVH == null
            || pageHeap == null
            || instances == null
            || headers == null
        )
            return;

        BindTransientResources(
            srb,
            cullingUB,
            globalBVH,
            pageHeap,
            instances,
            headers,
            candidates,
            candidateCount,
            debug,
            debugCount,
            pageFault,
            currentQueue,
            nextQueue,
            currentArgs,
            nextArgs
        );

        var depthVar = srb.GetVariableByName(ShaderType.Compute, "DepthIndexCB");
        unsafe
        {
            uint d = (uint)depth;
            depthVar?.SetInlineConstants(new IntPtr(&d), 0, 1);
        }

        ctx.SetPipelineState(_bvhTraversePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchComputeIndirect(
            new DispatchComputeIndirectAttribs
            {
                AttribsBuffer = currentArgs,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
            }
        );
    }

    public void SetupUpdateArgsPass(RenderGraphBuilder builder, bool targetIsA)
    {
        builder.WriteBuffer(targetIsA ? HArgsA : HArgsB, ResourceState.UnorderedAccess);
    }

    public void ExecuteUpdateArgsPass(RenderContext renderContext, RenderGraphContext rgCtx, bool targetIsA)
    {
        if (_bvhUpdateArgsPSO == null)
            return;

        var ctx = renderContext.ImmediateContext;
        if (ctx == null)
            return;

        var targetArgs = rgCtx.GetBuffer(targetIsA ? HArgsA : HArgsB);
        var srb = targetIsA ? _bvhUpdateArgsSRB_A : _bvhUpdateArgsSRB_B;
        if (targetArgs == null || srb == null)
            return;

        srb.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")
            ?.Set(targetArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(_bvhUpdateArgsPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = 1,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void SetupArgsReadbackPass(RenderGraphBuilder builder, bool argsA)
    {
        builder.ReadBuffer(argsA ? HArgsA : HArgsB, ResourceState.CopySource);
        builder.WriteBuffer(HReadbackBuffer, ResourceState.CopyDest);
    }

    public void ExecuteArgsReadbackPass(
        RenderContext renderContext,
        RenderGraphContext rgCtx,
        bool argsA,
        int depth
    )
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null)
            return;

        var readbackBuffer = rgCtx.GetBuffer(HReadbackBuffer);
        var args = rgCtx.GetBuffer(argsA ? HArgsA : HArgsB);
        if (readbackBuffer == null || args == null)
            return;

        EnqueueReadback(
            ctx,
            readbackBuffer,
            args,
            0,
            16,
            d =>
            {
                DebugBVHGroupCount[depth] = d.Length > 0 ? d[0] : 0;
                DebugBVHItemCount[depth] = d.Length > 3 ? d[3] : 0;
            }
        );
    }

    public void SetupPageFaultCopyPass(RenderGraphBuilder builder, RGResourceHandle hPageFaultReadback)
    {
        builder.ReadBuffer(HPageFaultBuffer, ResourceState.CopySource);
        if (hPageFaultReadback.IsValid)
            builder.WriteBuffer(hPageFaultReadback, ResourceState.CopyDest);
        else
            builder.WriteBuffer(HReadbackBuffer, ResourceState.CopyDest);
    }

    public void ExecutePageFaultCopyPass(
        RenderContext renderContext,
        RenderGraphContext rgCtx,
        RGResourceHandle hPageFaultReadback
    )
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null)
            return;

        var pageFaultBuffer = rgCtx.GetBuffer(HPageFaultBuffer);
        if (pageFaultBuffer == null)
            return;

        var readbackBuffer = hPageFaultReadback.IsValid
            ? rgCtx.GetBuffer(hPageFaultReadback)
            : rgCtx.GetBuffer(HReadbackBuffer);

        if (readbackBuffer == null)
            return;

        ctx.CopyBuffer(
            pageFaultBuffer,
            0,
            ResourceStateTransitionMode.Verify,
            readbackBuffer,
            0,
            clusterManager.PageFaultBufferSize,
            ResourceStateTransitionMode.Verify
        );

        if (hPageFaultReadback.IsValid)
            _pendingPageFaultReadback = true;
        else
            _pendingPageFaultReadback = false; // Will be handled by EnqueueReadback if needed, but here we just copy.
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
        IShaderResourceBinding srb,
        IBuffer cullingUB,
        IBuffer globalBVH,
        IBuffer pageHeap,
        IBuffer instances,
        IBuffer headers,
        IBuffer candidates,
        IBuffer candCount,
        IBuffer? debug,
        IBuffer? debugCount,
        IBuffer pageFaultBuffer,
        IBuffer queueCurrent,
        IBuffer queueNext,
        IBuffer argsCurrent,
        IBuffer argsNext
    )
    {
        srb.GetVariableByName(ShaderType.Compute, "GlobalBVH")
            ?.Set(globalBVH.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        srb.GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(cullingUB, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CandidateClusters")
            ?.Set(
                candidates.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        srb.GetVariableByName(ShaderType.Compute, "CandidateCount")
            ?.Set(
                candCount.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        if (debug != null)
            srb.GetVariableByName(ShaderType.Compute, "DebugAABBs")
                ?.Set(
                    debug.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
        if (debugCount != null)
            srb.GetVariableByName(ShaderType.Compute, "DebugAABBCount")
                ?.Set(
                    debugCount.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );

        srb.GetVariableByName(ShaderType.Compute, "PageFaultBuffer")
            ?.Set(
                pageFaultBuffer.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );

        srb.GetVariableByName(ShaderType.Compute, "Queue_Current")
            ?.Set(
                queueCurrent.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        srb.GetVariableByName(ShaderType.Compute, "Queue_Next")
            ?.Set(
                queueNext.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        srb.GetVariableByName(ShaderType.Compute, "CurrentDispatchArgs")
            ?.Set(
                argsCurrent.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        srb.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")
            ?.Set(
                argsNext.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );

        srb.GetVariableByName(ShaderType.Compute, "Instances")
            ?.Set(instances.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "InstanceHeaders")
            ?.Set(headers.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
    }

    private void ProcessReadbacks(IDeviceContext ctx, IBuffer? readbackBuffer)
    {
        if (readbackBuffer == null || _pendingReadbacks.Count == 0)
            return;
        var map = ctx.MapBuffer<uint>(readbackBuffer, MapType.Read, MapFlags.DoNotWait);
        if (map.Length == 0)
            return;
        try
        {
            while (_pendingReadbacks.Count > 0)
            {
                var (off, size, cb) = _pendingReadbacks.Peek();
                int idx = (int)(off / 4);
                int words = (int)(size / 4);
                if (idx + words <= map.Length)
                    cb(map.Slice(idx, words).ToArray());
                _pendingReadbacks.Dequeue();
            }
        }
        finally
        {
            ctx.UnmapBuffer(readbackBuffer, MapType.Read);
        }
        if (_pendingReadbacks.Count == 0)
            _readbackOffset = 0;
    }

    private void ProcessPageFaultReadback(IDeviceContext ctx)
    {
        if (!_pendingPageFaultReadback || clusterManager.PageFaultReadbackBuffer == null)
            return;

        var map = ctx.MapBuffer<uint>(
            clusterManager.PageFaultReadbackBuffer,
            MapType.Read,
            MapFlags.DoNotWait
        );
        if (map.Length == 0)
            return;

        try
        {
            DispatchPageFaults(map.ToArray());
            _pendingPageFaultReadback = false;
        }
        finally
        {
            ctx.UnmapBuffer(clusterManager.PageFaultReadbackBuffer, MapType.Read);
        }
    }

    private void EnqueueReadback(
        IDeviceContext ctx,
        IBuffer? readbackBuffer,
        IBuffer? src,
        uint srcOff,
        uint size,
        Action<uint[]> cb
    )
    {
        if (readbackBuffer == null || src == null)
            return;

        if (_readbackOffset + size > readbackBuffer.GetDesc().Size)
        {
            ProcessReadbacks(ctx, readbackBuffer);
            if (_readbackOffset + size > readbackBuffer.GetDesc().Size)
            {
                _pendingReadbacks.Clear();
                _readbackOffset = 0;
            }
        }

        if (_readbackOffset + size > readbackBuffer.GetDesc().Size)
            return;

        ctx.CopyBuffer(
            src,
            srcOff,
            ResourceStateTransitionMode.Verify,
            readbackBuffer,
            _readbackOffset,
            size,
            ResourceStateTransitionMode.Verify
        );
        _pendingReadbacks.Enqueue((_readbackOffset, size, cb));
        _readbackOffset = (_readbackOffset + size + 15) & ~15u;
    }

    public void Dispose()
    {
        _bvhTraverseSRB_A?.Dispose();
        _bvhTraverseSRB_B?.Dispose();
        _bvhTraversePSO?.Dispose();
        _bvhUpdateArgsSRB_A?.Dispose();
        _bvhUpdateArgsSRB_B?.Dispose();
        _bvhUpdateArgsPSO?.Dispose();
        _initQueueSRB?.Dispose();
        _initQueuePSO?.Dispose();
    }
}
