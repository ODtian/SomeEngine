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
    Phase1,
    Phase2,
}

/// <summary>
/// Static PSO 缓存：3 phase 各一个 PSO + SRB pool。
/// </summary>
internal static partial class ClusterCull
{
internal sealed class Resources : IDisposable
{
    internal const string ShaderFile = "cluster_cull.slang";
    internal const string Phase1EntryPoint = "main_phase1";
    internal const string Phase2EntryPoint = "main_phase2";
    internal const string UpdateArgsEntryPoint = "UpdateIndirectArgs";

    internal IPipelineState? Phase1PSO;
    internal IPipelineState? Phase2PSO;
    internal IPipelineState? UpdateArgsPSO;
    internal ShaderAsset? ShaderAsset;

    internal readonly SRBPool Phase1Pool = new();
    internal readonly SRBPool Phase2Pool = new();
    internal readonly SRBPool UpdateArgsPool = new();

    private bool _initialized;
    private bool _updateArgsInitialized;
    private readonly Lock _initLock = new();
    private readonly Lock _updateArgsInitLock = new();

    internal void EnsureInitialized(RenderContext context)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;

            var device = context.Device;
            if (device == null) return;

            string shaderPath = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(shaderPath);
            ShaderAsset = shaderAsset;

            var layoutDesc = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            IPipelineState CreatePSO(string entryPoint, string name)
            {
                var cs = shaderAsset.CreateShader(context, entryPoint);
                return device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc
                    {
                        Name = name,
                        PipelineType = PipelineType.Compute,
                        ResourceLayout = layoutDesc,
                    },
                    Cs = cs,
                })!;
            }

            Phase1PSO = CreatePSO(Phase1EntryPoint, "Cluster Cull Phase1 PSO");
            Phase2PSO = CreatePSO(Phase2EntryPoint, "Cluster Cull Phase2 PSO");
            _initialized = true;
        }
    }

    internal (IPipelineState pso, SRBPool pool) GetForPhase(ClusterCullPhase phase) => phase switch
    {
        ClusterCullPhase.Phase1 => (Phase1PSO!, Phase1Pool),
        ClusterCullPhase.Phase2 => (Phase2PSO!, Phase2Pool),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    internal void EnsureUpdateArgsInitialized(RenderContext context)
    {
        if (_updateArgsInitialized) return;
        lock (_updateArgsInitLock)
        {
            if (_updateArgsInitialized) return;
            var device = context.Device;
            if (device == null) return;

            string shaderPath = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(shaderPath);
            ShaderAsset = shaderAsset;

            var cs = shaderAsset.CreateShader(context, UpdateArgsEntryPoint);
            UpdateArgsPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "Cull Update Args PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                Cs = cs,
            });
            _updateArgsInitialized = true;
        }
    }

    public void Dispose()
    {
        Phase1Pool.Dispose();
        Phase2Pool.Dispose();
        UpdateArgsPool.Dispose();
        Phase1PSO?.Dispose();
        Phase2PSO?.Dispose();
        UpdateArgsPSO?.Dispose();
    }
}
}

