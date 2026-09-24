using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterResolvePass
{
    private const string EntryPoint = "CSResolve";
    private const string PassName = "Cluster Resolve";

    private readonly RenderContext _renderContext;
    private readonly ShaderBindingTable _layout;
    private PipelineTicket _pipeline;
    private bool _disposed;

    public ClusterResolvePass(RenderContext renderContext, Shader shader)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        IDevice device = renderContext.GraphicsDevice ?? throw new InvalidOperationException("cluster resolve stage requires an initialized render context.");
        _layout = ShaderBindings.Create(
            device,
            PassName,
            shader,
            [
                "Uniforms",
                "Instances",
                "InstanceHeaders",
                "InstanceDataHeap",
                "VisBuffer",
                "DepthBuffer",
                "VisibleClusters",
                "PageHeap",
                "OutputColor",
            ],
            EntryPoint);
        _pipeline = renderContext.PipelineCache!.QueueCompute(
            shader,
            _layout,
            EntryPoint,
            PassName,
            source: ClusterSources.Builtins);
    }

    public void AddTickets(List<PipelineTicket> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        tickets.Add(_pipeline);
    }

    public void AddPasses(
        RenderGraph graph,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        UniformFrame uniforms,
        RenderGraphHandle outputColor)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!buffers.PageHeap.IsValid || !instances.Transform.IsValid)
            throw new ArgumentException("cluster resolve requires valid cluster buffers and instance frame.", nameof(buffers));
        if (!raster.VisBuffer.IsValid || !raster.DepthTarget.IsValid)
            throw new ArgumentException("cluster resolve requires valid raster output.", nameof(raster));
        if (!cull.VisibleClusters.IsValid)
            throw new ArgumentException("cluster resolve requires valid cull output.", nameof(cull));
        if (!uniforms.IsValid)
            throw new ArgumentException("cluster resolve requires a valid draw uniform frame.", nameof(uniforms));
        if (!outputColor.IsValid)
            throw new ArgumentException("cluster resolve requires a valid output color texture.", nameof(outputColor));

        graph.AddComputePass(
            PassName,
            builder =>
            {
                builder.Read(uniforms.Buffer, ResourceState.ConstantBuffer);
                builder.Read(raster.VisBuffer, ResourceState.ShaderResource);
                builder.Read(raster.DepthTarget, ResourceState.ShaderResource);
                builder.Read(cull.VisibleClusters, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Read(instances.Transform, ResourceState.ShaderResource);
                builder.Read(instances.Header, ResourceState.ShaderResource);
                builder.Read(instances.Data, ResourceState.ShaderResource);
                builder.Write(outputColor, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                var colorDesc = context.GetTextureDesc(outputColor);
                var bindings = ResolveBindings(
                    context,
                    buffers,
                    instances,
                    raster,
                    cull,
                    uniforms,
                    outputColor);
                pass.SetPipeline(context.GetPipeline(_pipeline));
                pass.SetParameters(bindings.Set, bindings.Parameters);
                pass.Dispatch(
                    checked((colorDesc.Width + 7u) / 8u),
                    checked((colorDesc.Height + 7u) / 8u),
                    1);
            });
    }

    public void Dispose(PipelineCache store)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(store);

        store.ReleaseIdle(ref _pipeline);
        ShaderBindings.Destroy(_renderContext.GraphicsDevice, _layout);
        _disposed = true;
    }

    private (uint Set, PassBindings Parameters) ResolveBindings(
        RenderGraphContext context,
        ClusterBuffers buffers,
        InstanceFrame instances,
        ClusterRasterOutput raster,
        ClusterCullOutput cull,
        UniformFrame uniforms,
        RenderGraphHandle outputColor)
    {
        var uniformsBinding = _layout.GetRequired("Uniforms");
        uint? set = uniformsBinding.Set;
        PassBindings bindings = context.Bindings(_layout.Layout(uniformsBinding.Set)).Reserve(9);
        BindInput.AddResource(
            context,
            _layout,
            ref bindings,
            ref set,
            uniformsBinding,
            uniformsBinding.Buffer(
                context,
                uniforms.Buffer,
                new BufferViewDesc
                {
                    Kind = ViewKind.ConstantBuffer,
                    Offset = uniforms.Offset(0),
                    SizeInBytes = uniforms.SlotBytes,
                }),
            PassBindings.BindingAccess(uniformsBinding.Type),
            "cluster resolve",
            9);

        if (TryGetBinding(_layout, "Instances", out var instancesBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                instancesBinding,
                instancesBinding.Buffer(context, instances.Transform),
                PassBindings.BindingAccess(instancesBinding.Type),
                "cluster resolve",
                9);
        }

        if (TryGetBinding(_layout, "InstanceHeaders", out var headersBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                headersBinding,
                headersBinding.Buffer(context, instances.Header),
                PassBindings.BindingAccess(headersBinding.Type),
                "cluster resolve",
                9);
        }

        if (TryGetBinding(_layout, "InstanceDataHeap", out var dataBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                dataBinding,
                dataBinding.Buffer(context, instances.Data),
                PassBindings.BindingAccess(dataBinding.Type),
                "cluster resolve",
                9);
        }

        if (TryGetBinding(_layout, "VisBuffer", out var visBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                visBinding,
                visBinding.Texture(context.GetTextureView(raster.VisBuffer, ViewKind.ShaderResource, Format.R32UInt)),
                PassBindings.BindingAccess(visBinding.Type),
                "cluster resolve",
                9);
        }

        if (TryGetBinding(_layout, "DepthBuffer", out var depthBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                depthBinding,
                depthBinding.Texture(context.GetTextureView(raster.DepthTarget, ViewKind.ShaderResource, Format.D32Float)),
                PassBindings.BindingAccess(depthBinding.Type),
                "cluster resolve",
                9);
        }

        if (TryGetBinding(_layout, "VisibleClusters", out var visibleBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                visibleBinding,
                visibleBinding.Buffer(context, cull.VisibleClusters),
                PassBindings.BindingAccess(visibleBinding.Type),
                "cluster resolve",
                9);
        }

        if (TryGetBinding(_layout, "PageHeap", out var pageBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                pageBinding,
                pageBinding.Buffer(context, buffers.PageHeap),
                PassBindings.BindingAccess(pageBinding.Type),
                "cluster resolve",
                9);
        }

        if (TryGetBinding(_layout, "OutputColor", out var outputBinding))
        {
            BindInput.AddResource(
                context,
                _layout,
                ref bindings,
                ref set,
                outputBinding,
                outputBinding.Texture(context, outputColor, RenderGraphAccess.WriteOnly),
                RenderGraphAccess.WriteOnly,
                "cluster resolve",
                9);
        }

        if (!set.HasValue)
            throw new InvalidOperationException("cluster resolve shader reflected no bindable resources.");

        return (set.Value, bindings);
    }

    private static bool TryGetBinding(
        ShaderBindingTable layout,
        string name,
        out ReflectedBinding binding)
        => layout.TryGet(name, out binding);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ClusterResolvePass));
    }
}

