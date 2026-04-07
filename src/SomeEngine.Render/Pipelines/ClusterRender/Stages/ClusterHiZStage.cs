using System.Numerics;
using System.Runtime.InteropServices;
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
        /// <summary>Use software rasterizer instead of HW draw.</summary>
        public bool UseSWRaster { get; init; }
        /// <summary>Enable DeformCache: pre-deform vertices before raster.</summary>
        public bool UseDeformCache { get; init; }
        public float QuantStep { get; init; }
        public System.Numerics.Vector3 QuantOrigin { get; init; }
    }

    /// <summary>
    /// 2-Phase 编排结果。
    /// </summary>
    public readonly record struct HiZResult(
        ClusterCullOutput Cull,
        ClusterRasterOutput Raster,
        RenderGraphHandle HiZTexture,
        RenderGraphHandle DeformCache,
        RenderGraphHandle CacheOffsets
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
        int vertexEvalFieldIndex,
        PingPongHandle hizPingPong,
        RenderGraphHandle depthTarget,
        in HiZConfig hizConfig,
        uint instanceCount,
        ShadePSOGroup[]? deformPSOGroups = null,
        ShadePSOGroup[]? swRasterPSOGroups = null
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

        // ─── Rebuild CullingUniforms with correct HiZ params ───
        // Traverse stage hardcodes HiZ fields to 0/false; recreate with actual values.
        var prevViewProjT = Matrix4x4.Transpose(camera.PrevViewProj);
        var cullCullingData = CullingUniforms.Create(
            camera.View, camera.Proj, camera.CameraPos,
            camera.LodThreshold, camera.LodScale, camera.ForcedLODLevel,
            instanceCount, false, hizConfig.DumpNextFrame, hizConfig.DebugShowHiZAABBs,
            prevViewProjT, hizPingPong.HasHistory, hizMipCount, hizInvSize,
            camera.PrevView, camera.PrevProj,
            hizConfig.QuantOrigin, hizConfig.QuantStep,
            camera.ScreenWidth, camera.ScreenHeight
        );
        var hCullUniforms = CreateDynamicUniformPass(graph, "CullUniforms", cullCullingData);

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
            hCullUniforms, cullConfig, hCurrHiZ, hPrevHiZ,
            hizPingPong.HasHistory, hPhase2IndirectDrawArgs, hizConfig.DebugShowHiZAABBs);

        // ─── RasterBin Phase1 ───
        var rasterBinP1 = ClusterRasterBin.AddPasses(graph, context, cullOut,
            globals.GlobalInstanceHeader, cullOut.DrawArgs, cullOut.Phase2DrawArgs, hMaterialSlotBuffer,
            globals.PageHeap,
            (uint)binSpace.SlotCapacity, (uint)rasterBinFieldIndex, (uint)binSpace.GetTotalBinCount(rasterBinFieldIndex));
        uint rasterBinCount = Math.Max((uint)binSpace.GetTotalBinCount(rasterBinFieldIndex), 1u);

        // ─── DeformCache Phase1 (Optional) ───
        const uint MaxDeformClusters = 200_000;
        const uint MaxDeformVertices = MaxDeformClusters * 64;
        const uint DeformVertexStride = 8; // half3 packed
        var hDeformCache = RenderGraphHandle.Invalid;
        var hCacheOffsets = RenderGraphHandle.Invalid;
        var hCacheAllocCounter = RenderGraphHandle.Invalid;

        if (hizConfig.UseDeformCache)
        {
            hDeformCache = graph.CreateBuffer("DeformCache", new BufferDesc
            {
                Size = MaxDeformVertices * DeformVertexStride,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            });
            hCacheOffsets = graph.CreateBuffer("CacheOffsets", new BufferDesc
            {
                Size = (ulong)(ClusterLimits.MaxDraws * sizeof(uint)),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = sizeof(uint),
            });
            hCacheAllocCounter = graph.CreateBuffer("CacheAllocCounter", new BufferDesc
            {
                Size = 4,
                BindFlags = BindFlags.UnorderedAccess,
                Mode = BufferMode.Raw,
            });
            graph.AddPass(
                "ClearCacheAllocCounter",
                builder => { builder.Write(hCacheAllocCounter, ResourceState.CopyDest); },
                rgCtx =>
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    var buf = rgCtx.GetBuffer(hCacheAllocCounter);
                    if (ctx2 != null && buf != null)
                    {
                        Span<uint> zero = [0];
                        ctx2.UpdateBuffer(buf, 0, zero, ResourceStateTransitionMode.None);
                    }
                }
            );
            // Upload DeformUniforms
            var deformUniformData = new DeformUniforms
            {
                QuantOrigin = hizConfig.QuantOrigin,
                QuantStep = hizConfig.QuantStep,
                MaxVisibleClusters = ClusterLimits.MaxDraws,
                MaxDeformVertices = MaxDeformVertices,
                MaxRasterBins = rasterBinCount,
                MaxClusterVertices = 64,
            };
            var hDeformUniforms = CreateDynamicUniformPass(graph, "DeformUniforms", deformUniformData);

            // Create dispatch args buffer for deform pass
            var hDeformDispatchArgs = graph.CreateBuffer("DeformDispatchArgs", new BufferDesc
            {
                Size = 12,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
                Mode = BufferMode.Raw,
            });

            // Phase1 read offset must be zero (no prior phase).
            // Phase2DrawArgs is RG-cached and may have stale data from last frame,
            // so use a dedicated buffer that is explicitly cleared every frame.
            var hP1ZeroOffset = graph.CreateBuffer("P1DeformZeroOffset", new BufferDesc
            {
                Size = 12, // CullDrawArgs = {Pad0, SWCount, HWCount}
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            });
            graph.AddPass(
                "ClearP1DeformZeroOffset",
                builder => { builder.Write(hP1ZeroOffset, ResourceState.CopyDest); },
                rgCtx =>
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    var buf = rgCtx.GetBuffer(hP1ZeroOffset);
                    if (ctx2 != null && buf != null)
                    {
                        Span<uint> zeros = [0, 0, 0];
                        ctx2.UpdateBuffer(buf, 0, zeros, ResourceStateTransitionMode.None);
                    }
                }
            );

            graph.AddPass(new ClusterDeformPrepareVisibleArgsPass(context, "P1DeformPrepareVisibleArgs")
            {
                HDrawArgs = cullOut.DrawArgs,
                HDeformDispatchArgs = hDeformDispatchArgs,
            });

            graph.AddPass(new ClusterDeformInitVisiblePass(context, "P1DeformInitVisible")
            {
                HDrawArgs = cullOut.DrawArgs,
                HReadOffsetArgs = hP1ZeroOffset,
                HVisibleClusters = cullOut.VisibleClusters,
                HPageHeap = globals.PageHeap,
                HDeformUniforms = hDeformUniforms,
                HDeformDispatchArgs = hDeformDispatchArgs,
                HCacheAllocCounter = hCacheAllocCounter,
                HCacheOffsets = hCacheOffsets,
            });

            graph.AddPass(new ClusterDeformPass(context, "P1Deform")
            {
                HBinnedClusterIndex = rasterBinP1.BinnedClusterIndex,
                HVisibleClusters = cullOut.VisibleClusters,
                HRasterBinMeta = rasterBinP1.RasterBinMeta,
                HPageHeap = globals.PageHeap,
                HGlobalTransformBuffer = globals.GlobalTransform,
                HDeformUniforms = hDeformUniforms,
                HBinnedSWDispatchArgs = rasterBinP1.BinnedSWDispatchArgs,
                HDeformCache = hDeformCache,
                HCacheOffsets = hCacheOffsets,
                PSOGroups = deformPSOGroups,
                DeformUniformData = deformUniformData,
            });
        }

        // ─── Draw Phase1 (HW only or hybrid SW+HW) ───
        ClusterRasterOutput rasterP1;
        if (hizConfig.UseSWRaster)
        {
            var swRasterP1 = ClusterSWDraw.AddPasses(graph, context, rasterBinP1, cullOut, globals,
                camera, hizConfig.QuantStep, hizConfig.QuantOrigin, depthTarget, tag: "P1SW_",
                rasterBinCount: rasterBinCount,
                swRasterPSOGroups: swRasterPSOGroups,
                debugSWHWView: hizConfig.DebugMode == ClusterDebugMode.SWHWView,
                hDeformCache: hDeformCache, hCacheOffsets: hCacheOffsets);

            // ─── Merge SW depth into HW depth target ───
            var mergeP1 = new DepthMergePass(context, "P1DepthMerge")
            {
                HSWDepthUAV = swRasterP1.RasterDepth,
                HDepthTarget = depthTarget,
            };
            graph.AddPass(mergeP1);

            var hwDrawConfigP1 = ClusterDrawConfig.Opaque() with
            {
                ClearTargets = false,
                DebugMode = hizConfig.DebugMode,
                Wireframe = hizConfig.Wireframe,
                Overdraw = hizConfig.Overdraw,
                VisibleClusterMeta = rasterBinP1.BinnedDrawArgs,
                UseHWDrawArgs = true,
                Tag = "P1HW",
            };
            var hwRasterP1 = ClusterDraw.AddPasses(graph, context, rasterBinP1, cullOut, globals,
                hDrawUniforms, hwDrawConfigP1, depthTarget, screenWidth, screenHeight,
                hOutputVisBuffer: swRasterP1.VisBuffer, hOutputDepth: depthTarget);
            rasterP1 = new ClusterRasterOutput(hwRasterP1.VisBuffer, hwRasterP1.DepthTarget, swRasterP1.RasterDepth);
        }
        else
        {
            var drawConfigP1 = ClusterDrawConfig.Opaque() with
            {
                DebugMode = hizConfig.DebugMode,
                Wireframe = hizConfig.Wireframe,
                Overdraw = hizConfig.Overdraw,
                VisibleClusterMeta = hZeroOffsetBuffer,
            };
            rasterP1 = ClusterDraw.AddPasses(graph, context, rasterBinP1, cullOut, globals,
                hDrawUniforms, drawConfigP1, depthTarget, screenWidth, screenHeight);
        }

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
                hCullUniforms, hCurrHiZ);

            var rasterBinP2 = ClusterRasterBin.AddPasses(graph, context, cullOut,
                globals.GlobalInstanceHeader, cullOut.Phase2DrawArgs, cullOut.DrawArgs, hMaterialSlotBuffer,
                globals.PageHeap,
                (uint)binSpace.SlotCapacity, (uint)rasterBinFieldIndex, (uint)binSpace.GetTotalBinCount(rasterBinFieldIndex), tag: "P2");

            // ─── DeformCache Phase2 (reuse same buffers) ───
            if (hizConfig.UseDeformCache && hDeformCache.IsValid)
            {
                var deformUniformData2 = new DeformUniforms
                {
                    QuantOrigin = hizConfig.QuantOrigin,
                    QuantStep = hizConfig.QuantStep,
                    MaxVisibleClusters = ClusterLimits.MaxDraws,
                    MaxDeformVertices = MaxDeformVertices,
                    MaxRasterBins = rasterBinCount,
                    MaxClusterVertices = 64,
                };
                var hDeformUniforms2 = CreateDynamicUniformPass(graph, "P2DeformUniforms", deformUniformData2);

                // Create Phase2 dispatch args buffer
                var hDeformDispatchArgs2 = graph.CreateBuffer("P2DeformDispatchArgs", new BufferDesc
                {
                    Size = 12,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
                    Mode = BufferMode.Raw,
                });

                graph.AddPass(new ClusterDeformPrepareVisibleArgsPass(context, "P2DeformPrepareVisibleArgs")
                {
                    HDrawArgs = cullOut.Phase2DrawArgs,
                    HDeformDispatchArgs = hDeformDispatchArgs2,
                });

                graph.AddPass(new ClusterDeformInitVisiblePass(context, "P2DeformInitVisible")
                {
                    HDrawArgs = cullOut.Phase2DrawArgs,
                    HReadOffsetArgs = cullOut.DrawArgs,
                    HVisibleClusters = cullOut.VisibleClusters,
                    HPageHeap = globals.PageHeap,
                    HDeformUniforms = hDeformUniforms2,
                    HDeformDispatchArgs = hDeformDispatchArgs2,
                    HCacheAllocCounter = hCacheAllocCounter,
                    HCacheOffsets = hCacheOffsets,
                });

                graph.AddPass(new ClusterDeformPass(context, "P2Deform")
                {
                    HBinnedClusterIndex = rasterBinP2.BinnedClusterIndex,
                    HVisibleClusters = cullOut.VisibleClusters,
                    HRasterBinMeta = rasterBinP2.RasterBinMeta,
                    HPageHeap = globals.PageHeap,
                    HGlobalTransformBuffer = globals.GlobalTransform,
                    HDeformUniforms = hDeformUniforms2,
                    HBinnedSWDispatchArgs = rasterBinP2.BinnedSWDispatchArgs,
                    HDeformCache = hDeformCache,
                    HCacheOffsets = hCacheOffsets,
                    PSOGroups = deformPSOGroups,
                    DeformUniformData = deformUniformData2,
                });
            }

            if (hizConfig.UseSWRaster)
            {
                var swRasterP2 = ClusterSWDraw.AddPasses(graph, context, rasterBinP2, cullOut, globals,
                    camera, hizConfig.QuantStep, hizConfig.QuantOrigin, depthTarget,
                    hOutputVisBuffer: rasterP1.VisBuffer,
                    hOutputDepthUAV: rasterP1.RasterDepth,
                    clearTargets: false, tag: "P2SW_",
                    rasterBinCount: rasterBinCount,
                    swRasterPSOGroups: swRasterPSOGroups,
                    debugSWHWView: hizConfig.DebugMode == ClusterDebugMode.SWHWView,
                    hDeformCache: hDeformCache, hCacheOffsets: hCacheOffsets);

                // ─── Merge SW depth into HW depth target (Phase2) ───
                var mergeP2 = new DepthMergePass(context, "P2DepthMerge")
                {
                    HSWDepthUAV = swRasterP2.RasterDepth,
                    HDepthTarget = depthTarget,
                };
                graph.AddPass(mergeP2);

                var hwDrawConfigP2 = ClusterDrawConfig.Opaque() with
                {
                    ClearTargets = false,
                    DebugMode = hizConfig.DebugMode,
                    Wireframe = hizConfig.Wireframe,
                    Overdraw = hizConfig.Overdraw,
                    Tag = "P2HW",
                    VisibleClusterMeta = rasterBinP2.BinnedDrawArgs,
                    UseHWDrawArgs = true,
                };
                var hwRasterP2 = ClusterDraw.AddPasses(graph, context, rasterBinP2, cullOut, globals,
                    hDrawUniforms, hwDrawConfigP2, depthTarget, screenWidth, screenHeight,
                    hOutputVisBuffer: swRasterP2.VisBuffer, hOutputDepth: rasterP1.DepthTarget);
                finalRaster = new ClusterRasterOutput(hwRasterP2.VisBuffer, hwRasterP2.DepthTarget, swRasterP2.RasterDepth);
            }
            else
            {
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
            }

            ClusterCull.AddFinalHiZBuild(graph, context, depthTarget, hCurrHiZ, hizMipCount);
        }

        if (useHiZ)
            hizPingPong.EndFrame();

        return new HiZResult(cullOut, finalRaster, hCurrHiZ, hDeformCache, hCacheOffsets);
    }

    private static RenderGraphHandle CreateDynamicUniformPass<T>(
        RenderGraph graph, string name, T data) where T : unmanaged
    {
        var handle = graph.CreateBuffer(name, new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<T>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        graph.AddPass(
            $"Upload{name}",
            builder => { builder.Write(handle, ResourceState.ConstantBuffer); },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(handle);
                if (ctx2 != null && buf != null)
                {
                    var span = ctx2.MapBuffer<T>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = data;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );
        return handle;
    }
}
