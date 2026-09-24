using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class DepthMergePass
{
    private const string VertexEntryPoint = "VSFullscreen";
    private const string PixelEntryPoint = "PSDepthMerge";
    private const string PassName = "Depth Merge";
    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _layout;
    private PipelineTicket _pipeline;
    private bool _disposed;

    public DepthMergePass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        var device = _renderContext.GraphicsDevice ?? throw new InvalidOperationException("depth merge pipeline requires an initialized render context.");
        _layout = ShaderBindings.Create(
            device,
            PassName,
            shader,
            ["SWDepthUAV"],
            VertexEntryPoint,
            PixelEntryPoint);
        _pipeline = _renderContext.PipelineCache!.QueueGraphics(
            new GraphicsState
            {
                Name = "Depth Merge PipelineState",
                VertexShader = shader,
                VertexEntry = VertexEntryPoint,
                PixelShader = shader,
                PixelEntry = PixelEntryPoint,
                Layout = _layout.PipelineLayout,
                Bindings = _layout.Key,
                Topology = PrimitiveTopology.TriangleList,
                Rasterizer = new RasterizerDesc
                {
                    CullMode = CullMode.None,
                },
                DepthStencil = new DepthStencilDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthCompare = CompareOp.Less,
                },
                ColorFormats = [],
                DepthStencilFormat = Format.D32Float,
            },
            ClusterSources.Builtins);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_pipeline);
    }

    public ClusterRasterOutput AddPasses(
        RenderGraph graph,
        ClusterRasterOutput swRaster,
        RenderGraphHandle depthTarget = default,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!swRaster.VisBuffer.IsValid || !swRaster.RasterDepth.IsValid)
            throw new ArgumentException("depth merge requires valid SW raster output.", nameof(swRaster));

        RenderGraphHandle target = depthTarget.IsValid ? depthTarget : swRaster.DepthTarget;
        if (!target.IsValid)
            throw new ArgumentException("depth merge requires a valid HW depth target.", nameof(depthTarget));

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        ValidateMergeTargets(graph, swRaster.RasterDepth, target);
        AddMergePass(graph, swRaster.RasterDepth, target, prefix);
        return new ClusterRasterOutput(swRaster.VisBuffer, target, swRaster.RasterDepth);
    }

    public void AddPasses(
        RenderGraph graph,
        RenderGraphHandle swDepthUav,
        RenderGraphHandle depthTarget,
        string? tag = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!swDepthUav.IsValid)
            throw new ArgumentException("depth merge requires a valid SW depth UAV texture.", nameof(swDepthUav));
        if (!depthTarget.IsValid)
            throw new ArgumentException("depth merge requires a valid HW depth target.", nameof(depthTarget));

        string prefix = tag != null ? $"{tag}_" : string.Empty;
        ValidateMergeTargets(graph, swDepthUav, depthTarget);
        AddMergePass(graph, swDepthUav, depthTarget, prefix);
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        store.ReleaseIdle(ref _pipeline);
        var device = _renderContext.GraphicsDevice;
        if (device != null)
            ShaderBindings.Destroy(device, _layout);

        _disposed = true;
    }

    private void AddMergePass(
        RenderGraph graph,
        RenderGraphHandle swDepthUav,
        RenderGraphHandle depthTarget,
        string prefix)
    {
        graph.AddRasterPass(
            $"{prefix}{PassName}",
            builder =>
            {
                builder.Read(swDepthUav, ResourceState.ShaderResource);
                builder.ReadWrite(depthTarget, ResourceState.DepthWrite);
            },
            context =>
            {
                var layout = _layout;
                var depthDesc = context.GetTextureDesc(depthTarget);
                var swDepth = layout.GetRequired("SWDepthUAV");

                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    Name = $"{prefix}{PassName}",
                    RenderArea = new Rect(0, 0, checked((int)depthDesc.Width), checked((int)depthDesc.Height)),
                    DepthStencilAttachment = new DepthAttachDesc
                    {
                        View = context.GetTextureView(depthTarget, ViewKind.DepthStencil, Format.D32Float),
                        DepthLoadOp = LoadOp.Load,
                        DepthStoreOp = StoreOp.Store,
                        ClearValue = new ClearDepthStencil(1.0f, 0),
                    },
                });
                pass.SetViewport(new Viewport(0, 0, depthDesc.Width, depthDesc.Height));
                pass.SetScissor(new Rect(0, 0, checked((int)depthDesc.Width), checked((int)depthDesc.Height)));
                pass.SetPipeline(context.GetPipeline(_pipeline));
                pass.SetParameters(
                    swDepth.Set,
                    context.Bindings(layout.Layout(swDepth.Set))
                        .Texture(
                            swDepth.Binding,
                            swDepthUav,
                            new TextureViewDesc
                            {
                                Kind = ViewKind.ShaderResource,
                                Dimension = TextureViewDimension.Texture2D,
                                Format = Format.R32UInt,
                                FirstMip = 0,
                                MipCount = 1,
                                FirstSlice = 0,
                                SliceCount = 1,
                            }));
                pass.Draw(3);
                pass.End();
            });
    }

    private static void ValidateMergeTargets(
        RenderGraph graph,
        RenderGraphHandle swDepthUav,
        RenderGraphHandle depthTarget)
    {
        TextureDesc swDesc = graph.GetTextureDesc(swDepthUav);
        TextureDesc depthDesc = graph.GetTextureDesc(depthTarget);

        if (swDesc.Width != depthDesc.Width || swDesc.Height != depthDesc.Height)
        {
            throw new InvalidOperationException(
                $"depth merge requires matching dimensions, but SW depth is {swDesc.Width}x{swDesc.Height} and the HW depth target is {depthDesc.Width}x{depthDesc.Height}.");
        }

        if (swDesc.Format != Format.R32UInt)
        {
            throw new InvalidOperationException(
                $"depth merge requires SW depth format {Format.R32UInt} but was {swDesc.Format}.");
        }

        if (depthDesc.Format != Format.D32Float)
        {
            throw new InvalidOperationException(
                $"depth merge requires HW depth format {Format.D32Float} but was {depthDesc.Format}.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DepthMergePass));
    }

}

