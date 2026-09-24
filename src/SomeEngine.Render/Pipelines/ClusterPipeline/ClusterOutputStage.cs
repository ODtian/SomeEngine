using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal struct ClusterOutputStage
{
    private ClusterResolvePass? _resolve;
    private TemporalResolvePass? _temporal;

    public ClusterOutputStage(ClusterResolvePass resolve, TemporalResolvePass temporal)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _temporal = temporal ?? throw new ArgumentNullException(nameof(temporal));
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ThrowIfDisposed();
        _resolve!.AddTickets(tickets);
    }

    public void AddResolve(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        UniformFrame uniforms,
        RenderGraphHandle outputColor)
    {
        ThrowIfDisposed();
        _resolve!.AddPasses(graph, buffers, instances, raster, cull, uniforms, outputColor);
    }

    public void AddTemporal(
        RenderGraph graph,
        RenderGraphHandle currentScene,
        RenderGraphHandle previousScene,
        RenderGraphHandle motionVectors,
        RenderGraphHandle previousMotion,
        RenderGraphHandle currentDepth,
        RenderGraphHandle previousDepth,
        RenderGraphHandle outputScene,
        TemporalResolveSettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        _temporal!.AddTo(
            graph,
            currentScene,
            previousScene,
            motionVectors,
            previousMotion,
            currentDepth,
            previousDepth,
            outputScene,
            settings);
    }

    public void Dispose(PipelineCache store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _temporal?.Dispose();
        _resolve?.Dispose(store);
        _temporal = null;
        _resolve = null;
    }

    private void ThrowIfDisposed()
    {
        if (_resolve == null || _temporal == null)
            throw new ObjectDisposedException(nameof(ClusterOutputStage));
    }
}
