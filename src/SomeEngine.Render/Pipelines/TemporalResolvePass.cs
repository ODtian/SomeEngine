using System.Runtime.InteropServices;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

public sealed class TemporalResolvePass : IDisposable
{
    public const string VertexEntryPoint = "VSMain";
    public const string PixelEntryPoint = "PSMain";
    public const string PassName = "Temporal Resolve";
    private const int SceneColorResourceIndex = 0;
    private const int PreviousSceneColorResourceIndex = 1;
    private const int MotionVectorsResourceIndex = 2;
    private const int PreviousMotionVectorsResourceIndex = 3;
    private const int SceneDepthResourceIndex = 4;
    private const int PreviousSceneDepthResourceIndex = 5;
    private const int UniformsResourceIndex = 6;
    private const string SceneColorResource = "SceneColor";
    private const string PreviousSceneColorResource = "PreviousSceneColor";
    private const string MotionVectorsResource = "MotionVectors";
    private const string PreviousMotionVectorsResource = "PreviousMotionVectors";
    private const string SceneDepthResource = "SceneDepth";
    private const string PreviousSceneDepthResource = "PreviousSceneDepth";
    private const string UniformsResource = "TemporalResolveUniforms";
    private static readonly string[] RequiredPixelResources =
    [
        SceneColorResource,
        PreviousSceneColorResource,
        MotionVectorsResource,
        PreviousMotionVectorsResource,
        SceneDepthResource,
        PreviousSceneDepthResource,
        UniformsResource,
    ];
    private readonly FullscreenPipeline _pipeline;
    private readonly UniformGpu<TemporalResolveUniforms> _uniformGpu;

    public TemporalResolvePass(RenderContext renderContext, Shader shader)
    {
        var device = renderContext.GraphicsDevice
            ?? throw new InvalidOperationException("Temporal resolve requires an initialized render context.");
        _uniformGpu = new UniformGpu<TemporalResolveUniforms>(device);
        _pipeline = new FullscreenPipeline(
            renderContext,
            PassName,
            shader,
            VertexEntryPoint,
            PixelEntryPoint,
            "Temporal Resolve PipelineState",
            RequiredPixelResources);
    }

    public void AddTo(
        RenderGraph graph,
        RenderGraphHandle sceneColor,
        RenderGraphHandle motionVectors,
        RenderGraphHandle sceneDepth,
        RenderHistoryTexture temporalHistory,
        RenderHistoryTexture temporalMotionHistory,
        RenderHistoryTexture temporalDepthHistory,
        RenderGraphHandle output,
        TemporalResolveSettings settings)
    {
        if (!temporalHistory.HasPrevious || !temporalHistory.Previous.IsValid)
            throw new InvalidOperationException("Temporal resolve requires valid previous temporal history.");
        if (!temporalMotionHistory.HasPrevious || !temporalMotionHistory.Previous.IsValid)
            throw new InvalidOperationException("Temporal resolve requires valid previous temporal motion history.");
        if (!temporalDepthHistory.HasPrevious || !temporalDepthHistory.Previous.IsValid)
            throw new InvalidOperationException("Temporal resolve requires valid previous temporal depth history.");

        AddTo(
            graph,
            sceneColor,
            temporalHistory.Previous,
            motionVectors,
            temporalMotionHistory.Previous,
            sceneDepth,
            temporalDepthHistory.Previous,
            output,
            settings);
    }

