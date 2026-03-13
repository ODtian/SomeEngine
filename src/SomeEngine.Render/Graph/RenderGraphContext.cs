using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

public class RenderGraphContext(RenderGraph graph, RenderContext renderContext)
{
    public ITexture? GetTexture(RenderGraphHandle h)
    {
        return graph.GetPhysicalTexture(h);
    }

    public IBuffer? GetBuffer(RenderGraphHandle h)
    {
        return graph.GetPhysicalBuffer(h);
    }

    public ITextureView? GetView(RenderGraphHandle h, TextureViewType type)
    {
        return graph.GetPhysicalTextureView(h, type);
    }

    public ITextureView? GetTextureView(RenderGraphHandle h, TextureViewType type)
    {
        return graph.GetPhysicalTextureView(h, type);
    }

    public ITextureView? GetOrCreateView(RenderGraphHandle h, TextureViewDesc viewDesc)
    {
        return graph.GetOrCreateTextureView(h, viewDesc);
    }

    public IBufferView? GetBufferView(RenderGraphHandle h, BufferViewType type)
    {
        return graph.GetPhysicalBufferView(h, type);
    }

    public RenderContext RenderContext => renderContext;
    public IDeviceContext CommandList => renderContext.ImmediateContext!;
}
