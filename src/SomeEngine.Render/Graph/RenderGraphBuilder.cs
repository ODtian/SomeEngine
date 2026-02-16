using Diligent;

namespace SomeEngine.Render.Graph;

public class RenderGraphBuilder
{
    private readonly RenderGraph _graph;
    private readonly RenderPass _pass;

    public RenderGraphBuilder(RenderGraph graph, RenderPass pass)
    {
        _graph = graph;
        _pass = pass;
    }

    public RGResourceHandle ReadTexture(RGResourceHandle handle, ResourceState state = ResourceState.ShaderResource)
    {
        _graph.RegisterResourceRead(handle, _pass, state);
        return handle;
    }

    public RGResourceHandle WriteTexture(RGResourceHandle handle, ResourceState state = ResourceState.RenderTarget)
    {
        return _graph.RegisterResourceWrite(handle, _pass, state);
    }
    
    // Create transient texture for this pass (output)
    public RGResourceHandle CreateTexture(string name, TextureDesc desc)
    {
        return _graph.CreateTexture(name, desc);
    }

    // ... Add Buffer methods similarly
}
