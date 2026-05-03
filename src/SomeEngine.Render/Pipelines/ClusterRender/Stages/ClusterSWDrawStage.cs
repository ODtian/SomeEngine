using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Stateless SW raster stage �?mirrors ClusterDraw.AddPasses but dispatches
/// CSSWRaster compute instead of DrawIndirect.
/// </summary>
public static class ClusterSWDraw
{
    /// <summary>When true, next frame will dump SW raster debug info and reset.</summary>
    public static bool DebugDumpNextFrame { get; set; }

    // Persistent debug buffer + staging for readback
    private static IBuffer? s_debugBuffer;
    private static IBuffer? s_debugStaging;
    private const int DebugMaxEntries = 8192;
    private const int DebugEntryBytes = 24;
    private const int DebugBufferSize = 4 + DebugMaxEntries * DebugEntryBytes;

    private static void EnsureDebugBuffers(RenderContext context)
    {
        if (s_debugBuffer != null) return;
        s_debugBuffer = context.Device!.CreateBuffer(new BufferDesc
        {
            Name = "SWRaster_DebugOutput",
            Size = DebugBufferSize,
            BindFlags = BindFlags.UnorderedAccess,
            Mode = BufferMode.Raw,
            Usage = Usage.Default,
        });
        s_debugStaging = context.Device!.CreateBuffer(new BufferDesc
        {
            Name = "SWRaster_DebugStaging",
            Size = DebugBufferSize,
            BindFlags = BindFlags.None,
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    public static ClusterRasterOutput AddPasses(
        RenderGraph graph,
        RenderContext context,
        in ClusterRasterBinOutput rasterBin,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        in ClusterCameraData camera,
        float quantStep,
        Vector3 quantOrigin,
        RenderGraphHandle depthTarget,
        RenderGraphHandle hOutputVisBuffer = default,
        RenderGraphHandle hOutputDepthUAV = default,
        bool clearTargets = true,
        string tag = "SW_",
        bool debugSWHWView = false,
        uint rasterBinCount = 1,
        MaterialPSOGroup[]? swRasterPSOGroups = null,
        RenderGraphHandle hDeformCache = default,
        RenderGraphHandle hCacheOffsets = default
    )
    {
        uint sw = camera.ScreenWidth;
        uint sh = camera.ScreenHeight;

        // ─── VisBuffer (R32_UINT, UAV + SRV + RT for clear) ───
        var hVisBuffer = hOutputVisBuffer.IsValid
            ? hOutputVisBuffer
            : graph.CreateTexture($"{tag}VisBuffer", new TextureDesc
            {
                Type = ResourceDimension.Tex2d,
                Width = sw,
                Height = sh,
                MipLevels = 1,
                Format = TextureFormat.R32_UInt,
                Usage = Usage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.RenderTarget,
                ClearValue = new OptimizedClearValue
                {
                    Format = TextureFormat.R32_UInt,
                    Color = new Vector4(0, 0, 0, 0),
                },
            });

        // ─── SW Depth UAV (R32_UINT for InterlockedMax, cleared to 0) ───
        var hDepthUAV = hOutputDepthUAV.IsValid
            ? hOutputDepthUAV
            : graph.CreateTexture($"{tag}DepthUAV", new TextureDesc
            {
                Type = ResourceDimension.Tex2d,
                Width = sw,
                Height = sh,
                MipLevels = 1,
                Format = TextureFormat.R32_UInt,
                Usage = Usage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.RenderTarget,
                ClearValue = new OptimizedClearValue
                {
                    Format = TextureFormat.R32_UInt,
                    Color = new Vector4(0, 0, 0, 0),
                },
            });

        // ─── Clear VisBuffer + DepthUAV ───
        if (clearTargets)
        {
            graph.AddPass<object>(
                $"{tag}ClearSWTargets",
                (builder, _) =>
                {
                    builder.Write(hVisBuffer, ResourceState.RenderTarget);
                    builder.Write(hDepthUAV, ResourceState.RenderTarget);
                },
                (rgCtx, _) =>
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    if (ctx2 == null) return;

                    var vbRtv = rgCtx.GetTextureView(hVisBuffer, TextureViewType.RenderTarget);
                    var dRtv  = rgCtx.GetTextureView(hDepthUAV, TextureViewType.RenderTarget);
                    if (vbRtv != null)
                    {
                        ctx2.SetRenderTargets([vbRtv], null, ResourceStateTransitionMode.None);
                        ctx2.ClearRenderTarget(vbRtv, new Vector4(0, 0, 0, 0), ResourceStateTransitionMode.None);
                    }
                    if (dRtv != null)
                    {
                        ctx2.SetRenderTargets([dRtv], null, ResourceStateTransitionMode.None);
                        ctx2.ClearRenderTarget(dRtv, new Vector4(0, 0, 0, 0), ResourceStateTransitionMode.None);
                    }
                    ctx2.SetRenderTargets([], null, ResourceStateTransitionMode.None);
                }
            );
        }

        // ─── Upload SWRasterUniforms ───
        var hUniforms = graph.CreateBuffer($"{tag}SWRasterUniforms", new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<SWRasterUniforms>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // Don't consume the flag here �?let the caller reset it after all phases
        bool doDump = DebugDumpNextFrame;

        var uniforms = new SWRasterUniforms
        {
            ViewProj = Matrix4x4.Transpose(camera.View * camera.Proj),
            QuantOrigin = quantOrigin,
            QuantStep = quantStep,
            ScreenWidth = sw,
            ScreenHeight = sh,
            MaxBins = rasterBinCount,
            DebugDump = (doDump ? 1u : 0u) | (debugSWHWView ? 0x2u : 0u),
        };

        graph.AddPass<object>(
            $"{tag}UploadSWUniforms",
            (builder, _) => { builder.Write(hUniforms, ResourceState.ConstantBuffer); },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hUniforms);
                if (ctx2 != null && buf != null)
                {
                    var span = ctx2.MapBuffer<SWRasterUniforms>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = uniforms;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );

        // ─── Debug buffer: clear counter + create handle ───
        EnsureDebugBuffers(context);
        var hDebugBuf = graph.Import($"{tag}DebugSWOutput", s_debugBuffer!, ResourceState.Unknown);

        if (doDump)
        {
            graph.MarkOutput(hDebugBuf);
            // Clear the counter to 0 before the dispatch
            graph.AddPass<object>(
                $"{tag}ClearDebugSW",
                (builder, _) => { builder.Write(hDebugBuf, ResourceState.CopyDest); },
                (rgCtx, _) =>
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    var buf = rgCtx.GetBuffer(hDebugBuf);
                    if (ctx2 != null && buf != null)
                    {
                        Span<uint> zero = [0];
                        ctx2.UpdateBuffer(buf, 0, zero, ResourceStateTransitionMode.None);
                    }
                }
            );
        }

        // ─── Dispatch CSSWRaster ───
        var pass = new ClusterSWRasterPass(context, $"{tag}ClusterSWRaster")
        {
            HVisibleClusters = cull.VisibleClusters,
            HBinnedClusterIndex = rasterBin.BinnedClusterIndex,
            HRasterBinMeta = rasterBin.RasterBinMeta,
            HSWRasterUniforms = hUniforms,
            HGlobalTransformBuffer = globals.GlobalTransform,
            HPageHeap = globals.PageHeap,
            HVisBuffer = hVisBuffer,
            HDepthTarget = depthTarget,
            HDepthUAV = hDepthUAV,
            HDebugSWOutput = hDebugBuf,
            HBinnedSWDispatchArgs = rasterBin.BinnedSWDispatchArgs,
            PSOGroups = swRasterPSOGroups,
            TotalBinCount = rasterBinCount,
            SWRasterUniformData = uniforms,
            HDeformCache = hDeformCache,
            HCacheOffsets = hCacheOffsets,
        };
        graph.AddPass(pass);

        // ─── Readback debug data after dispatch ───
        if (doDump)
        {
            graph.AddPass<object>(
                $"{tag}ReadbackDebugSW",
                (builder, _) =>
                {
                    // Use Write so RenderGraph DCE keeps this pass alive
                    // (it's a marked output; Read-only passes get pruned)
                    builder.Write(hDebugBuf, ResourceState.CopySource);
                },
                (rgCtx, _) =>
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    var gpuBuf = rgCtx.GetBuffer(hDebugBuf);
                    if (ctx2 == null || gpuBuf == null || s_debugStaging == null) return;

                    ctx2.CopyBuffer(gpuBuf, 0, ResourceStateTransitionMode.None,
                                    s_debugStaging, 0, (ulong)DebugBufferSize, ResourceStateTransitionMode.None);

                    // Ensure GPU has finished before reading staging buffer
                    ctx2.WaitForIdle();

                    // Map staging and print
                    var span = ctx2.MapBuffer<uint>(s_debugStaging, MapType.Read, MapFlags.DoNotWait);
                    uint count = span[0];
                    Console.WriteLine($"[SWRaster Debug {tag}] Clusters processed: {count}");
                    int toPrint = (int)Math.Min(count, 200);
                    for (int i = 0; i < toPrint; i++)
                    {
                        int baseIdx = (4 + i * DebugEntryBytes) / 4; // uint offset
                        uint binnedIdx     = span[baseIdx + 0];
                        uint originalIdx   = span[baseIdx + 1];
                        uint clusterId     = span[baseIdx + 2];
                        uint triCount      = span[baseIdx + 3];
                        uint binIdx        = span[baseIdx + 4];
                        uint vertCount     = span[baseIdx + 5];
                        Console.WriteLine($"  [{i}] binned={binnedIdx} orig={originalIdx} cluster={clusterId} tris={triCount} verts={vertCount} binCount={binIdx}");
                    }
                    ctx2.UnmapBuffer(s_debugStaging, MapType.Read);
                }
            );
        }

        return new ClusterRasterOutput(hVisBuffer, depthTarget, hDepthUAV);
    }
}
