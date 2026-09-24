using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class FullscreenPipeline : IDisposable
{
    private readonly RenderContext _renderContext;
    private readonly string _passName;
    private readonly string _shaderName;
    private readonly Shader _shader;
    private readonly string _vertexEntryPoint;
    private readonly string _pixelEntryPoint;
    private readonly string _pipelineName;
    private readonly IReadOnlyList<string> _pixelResources;
    private ReflectedBinding[] _pixelBindings = [];
    private BindingResourceDesc[] _resourceBindings = [];
    private bool[] _resourceBound = [];
    private ShaderBindingTable? _layout;
    private PipelineTicket _pipeline;
    private Format _outputFormat = Format.Unknown;
    private IDevice? _device;
    private bool _disposed;

    public FullscreenPipeline(
        RenderContext renderContext,
        string passName,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        string pipelineName,
        IReadOnlyList<string> pixelResources)
    {
        _renderContext = renderContext ?? throw new ArgumentNullException(nameof(renderContext));
        _passName = passName;
        _shader = shader ?? throw new ArgumentNullException(nameof(shader));
        _shaderName = ShaderKeys.Name(shader);
        _vertexEntryPoint = vertexEntryPoint;
        _pixelEntryPoint = pixelEntryPoint;
        _pipelineName = pipelineName;
        _pixelResources = pixelResources ?? throw new ArgumentNullException(nameof(pixelResources));
    }

    public void EnsureReady(Format outputFormat)
    {
        ThrowIfDisposed();
        var device = _renderContext.GraphicsDevice
            ?? throw new InvalidOperationException($"{_passName} requires an initialized render context.");

        if (_device != null && !ReferenceEquals(_device, device))
        {
            DisposeDeviceResources();
        }

        _device = device;
        if (_pipeline.IsValid && _outputFormat == outputFormat)
            return;

        if (outputFormat == Format.Unknown)
            throw new InvalidOperationException($"{_passName} requires a known output texture format.");

        DestroyPipeline();

        _layout ??= ShaderBindings.Create(
            device,
            _passName,
            _shader,
            _pixelResources,
            _vertexEntryPoint,
            _pixelEntryPoint);

        ShaderBindingTable layout = _layout
            ?? throw new InvalidOperationException($"{_passName} pipeline layout was not created.");
        if (_pixelBindings.Length != _pixelResources.Count)
        {
            _pixelBindings = new ReflectedBinding[_pixelResources.Count];
            _resourceBindings = new BindingResourceDesc[_pixelResources.Count];
            _resourceBound = new bool[_pixelResources.Count];
            for (int resourceIndex = 0; resourceIndex < _pixelResources.Count; resourceIndex++)
            {
                string resourceName = _pixelResources[resourceIndex];
                try
                {
                    _pixelBindings[resourceIndex] = layout.GetRequired(resourceName);
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(
                        $"{_passName} shader '{_shaderName}' is missing required pixel resource '{resourceName}'.",
                        ex);
                }
            }
        }

        _pipeline = _renderContext.PipelineCache!.QueueGraphics(
            new GraphicsState
            {
                Name = _pipelineName,
                VertexShader = _shader,
                VertexEntry = _vertexEntryPoint,
                PixelShader = _shader,
                PixelEntry = _pixelEntryPoint,
                Layout = layout.PipelineLayout,
                Bindings = layout.Key,
                Topology = PrimitiveTopology.TriangleList,
                Rasterizer = new RasterizerDesc { CullMode = CullMode.None },
                DepthStencil = new DepthStencilDesc { DepthEnable = false, DepthWriteEnable = false },
                ColorFormats = [outputFormat],
            },
            _passName);
        _renderContext.WaitRequired([_pipeline]);
        _outputFormat = outputFormat;
    }

    public void Use(RenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.UsePipelineCache(_renderContext.PipelineCache);
    }

    public void SetTextureResource(int resourceIndex, TextureViewHandle view)
    {
        if (!view.IsValid)
            throw new ArgumentException("texture resource view must be valid.", nameof(view));

        ReflectedBinding binding = GetRequiredBinding(resourceIndex);
        SetResourceBinding(resourceIndex, binding.Texture(view));
    }

    public void SetBufferResource(int resourceIndex, BufferViewHandle view)
    {
        if (!view.IsValid)
            throw new ArgumentException("buffer resource view must be valid.", nameof(view));

        ReflectedBinding binding = GetRequiredBinding(resourceIndex);
        SetResourceBinding(resourceIndex, binding.Buffer(view));
    }

    public void Draw(
        RenderGraphContext context,
        TextureViewHandle outputRtv,
        TextureDesc outputDesc,
        LoadOp loadOp)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        if (!_pipeline.IsValid)
            throw new InvalidOperationException($"{_passName} fullscreen PipelineState is not ready.");
        if (!outputRtv.IsValid)
            throw new ArgumentException("output render target view must be valid.", nameof(outputRtv));

        ValidateResourceBindings();
        Span<ColorAttachmentDesc> colorAttachments = stackalloc ColorAttachmentDesc[1];
        colorAttachments[0] = new ColorAttachmentDesc
        {
            View = outputRtv,
            LoadOp = loadOp,
            StoreOp = StoreOp.Store,
        };

        try
        {
            var pass = context.BeginRenderPass(new RenderPassDesc
            {
                Name = _passName,
                RenderArea = new Rect(0, 0, checked((int)outputDesc.Width), checked((int)outputDesc.Height)),
                ColorAttachments = colorAttachments,
            });

            pass.SetViewport(new Viewport(0, 0, outputDesc.Width, outputDesc.Height));
            pass.SetScissor(new Rect(0, 0, checked((int)outputDesc.Width), checked((int)outputDesc.Height)));
            pass.SetPipeline(context.GetPipeline(_pipeline));
            SetBindings(context, pass);

            pass.Draw(3);
            pass.End();
        }
        finally
        {
            Array.Clear(_resourceBindings);
            Array.Clear(_resourceBound);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        DisposeDeviceResources();
        _disposed = true;
    }

    private ReflectedBinding GetRequiredBinding(int resourceIndex)
    {
        if (_layout == null)
            throw new InvalidOperationException($"{_passName} shader resources are not initialized.");
        if ((uint)resourceIndex >= (uint)_pixelBindings.Length)
            throw new ArgumentOutOfRangeException(nameof(resourceIndex), resourceIndex, $"{_passName} shader resource index is outside the required resource list.");

        return _pixelBindings[resourceIndex];
    }

    private void ValidateResourceBindings()
    {
        var layout = _layout ?? throw new InvalidOperationException($"{_passName} pipeline layout was not created.");
        for (int resourceIndex = 0; resourceIndex < _pixelBindings.Length; resourceIndex++)
        {
            ReflectedBinding binding = _pixelBindings[resourceIndex];
            if (!_resourceBound[resourceIndex])
            {
                throw new InvalidOperationException(
                    $"{_passName} shader resource '{binding.Name}' was not bound before drawing.");
            }

            if (binding.Set >= layout.SetCount)
            {
                throw new InvalidOperationException(
                    $"{_passName} shader binding set {binding.Set} was reflected but no binding layout was created.");
            }
        }
    }

    private void SetResourceBinding(int resourceIndex, BindingResourceDesc resource)
    {
        _resourceBindings[resourceIndex] = resource;
        _resourceBound[resourceIndex] = true;
    }

    private void SetBindings(RenderGraphContext context, RenderPassCommands pass)
    {
        var layout = _layout ?? throw new InvalidOperationException($"{_passName} pipeline layout was not created.");
        for (int set = 0; set < layout.SetCount; set++)
        {
            uint setIndex = checked((uint)set);
            PassBindings parameters = context.Bindings(layout.Layout(setIndex)).Reserve(_pixelResources.Count);
            for (int resourceIndex = 0; resourceIndex < _pixelBindings.Length; resourceIndex++)
            {
                if (_resourceBound[resourceIndex] && _pixelBindings[resourceIndex].Set == setIndex)
                    parameters = parameters.Resource(_resourceBindings[resourceIndex]);
            }

            if (parameters.Count == 0)
                continue;

            pass.SetParameters(setIndex, parameters);
        }
    }

    private void DestroyPipeline()
    {
        _renderContext.PipelineCache?.Release(ref _pipeline);

        _outputFormat = Format.Unknown;
        Array.Clear(_resourceBindings);
        Array.Clear(_resourceBound);
    }

    private void DisposeDeviceResources()
    {
        DestroyPipeline();
        if (_device != null)
        {
            _device.WaitIdle();
            _renderContext.PipelineCache?.Retire(ulong.MaxValue);
            if (_layout.HasValue)
                ShaderBindings.Destroy(_device, _layout.Value);
        }

        _layout = null;
        _device = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FullscreenPipeline));
    }

}

