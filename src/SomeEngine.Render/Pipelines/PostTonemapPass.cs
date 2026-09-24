using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

public sealed class PostTonemapPass : IDisposable
{
    public const string VertexEntryPoint = "VSMain";
    public const string PixelEntryPoint = "PSMain";
    public const string PassName = "Post Tonemap";
    private const int SceneColorResourceIndex = 0;
    private const string SceneColorResource = "SceneColor";
    private static readonly string[] RequiredPixelResources = [SceneColorResource];
    private readonly FullscreenPipeline _pipeline;

    public PostTonemapPass(RenderContext renderContext, Shader shader)
    {
        _pipeline = new FullscreenPipeline(
            renderContext,
            PassName,
            shader,
            VertexEntryPoint,
            PixelEntryPoint,
            "Post Tonemap PipelineState",
            RequiredPixelResources);
    }

    public void AddTo(RenderGraph graph, RenderGraphHandle sceneColor, RenderGraphHandle output)
    {
        if (!sceneColor.IsValid || !output.IsValid)
            throw new InvalidOperationException($"{PassName} requires valid scene color and output handles.");

        _pipeline.Use(graph);
        _pipeline.EnsureReady(graph.GetTextureDesc(output).Format);
        graph.AddRasterPass<PassData>(
            PassName,
            (builder, data) =>
            {
                data.SceneColor = sceneColor;
                data.Output = output;

                builder.Read(data.SceneColor, ResourceState.ShaderResource);
                builder.Write(data.Output, ResourceState.RenderTarget);
            },
            (context, data) =>
            {
                TextureDesc outputDesc = context.GetTextureDesc(data.Output);
                TextureViewHandle sceneSrv = context.GetTextureView(data.SceneColor, ViewKind.ShaderResource);
                TextureViewHandle outputRtv = context.GetTextureView(data.Output, ViewKind.RenderTarget, outputDesc.Format);

                _pipeline.SetTextureResource(SceneColorResourceIndex, sceneSrv);
                _pipeline.Draw(context, outputRtv, outputDesc, LoadOp.DontCare);
            });
    }

    public void Dispose()
        => _pipeline.Dispose();

    private sealed class PassData
    {
        public RenderGraphHandle SceneColor;
        public RenderGraphHandle Output;
    }
}
