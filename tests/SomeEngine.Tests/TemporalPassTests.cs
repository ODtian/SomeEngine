using SomeEngine.Assets.Importers;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Rhi;
using RenderContext = SomeEngine.Render.RHI.RenderContext;

namespace SomeEngine.Tests;

public class TemporalPassTests
{
    private const string TemporalResolveShaderFile = "temporal_resolve.slang";

    [Fact]
    public void UsesHistory()
    {
        using var graph = new RenderGraph();
        using var renderContext = RenderContext.CreateHeadless();
        using var pass = new TemporalResolvePass(
            renderContext,
            RuntimeAssetLoader.LoadShader(SlangShaderImporter.Import(TestProjectPaths.ShaderPath(TemporalResolveShaderFile))));
        var context = new FrameSurfaceContext(64, 32);

        var sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(context));
        var previous = PrevHistory(graph, context);
        var motionVectors = graph.CreateTexture(
            FrameResources.MotionVectors,
            FrameResources.MotionVectorTexture(context));
        var previousMotionVectors = PrevMotion(graph, context);
        var sceneDepth = graph.CreateTexture(
            FrameResources.SceneDepth,
            FrameResources.TemporalDepthTexture(context));
        var previousDepth = PrevDepth(graph, context);
        WriteInputs(graph, sceneColor, motionVectors, sceneDepth);
        var history = new RenderHistoryTexture(
            graph.CreateTexture(RenderHistoryNames.TemporalSceneColor, FrameResources.TemporalColorTexture(context)),
            previous,
            true);
        var motionHistory = new RenderHistoryTexture(
            graph.CreateTexture(RenderHistoryNames.TemporalMotionVectors, FrameResources.TemporalMotionTexture(context)),
            previousMotionVectors,
            true);
        var depthHistory = new RenderHistoryTexture(
            graph.CreateTexture(RenderHistoryNames.TemporalSceneDepth, FrameResources.TemporalDepthTexture(context)),
            previousDepth,
            true);
        graph.SetFinalState(history.Current, ResourceState.ShaderResource);

        pass.AddTo(
            graph,
            sceneColor,
            previous,
            motionVectors,
            previousMotionVectors,
            sceneDepth,
            previousDepth,
            history.Current,
            TemporalResolveSettings.Default);

        graph.Compile();

