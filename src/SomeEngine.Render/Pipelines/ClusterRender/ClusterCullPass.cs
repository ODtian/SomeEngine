using System;
using System.Collections.Concurrent;
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

/// <summary>
/// Static PSO 缓存：3 phase 各一个 PSO + SRB pool。
/// </summary>
internal static class ClusterCullPSOs
{
    internal static IPipelineState? LegacyPSO;
    internal static IPipelineState? Phase1PSO;
    internal static IPipelineState? Phase2PSO;

    internal static readonly ConcurrentBag<IShaderResourceBinding> LegacySRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> Phase1SRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> Phase2SRBPool = [];

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

            string shaderPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory,
                    "../../../../../../assets/Shaders/cluster_cull.slang"));
            var shaderAsset = SlangShaderImporter.Import(shaderPath);

            var layoutDesc = new PipelineResourceLayoutDesc
            {
                DefaultVariableType = ShaderResourceVariableType.Dynamic,
            };

            IPipelineState CreatePSO(string entryPoint, string name)
            {
                using var cs = shaderAsset.CreateShader(context, entryPoint);
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

            LegacyPSO = CreatePSO("main", "Cluster Cull Legacy PSO");
            Phase1PSO = CreatePSO("main_phase1", "Cluster Cull Phase1 PSO");
            Phase2PSO = CreatePSO("main_phase2", "Cluster Cull Phase2 PSO");

            s_initialized = true;
        }
    }

    internal static (IPipelineState pso, ConcurrentBag<IShaderResourceBinding> pool) GetForPhase(ClusterCullPhase phase) => phase switch
    {
        ClusterCullPhase.Phase1 => (Phase1PSO!, Phase1SRBPool),
        ClusterCullPhase.Phase2 => (Phase2PSO!, Phase2SRBPool),
        _ => (LegacyPSO!, LegacySRBPool),
    };

    internal static IShaderResourceBinding RentSRB(IPipelineState pso, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(false);

    internal static void ReturnSRB(IShaderResourceBinding srb, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.Add(srb);
}

public class ClusterCullPass(
    RenderContext context,
    ClusterCullPhase phase = ClusterCullPhase.Legacy,
    string passName = "ClusterCull"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;

    public bool UsesHiZ => phase != ClusterCullPhase.Legacy;

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
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;

    public void Init() => ClusterCullPSOs.EnsureInitialized(context);

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
        builder.Read(HPageHeap, ResourceState.ShaderResource);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        ClusterCullPSOs.EnsureInitialized(context);
        var (pso, pool) = ClusterCullPSOs.GetForPhase(phase);

        var ctx = context.ImmediateContext;
        if (ctx == null || pso == null) return;

        var candidates = rgCtx.GetBuffer(HCandidateClusters);
        var candArgs = rgCtx.GetBuffer(HCandidateArgs);
        var candCount = rgCtx.GetBuffer(HCandidateCount);
        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        var hiZSrv = phase != ClusterCullPhase.Legacy && HHiZTexture.IsValid
            ? rgCtx.GetTextureView(HHiZTexture, TextureViewType.ShaderResource) : null;
        var phase2Candidates = phase == ClusterCullPhase.Phase1 ? rgCtx.GetBuffer(HPhase2CandidateClusters) : null;
        var phase2Count = phase == ClusterCullPhase.Phase1 ? rgCtx.GetBuffer(HPhase2CandidateCount) : null;
        var pageHeapBuffer = rgCtx.GetBuffer(HPageHeap);

        if (candidates == null || visible == null || drawArgs == null) return;
        if (phase == ClusterCullPhase.Phase2 && hiZSrv == null) return;
        if (phase == ClusterCullPhase.Phase1 && (phase2Candidates == null || phase2Count == null)) return;

        var cullingUniformBuffer = rgCtx.GetBuffer(HCullingUniforms);
        if (cullingUniformBuffer == null) return;

        var srb = ClusterCullPSOs.RentSRB(pso, pool);

        srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(cullingUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(pageHeapBuffer?.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CandidateClusters")
            ?.Set(candidates.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "CandidateCount")
            ?.Set(candCount?.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "DrawArgs")
            ?.Set(drawArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
            ?.Set(visible.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        if (phase != ClusterCullPhase.Legacy && hiZSrv != null)
            srb.GetVariableByName(ShaderType.Compute, "HiZTexture")
                ?.Set(hiZSrv, SetShaderResourceFlags.None);

        if (phase == ClusterCullPhase.Phase1)
        {
            srb.GetVariableByName(ShaderType.Compute, "Phase2CandidateClusters")
                ?.Set(phase2Candidates!.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            srb.GetVariableByName(ShaderType.Compute, "Phase2CandidateCount")
                ?.Set(phase2Count!.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        }

        if (phase == ClusterCullPhase.Phase2 && HPhase2IndirectDrawArgs.IsValid)
        {
            var phase2DrawArgs = rgCtx.GetBuffer(HPhase2IndirectDrawArgs);
            if (phase2DrawArgs != null)
                srb.GetVariableByName(ShaderType.Compute, "Phase2DrawArgs")
                    ?.Set(phase2DrawArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        }

        if (HDebugHiZOutput.IsValid)
        {
            var debugHiZOutput = rgCtx.GetBuffer(HDebugHiZOutput);
            if (debugHiZOutput != null)
                srb.GetVariableByName(ShaderType.Compute, "DebugHiZOutput")
                    ?.Set(debugHiZOutput.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        }

        var globalTransformView = rgCtx.GetBufferView(HGlobalTransformBuffer, BufferViewType.ShaderResource);
        if (globalTransformView != null)
            srb.GetVariableByName(ShaderType.Compute, "Instances")
                ?.Set(globalTransformView, SetShaderResourceFlags.None);

        ctx.SetPipelineState(pso);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchComputeIndirect(
            new DispatchComputeIndirectAttribs
            {
                AttribsBuffer = candArgs,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
            }
        );

        ClusterCullPSOs.ReturnSRB(srb, pool);
    }

    /// <summary>No-op: PSO/SRB are static-cached.</summary>
    public void Dispose() { }
}
