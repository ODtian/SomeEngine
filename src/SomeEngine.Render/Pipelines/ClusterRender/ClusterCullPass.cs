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
    ClusterCullPhase phase = ClusterCullPhase.Legacy,
    string passName = "ClusterCull"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;
    private ShaderAsset? _cullShaderAsset;
    private IPipelineState? _cullPSO;
    private IShaderResourceBinding? _cullSRB;
    private bool _initialized;

    public bool UsesHiZ => phase != ClusterCullPhase.Legacy;

    // RenderGraph handles set by orchestrator
    public RenderGraphHandle HCandidateClusters = RenderGraphHandle.Invalid,
        HCandidateArgs = RenderGraphHandle.Invalid,
        HCandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid,
        HIndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCullingUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HHiZTexture = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPhase2CandidateClusters = RenderGraphHandle.Invalid,
        HPhase2CandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPhase2IndirectDrawArgs = RenderGraphHandle.Invalid; // Phase 2's own DrawArgs (for AppendVisiblePhase2)
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;

    public void Init()
    {
        if (_initialized)
            return;
        var device = context.Device;
        if (device == null)
            return;

        string shaderPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_cull.slang"
            )
        );
        _cullShaderAsset = SlangShaderImporter.Import(shaderPath);

        string cullEntryPoint = phase switch
        {
            ClusterCullPhase.Phase1 => "main_phase1",
            ClusterCullPhase.Phase2 => "main_phase2",
            _ => "main",
        };

        using var cs = _cullShaderAsset.CreateShader(context, cullEntryPoint);
        var ci = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Cluster Cull PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Dynamic,
                },
            },
            Cs = cs,
        };

        _cullPSO = device.CreateComputePipelineState(ci);
        if (_cullPSO != null)
            _cullSRB = _cullPSO.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HCandidateClusters, ResourceState.ShaderResource);
        builder.Read(HCandidateArgs, ResourceState.IndirectArgument);
        builder.Read(HCandidateCount, ResourceState.UnorderedAccess);
        builder.Read(HCullingUniforms, ResourceState.ConstantBuffer);
        builder.Write(HVisibleClusters, ResourceState.UnorderedAccess);
        if (phase == ClusterCullPhase.Phase2)
            builder.ReadWrite(HIndirectDrawArgs, ResourceState.UnorderedAccess);
        else
            builder.Write(HIndirectDrawArgs, ResourceState.UnorderedAccess);


        if (phase != ClusterCullPhase.Legacy && HHiZTexture.IsValid)
        {
            builder.Read(HHiZTexture, ResourceState.ShaderResource);
        }

        if (phase == ClusterCullPhase.Phase1)
        {
            builder.Write(HPhase2CandidateClusters, ResourceState.UnorderedAccess);
            builder.Write(HPhase2CandidateCount, ResourceState.UnorderedAccess);
        }

        if (phase == ClusterCullPhase.Phase2 && HPhase2IndirectDrawArgs.IsValid)
        {
            builder.Write(HPhase2IndirectDrawArgs, ResourceState.UnorderedAccess);
        }

        if (HDebugHiZOutput.IsValid)
        {
            builder.Write(HDebugHiZOutput, ResourceState.UnorderedAccess);
        }

        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);

        builder.Read(HPageHeap, ResourceState.ShaderResource);
    }

    public void Execute(RenderGraphContext rgCtx)
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
        var hiZTexture =
            phase != ClusterCullPhase.Legacy && HHiZTexture.IsValid
                ? rgCtx.GetTexture(HHiZTexture)
                : null;
        var hiZSrv =
            phase != ClusterCullPhase.Legacy && HHiZTexture.IsValid
                ? rgCtx.GetTextureView(HHiZTexture, TextureViewType.ShaderResource)
                : null;
        var phase2Candidates =
            phase == ClusterCullPhase.Phase1 ? rgCtx.GetBuffer(HPhase2CandidateClusters) : null;
        var phase2Count =
            phase == ClusterCullPhase.Phase1 ? rgCtx.GetBuffer(HPhase2CandidateCount) : null;

        var pageHeapBuffer = rgCtx.GetBuffer(HPageHeap);

        if (candidates == null || visible == null || drawArgs == null)
            return;
        if (phase == ClusterCullPhase.Phase2 && (hiZTexture == null || hiZSrv == null))
            return;
        if (phase == ClusterCullPhase.Phase1 && (phase2Candidates == null || phase2Count == null))
            return;

        var cullingUniformBuffer = rgCtx.GetBuffer(HCullingUniforms);
        if (cullingUniformBuffer == null)
            return;

        // Bind transient resources
        _cullSRB
            .GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(cullingUniformBuffer, SetShaderResourceFlags.None);

        _cullSRB
            .GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(
                pageHeapBuffer?.GetDefaultView(BufferViewType.ShaderResource),
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



        if (phase != ClusterCullPhase.Legacy && hiZSrv != null)
        {
            _cullSRB
                .GetVariableByName(ShaderType.Compute, "HiZTexture")
                ?.Set(hiZSrv, SetShaderResourceFlags.None);
        }

        if (phase == ClusterCullPhase.Phase1)
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

        if (phase == ClusterCullPhase.Phase2 && HPhase2IndirectDrawArgs.IsValid)
        {
            var phase2DrawArgs = rgCtx.GetBuffer(HPhase2IndirectDrawArgs);
            if (phase2DrawArgs != null)
            {
                _cullSRB
                    .GetVariableByName(ShaderType.Compute, "Phase2DrawArgs")
                    ?.Set(
                        phase2DrawArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                        SetShaderResourceFlags.None
                    );
            }
        }

        if (HDebugHiZOutput.IsValid)
        {
            var debugHiZOutput = rgCtx.GetBuffer(HDebugHiZOutput);
            if (debugHiZOutput != null)
            {
                _cullSRB
                    .GetVariableByName(ShaderType.Compute, "DebugHiZOutput")
                    ?.Set(
                        debugHiZOutput.GetDefaultView(BufferViewType.UnorderedAccess),
                        SetShaderResourceFlags.None
                    );
            }
        }

        var globalTransformView = rgCtx.GetBufferView(
            HGlobalTransformBuffer,
            BufferViewType.ShaderResource
        );
        if (globalTransformView != null)
        {
            _cullSRB
                .GetVariableByName(ShaderType.Compute, "Instances")
                ?.Set(globalTransformView, SetShaderResourceFlags.None);
        }



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
