using System.Numerics;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 无状态 Cull 工具函数。
/// PSO 在 ClusterCullPSOs 内 static 缓存。每次调用创建轻量 pass 实例。
/// </summary>
public static class ClusterCull
{
    /// <summary>计算纹理的完整 mip 链层数。</summary>
    public static uint CalculateMipCount(uint width, uint height)
    {
        uint levels = 1;
        uint size = Math.Max(width, height);
        while (size > 1) { size >>= 1; levels++; }
        return levels;
    }

    /// <summary>
    /// 向 RenderGraph 添加 Cull 相关 pass。
    /// 返回 CullOutput 包含 VisibleClusters 和 DrawArgs。
    /// </summary>
    public static ClusterCullOutput AddPasses(
        RenderGraph graph,
        RenderContext context,
        in ClusterTraverseOutput traverse,
        in ClusterGlobalResources globals,
        RenderGraphHandle hCullingUniforms,
        in ClusterCullConfig config,
        RenderGraphHandle hCurrHiZ,
        RenderGraphHandle hPrevHiZ,
        bool hasPrevHistory,
        RenderGraphHandle hPhase2IndirectDrawArgs,
        bool debugShowHiZAABBs = false
    )
    {
        ClusterCullPSOs.EnsureInitialized(context);

        uint maxDraws = ClusterLimits.MaxDraws;

        // ─── Create cull output buffers ───
        var hVisibleClusters = graph.CreateBuffer("VisibleClusters", new BufferDesc
        {
            Size = (ulong)(maxDraws * 16),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 16,
        });

        var hIndirectDrawArgs = graph.CreateBuffer("IndirectDrawArgs", new BufferDesc
        {
            Size = 256,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
        });

        // ─── HiZ mode management ───
        bool useHiZBuffers = config.HiZMode != HiZDebugMode.Legacy && config.HiZMode != HiZDebugMode.Phase1OnlyPassAll;

        // Phase 2 candidate buffers
        var hPhase2CandidateClusters = RenderGraphHandle.Invalid;
        var hPhase2CandidateCount = RenderGraphHandle.Invalid;
        var hPhase2CandidateArgs = RenderGraphHandle.Invalid;

        if (useHiZBuffers)
        {
            hPhase2CandidateClusters = graph.CreateBuffer("Phase2CandidateClusters", new BufferDesc
            {
                Size = (ulong)(maxDraws * 12),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 12,
            });
            hPhase2CandidateCount = graph.CreateBuffer("Phase2CandidateCount", new BufferDesc
            {
                Size = 4,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            });
            hPhase2CandidateArgs = graph.CreateBuffer("Phase2CandidateArgs", new BufferDesc
            {
                Size = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            });
        }

        // ─── Debug HiZ output buffer ───
        var hDebugHiZOutput = graph.CreateBuffer(
            debugShowHiZAABBs ? "DebugHiZOutput" : "DebugHiZOutputDummy",
            new BufferDesc
            {
                Size = debugShowHiZAABBs ? 196612u : 16u,
                BindFlags = BindFlags.UnorderedAccess | (debugShowHiZAABBs ? BindFlags.ShaderResource : BindFlags.None),
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            }
        );

        // ─── Clear Phase2 candidate buffers ───
        if (useHiZBuffers)
        {
            graph.AddPass(new ClusterClearBuffersPass(
                RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid,
                RenderGraphHandle.Invalid, hPhase2CandidateCount, RenderGraphHandle.Invalid,
                RenderGraphHandle.Invalid, hPhase2CandidateArgs
            ));
        }

        // Clear debug buffer if needed
        if (debugShowHiZAABBs)
        {
            graph.AddPass<object>(
                "ClearDebugHiZ",
                (builder, _) => { builder.Write(hDebugHiZOutput, ResourceState.CopyDest); },
                (rgCtx, _) =>
                {
                    var buf = rgCtx.GetBuffer(hDebugHiZOutput);
                    if (buf != null)
                    {
                        var ctx2 = rgCtx.RenderContext.ImmediateContext;
                        ReadOnlySpan<uint> zero = stackalloc uint[] { 0 };
                        ctx2?.UpdateBuffer(buf, 0, zero, ResourceStateTransitionMode.Verify);
                    }
                }
            );
        }

        // ─── Dispatch cull path ───
        if (config.HiZMode == HiZDebugMode.Legacy || config.HiZMode == HiZDebugMode.Phase1OnlyPassAll)
        {
            var cullPass = new ClusterCullPass(context, ClusterCullPhase.Legacy, "CullLegacy");
            cullPass.HCandidateClusters = traverse.CandidateClusters;
            cullPass.HCandidateArgs = traverse.CandidateArgs;
            cullPass.HCandidateCount = traverse.CandidateCount;
            cullPass.HVisibleClusters = hVisibleClusters;
            cullPass.HIndirectDrawArgs = hIndirectDrawArgs;
            cullPass.HCullingUniforms = hCullingUniforms;
            cullPass.HGlobalTransformBuffer = globals.GlobalTransform;
            cullPass.HPageHeap = globals.PageHeap;
            cullPass.HDebugHiZOutput = hDebugHiZOutput;
            graph.AddPass(cullPass);

            return new ClusterCullOutput(hVisibleClusters, hIndirectDrawArgs, hPhase2IndirectDrawArgs, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, hDebugHiZOutput);
        }

        // Phase1 Cull
        var phase1Pass = new ClusterCullPass(context, ClusterCullPhase.Phase1, "CullPhase1");
        phase1Pass.HCandidateClusters = traverse.CandidateClusters;
        phase1Pass.HCandidateArgs = traverse.CandidateArgs;
        phase1Pass.HCandidateCount = traverse.CandidateCount;
        phase1Pass.HVisibleClusters = hVisibleClusters;
        phase1Pass.HIndirectDrawArgs = hIndirectDrawArgs;
        phase1Pass.HHiZTexture = config.HiZMode == HiZDebugMode.Phase1Only ? RenderGraphHandle.Invalid : hPrevHiZ;
        phase1Pass.HCullingUniforms = hCullingUniforms;
        phase1Pass.HGlobalTransformBuffer = globals.GlobalTransform;
        phase1Pass.HPageHeap = globals.PageHeap;
        phase1Pass.HPhase2CandidateClusters = hPhase2CandidateClusters;
        phase1Pass.HPhase2CandidateCount = hPhase2CandidateCount;
        phase1Pass.HDebugHiZOutput = hDebugHiZOutput;
        graph.AddPass(phase1Pass);

        return new ClusterCullOutput(hVisibleClusters, hIndirectDrawArgs, hPhase2IndirectDrawArgs, hPhase2CandidateCount, hPhase2CandidateClusters, hPhase2CandidateArgs, hDebugHiZOutput);
    }

