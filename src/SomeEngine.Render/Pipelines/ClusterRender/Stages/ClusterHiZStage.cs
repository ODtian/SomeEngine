using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 封装 HiZ 2-Phase 遮挡剔除全流程的静�?Stage�?/// Phase 1: Cull �?RasterBin �?Draw �?HiZ Build
/// Phase 2: Cull �?RasterBin �?Draw �?Final HiZ Build
/// </summary>
public static partial class ClusterHiZ
{
    public sealed class Resources : IDisposable
    {
        internal readonly ClusterCull.Resources Cull = new();
        internal readonly ClusterRasterBin.Resources RasterBin = new();
        internal readonly ClusterDraw.Resources Draw = new();
        internal readonly HiZBuildResources HiZBuild = new();
        internal readonly DepthMergeResources DepthMerge = new();
        internal readonly DeformResources Deform = new();
        internal readonly ClusterDeformBinStage.Resources DeformBin = new();

        public void Dispose()
        {
            Cull.Dispose();
            RasterBin.Dispose();
            Draw.Dispose();
            HiZBuild.Dispose();
            DepthMerge.Dispose();
            Deform.Dispose();
            DeformBin.Dispose();
        }
    }

    /// <summary>
    /// HiZ 2-Phase 编排配置�?    /// </summary>
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
        public ulong DeformCacheByteCapacity { get; init; }
        public float QuantStep { get; init; }
        public System.Numerics.Vector3 QuantOrigin { get; init; }
    }

    /// <summary>
    /// 2-Phase 编排结果�?    /// </summary>
    public readonly record struct HiZResult(
        ClusterCullOutput Cull,
        ClusterRasterOutput Raster,
        RenderGraphHandle HiZTexture,
        RenderGraphHandle DeformCache,
        RenderGraphHandle CacheOffsets
    );

    private const ulong MaxShaderAddressableDeformCacheBytes = 0xFFFFF000UL;

    /// <summary>
    /// 完整 2-Phase HiZ 编排流程�?    /// 内部管理：Phase2 buffers / HiZ PingPong / Cull / RasterBin / Draw / HiZ Build�?    /// </summary>
    public static HiZResult Add2PhasePipeline(
        RenderGraph graph,
        RenderContext context,
        Resources resources,
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
        MaterialPSOGroup[]? deformPSOGroups = null,
        MaterialPSOGroup[]? swRasterPSOGroups = null,
        MaterialPSOGroup[]? hwDrawPSOGroups = null,
        FrameTargetRegistry? frameTargets = null
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
        bool useHiZ = true;
        bool hasHiZHistory = false;

        if (useHiZ)
        {
            var hizDesc = CreateHiZTextureDesc(hizWidth, hizHeight, hizMipCount);
            if (frameTargets != null)
            {
                if (!frameTargets.TryGetDeclaration(StandardFrameTargets.HiZ, out _))
                {
                    frameTargets.DeclareTexture(
                        StandardFrameTargets.HiZ,
                        _ => hizDesc,
                        FrameTargetLifetime.History,
                        ResourceState.Unknown,
                        "HiZ"
                    );
                }

                var history = frameTargets.ResolveHistoryTexture(StandardFrameTargets.HiZ);
                hCurrHiZ = history.Current;
                hPrevHiZ = history.Previous;
                hasHiZHistory = history.HasPrevious;
            }
            else
            {
                hizPingPong.Prepare(graph, "HiZ", hizDesc, out hCurrHiZ, out hPrevHiZ);
                hasHiZHistory = hizPingPong.HasHistory;
            }
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
            prevViewProjT, hasHiZHistory, hizMipCount, hizInvSize,
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
            HasPrevHistory = hasHiZHistory,
            HiZMipCount = hizMipCount,
            HiZInvSize = hizInvSize,
            DebugShowHiZAABBs = hizConfig.DebugShowHiZAABBs,
            DumpNextFrame = hizConfig.DumpNextFrame,
        };
        var cullOut = ClusterCull.AddPasses(graph, context, resources.Cull, traverse, globals,
            hCullUniforms, cullConfig, hCurrHiZ, hPrevHiZ,
            hasHiZHistory, hPhase2IndirectDrawArgs, hizConfig.DebugShowHiZAABBs);

        // ─── RasterBin Phase1 ───
        var rasterBinP1 = ClusterRasterBin.AddPasses(graph, context, resources.RasterBin, cullOut,
            globals.GlobalInstanceHeader, cullOut.DrawArgs, cullOut.Phase2DrawArgs, hMaterialSlotBuffer,
            globals.PageHeap,
            (uint)binSpace.SlotCapacity, (uint)rasterBinFieldIndex, (uint)binSpace.GetTotalBinCount(rasterBinFieldIndex));
        uint rasterBinCount = Math.Max((uint)binSpace.GetTotalBinCount(rasterBinFieldIndex), 1u);
        uint deformBinCount = vertexEvalFieldIndex >= 0
            ? Math.Max((uint)binSpace.GetTotalBinCount(vertexEvalFieldIndex), 1u)
            : 1u;
        uint deformBinField = vertexEvalFieldIndex >= 0 ? (uint)vertexEvalFieldIndex : 0u;
        bool hasHWGraphicsGroups = hwDrawPSOGroups is { Length: > 0 };
        var hwMaterialGroups = hasHWGraphicsGroups ? hwDrawPSOGroups : swRasterPSOGroups;
        bool hasDeformGroups = deformPSOGroups is { Length: > 0 };

        // ─── DeformCache Phase1 (Optional) ───
        var hDeformCache = RenderGraphHandle.Invalid;
        var hCacheOffsets = RenderGraphHandle.Invalid;
        var hCacheRequestBytes = RenderGraphHandle.Invalid;
        var hCacheBlockSums0 = RenderGraphHandle.Invalid;
        var hCacheBlockSums1 = RenderGraphHandle.Invalid;
        var hCacheBlockOffsets0 = RenderGraphHandle.Invalid;
        var hCacheBlockOffsets1 = RenderGraphHandle.Invalid;
        ulong deformCacheByteCapacity = 0;
        uint cacheScanBlockCount0 = 0;
        uint cacheScanBlockCount1 = 0;

        if (hizConfig.UseDeformCache && hasDeformGroups)
        {
            const uint cacheStrideBytes = ClusterLimits.DefaultDeformCacheStrideBytes;
            cacheScanBlockCount0 = (ClusterLimits.MaxDraws + DeformResources.CacheScanBlockSize - 1u) / DeformResources.CacheScanBlockSize;
            cacheScanBlockCount1 = (cacheScanBlockCount0 + DeformResources.CacheScanBlockSize - 1u) / DeformResources.CacheScanBlockSize;

            deformCacheByteCapacity = Math.Clamp(
                hizConfig.DeformCacheByteCapacity == 0
                    ? ClusterLimits.DefaultDeformCacheByteCapacity
                    : hizConfig.DeformCacheByteCapacity,
                16UL,
                MaxShaderAddressableDeformCacheBytes);

            hDeformCache = graph.CreateBuffer("DeformCache", new BufferDesc
            {
                Size = deformCacheByteCapacity,
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
            hCacheRequestBytes = graph.CreateBuffer("CacheRequestBytes", new BufferDesc
            {
                Size = (ulong)(ClusterLimits.MaxDraws * sizeof(uint)),
                BindFlags = BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = sizeof(uint),
            });
            hCacheBlockSums0 = graph.CreateBuffer("CacheBlockSums0", new BufferDesc
            {
                Size = (ulong)(cacheScanBlockCount0 * sizeof(uint)),
                BindFlags = BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = sizeof(uint),
            });
            hCacheBlockOffsets0 = graph.CreateBuffer("CacheBlockOffsets0", new BufferDesc
            {
                Size = (ulong)(cacheScanBlockCount0 * sizeof(uint)),
                BindFlags = BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = sizeof(uint),
            });
            hCacheBlockSums1 = graph.CreateBuffer("CacheBlockSums1", new BufferDesc
            {
                Size = (ulong)(cacheScanBlockCount1 * sizeof(uint)),
                BindFlags = BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = sizeof(uint),
            });
            hCacheBlockOffsets1 = graph.CreateBuffer("CacheBlockOffsets1", new BufferDesc
            {
                Size = (ulong)((cacheScanBlockCount1 + 1) * sizeof(uint)),
                BindFlags = BindFlags.UnorderedAccess,
                Mode = BufferMode.Structured,
                ElementByteStride = sizeof(uint),
            });
            // Upload DeformUniforms
            var deformUniformData = new DeformUniforms
            {
                QuantOrigin = hizConfig.QuantOrigin,
                QuantStep = hizConfig.QuantStep,
                MaxVisibleClusters = ClusterLimits.MaxDraws,
                MaxDeformCacheBytes = (uint)Math.Min(deformCacheByteCapacity, uint.MaxValue),
                MaxClusterVertices = ClusterLimits.MaxClusterVertices,
                CacheStrideBytes = cacheStrideBytes,
                CacheScanBlockCount0 = cacheScanBlockCount0,
                CacheScanBlockCount1 = cacheScanBlockCount1,
                ResetCacheAllocationState = 1,
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
                Size = 12, // CullDrawArgs = {VertexCountPerCluster, SWCount, HWCount}
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

            var deformBinP1 = ClusterDeformBinStage.AddPasses(
                graph, context, resources.DeformBin, cullOut,
                globals.GlobalInstanceHeader, cullOut.DrawArgs, hP1ZeroOffset, hMaterialSlotBuffer,
                globals.PageHeap,
                (uint)binSpace.SlotCapacity, deformBinField, deformBinCount, tag: "P1Deform");

            graph.AddPass(new ClusterDeformPrepareVisibleArgsPass(context, resources.Deform, "P1DeformPrepareVisibleArgs")
            {
                HDrawArgs = cullOut.DrawArgs,
                HDeformDispatchArgs = hDeformDispatchArgs,
            });

            graph.AddPass(new ClusterDeformCacheRequestPass(context, resources.Deform, "P1DeformCacheRequest")
            {
                HDrawArgs = cullOut.DrawArgs,
                HReadOffsetArgs = hP1ZeroOffset,
                HVisibleClusters = cullOut.VisibleClusters,
                HPageHeap = globals.PageHeap,
                HDeformUniforms = hDeformUniforms,
                HDeformDispatchArgs = hDeformDispatchArgs,
                HCacheRequestBytes = hCacheRequestBytes,
                HCacheOffsets = hCacheOffsets,
            });

            graph.AddPass(new ClusterDeformCacheScanVisiblePass(context, resources.Deform, "P1DeformCacheScanVisible")
            {
                HDrawArgs = cullOut.DrawArgs,
                HReadOffsetArgs = hP1ZeroOffset,
                HDeformUniforms = hDeformUniforms,
                HCacheRequestBytes = hCacheRequestBytes,
                HCacheOffsets = hCacheOffsets,
                HCacheBlockSums0 = hCacheBlockSums0,
                ThreadGroupCountX = cacheScanBlockCount0,
            });

            graph.AddPass(new ClusterDeformCacheScanBlocks0Pass(context, resources.Deform, "P1DeformCacheScanBlocks0")
            {
                HDeformUniforms = hDeformUniforms,
                HCacheBlockSums0 = hCacheBlockSums0,
                HCacheBlockOffsets0 = hCacheBlockOffsets0,
                HCacheBlockSums1 = hCacheBlockSums1,
                ThreadGroupCountX = cacheScanBlockCount1,
            });

            graph.AddPass(new ClusterDeformCacheScanBlocks1Pass(context, resources.Deform, "P1DeformCacheScanBlocks1")
            {
                HDeformUniforms = hDeformUniforms,
                HCacheBlockSums1 = hCacheBlockSums1,
                HCacheBlockOffsets1 = hCacheBlockOffsets1,
                ThreadGroupCountX = 1,
            });

            graph.AddPass(new ClusterDeformCacheApplyBlockOffsetsPass(context, resources.Deform, "P1DeformCacheApplyOffsets")
            {
                HDrawArgs = cullOut.DrawArgs,
                HReadOffsetArgs = hP1ZeroOffset,
                HDeformUniforms = hDeformUniforms,
                HDeformDispatchArgs = hDeformDispatchArgs,
                HCacheRequestBytes = hCacheRequestBytes,
                HCacheOffsets = hCacheOffsets,
                HCacheBlockOffsets0 = hCacheBlockOffsets0,
                HCacheBlockOffsets1 = hCacheBlockOffsets1,
            });

            graph.AddPass(new ClusterDeformCacheCommitAllocationPass(context, resources.Deform, "P1DeformCacheCommitAllocation")
            {
                HDeformUniforms = hDeformUniforms,
                HCacheBlockSums1 = hCacheBlockSums1,
                HCacheBlockOffsets1 = hCacheBlockOffsets1,
            });

            graph.AddPass(new ClusterDeformPass(context, "P1Deform")
            {
                HBinnedClusterIndex = deformBinP1.DeformBinnedClusterIndex,
                HVisibleClusters = cullOut.VisibleClusters,
                HDeformBinMeta = deformBinP1.DeformBinMeta,
                HPageHeap = globals.PageHeap,
                HGlobalTransformBuffer = globals.GlobalTransform,
                HDeformUniforms = hDeformUniforms,
                HDeformDispatchArgs = deformBinP1.PreDeformDispatchArgs,
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
            var mergeP1 = new DepthMergePass(context, resources.DepthMerge, "P1DepthMerge")
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
                VisibleClusterMeta = rasterBinP1.BinnedHWDrawArgs,
                UseHWDrawArgs = true,
                Tag = "P1HW",
            };
            var hwRasterP1 = ClusterDraw.AddPasses(graph, context, resources.Draw, rasterBinP1, cullOut, globals,
                hDrawUniforms, hwDrawConfigP1, depthTarget, screenWidth, screenHeight,
                materialDispatchGroups: hwMaterialGroups,
                materialDispatchGroupsAreGraphics: hasHWGraphicsGroups,
                hOutputVisBuffer: swRasterP1.VisBuffer, hOutputDepth: depthTarget,
                hDeformCache: hDeformCache, hCacheOffsets: hCacheOffsets);
            rasterP1 = new ClusterRasterOutput(hwRasterP1.VisBuffer, hwRasterP1.DepthTarget, swRasterP1.RasterDepth);
        }
        else
        {
            var drawConfigP1 = ClusterDrawConfig.Opaque() with
            {
                DebugMode = hizConfig.DebugMode,
                Wireframe = hizConfig.Wireframe,
                Overdraw = hizConfig.Overdraw,
                VisibleClusterMeta = rasterBinP1.BinnedDrawArgs,
            };
            rasterP1 = ClusterDraw.AddPasses(graph, context, resources.Draw, rasterBinP1, cullOut, globals,
                hDrawUniforms, drawConfigP1, depthTarget, screenWidth, screenHeight,
                materialDispatchGroups: hwMaterialGroups,
                materialDispatchGroupsAreGraphics: hasHWGraphicsGroups,
                hDeformCache: hDeformCache, hCacheOffsets: hCacheOffsets);
        }

        // ─── Phase1 HiZ Build ───
        if ((hizConfig.HiZMode == HiZDebugMode.Phase1ThenHiZ
          || hizConfig.HiZMode == HiZDebugMode.Full2Phase)
            && hCurrHiZ.IsValid)
        {
            ClusterCull.AddFinalHiZBuild(graph, context, resources.HiZBuild, depthTarget, hCurrHiZ, hizMipCount);
        }

        ClusterRasterOutput finalRaster = rasterP1;

        // ─── Phase2 (only for Full2Phase mode) ───
        if (hizConfig.HiZMode == HiZDebugMode.Full2Phase && hCurrHiZ.IsValid)
        {
            ClusterCull.AddPhase2Passes(graph, context, resources.Cull, cullOut, globals,
                hCullUniforms, hCurrHiZ);

            var rasterBinP2 = ClusterRasterBin.AddPasses(graph, context, resources.RasterBin, cullOut,
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
                    MaxDeformCacheBytes = (uint)Math.Min(deformCacheByteCapacity, uint.MaxValue),
                    MaxClusterVertices = ClusterLimits.MaxClusterVertices,
                    CacheStrideBytes = ClusterLimits.DefaultDeformCacheStrideBytes,
                    CacheScanBlockCount0 = cacheScanBlockCount0,
                    CacheScanBlockCount1 = cacheScanBlockCount1,
                    ResetCacheAllocationState = 0,
                };
                var hDeformUniforms2 = CreateDynamicUniformPass(graph, "P2DeformUniforms", deformUniformData2);

                // Create Phase2 dispatch args buffer
                var hDeformDispatchArgs2 = graph.CreateBuffer("P2DeformDispatchArgs", new BufferDesc
                {
                    Size = 12,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
                    Mode = BufferMode.Raw,
                });

                graph.AddPass(new ClusterDeformPrepareVisibleArgsPass(context, resources.Deform, "P2DeformPrepareVisibleArgs")
                {
                    HDrawArgs = cullOut.Phase2DrawArgs,
                    HDeformDispatchArgs = hDeformDispatchArgs2,
                });

                graph.AddPass(new ClusterDeformCacheRequestPass(context, resources.Deform, "P2DeformCacheRequest")
                {
                    HDrawArgs = cullOut.Phase2DrawArgs,
                    HReadOffsetArgs = cullOut.DrawArgs,
                    HVisibleClusters = cullOut.VisibleClusters,
                    HPageHeap = globals.PageHeap,
                    HDeformUniforms = hDeformUniforms2,
                    HDeformDispatchArgs = hDeformDispatchArgs2,
                    HCacheRequestBytes = hCacheRequestBytes,
                    HCacheOffsets = hCacheOffsets,
                });

                graph.AddPass(new ClusterDeformCacheScanVisiblePass(context, resources.Deform, "P2DeformCacheScanVisible")
                {
                    HDrawArgs = cullOut.Phase2DrawArgs,
                    HReadOffsetArgs = cullOut.DrawArgs,
                    HDeformUniforms = hDeformUniforms2,
                    HCacheRequestBytes = hCacheRequestBytes,
                    HCacheOffsets = hCacheOffsets,
                    HCacheBlockSums0 = hCacheBlockSums0,
                    ThreadGroupCountX = cacheScanBlockCount0,
                });

                graph.AddPass(new ClusterDeformCacheScanBlocks0Pass(context, resources.Deform, "P2DeformCacheScanBlocks0")
                {
                    HDeformUniforms = hDeformUniforms2,
                    HCacheBlockSums0 = hCacheBlockSums0,
                    HCacheBlockOffsets0 = hCacheBlockOffsets0,
                    HCacheBlockSums1 = hCacheBlockSums1,
                    ThreadGroupCountX = cacheScanBlockCount1,
                });

                graph.AddPass(new ClusterDeformCacheScanBlocks1Pass(context, resources.Deform, "P2DeformCacheScanBlocks1")
                {
                    HDeformUniforms = hDeformUniforms2,
                    HCacheBlockSums1 = hCacheBlockSums1,
                    HCacheBlockOffsets1 = hCacheBlockOffsets1,
                    ThreadGroupCountX = 1,
                });

                graph.AddPass(new ClusterDeformCacheApplyBlockOffsetsPass(context, resources.Deform, "P2DeformCacheApplyOffsets")
                {
                    HDrawArgs = cullOut.Phase2DrawArgs,
                    HReadOffsetArgs = cullOut.DrawArgs,
                    HDeformUniforms = hDeformUniforms2,
                    HDeformDispatchArgs = hDeformDispatchArgs2,
                    HCacheRequestBytes = hCacheRequestBytes,
                    HCacheOffsets = hCacheOffsets,
                    HCacheBlockOffsets0 = hCacheBlockOffsets0,
                    HCacheBlockOffsets1 = hCacheBlockOffsets1,
                });

                graph.AddPass(new ClusterDeformCacheCommitAllocationPass(context, resources.Deform, "P2DeformCacheCommitAllocation")
                {
                    HDeformUniforms = hDeformUniforms2,
                    HCacheBlockSums1 = hCacheBlockSums1,
                    HCacheBlockOffsets1 = hCacheBlockOffsets1,
                });

                var deformBinP2 = ClusterDeformBinStage.AddPasses(
                    graph, context, resources.DeformBin, cullOut,
                    globals.GlobalInstanceHeader, cullOut.Phase2DrawArgs, cullOut.DrawArgs, hMaterialSlotBuffer,
                    globals.PageHeap,
                    (uint)binSpace.SlotCapacity, deformBinField, deformBinCount, tag: "P2Deform");

                graph.AddPass(new ClusterDeformPass(context, "P2Deform")
                {
                    HBinnedClusterIndex = deformBinP2.DeformBinnedClusterIndex,
                    HVisibleClusters = cullOut.VisibleClusters,
                    HDeformBinMeta = deformBinP2.DeformBinMeta,
                    HPageHeap = globals.PageHeap,
                    HGlobalTransformBuffer = globals.GlobalTransform,
                    HDeformUniforms = hDeformUniforms2,
                    HDeformDispatchArgs = deformBinP2.PreDeformDispatchArgs,
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
                var mergeP2 = new DepthMergePass(context, resources.DepthMerge, "P2DepthMerge")
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
                    VisibleClusterMeta = rasterBinP2.BinnedHWDrawArgs,
                    UseHWDrawArgs = true,
                };
                var hwRasterP2 = ClusterDraw.AddPasses(graph, context, resources.Draw, rasterBinP2, cullOut, globals,
                    hDrawUniforms, hwDrawConfigP2, depthTarget, screenWidth, screenHeight,
                    materialDispatchGroups: hwMaterialGroups,
                    materialDispatchGroupsAreGraphics: hasHWGraphicsGroups,
                    hOutputVisBuffer: swRasterP2.VisBuffer, hOutputDepth: rasterP1.DepthTarget,
                    hDeformCache: hDeformCache, hCacheOffsets: hCacheOffsets);
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
                    VisibleClusterMeta = rasterBinP2.BinnedDrawArgs,
                };
                finalRaster = ClusterDraw.AddPasses(graph, context, resources.Draw, rasterBinP2, cullOut, globals,
                    hDrawUniforms, drawConfigP2, depthTarget, screenWidth, screenHeight,
                    materialDispatchGroups: hwMaterialGroups,
                    materialDispatchGroupsAreGraphics: hasHWGraphicsGroups,
                    hOutputVisBuffer: rasterP1.VisBuffer, hOutputDepth: rasterP1.DepthTarget,
                    hDeformCache: hDeformCache, hCacheOffsets: hCacheOffsets);
            }

            ClusterCull.AddFinalHiZBuild(graph, context, resources.HiZBuild, depthTarget, hCurrHiZ, hizMipCount);
        }

        if (useHiZ && frameTargets == null)
            hizPingPong.EndFrame();

        return new HiZResult(cullOut, finalRaster, hCurrHiZ, hDeformCache, hCacheOffsets);
    }

    public static TextureDesc CreateHiZTextureDesc(in ClusterCameraData camera)
    {
        uint width = Math.Max(camera.ScreenWidth, 1);
        uint height = Math.Max(camera.ScreenHeight, 1);
        return CreateHiZTextureDesc(width, height, ClusterCull.CalculateMipCount(width, height));
    }

    private static TextureDesc CreateHiZTextureDesc(uint width, uint height, uint mipLevels)
    {
        return new TextureDesc
        {
            Type = ResourceDimension.Tex2d,
            Width = width,
            Height = height,
            MipLevels = mipLevels,
            Format = TextureFormat.R32_Float,
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        };
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
