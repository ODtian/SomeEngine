using Diligent;

namespace SomeEngine.Render.Graph;

public struct RenderGraphBuilder(RenderGraph graph, int passIndex)
{
    public RenderGraphHandle Read(
        RenderGraphHandle h,
        ResourceState state = ResourceState.ShaderResource
    ) => Read(h, state, SubResourceRange.All);

    public RenderGraphHandle Read(RenderGraphHandle h, ResourceState state, SubResourceRange range)
    {
        graph.RegisterResourceRead(h, passIndex, state, range);
        return h;
    }

    public RenderGraphHandle Write(
        RenderGraphHandle h,
        ResourceState state = ResourceState.RenderTarget
    ) => Write(h, state, SubResourceRange.All);

    public RenderGraphHandle Write(RenderGraphHandle h, ResourceState state, SubResourceRange range)
    {
        graph.RegisterResourceWrite(h, passIndex, state, range);
        return h;
    }

    public RenderGraphHandle ReadWrite(RenderGraphHandle h, ResourceState state) =>
        ReadWrite(h, state, SubResourceRange.All);

    public RenderGraphHandle ReadWrite(
        RenderGraphHandle h,
        ResourceState state,
        SubResourceRange range
    )
    {
        graph.RegisterResourceRead(h, passIndex, state, range);
        graph.RegisterResourceWrite(h, passIndex, state, range);
        return h;
    }
}
