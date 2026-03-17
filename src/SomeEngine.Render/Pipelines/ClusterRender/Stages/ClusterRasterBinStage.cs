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
        in ClusterGlobalResources globals,
        in ClusterRasterBinConfig config,
        RenderGraphHandle hDrawArgs,
        RenderGraphHandle hClusterReadOffsetArgs,
        string? tag = null
    )
    {
        ClusterBinningPSOs.EnsureInitialized(context);

        string prefix = tag != null ? $"{tag}_" : "";

        var hRasterBinMeta = graph.CreateBuffer($"{prefix}RasterBinMeta", new BufferDesc
        {
            Size = config.MaxBins * 16,
            BindFlags = BindFlags.UnorderedAccess,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hBinnedClusterIndex = config.OutputBinnedClusterIndex.IsValid
            ? config.OutputBinnedClusterIndex
            : graph.CreateBuffer($"{prefix}BinnedClusterIndexBuffer", new BufferDesc
            {
                Size = (ulong)(ClusterLimits.MaxDraws * 4),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 4,
            });
        var hBinnedDrawArgs = config.OutputBinnedDrawArgs.IsValid
            ? config.OutputBinnedDrawArgs
            : graph.CreateBuffer($"{prefix}BinnedDrawArgs", new BufferDesc
            {
                Size = config.MaxBins * 16,
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

        graph.AddPass(new ClusterBinningInitPass(context)
        {
            HBinningUniforms = globals.BinningUniforms,
            HDrawArgs = hDrawArgs,
            HBinningDispatchArgs = hBinningDispatchArgs,
            HRasterBinMeta = hRasterBinMeta,
            HBinnedDrawArgs = hBinnedDrawArgs,
        });

        graph.AddPass(new ClusterBinningScatterPass(context)
        {
            HBinningUniforms = globals.BinningUniforms,
            HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = globals.GlobalInstanceHeader,
            HDrawArgs = hDrawArgs,
            HClusterReadOffsetArgs = hClusterReadOffsetArgs,
            HBinningDispatchArgs = hBinningDispatchArgs,
            HRasterBinMeta = hRasterBinMeta,
            HBinnedDrawArgs = hBinnedDrawArgs,
            HBinnedClusterBuffer = hBinnedClusterIndex,
        });

        return new ClusterRasterBinOutput(hBinnedClusterIndex, hBinnedDrawArgs, hRasterBinMeta, hBinningDispatchArgs);
    }
}
