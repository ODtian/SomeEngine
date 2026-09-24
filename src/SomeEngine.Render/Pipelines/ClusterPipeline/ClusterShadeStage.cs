using SomeEngine.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal struct ClusterShadeStage
{
    private ShadeBinPass? _bin;
    private MaterialShadePass? _shade;

    public ClusterShadeStage(ShadeBinPass bin, MaterialShadePass shade)
    {
        _bin = bin ?? throw new ArgumentNullException(nameof(bin));
        _shade = shade ?? throw new ArgumentNullException(nameof(shade));
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ThrowIfDisposed();
        _bin!.AddTickets(tickets);
    }

    public ShadeBinFrame AddBins(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        SlotFrame slots,
        uint width,
        uint height)
    {
        ThrowIfDisposed();
        return _bin!.AddPasses(
            graph,
            buffers,
            instances,
            raster,
            cull,
            slots.Buffer,
            slots.SlotCapacity,
            slots.ShadingBinFieldIndex,
            slots.ShadingBinCount,
            width,
            height);
    }

    public ClusterShadeOutput AddShade(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        ShadeBinFrame bins,
        IReadOnlyList<MaterialBin> states,
        MaterialResourceFallbacks fallbacks,
        AssetStore assets,
        in ShadeUniforms uniforms,
        in SceneLights sceneLights,
        uint lightVersion,
        uint width,
        uint height,
        uint depthSliceCount,
        RenderGraphHandle outputColor,
        RenderGraphHandle outputMotionVectors,
        RenderGraphHandle deformCache,
        RenderGraphHandle cacheOffsets,
        bool clearTargets)
    {
        ThrowIfDisposed();
        return _shade!.AddPasses(
            graph,
            buffers,
            instances,
            raster,
            cull,
            bins,
            states,
            fallbacks,
            assets,
            uniforms,
            sceneLights,
            lightVersion,
            width,
            height,
            depthSliceCount,
            outputColor,
            outputMotionVectors,
            deformCache,
            cacheOffsets,
            clearTargets);
    }

    public void Dispose(PipelineCache store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _shade?.Dispose();
        _bin?.Dispose(store);
        _shade = null;
        _bin = null;
    }

    private void ThrowIfDisposed()
    {
        if (_bin == null || _shade == null)
            throw new ObjectDisposedException(nameof(ClusterShadeStage));
    }
}
