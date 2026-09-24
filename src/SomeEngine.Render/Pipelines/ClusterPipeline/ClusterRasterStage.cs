using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal struct ClusterRasterStage
{
    private RasterBinPass? _bin;
    private ClusterDeformPass? _deform;
    private SwRasterPass? _sw;
    private ClusterDrawPass? _draw;
    private DepthMergePass? _merge;
    private HiZPass? _hiZ;

    public ClusterRasterStage(
        RasterBinPass bin,
        ClusterDeformPass deform,
        SwRasterPass sw,
        ClusterDrawPass draw,
        DepthMergePass merge,
        HiZPass hiZ)
    {
        _bin = bin ?? throw new ArgumentNullException(nameof(bin));
        _deform = deform ?? throw new ArgumentNullException(nameof(deform));
        _sw = sw ?? throw new ArgumentNullException(nameof(sw));
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
        _merge = merge ?? throw new ArgumentNullException(nameof(merge));
        _hiZ = hiZ ?? throw new ArgumentNullException(nameof(hiZ));
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ThrowIfDisposed();
        _bin!.AddTickets(tickets);
        _draw!.AddTickets(tickets);
        _merge!.AddTickets(tickets);
        _hiZ!.AddTickets(tickets);
    }

    public RasterDeformBins AddDeformBins(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgs,
        RenderGraphHandle readOffsetArgs,
        SlotFrame slots,
        DeformCacheResources cache,
        bool resetAllocation,
        string tag)
    {
        ThrowIfDisposed();
        return _bin!.AddRasterDeform(
            graph,
            buffers,
            instances,
            cull,
            drawArgs,
            readOffsetArgs,
            slots.Buffer,
            slots.SlotCapacity,
            slots.RasterBinFieldIndex,
            slots.RasterBinCount,
            slots.VertexEvalFieldIndex,
            slots.VertexEvalBinCount,
            cache.CacheOffsets,
            cache.CacheAllocationCounter,
            resetAllocation,
            tag);
    }

    public RasterBinFrame AddBins(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgs,
        RenderGraphHandle readOffsetArgs,
        SlotFrame slots,
        string tag)
    {
        ThrowIfDisposed();
        return _bin!.AddPasses(
            graph,
            buffers,
            instances,
            cull,
            drawArgs,
            readOffsetArgs,
            slots.Buffer,
            slots.SlotCapacity,
            slots.RasterBinFieldIndex,
            slots.RasterBinCount,
            tag);
    }

    public void AddDeform(
        RenderGraph graph,
        DeformCacheResources cache,
        DeformBinFrame bins,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RenderGraphHandle drawArgs,
        RenderGraphHandle readOffsetArgs,
        IReadOnlyList<MaterialBin> states,
        MaterialResourceFallbacks fallbacks,
        bool resetAllocation,
        string tag)
    {
        ThrowIfDisposed();
        DeformCacheFrame frame = _deform!.CreateFrame(
            cache,
            bins,
            cull,
            buffers,
            instances,
            drawArgs,
            readOffsetArgs,
            resetAllocation);
        _deform.AddMaterialPass(
            graph,
            cache,
            frame,
            bins,
            cull,
            buffers,
            instances,
            drawArgs,
            readOffsetArgs,
            states,
            fallbacks,
            tag);
    }

    public ClusterRasterOutput AddRaster(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterCullOutput cull,
        RasterBinFrame bins,
        in DrawUniforms drawData,
        UniformFrame drawUniforms,
        uint width,
        uint height,
        RenderGraphHandle depthTarget,
        RenderGraphHandle outputVisBuffer,
        RenderGraphHandle outputDepthUav,
        RenderGraphHandle deformCache,
        RenderGraphHandle cacheOffsets,
        ClusterConfig modes,
        IReadOnlyList<MaterialBin> swStates,
        IReadOnlyList<MaterialBin> drawStates,
        MaterialResourceFallbacks fallbacks,
        bool clearTargets,
        string tag)
    {
        ThrowIfDisposed();
        bool useSoftwareRaster = modes.UseSw() && swStates.Count > 0;
        if (!useSoftwareRaster)
        {
            return _draw!.AddPasses(
                graph,
                buffers,
                instances,
                bins,
                cull,
                drawUniforms,
                width,
                height,
                clearTargets: clearTargets,
                outputVisBuffer: outputVisBuffer,
                outputDepth: depthTarget,
                deformCache: deformCache,
                cacheOffsets: cacheOffsets,
                states: drawStates,
                materialFallbacks: fallbacks,
                useHWDrawArgs: false,
                tag: tag);
        }

        ClusterRasterOutput swRaster = _sw!.AddPasses(
            graph,
            buffers,
            instances,
            bins,
            cull,
            drawData,
            depthTarget,
            swStates,
            clearTargets: clearTargets,
            outputVisBuffer: outputVisBuffer,
            outputDepthUav: outputDepthUav,
            deformCache: deformCache,
            cacheOffsets: cacheOffsets,
            materialFallbacks: fallbacks,
            debugDump: false,
            debugSWHWView: modes.DebugSwHw(),
            rasterBinCount: bins.DrawBinCount,
            tag: $"{tag}SW");
        _merge!.AddPasses(graph, swRaster, depthTarget, $"{tag}DepthMerge");
        ClusterRasterOutput hwRaster = _draw!.AddPasses(
            graph,
            buffers,
            instances,
            bins,
            cull,
            drawUniforms,
            width,
            height,
            clearTargets: false,
            outputVisBuffer: swRaster.VisBuffer,
            outputDepth: depthTarget,
            deformCache: deformCache,
            cacheOffsets: cacheOffsets,
            states: drawStates,
            materialFallbacks: fallbacks,
            useHWDrawArgs: true,
            tag: $"{tag}HW");

        return new ClusterRasterOutput(hwRaster.VisBuffer, hwRaster.DepthTarget, swRaster.RasterDepth);
    }

    public HiZOutput AddHiZ(
        RenderGraph graph,
        RenderGraphHandle depthTarget,
        uint width,
        uint height,
        RenderGraphHandle output,
        string tag)
    {
        ThrowIfDisposed();
        return _hiZ!.AddPasses(graph, depthTarget, width, height, output, tag);
    }

    public void Dispose(PipelineCache store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _hiZ?.Dispose(store);
        _merge?.Dispose(store);
        _draw?.Dispose(store);
        _sw?.Dispose();
        _deform?.Dispose();
        _bin?.Dispose(store);
        _hiZ = null;
        _merge = null;
        _draw = null;
        _sw = null;
        _deform = null;
        _bin = null;
    }

    private void ThrowIfDisposed()
    {
        if (_bin == null || _deform == null || _sw == null || _draw == null || _merge == null || _hiZ == null)
            throw new ObjectDisposedException(nameof(ClusterRasterStage));
    }
}
