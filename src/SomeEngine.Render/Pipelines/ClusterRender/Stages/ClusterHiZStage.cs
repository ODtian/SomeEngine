using System.Numerics;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 封装 HiZ 2-Phase 遮挡剔除全流程的静态 Stage。
/// Phase 1: Cull → RasterBin → Draw → HiZ Build
/// Phase 2: Cull → RasterBin → Draw → Final HiZ Build
/// </summary>
public static class ClusterHiZ
{
    /// <summary>
    /// HiZ 2-Phase 编排配置。
    /// </summary>
    public readonly record struct HiZConfig
    {
        public HiZDebugMode HiZMode { get; init; }
        public ClusterDebugMode DebugMode { get; init; }
        public bool Wireframe { get; init; }
        public bool Overdraw { get; init; }
        public bool DebugShowHiZAABBs { get; init; }
        public bool DumpNextFrame { get; init; }
        public bool BypassCulling { get; init; }
    }

    /// <summary>
    /// 2-Phase 编排结果。
    /// </summary>
    public readonly record struct HiZResult(
        ClusterCullOutput Cull,
        ClusterRasterOutput Raster
    );

    /// <summary>
    /// 完整 2-Phase HiZ 编排流程。
    /// 内部管理：Phase2 buffers / HiZ PingPong / Cull / RasterBin / Draw / HiZ Build。
    /// </summary>
    public static HiZResult Add2PhasePipeline(
        RenderGraph graph,
        RenderContext context,
        in ClusterTraverseOutput traverse,
        in ClusterGlobalResources globals,
        in ClusterCameraData camera,
        RenderGraphHandle hDrawUniforms,
        RenderGraphHandle hMaterialSlotBuffer,
        BinSpace binSpace,
        int rasterBinFieldIndex,
        PingPongHandle hizPingPong,
        RenderGraphHandle depthTarget,
        in HiZConfig hizConfig
    )
    {
        uint screenWidth = camera.ScreenWidth;
        uint screenHeight = camera.ScreenHeight;
        uint hizWidth = Math.Max(screenWidth, 1);
        uint hizHeight = Math.Max(screenHeight, 1);
        uint hizMipCount = ClusterCull.CalculateMipCount(hizWidth, hizHeight);
        var hizInvSize = new Vector2(1.0f / hizWidth, 1.0f / hizHeight);

        // ─── HiZ PingPong textures ───
        var hCurrHiZ = RenderGraphHandle.Invalid;
        var hPrevHiZ = RenderGraphHandle.Invalid;
        bool useHiZ = hizConfig.HiZMode != HiZDebugMode.Legacy
                   && hizConfig.HiZMode != HiZDebugMode.Phase1OnlyPassAll;

        if (useHiZ)
        {
            var hizDesc = new TextureDesc
            {
                Type = ResourceDimension.Tex2d,
                Width = hizWidth,
                Height = hizHeight,
                MipLevels = hizMipCount,
                Format = TextureFormat.R32_Float,
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            };
            hizPingPong.Prepare(graph, "HiZ", hizDesc, out hCurrHiZ, out hPrevHiZ);
        }

        if (hCurrHiZ.IsValid)
            graph.MarkOutput(hCurrHiZ);

        // ─── Phase2 + utility buffers ───
        var hPhase2IndirectDrawArgs = graph.CreateBuffer("Phase2IndirectDrawArgs", new BufferDesc
        {
            Size = 256,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
        });
        var hZeroOffsetBuffer = graph.CreateBuffer("ZeroOffsetBuffer", new BufferDesc
        {
            Size = 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
        });
        graph.AddPass(new ClusterClearBuffersPass(
            RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, RenderGraphHandle.Invalid,
            RenderGraphHandle.Invalid, RenderGraphHandle.Invalid,
            hPhase2IndirectDrawArgs, hZeroOffsetBuffer, RenderGraphHandle.Invalid
        ));

        // ─── Cull Phase1 ───
        var cullConfig = ClusterCullConfig.Default() with
        {
            HiZMode = hizConfig.HiZMode,
            HiZTexture = hPrevHiZ,
            HasPrevHistory = hizPingPong.HasHistory,
            HiZMipCount = hizMipCount,
            HiZInvSize = hizInvSize,
            DebugShowHiZAABBs = hizConfig.DebugShowHiZAABBs,
            DumpNextFrame = hizConfig.DumpNextFrame,
        };
        var cullOut = ClusterCull.AddPasses(graph, context, traverse, globals,
            traverse.CullingUniforms, cullConfig, hCurrHiZ, hPrevHiZ,
            hizPingPong.HasHistory, hPhase2IndirectDrawArgs, hizConfig.DebugShowHiZAABBs);

        // ─── RasterBin Phase1 ───
        var rasterBinP1 = ClusterRasterBin.AddPasses(graph, context, cullOut,
            globals.GlobalInstanceHeader, cullOut.DrawArgs, cullOut.Phase2DrawArgs, hMaterialSlotBuffer,
            (uint)binSpace.SlotCapacity, (uint)rasterBinFieldIndex);

        // ─── Draw Phase1 ───
        var drawConfigP1 = ClusterDrawConfig.Opaque() with
        {
            DebugMode = hizConfig.DebugMode,
            Wireframe = hizConfig.Wireframe,
            Overdraw = hizConfig.Overdraw,
            VisibleClusterMeta = hZeroOffsetBuffer,
        };
        var rasterP1 = ClusterDraw.AddPasses(graph, context, rasterBinP1, cullOut, globals,
            hDrawUniforms, drawConfigP1, depthTarget, screenWidth, screenHeight);

        // ─── Phase1 HiZ Build ───
        if ((hizConfig.HiZMode == HiZDebugMode.Phase1ThenHiZ
          || hizConfig.HiZMode == HiZDebugMode.Full2Phase)
            && hCurrHiZ.IsValid)
        {
            ClusterCull.AddFinalHiZBuild(graph, context, depthTarget, hCurrHiZ, hizMipCount);
        }

        ClusterRasterOutput finalRaster = rasterP1;

        // ─── Phase2 (only for Full2Phase mode) ───
        if (hizConfig.HiZMode == HiZDebugMode.Full2Phase && hCurrHiZ.IsValid)
        {
            ClusterCull.AddPhase2Passes(graph, context, cullOut, globals,
                traverse.CullingUniforms, hCurrHiZ);

            var rasterBinP2 = ClusterRasterBin.AddPasses(graph, context, cullOut,
                globals.GlobalInstanceHeader, cullOut.Phase2DrawArgs, cullOut.DrawArgs, hMaterialSlotBuffer,
                (uint)binSpace.SlotCapacity, (uint)rasterBinFieldIndex, tag: "P2");

            var drawConfigP2 = ClusterDrawConfig.Opaque() with
            {
                ClearTargets = false,
                DebugMode = hizConfig.DebugMode,
                Wireframe = hizConfig.Wireframe,
                Overdraw = hizConfig.Overdraw,
                Tag = "P2",
                VisibleClusterMeta = hZeroOffsetBuffer,
            };
            finalRaster = ClusterDraw.AddPasses(graph, context, rasterBinP2, cullOut, globals,
                hDrawUniforms, drawConfigP2, depthTarget, screenWidth, screenHeight,
                hOutputVisBuffer: rasterP1.VisBuffer, hOutputDepth: rasterP1.DepthTarget);

            ClusterCull.AddFinalHiZBuild(graph, context, depthTarget, hCurrHiZ, hizMipCount);
        }

        if (useHiZ)
            hizPingPong.EndFrame();

        return new HiZResult(cullOut, finalRaster);
    }
}
