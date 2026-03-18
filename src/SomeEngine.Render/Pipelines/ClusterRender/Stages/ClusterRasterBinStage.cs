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
        uint slotCapacity,
        uint rasterBinFieldIndex,
        string? tag = null
    )
    {
        ClusterBinningPSOs.EnsureInitialized(context);

        string prefix = tag != null ? $"{tag}_" : "";

        uint maxBins = slotCapacity > 0 ? slotCapacity : 1;

        var hRasterBinMeta = graph.CreateBuffer($"{prefix}RasterBinMeta", new BufferDesc
        {
            Size = maxBins * 16,
            BindFlags = BindFlags.UnorderedAccess,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hBinnedClusterIndex = graph.CreateBuffer($"{prefix}BinnedClusterIndexBuffer", new BufferDesc
        {
            Size = (ulong)(ClusterLimits.MaxDraws * 4),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
        });
        var hBinnedDrawArgs = graph.CreateBuffer($"{prefix}BinnedDrawArgs", new BufferDesc
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
            MaxBins = ClusterLimits.MaxBins,
            MaxClustersPerBin = ClusterLimits.MaxClustersPerBin,
            SlotCapacity = slotCapacity,
            BinFieldIndex = rasterBinFieldIndex,
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
            HBinnedDrawArgs = hBinnedDrawArgs,
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
            HBinnedDrawArgs = hBinnedDrawArgs,
            HBinnedClusterBuffer = hBinnedClusterIndex,
            HMaterialSlotBuffer = hMaterialSlotBuffer,
        });

        return new ClusterRasterBinOutput(hBinnedClusterIndex, hBinnedDrawArgs, hRasterBinMeta, hBinningDispatchArgs);
    }
}
