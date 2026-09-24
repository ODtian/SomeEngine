using SomeEngine.Assets.Importers;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Rhi;
using RenderContext = SomeEngine.Render.RHI.RenderContext;

namespace SomeEngine.Tests;

public class PostTonemapPassTests
{
    private const string PostTonemapShaderFile = "post_tonemap.slang";

    [Fact]
    public void AddToMarksBackBufferOutputAndKeepsPassAlive()
    {
        using var graph = new RenderGraph();
        using var renderContext = RenderContext.CreateHeadless();
        using var pass = new PostTonemapPass(
            renderContext,
            RuntimeAssetLoader.LoadShader(SlangShaderImporter.Import(TestProjectPaths.ShaderPath(PostTonemapShaderFile))));

        var sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(new FrameSurfaceContext(64, 32)));
        RenderGraphTestHelpers.WriteTexture(graph, sceneColor, "Write SceneColor");
        var backBuffer = graph.CreateTexture(
            FrameResources.OutputColor,
            RenderGraphTestHelpers.BackBufferDesc(64, 32));
        graph.SetFinalState(backBuffer, ResourceState.RenderTarget);

        pass.AddTo(graph, sceneColor, backBuffer);
        graph.Compile();

        Assert.Equal(
            ["Write SceneColor", PostTonemapPass.PassName],
            RenderGraphTestHelpers.ExecutedPassNames(graph));
    }

    [Fact]
    public void AddToExecutesWithDeclaredOutputAccess()
    {
        using var renderContext = RenderContext.CreateHeadless(width: 64, height: 32);
        using var pass = new PostTonemapPass(
            renderContext,
            RuntimeAssetLoader.LoadShader(SlangShaderImporter.Import(TestProjectPaths.ShaderPath(PostTonemapShaderFile))));
        using var graph = new RenderGraph();
        var device = renderContext.GraphicsDevice!;
        var queue = renderContext.GraphicsQueue!;

        graph.BeginFrame();
        var sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(new FrameSurfaceContext(64, 32)));
        RenderGraphTestHelpers.WriteTexture(graph, sceneColor, "Write SceneColor");
        var backBuffer = graph.CreateTexture(
            FrameResources.OutputColor,
            RenderGraphTestHelpers.BackBufferDesc(64, 32));
        graph.SetFinalState(backBuffer, ResourceState.RenderTarget);

        pass.AddTo(graph, sceneColor, backBuffer);

        graph.Execute(device, queue);
    }

    [Fact]
    public void PostTonemapShader_ImportsAsSlang()
    {
        Assert.EndsWith(".slang", PostTonemapShaderFile);

        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath(PostTonemapShaderFile));

        Assert.Contains(asset.Variants!, v =>
            v.EntryPoint == PostTonemapPass.VertexEntryPoint
            && v.Data.HasValue
            && v.Data.Value.Length > 0);
        Assert.Contains(asset.Variants!, v =>
            v.EntryPoint == PostTonemapPass.PixelEntryPoint
            && v.Data.HasValue
            && v.Data.Value.Length > 0);
    }

    [Fact]
    public void AddToRejectsInvalidOutputHandle()
    {
        using var graph = new RenderGraph();
        using var renderContext = RenderContext.CreateHeadless();
        using var pass = new PostTonemapPass(
            renderContext,
            RuntimeAssetLoader.LoadShader(SlangShaderImporter.Import(TestProjectPaths.ShaderPath(PostTonemapShaderFile))));
        var sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(new FrameSurfaceContext(64, 32)));

        Assert.Throws<InvalidOperationException>(() =>
            pass.AddTo(graph, sceneColor, RenderGraphHandle.Invalid));
    }
}
