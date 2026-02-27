using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public enum ClusterCullPhase
{
    Legacy,
    Phase1,
    Phase2,
}

public class ClusterCullPass(
    RenderContext context,
    ClusterResourceManager clusterManager,
    InstanceSyncSystem transformSystem,
    ClusterCullPhase phase = ClusterCullPhase.Legacy,
    string passName = "ClusterCull"
) : RenderPass(passName), IDisposable
{
    private readonly RenderContext _context = context;
    private readonly ClusterCullPhase _phase = phase;
    private ShaderAsset? _cullShaderAsset;
    private IPipelineState? _cullPSO;
    private IShaderResourceBinding? _cullSRB;
    private bool _initialized;

    public bool UsesHiZ => _phase != ClusterCullPhase.Legacy;

    // RenderGraph handles set by orchestrator
    public RGResourceHandle HCandidateClusters = RGResourceHandle.Invalid,
        HCandidateArgs = RGResourceHandle.Invalid,
        HCandidateCount = RGResourceHandle.Invalid;
    public RGResourceHandle HVisibleClusters = RGResourceHandle.Invalid,
        HIndirectDrawArgs = RGResourceHandle.Invalid;
    public RGResourceHandle HCullingUniforms = RGResourceHandle.Invalid;
    public RGResourceHandle HHiZTexture = RGResourceHandle.Invalid;
    public RGResourceHandle HPhase2CandidateClusters = RGResourceHandle.Invalid,
        HPhase2CandidateCount = RGResourceHandle.Invalid;
    public RGResourceHandle HGlobalTransformBuffer = RGResourceHandle.Invalid;
    public RGResourceHandle HPageHeap = RGResourceHandle.Invalid;

    private IBuffer? _cullingUniformBuffer;

    public void SetFrameData(IBuffer cullingUB)
    {
        _cullingUniformBuffer = cullingUB;
    }

    public void Init()
    {
        if (_initialized)
            return;
        var device = _context.Device;
        if (device == null)
            return;

        string shaderPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_cull.slang"
            )
        );
        _cullShaderAsset = SlangShaderImporter.Import(shaderPath);

        string cullEntryPoint = _phase switch
        {
            ClusterCullPhase.Phase1 => "main_phase1",
            ClusterCullPhase.Phase2 => "main_phase2",
            _ => "main",
        };

        using var cs = _cullShaderAsset.CreateShader(_context, cullEntryPoint);
        var ci = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Cluster Cull PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = _cullShaderAsset.GetResourceVariables(
                        _context,
                        name =>
                            (
                                name == "CandidateClusters"
                                || name == "CandidateCount"
                                || name == "DrawArgs"
                                || name == "VisibleClusters"
                                || name == "Instances"
                                || name == "HiZTexture"
                                || name == "Phase2CandidateClusters"
                                || name == "Phase2CandidateCount"
                            )
                                ? ShaderResourceVariableType.Dynamic
                                : null
                    ),
                },
            },
            Cs = cs,
        };

        _cullPSO = device.CreateComputePipelineState(ci);
        if (_cullPSO != null)
            _cullSRB = _cullPSO.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ReadBuffer(HCandidateClusters, ResourceState.ShaderResource);
        builder.ReadBuffer(HCandidateArgs, ResourceState.IndirectArgument);
        builder.ReadBuffer(HCandidateCount, ResourceState.UnorderedAccess);
        builder.ReadBuffer(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.WriteBuffer(HVisibleClusters, ResourceState.UnorderedAccess);
        builder.WriteBuffer(HIndirectDrawArgs, ResourceState.UnorderedAccess);

        if (_phase != ClusterCullPhase.Legacy)
        {
            builder.ReadTexture(HHiZTexture, ResourceState.ShaderResource);
        }

        if (_phase == ClusterCullPhase.Phase1)
        {
            builder.WriteBuffer(HPhase2CandidateClusters, ResourceState.UnorderedAccess);
            builder.WriteBuffer(HPhase2CandidateCount, ResourceState.UnorderedAccess);
        }

        builder.ReadBuffer(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.ReadBuffer(HPageHeap, ResourceState.ShaderResource);
    }

    public override void Execute(RenderContext context, RenderGraphContext rgCtx)
    {
        if (_cullPSO == null || _cullSRB == null)
            return;
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var candidates = rgCtx.GetBuffer(HCandidateClusters);
        var candArgs = rgCtx.GetBuffer(HCandidateArgs);
        var candCount = rgCtx.GetBuffer(HCandidateCount);
        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        var hiZTexture = _phase != ClusterCullPhase.Legacy ? rgCtx.GetTexture(HHiZTexture) : null;
        var hiZSrv =
            _phase != ClusterCullPhase.Legacy
                ? rgCtx.GetTextureView(HHiZTexture, TextureViewType.ShaderResource)
                : null;
        var phase2Candidates =
            _phase == ClusterCullPhase.Phase1
                ? rgCtx.GetBuffer(HPhase2CandidateClusters)
                : null;
        var phase2Count = _phase == ClusterCullPhase.Phase1 ? rgCtx.GetBuffer(HPhase2CandidateCount) : null;

        if (candidates == null || visible == null || drawArgs == null)
            return;
        if (_phase != ClusterCullPhase.Legacy && (hiZTexture == null || hiZSrv == null))
            return;
        if (_phase == ClusterCullPhase.Phase1 && (phase2Candidates == null || phase2Count == null))
            return;

        // Bind transient resources
        _cullSRB
            .GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(_cullingUniformBuffer, SetShaderResourceFlags.None);

        _cullSRB
            .GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(
                clusterManager.PageHeap?.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariableByName(ShaderType.Compute, "CandidateClusters")
            ?.Set(
                candidates.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariableByName(ShaderType.Compute, "CandidateCount")
            ?.Set(
                candCount?.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariableByName(ShaderType.Compute, "DrawArgs")
            ?.Set(
                drawArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _cullSRB
            .GetVariableByName(ShaderType.Compute, "VisibleClusters")
            ?.Set(
                visible.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );

        if (_phase != ClusterCullPhase.Legacy)
        {
            _cullSRB
                .GetVariableByName(ShaderType.Compute, "HiZTexture")
                ?.Set(hiZSrv, SetShaderResourceFlags.None);
        }

        if (_phase == ClusterCullPhase.Phase1)
        {
            _cullSRB
                .GetVariableByName(ShaderType.Compute, "Phase2CandidateClusters")
                ?.Set(
                    phase2Candidates!.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _cullSRB
                .GetVariableByName(ShaderType.Compute, "Phase2CandidateCount")
                ?.Set(
                    phase2Count!.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
        }

        if (transformSystem.GlobalTransformBuffer != null)
            _cullSRB
                .GetVariableByName(ShaderType.Compute, "Instances")
                ?.Set(
                    transformSystem.GlobalTransformBuffer.GetDefaultView(
                        BufferViewType.ShaderResource
                    ),
                    SetShaderResourceFlags.None
                );

        ctx.SetPipelineState(_cullPSO);
        ctx.CommitShaderResources(_cullSRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchComputeIndirect(
            new DispatchComputeIndirectAttribs
            {
                AttribsBuffer = candArgs,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
            }
        );
    }

    public void Dispose()
    {
        _cullSRB?.Dispose();
        _cullPSO?.Dispose();
    }
}
