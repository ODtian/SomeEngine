using SomeEngine.Rhi;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Graph;

[Flags]
public enum RenderGraphAccess
{
    None = 0,
    ReadOnly = 1,
    WriteOnly = 2,
    ReadWrite = ReadOnly | WriteOnly,
}

/// <summary>
/// Pass setup surface for declaring the resources a pass will access.
/// Setup may be replayed when a frame declaration is rebuilt, so callbacks must
/// only declare graph access and fill idempotent pass data used by execution.
/// </summary>
public readonly struct RenderGraphBuilder(RenderGraph graph, int passIndex, int setupToken)
{
    public void SideEffect()
        => graph.MarkSideEffect(passIndex, setupToken);

    public RenderGraphHandle Read(
        RenderGraphHandle handle,
        ResourceState state = ResourceState.ShaderResource)
        => Read(handle, state, SubResourceRange.All);

    public RenderGraphHandle Read(
        RenderGraphHandle handle,
        ResourceState state,
        SubResourceRange range)
        => Declare(handle, state, state, RenderGraphAccess.ReadOnly, range);

    internal RenderGraphHandle Read(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        SubResourceRange range)
        => Declare(handle, entryState, exitState, RenderGraphAccess.ReadOnly, range);

    public RenderGraphHandle Write(
        RenderGraphHandle handle,
        ResourceState state = ResourceState.RenderTarget)
        => Write(handle, state, SubResourceRange.All);

    public RenderGraphHandle Write(
        RenderGraphHandle handle,
        ResourceState state,
        SubResourceRange range)
        => Declare(handle, state, state, RenderGraphAccess.WriteOnly, range);

    internal RenderGraphHandle Write(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        SubResourceRange range)
        => Declare(handle, entryState, exitState, RenderGraphAccess.WriteOnly, range);

    public RenderGraphHandle ReadWrite(
        RenderGraphHandle handle,
        ResourceState state)
        => ReadWrite(handle, state, SubResourceRange.All);

    public RenderGraphHandle ReadWrite(
        RenderGraphHandle handle,
        ResourceState state,
        SubResourceRange range)
        => Declare(handle, state, state, RenderGraphAccess.ReadWrite, range);

    internal RenderGraphHandle ReadWrite(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState)
        => ReadWrite(handle, entryState, exitState, SubResourceRange.All);

    internal RenderGraphHandle ReadWrite(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        SubResourceRange range)
        => Declare(handle, entryState, exitState, RenderGraphAccess.ReadWrite, range);

    internal RenderGraphHandle Use(ReflectedBinding binding, RenderGraphHandle handle)
        => Use(binding, handle, SubResourceRange.All);

    internal RenderGraphHandle Use(
        ReflectedBinding binding,
        RenderGraphHandle handle,
        SubResourceRange range)
    {
        BindStateInfo info = BindRules.Resolve(binding.Type);
        if (info.Target is not BindTarget.BufferView and not BindTarget.TextureView)
        {
            throw new InvalidOperationException(
                $"Binding '{binding.Name}' is {binding.Type} and does not declare a RenderGraph resource use.");
        }

        return Declare(handle, info.State, info.State, PassBindings.BindingAccess(binding.Type), range);
    }

    internal RenderGraphHandle Use(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access,
        SubResourceRange range)
        => Declare(handle, entryState, exitState, access, range);

    internal RenderGraphHandle Use(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access)
        => Declare(handle, entryState, exitState, access, SubResourceRange.All);

    private RenderGraphHandle Declare(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access,
        SubResourceRange range)
    {
        graph.RegisterResourceUse(handle, passIndex, setupToken, entryState, exitState, access, range);
        return handle;
    }
}
