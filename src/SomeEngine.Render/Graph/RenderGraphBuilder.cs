using Diligent;

namespace SomeEngine.Render.Graph;

[Flags]
public enum RenderGraphAccess
{
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

public struct RenderGraphBuilder(RenderGraph graph, int passIndex)
{
    public RenderGraphHandle Read(
        RenderGraphHandle h,
        ResourceState state = ResourceState.ShaderResource
    ) => Read(h, state, SubResourceRange.All);

    public RenderGraphHandle Read(RenderGraphHandle h, ResourceState state, SubResourceRange range)
    {
        return Use(h, state, state, RenderGraphAccess.Read, range);
    }

    public RenderGraphHandle Write(
        RenderGraphHandle h,
        ResourceState state = ResourceState.RenderTarget
    ) => Write(h, state, SubResourceRange.All);

    public RenderGraphHandle Write(RenderGraphHandle h, ResourceState state, SubResourceRange range)
    {
        return Use(h, state, state, RenderGraphAccess.Write, range);
    }

    public RenderGraphHandle ReadWrite(RenderGraphHandle h, ResourceState state) =>
        ReadWrite(h, state, SubResourceRange.All);

    public RenderGraphHandle ReadWrite(
        RenderGraphHandle h,
        ResourceState state,
        SubResourceRange range
    )
    {
        return Use(h, state, state, RenderGraphAccess.ReadWrite, range);
    }

    public RenderGraphHandle Use(
        RenderGraphHandle h,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access
    ) => Use(h, entryState, exitState, access, SubResourceRange.All);

    public RenderGraphHandle Use(
        RenderGraphHandle h,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access,
        SubResourceRange range
    )
    {
        graph.RegisterResourceUse(h, passIndex, entryState, exitState, access, range);
        return h;
    }
}
