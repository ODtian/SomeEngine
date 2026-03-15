using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: Raster Binning — 按 RasterBinKey 分组可见 Cluster。
/// </summary>
public class ClusterRasterBinStage : IDisposable
{
    private readonly RenderContext _context;
    private ClusterBinningPass? _binningPass;
    private bool _initialized;

    public ClusterRasterBinStage(RenderContext context)
    {
        _context = context;
    }

    public void Init()
    {
        if (_initialized) return;
        _binningPass = new ClusterBinningPass(_context);
        _binningPass.Init();
        _initialized = true;
    }

    /// <summary>
    /// 添加 Raster Binning pass（Init + Scatter），返回 binned 结果。
    /// </summary>
    public ClusterRasterBinOutput AddPasses(
        RenderGraph graph,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        in ClusterRasterBinConfig config,
        RenderGraphHandle hDrawArgs,
        RenderGraphHandle hClusterReadOffsetArgs,
        string? tag = null
    )
    {
        if (!_initialized) Init();

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
                Size = (ulong)(ClusterRenderFeature.MaxDraws * 4),
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

        // AddBinningPasses: Init + Scatter
        var initPass = new ClusterBinningInitPass(_binningPass!)
        {
            HBinningUniforms = globals.BinningUniforms,
            HDrawArgs = hDrawArgs,
            HBinningDispatchArgs = hBinningDispatchArgs,
            HRasterBinMeta = hRasterBinMeta,
            HBinnedDrawArgs = hBinnedDrawArgs,
        };
        graph.AddPass(initPass);

        var scatterPass = new ClusterBinningScatterPass(_binningPass!)
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
        };
        graph.AddPass(scatterPass);

        return new ClusterRasterBinOutput(hBinnedClusterIndex, hBinnedDrawArgs, hRasterBinMeta, hBinningDispatchArgs);
    }

    public void Dispose()
    {
        _binningPass?.Dispose();
    }
}
