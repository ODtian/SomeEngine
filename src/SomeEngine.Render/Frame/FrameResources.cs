using SomeEngine.Render.Graph;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Frame;

public readonly record struct FrameSurfaceContext(uint Width, uint Height);

public static class FrameResources
{
    public const string OutputColor = "OutputColor";
    public const string SceneColor = "SceneColor";
    public const string SceneDepth = "SceneDepth";
    public const string MotionVectors = "MotionVectors";

    public const Format HdrSceneColorFormat = Format.Rgba16Float;
    public const Format MotionVectorsFormat = Format.Rg16Float;

    public static SceneTextures CreateSceneTextures(
        RenderGraph graph,
        in FrameData frame,
        in ViewData view,
        ResourceState outputState = ResourceState.Present)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var outputColor = graph.ImportTexture(
            OutputColor,
            frame.BackBuffer,
            frame.BackBufferDesc,
            new ImportDesc(outputState)
            {
                FinalState = ResourceState.Present,
                AllowWrite = true,
            },
            [frame.BackBufferView]);
        var sceneColor = graph.CreateTexture(
            SceneColor,
            SceneColorTexture(view));
        var sceneDepth = graph.CreateTexture(
            SceneDepth,
            SceneDepthTexture(view));

        return new SceneTextures(view, outputColor, sceneColor, sceneDepth);
    }

    internal static void AddClearPass(
        RenderGraph graph,
        RenderGraphHandle target,
        Color clearColor,
        string passName = "Clear Render Target")
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);

        graph.AddRasterPass(
            passName,
            builder => builder.Write(target, ResourceState.RenderTarget),
            context =>
            {
                var desc = context.GetTextureDesc(target);
                var rtv = context.GetTextureView(target, ViewKind.RenderTarget, desc.Format);
                ColorAttachmentDesc[] colorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = rtv,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = clearColor,
                    },
                ];

                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    Name = passName,
                    RenderArea = new Rect(0, 0, checked((int)desc.Width), checked((int)desc.Height)),
                    ColorAttachments = colorAttachments,
                });
                pass.End();
            });
    }

    public static TextureDesc SceneColorTexture(FrameSurfaceContext context)
        => new()
        {
            Name = SceneColor,
            Dimension = ResourceDimension.Texture2D,
            Width = Math.Max(context.Width, 1u),
            Height = Math.Max(context.Height, 1u),
            MipLevels = 1,
            Format = HdrSceneColorFormat,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            InitialState = ResourceState.Undefined,
        };

    public static TextureDesc SceneColorTexture(ViewData view)
        => SceneColorTexture(new FrameSurfaceContext(view.Width, view.Height));

    public static TextureDesc SceneDepthTexture(FrameSurfaceContext context)
        => new()
        {
            Name = SceneDepth,
            Dimension = ResourceDimension.Texture2D,
            Width = Math.Max(context.Width, 1u),
            Height = Math.Max(context.Height, 1u),
            MipLevels = 1,
            Format = Format.D32Float,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
            InitialState = ResourceState.Undefined,
            OptimizedClearValue = ClearValue.FromDepthStencil(
                Format.D32Float,
                new ClearDepthStencil(1.0f, 0)),
        };

    public static TextureDesc SceneDepthTexture(ViewData view)
        => SceneDepthTexture(new FrameSurfaceContext(view.Width, view.Height));

    public static TextureDesc MotionVectorTexture(FrameSurfaceContext context)
        => new()
        {
            Name = MotionVectors,
            Dimension = ResourceDimension.Texture2D,
            Width = Math.Max(context.Width, 1u),
            Height = Math.Max(context.Height, 1u),
            MipLevels = 1,
            Format = MotionVectorsFormat,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.RenderTarget,
            InitialState = ResourceState.Undefined,
        };

    public static TextureDesc MotionVectorTexture(ViewData view)
        => MotionVectorTexture(new FrameSurfaceContext(view.Width, view.Height));

    public static TextureDesc TemporalColorTexture(FrameSurfaceContext context)
        => SceneColorTexture(context) with
        {
            Name = RenderHistoryNames.TemporalSceneColor,
            BindFlags = SceneColorTexture(context).BindFlags | BindFlags.CopyDestination,
        };

    public static TextureDesc TemporalColorTexture(ViewData view)
        => TemporalColorTexture(new FrameSurfaceContext(view.Width, view.Height));

    public static TextureDesc TemporalMotionTexture(FrameSurfaceContext context)
        => MotionVectorTexture(context) with
        {
            Name = RenderHistoryNames.TemporalMotionVectors,
            BindFlags = MotionVectorTexture(context).BindFlags | BindFlags.CopyDestination,
        };

    public static TextureDesc TemporalMotionTexture(ViewData view)
        => TemporalMotionTexture(new FrameSurfaceContext(view.Width, view.Height));

    public static TextureDesc TemporalDepthTexture(FrameSurfaceContext context)
        => SceneDepthTexture(context) with
        {
            Name = RenderHistoryNames.TemporalSceneDepth,
            BindFlags = SceneDepthTexture(context).BindFlags | BindFlags.CopyDestination,
        };

    public static TextureDesc TemporalDepthTexture(ViewData view)
        => TemporalDepthTexture(new FrameSurfaceContext(view.Width, view.Height));
}
