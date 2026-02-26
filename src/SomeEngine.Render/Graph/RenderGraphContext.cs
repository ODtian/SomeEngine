using System;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

public class RenderGraphContext(RenderGraph graph, RenderContext renderContext)
{
    public ITexture? GetTexture(RGResourceHandle handle)
    {
        return graph.GetPhysicalTexture(handle);
    }

    public IBuffer? GetBuffer(RGResourceHandle handle)
    {
        return graph.GetPhysicalBuffer(handle);
    }

    public ITextureView? GetTextureView(RGResourceHandle handle, TextureViewType type)
    {
        return graph.GetPhysicalTextureView(handle, type);
    }

    public IBufferView? GetBufferView(RGResourceHandle handle, BufferViewType type)
    {
        return graph.GetPhysicalBufferView(handle, type);
    }

    // Helper accessors
    public RenderContext RenderContext => renderContext;
    public IDeviceContext CommandList => renderContext.ImmediateContext!;
}