    /// <summary>
    /// Phase2 passes（在 Phase1 Draw + HiZ Build 之后调用）。
    /// </summary>
    public static void AddPhase2Passes(
        RenderGraph graph,
        RenderContext context,
        in ClusterCullOutput cullOut,
        in ClusterGlobalResources globals,
        RenderGraphHandle hCullingUniforms,
        RenderGraphHandle hHiZ)
    {
        // Phase 2 Update Args
        var updateArgsPass = new ClusterCullUpdateArgsPass(context, "CullUpdateArgsPhase2");
        updateArgsPass.HCandidateCount = cullOut.Phase2CandidateCount;
        updateArgsPass.HCandidateArgs = cullOut.Phase2CandidateArgs;
        graph.AddPass(updateArgsPass);

        // Phase 2 Cull
        var phase2Pass = new ClusterCullPass(context, ClusterCullPhase.Phase2, "CullPhase2");
        phase2Pass.HCandidateClusters = cullOut.Phase2CandidateClusters;
        phase2Pass.HCandidateArgs = cullOut.Phase2CandidateArgs;
        phase2Pass.HCandidateCount = cullOut.Phase2CandidateCount;
        phase2Pass.HVisibleClusters = cullOut.VisibleClusters;
        phase2Pass.HIndirectDrawArgs = cullOut.DrawArgs;
        phase2Pass.HPhase2IndirectDrawArgs = cullOut.Phase2DrawArgs;
        phase2Pass.HHiZTexture = hHiZ;
        phase2Pass.HCullingUniforms = hCullingUniforms;
        phase2Pass.HGlobalTransformBuffer = globals.GlobalTransform;
        phase2Pass.HPageHeap = globals.PageHeap;
        phase2Pass.HDebugHiZOutput = cullOut.DebugHiZOutput;
        graph.AddPass(phase2Pass);
    }

    /// <summary>
    /// 全 HiZ 重建（在 Draw 后调用，为下帧 Phase1 准备完整深度）。
    /// </summary>
    public static void AddFinalHiZBuild(RenderGraph graph, RenderContext context, RenderGraphHandle depthTarget, RenderGraphHandle hCurrHiZ, uint hizMipCount)
    {
        HiZBuildPSOs.EnsureInitialized(context);
        graph.AddPass(new HiZMip0Pass(context, depthTarget, hCurrHiZ));
        for (uint mip = 1; mip < hizMipCount; mip++)
            graph.AddPass(new HiZDownsamplePass(context, hCurrHiZ, mip));
    }
}
