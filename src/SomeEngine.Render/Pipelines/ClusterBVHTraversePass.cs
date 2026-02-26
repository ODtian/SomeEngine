using System;
using System.Collections.Generic;
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
    private IPipelineState? _initQueuePSO;
    private IShaderResourceBinding? _initQueueSRB;

    private IBuffer? _queueA,
        _queueB,
        _argsA,
        _argsB;
    private IBuffer? _readbackBuffer;
    private bool _initialized;

    private readonly Queue<(uint Offset, uint Size, Action<uint[]> Callback)> _pendingReadbacks =
        new();
    private uint _readbackOffset;
    private bool _pendingPageFaultReadback;

    // RenderGraph handles set by orchestrator
    public RGResourceHandle HCandidateClusters,
        HCandidateArgs,
        HCandidateCount;
    public RGResourceHandle HIndirectDrawArgs,
        HBvhDebugBuffer,
        HBvhDebugCountBuffer;
    public RGResourceHandle HCullingUniforms;
    public RGResourceHandle HPageFaultBuffer;

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
        _visualiseBVH;
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
        int debugDepth
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
    }

    public void Init()
    {
        if (_initialized)
            return;
        var device = context.Device;
        if (device == null)
            return;

        _queueA = device.CreateBuffer(
            new BufferDesc
            {
                Name = "BVH Queue A",
                Size = 4 * 1024 * 1024 * 8u,
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = 8,
            }
        );
        _queueB = device.CreateBuffer(
            new BufferDesc
            {
                Name = "BVH Queue B",
                Size = 4 * 1024 * 1024 * 8u,
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = 8,
            }
        );
        _argsA = device.CreateBuffer(
            new BufferDesc
            {
                Name = "BVH Args A",
                Size = 16,
                Usage = Usage.Default,
                BindFlags =
                    BindFlags.UnorderedAccess
                    | BindFlags.IndirectDrawArgs
                    | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            }
        );
        _argsB = device.CreateBuffer(
            new BufferDesc
            {
                Name = "BVH Args B",
                Size = 16,
                Usage = Usage.Default,
                BindFlags =
                    BindFlags.UnorderedAccess
                    | BindFlags.IndirectDrawArgs
                    | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            }
        );
        _readbackBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "BVH Readback",
                Size = Math.Max(65536u, clusterManager.PageFaultBufferSize + 4096u),
                Usage = Usage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            }
        );

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
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables =
                    [
                        .. _shaderAsset.GetResourceVariables(
                            context,
                            (name, cat) =>
                                (
                                    name == "CandidateClusters"
                                    || name == "CandidateCount"
                                    || name == "DebugAABBs"
                                    || name == "DebugAABBCount"
                                    || name == "Instances"
                                    || name == "InstanceHeaders"
                                )
                                    ? ShaderResourceVariableType.Dynamic
                                    : null
                        ),
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

        // Static: only uniform buffer
        _bvhTraverseSRB_A = _bvhTraversePSO.CreateShaderResourceBinding(false);
        _bvhTraverseSRB_B = _bvhTraversePSO.CreateShaderResourceBinding(false);

        // Bind queue A/B ping-pong (these are persistent, owned by this pass)
        _bvhTraverseSRB_A
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "Queue_Current")
            ?.Set(
                _queueA!.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _bvhTraverseSRB_A
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "Queue_Next")
            ?.Set(
                _queueB!.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _bvhTraverseSRB_A
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "NextDispatchArgs")
            ?.Set(
                _argsB!.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _bvhTraverseSRB_A
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "CurrentDispatchArgs")
            ?.Set(
                _argsA!.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        _bvhTraverseSRB_B
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "Queue_Current")
            ?.Set(
                _queueB!.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _bvhTraverseSRB_B
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "Queue_Next")
            ?.Set(
                _queueA!.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _bvhTraverseSRB_B
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "NextDispatchArgs")
            ?.Set(
                _argsA!.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _bvhTraverseSRB_B
            .GetVariable(context, _shaderAsset, ShaderType.Compute, "CurrentDispatchArgs")
            ?.Set(
                _argsB!.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

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
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = _shaderAsset.GetResourceVariables(
                        context,
                        (name, cat) =>
                            (name == "InstanceHeaders") ? ShaderResourceVariableType.Dynamic : null
                    ),
                },
            },
            Cs = upCs,
        };

        _bvhUpdateArgsPSO = device.CreateComputePipelineState(upCi);
        if (_bvhUpdateArgsPSO != null)
        {
            _bvhUpdateArgsSRB_A = _bvhUpdateArgsPSO.CreateShaderResourceBinding(false);
            _bvhUpdateArgsSRB_A
                .GetVariable(context, _shaderAsset, ShaderType.Compute, "NextDispatchArgs")
                ?.Set(
                    _argsA!.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _bvhUpdateArgsSRB_B = _bvhUpdateArgsPSO.CreateShaderResourceBinding(false);
            _bvhUpdateArgsSRB_B
                .GetVariable(context, _shaderAsset, ShaderType.Compute, "NextDispatchArgs")
                ?.Set(
                    _argsB!.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
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
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = _shaderAsset.GetResourceVariables(
                        context,
                        (name, cat) =>
                            (name == "InstanceHeaders" || name == "Uniforms")
                                ? ShaderResourceVariableType.Dynamic
                                : null
                    ),
                },
            },
            Cs = initCs,
        };

        _initQueuePSO = device.CreateComputePipelineState(initCi);
        if (_initQueuePSO != null)
        {
            _initQueueSRB = _initQueuePSO.CreateShaderResourceBinding(false);
            _initQueueSRB
                .GetVariable(context, _shaderAsset, ShaderType.Compute, "Queue_Next")
                ?.Set(
                    _queueA!.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _initQueueSRB
                .GetVariable(context, _shaderAsset, ShaderType.Compute, "NextDispatchArgs")
                ?.Set(
                    _argsA!.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
        }
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.WriteBuffer(HCandidateClusters, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HCandidateArgs, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HCandidateCount, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HIndirectDrawArgs, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HBvhDebugBuffer, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HBvhDebugCountBuffer, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HPageFaultBuffer, ResourceState.UnorderedAccess);
        builder.ReadBuffer(HCullingUniforms, ResourceState.ConstantBuffer);
    }

    public override void Execute(RenderContext context, RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext ?? throw new InvalidOperationException();
        ProcessReadbacks(ctx);
        ProcessPageFaultReadback(ctx);

        // Get physical buffers from RenderGraph
        var candidateClusters = rgCtx.GetBuffer(HCandidateClusters);
        var candidateArgs = rgCtx.GetBuffer(HCandidateArgs);
        var candidateCount = rgCtx.GetBuffer(HCandidateCount);
        var indirectArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        var bvhDebug = rgCtx.GetBuffer(HBvhDebugBuffer);
        var bvhDebugCount = rgCtx.GetBuffer(HBvhDebugCountBuffer);
        var pageFaultBuffer = rgCtx.GetBuffer(HPageFaultBuffer);
        if (
            candidateClusters == null
            || candidateArgs == null
            || candidateCount == null
            || indirectArgs == null
            || pageFaultBuffer == null
        )
            return;

        // Bind transient RG buffers to SRBs (Mutable variables)
        BindTransientResources(
            _bvhTraverseSRB_A!,
            candidateClusters,
            candidateCount,
            bvhDebug,
            bvhDebugCount,
            pageFaultBuffer
        );
        BindTransientResources(
            _bvhTraverseSRB_B!,
            candidateClusters,
            candidateCount,
            bvhDebug,
            bvhDebugCount,
            pageFaultBuffer
        );

        // Reset buffers
        Span<uint> resetDrawArgs = [372, 0, 0, 0];
        ctx.UpdateBuffer(indirectArgs, 0, resetDrawArgs, ResourceStateTransitionMode.Transition);
        Span<uint> resetCandArgs = [1, 1, 1, 0];
        ctx.UpdateBuffer(candidateArgs, 0, resetCandArgs, ResourceStateTransitionMode.Transition);
        Span<uint> resetCandCount = [0];
        ctx.UpdateBuffer(candidateCount, 0, resetCandCount, ResourceStateTransitionMode.Transition);
        Span<uint> resetDebugArgs = [24, 0, 0, 0];
        if (bvhDebugCount != null)
            ctx.UpdateBuffer(
                bvhDebugCount,
                0,
                resetDebugArgs,
                ResourceStateTransitionMode.Transition
            );

        Span<uint> resetPageFaultCount = [0u];
        ctx.UpdateBuffer(
            pageFaultBuffer,
            0,
            resetPageFaultCount,
            ResourceStateTransitionMode.Transition
        );

        ctx.TransitionResourceStates([
            new StateTransitionDesc
            {
                Resource = candidateArgs,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc
            {
                Resource = candidateCount,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc
            {
                Resource = candidateClusters,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc
            {
                Resource = bvhDebugCount,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc
            {
                Resource = bvhDebug,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState,
            },
            new StateTransitionDesc
            {
                Resource = pageFaultBuffer,
                NewState = ResourceState.UnorderedAccess,
                Flags = StateTransitionFlags.UpdateState,
            },
        ]);

        if (transformSystem.Count == 0)
            return;

        uint initialCount = (uint)transformSystem.Count;
        uint groups = (initialCount + 63) / 64;
        Span<uint> resetArgsA = [0, 1, 1, 0];
        ctx.UpdateBuffer(_argsA, 0, resetArgsA, ResourceStateTransitionMode.Transition);
        Span<uint> resetArgsB = [0, 1, 1, 0];
        ctx.UpdateBuffer(_argsB, 0, resetArgsB, ResourceStateTransitionMode.Transition);

        var viewProjT = Matrix4x4.Transpose(_view * _proj);
        var mapped = ctx.MapBuffer<CullingUniforms>(
            _cullingUniformBuffer,
            MapType.Write,
            MapFlags.Discard
        );
        mapped[0] = new CullingUniforms
        {
            ViewProj = viewProjT,
            CameraPos = _cameraPos,
            LodThreshold = _lodThreshold,
            LodScale = _lodScale,
            MaxQueueNodes = 4 * 1024 * 1024u,
            MaxCandidates = ClusterPipeline.MaxDraws,
            ForcedLODLevel = _forcedLODLevel,
            InstanceCount = (uint)transformSystem.Count,
            DebugMode = _bypassCulling ? 1u : 0u,
            VisualiseBVH = _visualiseBVH ? 1u : 0u,
            DebugBVHDepth = _debugBVHDepth,
            CurrentDepth = 0,
        }; // CurrentDepth is functionally unused now in the shader
        ctx.UnmapBuffer(_cullingUniformBuffer, MapType.Write);

        // Bind transient resources for InitQueue
        if (_initQueuePSO != null && _initQueueSRB != null)
        {
            _initQueueSRB
                .GetVariable(context, _shaderAsset, ShaderType.Compute, "Uniforms")
                ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);
            if (transformSystem.GlobalInstanceHeaderBuffer != null)
                _initQueueSRB
                    .GetVariable(context, _shaderAsset, ShaderType.Compute, "InstanceHeaders")
                    ?.Set(
                        transformSystem.GlobalInstanceHeaderBuffer.GetDefaultView(
                            BufferViewType.ShaderResource
                        ),
                        SetShaderResourceFlags.None
                    );

            ctx.SetPipelineState(_initQueuePSO);
            ctx.CommitShaderResources(_initQueueSRB, ResourceStateTransitionMode.Transition);
            ctx.DispatchCompute(
                new DispatchComputeAttribs
                {
                    ThreadGroupCountX = groups,
                    ThreadGroupCountY = 1,
                    ThreadGroupCountZ = 1,
                }
            );

            // Wait for InitQueue to finish writing to args buffer
            ctx.SetPipelineState(_bvhUpdateArgsPSO);
            _bvhUpdateArgsSRB_A
                ?.GetVariableByName(ShaderType.Compute, "NextDispatchArgs")
                ?.Set(
                    _argsA!.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            ctx.CommitShaderResources(_bvhUpdateArgsSRB_A, ResourceStateTransitionMode.Transition);
            ctx.DispatchCompute(
                new DispatchComputeAttribs
                {
                    ThreadGroupCountX = 1,
                    ThreadGroupCountY = 1,
                    ThreadGroupCountZ = 1,
                }
            );
        }

        IBuffer currentArgs = _argsA!;
        IBuffer nextArgs = _argsB!;
        IShaderResourceBinding currentSRB = _bvhTraverseSRB_A!;
        IShaderResourceBinding nextUpdateSRB = _bvhUpdateArgsSRB_B!;

        if (
            _bvhTraversePSO == null
            || _bvhUpdateArgsPSO == null
            || currentArgs == null
            || nextArgs == null
            || currentSRB == null
            || nextUpdateSRB == null
        )
            return;

        for (int i = 0; i < 32; ++i)
        {
            var depthVar = currentSRB.GetVariableByName(ShaderType.Compute, "DepthIndexCB");
            unsafe
            {
                uint depth = (uint)i;
                depthVar?.SetInlineConstants(new IntPtr(&depth), 0, 1);
            }

            ctx.SetPipelineState(_bvhTraversePSO);
            ctx.CommitShaderResources(currentSRB, ResourceStateTransitionMode.Transition);
            ctx.DispatchComputeIndirect(
                new DispatchComputeIndirectAttribs
                {
                    AttribsBuffer = currentArgs,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Transition,
                }
            );

            ctx.SetPipelineState(_bvhUpdateArgsPSO);
            ctx.CommitShaderResources(nextUpdateSRB, ResourceStateTransitionMode.Transition);
            ctx.DispatchCompute(
                new DispatchComputeAttribs
                {
                    ThreadGroupCountX = 1,
                    ThreadGroupCountY = 1,
                    ThreadGroupCountZ = 1,
                }
            );

            (currentArgs, nextArgs) = (nextArgs, currentArgs);
            currentSRB =
                (currentSRB == _bvhTraverseSRB_A) ? _bvhTraverseSRB_B! : _bvhTraverseSRB_A!;
            nextUpdateSRB =
                (nextUpdateSRB == _bvhUpdateArgsSRB_B)
                    ? _bvhUpdateArgsSRB_A!
                    : _bvhUpdateArgsSRB_B!;

            if (i < 8)
            {
                int lvl = i;
                EnqueueReadback(
                    ctx,
                    currentArgs,
                    0,
                    16,
                    d =>
                    {
                        DebugBVHGroupCount[lvl] = d[0];
                        DebugBVHItemCount[lvl] = d[3];
                    }
                );
            }

            ctx.UpdateBuffer(nextArgs, 0, resetArgsB, ResourceStateTransitionMode.Transition);
            ctx.TransitionResourceStates([
                new StateTransitionDesc
                {
                    Resource = nextArgs,
                    OldState = ResourceState.Unknown,
                    NewState = ResourceState.UnorderedAccess,
                    Flags = StateTransitionFlags.UpdateState,
                },
            ]);
        }

        if (clusterManager.PageFaultReadbackBuffer != null)
        {
            ctx.CopyBuffer(
                pageFaultBuffer,
                0,
                ResourceStateTransitionMode.Transition,
                clusterManager.PageFaultReadbackBuffer,
                0,
                clusterManager.PageFaultBufferSize,
                ResourceStateTransitionMode.Transition
            );
            _pendingPageFaultReadback = true;
        }
        else
        {
            EnqueueReadback(
                ctx,
                pageFaultBuffer,
                0,
                clusterManager.PageFaultBufferSize,
                DispatchPageFaults
            );
        }
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
        IBuffer candidates,
        IBuffer candCount,
        IBuffer? debug,
        IBuffer? debugCount,
        IBuffer pageFaultBuffer
    )
    {
        srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "GlobalBVH")
            ?.Set(
                clusterManager.GlobalBVHBuffer?.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "PageHeap")
            ?.Set(
                clusterManager.PageHeap?.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "CandidateClusters")
            ?.Set(
                candidates.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "CandidateCount")
            ?.Set(
                candCount.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        if (debug != null)
            srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "DebugAABBs")
                ?.Set(
                    debug.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
        if (debugCount != null)
            srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "DebugAABBCount")
                ?.Set(
                    debugCount.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );

        srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "PageFaultBuffer")
            ?.Set(
                pageFaultBuffer.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );

        if (transformSystem.GlobalTransformBuffer != null)
            srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "Instances")
                ?.Set(
                    transformSystem.GlobalTransformBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
        if (transformSystem.GlobalInstanceHeaderBuffer != null)
            srb.GetVariable(context, _shaderAsset, ShaderType.Compute, "InstanceHeaders")
                ?.Set(
                    transformSystem.GlobalInstanceHeaderBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );
    }

    private void ProcessReadbacks(IDeviceContext ctx)
    {
        if (_readbackBuffer == null || _pendingReadbacks.Count == 0)
            return;
        var map = ctx.MapBuffer<uint>(_readbackBuffer, MapType.Read, MapFlags.DoNotWait);
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
            ctx.UnmapBuffer(_readbackBuffer, MapType.Read);
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
        IBuffer? src,
        uint srcOff,
        uint size,
        Action<uint[]> cb
    )
    {
        if (_readbackBuffer == null || src == null)
            return;

        if (_readbackOffset + size > _readbackBuffer.GetDesc().Size)
        {
            ProcessReadbacks(ctx);
            if (_readbackOffset + size > _readbackBuffer.GetDesc().Size)
            {
                _pendingReadbacks.Clear();
                _readbackOffset = 0;
            }
        }

        if (_readbackOffset + size > _readbackBuffer.GetDesc().Size)
            return;

        ctx.CopyBuffer(
            src,
            srcOff,
            ResourceStateTransitionMode.Transition,
            _readbackBuffer,
            _readbackOffset,
            size,
            ResourceStateTransitionMode.Transition
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
        _queueA?.Dispose();
        _queueB?.Dispose();
        _argsA?.Dispose();
        _argsB?.Dispose();
        _readbackBuffer?.Dispose();
    }
}