        var passNames = RenderGraphTestHelpers.ExecutedPassNames(graph);
        Assert.Contains("TemporalResolveUniforms", graph.DumpText());
        Assert.Contains(TemporalResolvePass.PassName, passNames);
    }

    [Fact]
    public void RejectsMissing()
    {
        using var graph = new RenderGraph();
        using var renderContext = RenderContext.CreateHeadless();
        using var pass = new TemporalResolvePass(
            renderContext,
            RuntimeAssetLoader.LoadShader(SlangShaderImporter.Import(TestProjectPaths.ShaderPath(TemporalResolveShaderFile))));
        var context = new FrameSurfaceContext(64, 32);

        var sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(context));
        var motionVectors = graph.CreateTexture(
            FrameResources.MotionVectors,
            FrameResources.MotionVectorTexture(context));
        var sceneDepth = graph.CreateTexture(
            FrameResources.SceneDepth,
            FrameResources.TemporalDepthTexture(context));
        var output = graph.CreateTexture(
            "TemporalResolveOutput",
            FrameResources.TemporalColorTexture(context) with { Name = "TemporalResolveOutput" });

        Assert.Throws<InvalidOperationException>((Action)(() => pass.AddTo(
            graph,
            sceneColor,
            motionVectors,
            sceneDepth,
            new RenderHistoryTexture(RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, false),
            new RenderHistoryTexture(RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, false),
            new RenderHistoryTexture(RenderGraphHandle.Invalid, RenderGraphHandle.Invalid, false),
            output,
            TemporalResolveSettings.Default)));
    }

    [Fact]
    public void TonemapAfter()
    {
        using var graph = new RenderGraph();
        using var renderContext = RenderContext.CreateHeadless();
        using var resolvePass = new TemporalResolvePass(
            renderContext,
            RuntimeAssetLoader.LoadShader(SlangShaderImporter.Import(TestProjectPaths.ShaderPath(TemporalResolveShaderFile))));
        using var tonemapPass = new PostTonemapPass(
            renderContext,
            RuntimeAssetLoader.LoadShader(SlangShaderImporter.Import(TestProjectPaths.ShaderPath("post_tonemap.slang"))));
        var context = new FrameSurfaceContext(64, 32);

        var sceneColor = graph.CreateTexture(
            FrameResources.SceneColor,
            FrameResources.SceneColorTexture(context));
        var previous = PrevHistory(graph, context);
        var motionVectors = graph.CreateTexture(
            FrameResources.MotionVectors,
            FrameResources.MotionVectorTexture(context));
        var previousMotionVectors = PrevMotion(graph, context);
        var sceneDepth = graph.CreateTexture(
            FrameResources.SceneDepth,
            FrameResources.TemporalDepthTexture(context));
        WriteInputs(graph, sceneColor, motionVectors, sceneDepth);
        var previousDepth = PrevDepth(graph, context);
        var history = new RenderHistoryTexture(
            graph.CreateTexture(RenderHistoryNames.TemporalSceneColor, FrameResources.TemporalColorTexture(context)),
            previous,
            true);
        var motionHistory = new RenderHistoryTexture(
            graph.CreateTexture(RenderHistoryNames.TemporalMotionVectors, FrameResources.TemporalMotionTexture(context)),
            previousMotionVectors,
            true);
        var depthHistory = new RenderHistoryTexture(
            graph.CreateTexture(RenderHistoryNames.TemporalSceneDepth, FrameResources.TemporalDepthTexture(context)),
            previousDepth,
            true);
        var backBuffer = graph.CreateTexture(
            FrameResources.OutputColor,
            RenderGraphTestHelpers.BackBufferDesc(64, 32));
        graph.SetFinalState(backBuffer, ResourceState.RenderTarget);

        resolvePass.AddTo(
            graph,
            sceneColor,
            motionVectors,
            sceneDepth,
            history,
            motionHistory,
            depthHistory,
            history.Current,
            TemporalResolveSettings.Default);
        tonemapPass.AddTo(graph, history.Current, backBuffer);

        graph.Compile();

        var passNames = RenderGraphTestHelpers.ExecutedPassNames(graph);
        int resolveIndex = Array.IndexOf(passNames, TemporalResolvePass.PassName);
        int tonemapIndex = Array.IndexOf(passNames, PostTonemapPass.PassName);
        Assert.True(resolveIndex >= 0);
        Assert.True(tonemapIndex > resolveIndex);
    }

    [Fact]
    public void ShaderContract()
    {
        Assert.EndsWith(".slang", TemporalResolveShaderFile);
        string source = File.ReadAllText(TestProjectPaths.ShaderPath(TemporalResolveShaderFile));

        Assert.Contains("PreviousSceneColor", source);
        Assert.Contains("MotionVectors", source);
        Assert.Contains("PreviousMotionVectors", source);
        Assert.Contains("SceneDepth", source);
        Assert.Contains("PreviousSceneDepth", source);
        Assert.Contains("TemporalResolveUniforms", source);
        Assert.Contains("SceneColor.GetDimensions", source);
        Assert.Contains("HistoryWeight", source);
        Assert.Contains("NeighborhoodClampScale", source);
        Assert.Contains("NeighborhoodClampMin", source);
        Assert.Contains("MotionRejectionScale", source);
        Assert.DoesNotContain("CurrentJitterPixels", source);
        Assert.DoesNotContain("LoadCurrentReconstructed", source);
        Assert.Contains("previousUv = uv - motion", source);
        Assert.DoesNotContain("previousUv = uv - currentJitterUv - motion", source);
        Assert.Contains("LoadPreviousSceneColorBilinear", source);
        Assert.Contains("LoadPreviousMotionBilinear", source);
        Assert.Contains("ResolveHistoryWeight", source);
        Assert.Contains("HistoryColorRejection", source);
        Assert.Contains("RgbToYCoCg", source);
        Assert.Contains("ClipAabb", source);
        Assert.Contains("ComputeCurrentNeighborhoodYCoCg", source);
        Assert.Contains("sigmaColor", source);
        Assert.Contains("motionPixels", source);
        Assert.Contains("LoadDilatedMotionByClosestDepth", source);
        Assert.Contains("VelocityDisocclusionRejection", source);
        Assert.Contains("DepthDisocclusionRejection", source);
        Assert.Contains("disocclusion = currentDepth - previousDepth", source);
    }

    [Fact]
    public void ShaderImports()
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath(TemporalResolveShaderFile));

        Assert.Contains(asset.Variants!, v =>
            v.EntryPoint == TemporalResolvePass.VertexEntryPoint
            && v.Data.HasValue
            && v.Data.Value.Length > 0);
        Assert.Contains(asset.Variants!, v =>
            v.EntryPoint == TemporalResolvePass.PixelEntryPoint
            && v.Data.HasValue
            && v.Data.Value.Length > 0);
    }

    [Fact]
    public void SettingsClamp()
    {
        TemporalResolveUniforms high = new TemporalResolveSettings(
            HistoryWeight: 3.0f,
            NeighborhoodClampScale: 10.0f,
            NeighborhoodClampMin: 2.0f,
            MotionRejectionScale: 999.0f).ToUniforms();

        Assert.Equal(TemporalResolveSettings.MaxHistoryWeight, high.HistoryWeight);
        Assert.Equal(TemporalResolveSettings.MaxNeighborhoodClampScale, high.NeighborhoodClampScale);
        Assert.Equal(TemporalResolveSettings.MaxNeighborhoodClampMin, high.NeighborhoodClampMin);
        Assert.Equal(TemporalResolveSettings.MaxMotionRejectionScale, high.MotionRejectionScale);

        TemporalResolveUniforms low = new TemporalResolveSettings(
            HistoryWeight: -1.0f,
            NeighborhoodClampScale: -1.0f,
            NeighborhoodClampMin: -1.0f,
            MotionRejectionScale: -1.0f).ToUniforms();

        Assert.Equal(TemporalResolveSettings.MinHistoryWeight, low.HistoryWeight);
        Assert.Equal(TemporalResolveSettings.MinNeighborhoodClampScale, low.NeighborhoodClampScale);
        Assert.Equal(TemporalResolveSettings.MinNeighborhoodClampMin, low.NeighborhoodClampMin);
        Assert.Equal(TemporalResolveSettings.MinMotionRejectionScale, low.MotionRejectionScale);
    }

    [Fact]
    public void DefaultHistory()
    {
        TemporalResolveUniforms uniforms = TemporalResolveSettings.Default.ToUniforms();

        Assert.InRange(uniforms.HistoryWeight, 0.92f, 0.96f);
        Assert.InRange(uniforms.MotionRejectionScale, 0.2f, 0.6f);
    }

    [Fact]
    public void UsesUniformGpu()
    {
        string source = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "TemporalResolvePass.cs"));

        Assert.Contains("UniformGpu<TemporalResolveUniforms>", source);
        Assert.DoesNotContain("RenderGraphUniforms.AddDynamicUniform", source);
    }

    private static RenderGraphHandle PrevHistory(
        RenderGraph graph,
        FrameSurfaceContext context)
    {
        const string name = $"{RenderHistoryNames.TemporalSceneColor}_Previous";
        var texture = graph.CreateTexture(
            name,
            FrameResources.TemporalColorTexture(context) with { Name = name });
        RenderGraphTestHelpers.WriteTexture(graph, texture, $"Write {name}");
        return texture;
    }

    private static RenderGraphHandle PrevDepth(
        RenderGraph graph,
        FrameSurfaceContext context)
    {
        const string name = $"{RenderHistoryNames.TemporalSceneDepth}_Previous";
        var texture = graph.CreateTexture(
            name,
            FrameResources.TemporalDepthTexture(context) with { Name = name });
        RenderGraphTestHelpers.WriteTexture(graph, texture, $"Write {name}", ResourceState.DepthWrite);
        return texture;
    }

    private static RenderGraphHandle PrevMotion(
        RenderGraph graph,
        FrameSurfaceContext context)
    {
        const string name = $"{RenderHistoryNames.TemporalMotionVectors}_Previous";
        var texture = graph.CreateTexture(
            name,
            FrameResources.TemporalMotionTexture(context) with { Name = name });
        RenderGraphTestHelpers.WriteTexture(graph, texture, $"Write {name}");
        return texture;
    }

    private static void WriteInputs(
        RenderGraph graph,
        RenderGraphHandle sceneColor,
        RenderGraphHandle motionVectors,
        RenderGraphHandle sceneDepth)
    {
        RenderGraphTestHelpers.WriteTexture(graph, sceneColor, "Write SceneColor");
        RenderGraphTestHelpers.WriteTexture(graph, motionVectors, "Write MotionVectors");
        RenderGraphTestHelpers.WriteTexture(graph, sceneDepth, "Write SceneDepth", ResourceState.DepthWrite);
    }
}
