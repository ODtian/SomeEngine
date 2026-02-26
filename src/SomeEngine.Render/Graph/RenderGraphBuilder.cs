using Diligent;

namespace SomeEngine.Render.Graph;

public class RenderGraphBuilder(RenderGraph graph, RenderPass pass)
{
    public RGResourceHandle ReadTexture(
        RGResourceHandle handle,
        ResourceState state = ResourceState.ShaderResource
    )
    {
        graph.RegisterResourceRead(handle, pass, state);
        return handle;
    }

    public RGResourceHandle WriteTexture(
        RGResourceHandle handle,
        ResourceState state = ResourceState.RenderTarget
    )
    {
        return graph.RegisterResourceWrite(handle, pass, state);
    }

    public RGResourceHandle ReadBuffer(
        RGResourceHandle handle,
        ResourceState state = ResourceState.ShaderResource
    )
    {
        graph.RegisterResourceRead(handle, pass, state);
        return handle;
    }

    public RGResourceHandle WriteBuffer(
        RGResourceHandle handle,
        ResourceState state = ResourceState.UnorderedAccess
    )
    {
        return graph.RegisterResourceWrite(handle, pass, state);
    }

    // Create transient texture for this pass (output)
    public RGResourceHandle CreateTexture(string name, TextureDesc desc)
    {
        return graph.CreateTexture(name, desc);
    }

    public RGResourceHandle CreateBuffer(string name, BufferDesc desc)
    {
        return graph.CreateBuffer(name, desc);
    }

    public void MarkAsOutput(RGResourceHandle handle)
    {
        graph.MarkAsOutput(handle);
    }

    // ... Add Buffer methods similarly
}