    public void AddTo(
        RenderGraph graph,
        RenderGraphHandle sceneColor,
        RenderGraphHandle previousSceneColor,
        RenderGraphHandle motionVectors,
        RenderGraphHandle previousMotionVectors,
        RenderGraphHandle sceneDepth,
        RenderGraphHandle previousSceneDepth,
        RenderGraphHandle output,
        TemporalResolveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _pipeline.Use(graph);

        RenderGraphHandle resolvedSceneColor = RequireValid(sceneColor, nameof(sceneColor));
        RenderGraphHandle resolvedPreviousSceneColor = RequireValid(previousSceneColor, nameof(previousSceneColor));
        RenderGraphHandle resolvedMotionVectors = RequireValid(motionVectors, nameof(motionVectors));
        RenderGraphHandle resolvedPreviousMotionVectors = RequireValid(previousMotionVectors, nameof(previousMotionVectors));
        RenderGraphHandle resolvedSceneDepth = RequireValid(sceneDepth, nameof(sceneDepth));
        RenderGraphHandle resolvedPreviousSceneDepth = RequireValid(previousSceneDepth, nameof(previousSceneDepth));
        RenderGraphHandle resolvedOutput = RequireValid(output, nameof(output));
        _pipeline.EnsureReady(graph.GetTextureDesc(output).Format);
        TemporalResolveUniforms uniforms = settings.ToUniforms();
        RenderGraphHandle uniformBuffer = _uniformGpu.Add(
            graph,
            MemoryMarshal.CreateReadOnlySpan(ref uniforms, 1),
            UniformsResource).Buffer;

        graph.AddRasterPass<PassData>(
            PassName,
            (builder, data) =>
            {
                data.SceneColor = resolvedSceneColor;
                data.PreviousSceneColor = resolvedPreviousSceneColor;
                data.MotionVectors = resolvedMotionVectors;
                data.PreviousMotionVectors = resolvedPreviousMotionVectors;
                data.SceneDepth = resolvedSceneDepth;
                data.PreviousSceneDepth = resolvedPreviousSceneDepth;
                data.Uniforms = uniformBuffer;
                data.Output = resolvedOutput;

                builder.Read(data.SceneColor, ResourceState.ShaderResource);
                builder.Read(data.PreviousSceneColor, ResourceState.ShaderResource);
                builder.Read(data.MotionVectors, ResourceState.ShaderResource);
                builder.Read(data.PreviousMotionVectors, ResourceState.ShaderResource);
                builder.Read(data.SceneDepth, ResourceState.ShaderResource);
                builder.Read(data.PreviousSceneDepth, ResourceState.ShaderResource);
                builder.Read(data.Uniforms, ResourceState.ConstantBuffer);
                builder.Write(data.Output, ResourceState.RenderTarget);
            },
            (context, data) =>
            {
                TextureDesc outputDesc = context.GetTextureDesc(data.Output);

                TextureViewHandle sceneSrv = GetSrv(context, data.SceneColor);
                TextureViewHandle previousSrv = GetSrv(context, data.PreviousSceneColor);
                TextureViewHandle motionSrv = GetSrv(context, data.MotionVectors);
                TextureViewHandle previousMotionSrv = GetSrv(context, data.PreviousMotionVectors);
                TextureViewHandle sceneDepthSrv = GetSrv(context, data.SceneDepth);
                TextureViewHandle previousDepthSrv = GetSrv(context, data.PreviousSceneDepth);
                BufferViewHandle resolvedUniformBuffer = context.GetBufferView(data.Uniforms, ViewKind.ConstantBuffer);
                TextureViewHandle outputRtv = context.GetTextureView(data.Output, ViewKind.RenderTarget, outputDesc.Format);

                _pipeline.SetTextureResource(SceneColorResourceIndex, sceneSrv);
                _pipeline.SetTextureResource(PreviousSceneColorResourceIndex, previousSrv);
                _pipeline.SetTextureResource(MotionVectorsResourceIndex, motionSrv);
                _pipeline.SetTextureResource(PreviousMotionVectorsResourceIndex, previousMotionSrv);
                _pipeline.SetTextureResource(SceneDepthResourceIndex, sceneDepthSrv);
                _pipeline.SetTextureResource(PreviousSceneDepthResourceIndex, previousDepthSrv);
                _pipeline.SetBufferResource(UniformsResourceIndex, resolvedUniformBuffer);
                _pipeline.Draw(context, outputRtv, outputDesc, LoadOp.DontCare);
            });
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _uniformGpu.Dispose();
    }

    private static TextureViewHandle GetSrv(RenderGraphContext context, RenderGraphHandle handle)
        => context.GetTextureView(handle, ViewKind.ShaderResource);

    private static RenderGraphHandle RequireValid(RenderGraphHandle handle, string name)
        => handle.IsValid
            ? handle
            : throw new ArgumentException("Temporal resolve requires a valid render graph handle.", name);

    private sealed class PassData
    {
        public RenderGraphHandle SceneColor;
        public RenderGraphHandle PreviousSceneColor;
        public RenderGraphHandle MotionVectors;
        public RenderGraphHandle PreviousMotionVectors;
        public RenderGraphHandle SceneDepth;
        public RenderGraphHandle PreviousSceneDepth;
        public RenderGraphHandle Uniforms;
        public RenderGraphHandle Output;
    }
}
