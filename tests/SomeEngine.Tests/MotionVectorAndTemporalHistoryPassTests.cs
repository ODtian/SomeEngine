using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Tests;

public class TemporalHistoryCopyTests
{
    private const string TemporalHistoryUpdatePassName = "Temporal History Update";
    private const string TemporalHistoriesBatchPassName = "Update Temporal Histories";

    [Fact]
    public void TemporalHistoryUpdateIsKeptAliveByHistoryExport()
    {
        using var graph = new RenderGraph();
        var context = new FrameSurfaceContext(128, 72);

        RenderGraphHandle sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(context));
        RenderGraphTestHelpers.WriteTexture(graph, sceneColor, "Write SceneColor");
        RenderGraphHandle history = CreateHistoryTarget(
            graph,
            RenderHistoryNames.TemporalSceneColor,
            FrameResources.TemporalColorTexture(context));

        TextureCopyPasses.AddCopyPass(graph, TemporalHistoryUpdatePassName, sceneColor, history);
        graph.Compile();

        var passNames = RenderGraphTestHelpers.ExecutedPassNames(graph);
        Assert.Contains(TemporalHistoryUpdatePassName, passNames);
    }

    [Fact]
    public void TemporalMotionHistoryUpdateIsKeptAliveByHistoryExport()
    {
        using var graph = new RenderGraph();
        var context = new FrameSurfaceContext(128, 72);

        RenderGraphHandle motionVectors = graph.CreateTexture(
            FrameResources.MotionVectors,
            FrameResources.MotionVectorTexture(context));
        RenderGraphTestHelpers.WriteTexture(graph, motionVectors, "Write MotionVectors", ResourceState.UnorderedAccess);
        RenderGraphHandle history = CreateHistoryTarget(
            graph,
            RenderHistoryNames.TemporalMotionVectors,
            FrameResources.TemporalMotionTexture(context));

        TextureCopyPasses.AddCopyPass(graph, "Temporal Motion History Update", motionVectors, history);
        graph.Compile();

        var passNames = RenderGraphTestHelpers.ExecutedPassNames(graph);
        Assert.Contains("Temporal Motion History Update", passNames);
    }

    [Fact]
    public void TemporalHistoryBatchUpdateIsKeptAliveByHistoryExports()
    {
        using var graph = new RenderGraph();
        var context = new FrameSurfaceContext(128, 72);

        RenderGraphHandle sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(context));
        RenderGraphHandle motionVectors = graph.CreateTexture(
            FrameResources.MotionVectors,
            FrameResources.MotionVectorTexture(context));
        RenderGraphHandle depth = graph.CreateTexture(
            FrameResources.SceneDepth,
            FrameResources.TemporalDepthTexture(context));
        RenderGraphTestHelpers.WriteTexture(graph, sceneColor, "Write SceneColor");
        RenderGraphTestHelpers.WriteTexture(graph, motionVectors, "Write MotionVectors", ResourceState.UnorderedAccess);
        RenderGraphTestHelpers.WriteTexture(graph, depth, "Write SceneDepth", ResourceState.CopySource);

        RenderGraphHandle sceneHistory = CreateHistoryTarget(
            graph,
            RenderHistoryNames.TemporalSceneColor,
            FrameResources.TemporalColorTexture(context));
        RenderGraphHandle motionHistory = CreateHistoryTarget(
            graph,
            RenderHistoryNames.TemporalMotionVectors,
            FrameResources.TemporalMotionTexture(context));
        RenderGraphHandle depthHistory = CreateHistoryTarget(
            graph,
            RenderHistoryNames.TemporalSceneDepth,
            FrameResources.TemporalDepthTexture(context));

        TextureCopyPasses.AddCopyBatch(
            graph,
            TemporalHistoriesBatchPassName,
            [
                FullCopy(graph, sceneColor, sceneHistory),
                FullCopy(graph, motionVectors, motionHistory),
                FullCopy(graph, depth, depthHistory),
            ]);
        graph.Compile();

        var passNames = RenderGraphTestHelpers.ExecutedPassNames(graph);
        Assert.Contains(TemporalHistoriesBatchPassName, passNames);
    }

    private static TextureCopyRequest FullCopy(
        RenderGraph graph,
        RenderGraphHandle source,
        RenderGraphHandle destination)
    {
        TextureDesc sourceDesc = graph.GetTextureDesc(source);
        TextureDesc destinationDesc = graph.GetTextureDesc(destination);
        uint width = Math.Min(sourceDesc.Width, destinationDesc.Width);
        uint height = Math.Min(sourceDesc.Height, destinationDesc.Height);
        var region = new TextureCopyRegion(0, 0, 0, 0, 0, width, height, 1);
        return new TextureCopyRequest(source, region, destination, region);
    }

    private static RenderGraphHandle CreateHistoryTarget(
        RenderGraph graph,
        string name,
        TextureDesc desc)
    {
        RenderGraphHandle target = graph.CreateTexture(name, desc);
        graph.SetFinalState(target, ResourceState.ShaderResource);
        return target;
    }
}
