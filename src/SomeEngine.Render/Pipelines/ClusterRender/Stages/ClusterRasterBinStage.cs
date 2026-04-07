using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 无状态 Raster Binning 工具函数。
/// PSO/SRB 在 ClusterBinningPSOs 中 static 缓存。
/// </summary>
public static class ClusterRasterBin
{
    /// <summary>
    /// 添加 Raster Binning pass（Init + Scatter），返回 binned 结果。
    /// </summary>
    public static ClusterRasterBinOutput AddPasses(
        RenderGraph graph,
        RenderContext context,
        in ClusterCullOutput cull,
        RenderGraphHandle hInstanceHeaders,
        RenderGraphHandle hDrawArgs,
        RenderGraphHandle hClusterReadOffsetArgs,
        RenderGraphHandle hMaterialSlotBuffer,
        RenderGraphHandle hPageHeap,
        uint slotCapacity,
        uint rasterBinFieldIndex,
        uint totalBins,
        string? tag = null
    )
    {
        ClusterBinningPSOs.EnsureInitialized(context);

        string prefix = tag != null ? $"{tag}_" : "";

        uint maxBins = totalBins > 0 ? totalBins : 1;

        var hRasterBinMeta = graph.CreateBuffer($"{prefix}RasterBinMeta", new BufferDesc
        {
            Size = maxBins * 32,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 32,
        });
        var hBinnedClusterIndex = graph.CreateBuffer($"{prefix}BinnedClusterIndexBuffer", new BufferDesc
        {
            Size = (ulong)(ClusterLimits.MaxDraws * 8 * 6), // uint2 per entry, ~6 entries per cluster (per-batch)
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 8,
        });
        var hBinnedDrawArgs = graph.CreateBuffer($"{prefix}BinnedDrawArgs", new BufferDesc
        {
            Size = maxBins * 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hBinnedHWDrawArgs = graph.CreateBuffer($"{prefix}BinnedHWDrawArgs", new BufferDesc
        {
            Size = maxBins * 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hBinningDispatchArgs = graph.CreateBuffer($"{prefix}BinningDispatchArgs", new BufferDesc
        {
            Size = 12,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hBinnedSWDispatchArgs = graph.CreateBuffer($"{prefix}BinnedSWDispatchArgs", new BufferDesc
        {
            Size = maxBins * 24, // Two sections: [0..maxBins*12) = SW, [maxBins*12..maxBins*24) = HW
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        // Create BinningUniforms buffer
        var hBinningUniforms = graph.CreateBuffer($"{prefix}BinningUniforms", new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<BinningUniforms>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // Upload binning uniforms
        var binUniData = new BinningUniforms
        {
            MaxBins = maxBins,
            SlotCapacity = slotCapacity,
            BinFieldIndex = rasterBinFieldIndex,
            MaxVisibleClusters = ClusterLimits.MaxDraws,
        };
        graph.AddPass<object>(
            $"{prefix}UploadBinningUniforms",
            (builder, _) => { builder.Write(hBinningUniforms, ResourceState.ConstantBuffer); },
            (rgCtx, _) =>
            {
                var ctx = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinningUniforms);
                if (ctx != null && buf != null)
                {
                    var span = ctx.MapBuffer<BinningUniforms>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = binUniData;
                    ctx.UnmapBuffer(buf, MapType.Write);
                }
            }
        );

        graph.AddPass(new ClusterBinningInitPass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HDrawArgs = hDrawArgs,
            HBinningDispatchArgs = hBinningDispatchArgs,
            HRasterBinMeta = hRasterBinMeta,
            MaxBins = maxBins,
        });

        graph.AddPass(new ClusterBinningCountPass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = hInstanceHeaders,
            HDrawArgs = hDrawArgs,
            HClusterReadOffsetArgs = hClusterReadOffsetArgs,
            HBinningDispatchArgs = hBinningDispatchArgs,
            HRasterBinMeta = hRasterBinMeta,
            HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = hPageHeap,
        });

        graph.AddPass(new ClusterBinningReservePass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HRasterBinMeta = hRasterBinMeta,
            HBinnedDrawArgs = hBinnedDrawArgs,
            HBinnedHWDrawArgs = hBinnedHWDrawArgs,
            HBinnedSWDispatchArgs = hBinnedSWDispatchArgs,
        });

        graph.AddPass(new ClusterBinningScatterPass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = hInstanceHeaders,
            HDrawArgs = hDrawArgs,
            HClusterReadOffsetArgs = hClusterReadOffsetArgs,
            HBinningDispatchArgs = hBinningDispatchArgs,
            HRasterBinMeta = hRasterBinMeta,
            HBinnedClusterBuffer = hBinnedClusterIndex,
            HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = hPageHeap,
        });

        return new ClusterRasterBinOutput(hBinnedClusterIndex, hBinnedDrawArgs, hBinnedHWDrawArgs, hRasterBinMeta, hBinningDispatchArgs, hBinnedSWDispatchArgs);
    }
}
