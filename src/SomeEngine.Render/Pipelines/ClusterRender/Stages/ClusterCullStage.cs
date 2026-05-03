using System.Numerics;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 无状态 Cull 工具函数。
/// PSO 在 ClusterCullPSOs 内 static 缓存。每次调用创建轻量 pass 实例。
/// </summary>
internal static partial class ClusterCull
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
        Resources resources,
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
        resources.EnsureInitialized(context);

        uint maxDraws = ClusterLimits.MaxDraws;

        // ─── Create cull output buffers ───
        var hVisibleClusters = graph.CreateBuffer("VisibleClusters", new BufferDesc
        {
            Size = (ulong)(maxDraws * 16),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 16,
        });

        var hIndirectDrawArgs = traverse.IndirectDrawArgs;

        // Phase 2 candidate buffers
        var hPhase2CandidateClusters = RenderGraphHandle.Invalid;
        var hPhase2CandidateCount = RenderGraphHandle.Invalid;
        var hPhase2CandidateArgs = RenderGraphHandle.Invalid;

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
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        hPhase2CandidateArgs = graph.CreateBuffer("Phase2CandidateArgs", new BufferDesc
        {
            Size = 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        // ─── Debug HiZ output buffer ───
        // DumpNextFrame and the AABB overlay both make the shader write
        // DebugHiZOutput. Allocate the full buffer when either is enabled.
        bool enableHiZDebugOutput = debugShowHiZAABBs || config.DumpNextFrame;
        var hDebugHiZOutput = graph.CreateBuffer(
            enableHiZDebugOutput ? "DebugHiZOutput" : "DebugHiZOutputDummy",
            new BufferDesc
            {
                Size = enableHiZDebugOutput ? ClusterHiZDebugLayout.BufferBytes : 16u,
                BindFlags = BindFlags.UnorderedAccess | (debugShowHiZAABBs ? BindFlags.ShaderResource : BindFlags.None),
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            }
        );

        // ─── Clear Phase2 candidate buffers ───
        graph.AddPass(new ClusterClearBuffersPass(
            RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid,
            RenderGraphHandle.Invalid, hPhase2CandidateCount, RenderGraphHandle.Invalid,
            RenderGraphHandle.Invalid, hPhase2CandidateArgs
        ));

        // Clear debug buffer if needed
        if (enableHiZDebugOutput)
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
                        Span<uint> header = stackalloc uint[(int)ClusterHiZDebugLayout.HeaderWords];
                        header[(int)ClusterHiZDebugLayout.SampleCapacityWord] = ClusterHiZDebugLayout.MaxSamples;
                        header[(int)ClusterHiZDebugLayout.SampleStrideBytesWord] = ClusterHiZDebugLayout.SampleStrideBytes;
                        header[(int)ClusterHiZDebugLayout.HeaderBytesWord] = ClusterHiZDebugLayout.HeaderBytes;
                        ctx2?.UpdateBuffer(buf, 0, header, ResourceStateTransitionMode.None);
                    }
                }
            );
        }

        // Phase1 Cull
        var phase1Pass = new ClusterCullPass(context, resources, ClusterCullPhase.Phase1, "CullPhase1");
        phase1Pass.HCandidateClusters = traverse.CandidateClusters;
        phase1Pass.HCandidateArgs = traverse.CandidateArgs;
        phase1Pass.HCandidateCount = traverse.CandidateCount;
        phase1Pass.HVisibleClusters = hVisibleClusters;
        phase1Pass.HIndirectDrawArgs = hIndirectDrawArgs;
        phase1Pass.HHiZTexture = config.HiZMode == HiZDebugMode.Phase1Only ? RenderGraphHandle.Invalid : hPrevHiZ;
        phase1Pass.HCullingUniforms = hCullingUniforms;
        phase1Pass.HGlobalTransformBuffer = globals.GlobalTransform;
        phase1Pass.HGlobalInstanceHeaderBuffer = globals.GlobalInstanceHeader;
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
        Resources resources,
        in ClusterCullOutput cullOut,
        in ClusterGlobalResources globals,
        RenderGraphHandle hCullingUniforms,
        RenderGraphHandle hHiZ)
    {
        // Phase 2 Update Args
        var updateArgsPass = new ClusterCullUpdateArgsPass(context, resources, "CullUpdateArgsPhase2");
        updateArgsPass.HCandidateCount = cullOut.Phase2CandidateCount;
        updateArgsPass.HCandidateArgs = cullOut.Phase2CandidateArgs;
        updateArgsPass.HCullingUniforms = hCullingUniforms;
        graph.AddPass(updateArgsPass);

        // Phase 2 Cull
        var phase2Pass = new ClusterCullPass(context, resources, ClusterCullPhase.Phase2, "CullPhase2");
        phase2Pass.HCandidateClusters = cullOut.Phase2CandidateClusters;
        phase2Pass.HCandidateArgs = cullOut.Phase2CandidateArgs;
        phase2Pass.HCandidateCount = cullOut.Phase2CandidateCount;
        phase2Pass.HVisibleClusters = cullOut.VisibleClusters;
        phase2Pass.HIndirectDrawArgs = cullOut.DrawArgs;
        phase2Pass.HPhase2IndirectDrawArgs = cullOut.Phase2DrawArgs;
        phase2Pass.HHiZTexture = hHiZ;
        phase2Pass.HCullingUniforms = hCullingUniforms;
        phase2Pass.HGlobalTransformBuffer = globals.GlobalTransform;
        phase2Pass.HGlobalInstanceHeaderBuffer = globals.GlobalInstanceHeader;
        phase2Pass.HPageHeap = globals.PageHeap;
        phase2Pass.HDebugHiZOutput = cullOut.DebugHiZOutput;
        graph.AddPass(phase2Pass);
    }

    /// <summary>
    /// 全 HiZ 重建（在 Draw 后调用，为下帧 Phase1 准备完整深度）。
    /// </summary>
    public static void AddFinalHiZBuild(RenderGraph graph, RenderContext context, ClusterHiZ.HiZBuildResources hiZResources, RenderGraphHandle depthTarget, RenderGraphHandle hCurrHiZ, uint hizMipCount)
    {
        hiZResources.EnsureInitialized(context);
        graph.AddPass(new HiZMip0Pass(context, hiZResources, depthTarget, hCurrHiZ));
        for (uint mip = 1; mip < hizMipCount; mip++)
            graph.AddPass(new HiZDownsamplePass(context, hiZResources, hCurrHiZ, mip));
    }
}
