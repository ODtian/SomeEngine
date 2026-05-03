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

    private StateTransitionDesc[] _pendingTransitions = new StateTransitionDesc[16];
    private int _pendingTransitionCount;

    public void QueueTransition(RenderGraphHandle h, ResourceState oldState, ResourceState newState)
    {
        QueueTransition(h, oldState, newState, SubResourceRange.All);
    }

    public void QueueTransition(
        RenderGraphHandle h,
        ResourceState oldState,
        ResourceState newState,
        SubResourceRange range
    )
    {
        if (!graph.TryBuildResourceTransition(h, oldState, newState, range, out var transition))
            return;

        if (_pendingTransitionCount == _pendingTransitions.Length)
            Array.Resize(ref _pendingTransitions, _pendingTransitions.Length * 2);

        _pendingTransitions[_pendingTransitionCount++] = transition;
    }

    public void QueueUavBarrier(RenderGraphHandle h)
    {
        QueueTransition(h, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess);
    }

    public void FlushTransitions()
    {
        graph.FlushResourceTransitions(renderContext, _pendingTransitions, _pendingTransitionCount);
        _pendingTransitionCount = 0;
    }

    public RenderContext RenderContext => renderContext;
    public IDeviceContext CommandList => renderContext.ImmediateContext!;
}