internal class ClusterCullPass(
    RenderContext context,
    ClusterCull.Resources resources,
    ClusterCullPhase phase = ClusterCullPhase.Phase1,
    string passName = "ClusterCull"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;

    public bool UsesHiZ => HHiZTexture.IsValid;

    public RenderGraphHandle HCandidateClusters = RenderGraphHandle.Invalid,
        HCandidateArgs = RenderGraphHandle.Invalid,
        HCandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid,
        HIndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCullingUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HHiZTexture = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPhase2CandidateClusters = RenderGraphHandle.Invalid,
        HPhase2CandidateCount = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPhase2IndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalInstanceHeaderBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;

    public void Init() => resources.EnsureInitialized(context);

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

        if (HHiZTexture.IsValid)
            builder.Read(HHiZTexture, ResourceState.ShaderResource);

        if (phase == ClusterCullPhase.Phase1)
        {
            builder.Write(HPhase2CandidateClusters, ResourceState.UnorderedAccess);
            builder.Write(HPhase2CandidateCount, ResourceState.UnorderedAccess);
        }

        if (phase == ClusterCullPhase.Phase2 && HPhase2IndirectDrawArgs.IsValid)
            builder.Write(HPhase2IndirectDrawArgs, ResourceState.UnorderedAccess);

        if (HDebugHiZOutput.IsValid)
            builder.Write(HDebugHiZOutput, ResourceState.UnorderedAccess);

        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HGlobalInstanceHeaderBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var (pso, pool) = resources.GetForPhase(phase);

        var ctx = context.ImmediateContext;
        if (ctx == null || pso == null) return;

        var candidates = rgCtx.GetBuffer(HCandidateClusters);
        var candArgs = rgCtx.GetBuffer(HCandidateArgs);
        var candCount = rgCtx.GetBuffer(HCandidateCount);
        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        var hiZSrv = HHiZTexture.IsValid
            ? rgCtx.GetTextureView(HHiZTexture, TextureViewType.ShaderResource) : null;
        var phase2Candidates = phase == ClusterCullPhase.Phase1 ? rgCtx.GetBuffer(HPhase2CandidateClusters) : null;
        var phase2Count = phase == ClusterCullPhase.Phase1 ? rgCtx.GetBuffer(HPhase2CandidateCount) : null;
        var pageHeapBuffer = rgCtx.GetBuffer(HPageHeap);

        if (candidates == null || visible == null || drawArgs == null) return;
        if (phase == ClusterCullPhase.Phase2 && hiZSrv == null) return;
        if (phase == ClusterCullPhase.Phase1 && (phase2Candidates == null || phase2Count == null)) return;

        var cullingUniformBuffer = rgCtx.GetBuffer(HCullingUniforms);
        if (cullingUniformBuffer == null) return;

        var srb = pool.Rent(pso);

        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Uniforms")
            ?.Set(cullingUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "PageHeap")
            ?.Set(pageHeapBuffer?.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CandidateClusters")
            ?.Set(candidates.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "CandidateCount")
            ?.Set(candCount?.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "VisibleClusters")
            ?.Set(visible.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        if (hiZSrv != null)
            srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "HiZTexture")
                ?.Set(hiZSrv, SetShaderResourceFlags.None);

        if (phase == ClusterCullPhase.Phase1)
        {
            srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Phase2CandidateClusters")
                ?.Set(phase2Candidates!.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Phase2CandidateCount")
                ?.Set(phase2Count!.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        }

        if (phase == ClusterCullPhase.Phase2 && HPhase2IndirectDrawArgs.IsValid)
        {
            var phase2DrawArgs = rgCtx.GetBuffer(HPhase2IndirectDrawArgs);
            if (phase2DrawArgs != null)
                srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Phase2DrawArgs")
                    ?.Set(phase2DrawArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        }

        if (HDebugHiZOutput.IsValid)
        {
            var debugHiZOutput = rgCtx.GetBuffer(HDebugHiZOutput);
            if (debugHiZOutput != null)
                srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "DebugHiZOutput")
                    ?.Set(debugHiZOutput.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        }

        var globalTransformView = rgCtx.GetBufferView(HGlobalTransformBuffer, BufferViewType.ShaderResource);
        if (globalTransformView != null)
            srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "Instances")
                ?.Set(globalTransformView, SetShaderResourceFlags.None);

        var globalInstanceHeaderView = rgCtx.GetBufferView(HGlobalInstanceHeaderBuffer, BufferViewType.ShaderResource);
        if (globalInstanceHeaderView != null)
            srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Compute, "InstanceHeaders")
                ?.Set(globalInstanceHeaderView, SetShaderResourceFlags.None);

        ctx.SetPipelineState(pso);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchComputeIndirect(
            new DispatchComputeIndirectAttribs
            {
                AttribsBuffer = candArgs,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
            }
        );

        pool.Return(srb);
    }

    /// <summary>No-op: PSO/SRB are static-cached.</summary>
    public void Dispose() { }
}
