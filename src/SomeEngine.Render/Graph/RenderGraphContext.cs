using System;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

public class RenderGraphContext
{
    private readonly RenderGraph _graph;
    private readonly RenderContext _renderContext;

    public RenderGraphContext(RenderGraph graph, RenderContext renderContext)
    {
        _graph = graph;
        _renderContext = renderContext;
    }

    public ITexture? GetTexture(RGResourceHandle handle)
    {
        return _graph.GetPhysicalTexture(handle);
    }

    public ITextureView? GetTextureView(RGResourceHandle handle, TextureViewType type)
    {
        return _graph.GetPhysicalTextureView(handle, type);
    }
    
    // Helper accessors
    public RenderContext RenderContext => _renderContext;
    public IDeviceContext CommandList => _renderContext.ImmediateContext!;
}
