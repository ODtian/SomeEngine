using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Frame;

public sealed class ViewHistory : IDisposable
{
    private readonly TextureHistoryState _hiZ = new(RenderHistoryNames.HiZ);
    private readonly TextureHistoryState _temporalSceneColor = new(RenderHistoryNames.TemporalSceneColor);
    private readonly TextureHistoryState _temporalMotionVectors = new(RenderHistoryNames.TemporalMotionVectors);
    private readonly TextureHistoryState _temporalSceneDepth = new(RenderHistoryNames.TemporalSceneDepth);

    private IDevice? _device;
    private bool _disposed;

    public RenderHistoryTexture ImportHiZ(
        RenderGraph graph,
        IDevice device,
        TextureDesc desc,
        ResourceState finalState = ResourceState.ShaderResource)
        => ImportTexture(_hiZ, graph, device, desc, finalState);

    public RenderHistoryTexture ImportTemporalSceneColor(
        RenderGraph graph,
        IDevice device,
        TextureDesc desc,
        ResourceState finalState = ResourceState.ShaderResource)
        => ImportTexture(_temporalSceneColor, graph, device, desc, finalState);

    public RenderHistoryTexture ImportTemporalMotionVectors(
        RenderGraph graph,
        IDevice device,
        TextureDesc desc,
        ResourceState finalState = ResourceState.ShaderResource)
        => ImportTexture(_temporalMotionVectors, graph, device, desc, finalState);

    public RenderHistoryTexture ImportTemporalSceneDepth(
        RenderGraph graph,
        IDevice device,
        TextureDesc desc,
        ResourceState finalState = ResourceState.ShaderResource)
        => ImportTexture(_temporalSceneDepth, graph, device, desc, finalState);

    public void ResetTemporal(bool waitForGpu = true)
    {
        ThrowIfDisposed();
        _temporalSceneColor.Reset(_device, waitForGpu);
        _temporalMotionVectors.Reset(_device, waitForGpu);
        _temporalSceneDepth.Reset(_device, waitForGpu);
    }

    public void ResetAll(bool waitForGpu = true)
    {
        ThrowIfDisposed();
        _hiZ.Reset(_device, waitForGpu);
        ResetTemporal(waitForGpu);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ResetAll(waitForGpu: true);
        _device = null;
        _disposed = true;
    }

    private RenderHistoryTexture ImportTexture(
        TextureHistoryState state,
        RenderGraph graph,
        IDevice device,
        TextureDesc desc,
        ResourceState finalState)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        RegisterDevice(device);
        desc = NormalizeTextureDesc(desc);

        if (state.IsAllocated && !AreCompatible(state.Desc, desc))
            state.Reset(device, waitForGpu: true);

        RenderGraphHandle previous = RenderGraphHandle.Invalid;
        bool hasPrevious = state.HasPrevious && state.PreviousHandle.IsValid;
        if (hasPrevious)
        {
            previous = graph.ImportTexture(
                state.PreviousName,
                state.PreviousHandle,
                state.Desc with { Name = state.PreviousName },
                new ImportDesc(state.PreviousState));
        }

        if (!state.CurrentHandle.IsValid)
        {
            state.CurrentHandle = device.CreateTexture(desc with { Name = state.CurrentPhysicalName });
            state.CurrentState = desc.InitialState;
        }

        state.Desc = desc;
        RenderGraphHandle current = graph.ImportTexture(
            state.CurrentName,
            state.CurrentHandle,
            desc with { Name = state.CurrentName },
            new ImportDesc(state.CurrentState)
            {
                AllowWrite = true,
            });
        graph.ExtractTexture(current, finalState, (_, publishedState) => state.CommitCurrent(publishedState));

        return new RenderHistoryTexture(current, previous, hasPrevious);
    }

    private void RegisterDevice(IDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_device != null && !ReferenceEquals(_device, device))
            throw new InvalidOperationException("ViewHistory cannot be reused with a different RHI device.");

        _device = device;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ViewHistory));
    }

    private static TextureDesc NormalizeTextureDesc(TextureDesc desc)
        => desc with
        {
            Width = Math.Max(desc.Width, 1u),
            Height = Math.Max(desc.Height, 1u),
        };

    private static bool AreCompatible(TextureDesc left, TextureDesc right)
        => left.Dimension == right.Dimension
            && left.Width == right.Width
            && left.Height == right.Height
            && left.Depth == right.Depth
            && left.ArraySize == right.ArraySize
            && left.MipLevels == right.MipLevels
            && left.SampleCount == right.SampleCount
            && left.Format == right.Format
            && left.BindFlags == right.BindFlags
            && left.Memory == right.Memory;

    private sealed class TextureHistoryState(string name)
    {
        public TextureDesc Desc = new();
        public TextureHandle AHandle;
        public TextureHandle BHandle;
        public ResourceState AState;
        public ResourceState BState;
        public bool HasPrevious;
        public bool Flip;

        public bool IsAllocated => AHandle.IsValid || BHandle.IsValid;
        public string CurrentName => $"{name}.Current";
        public string PreviousName => $"{name}.Previous";
        public string CurrentPhysicalName => Flip ? $"{name}_B" : $"{name}_A";

        public TextureHandle CurrentHandle
        {
            get => Flip ? BHandle : AHandle;
            set
            {
                if (Flip)
                    BHandle = value;
                else
                    AHandle = value;
            }
        }

        public ResourceState CurrentState
        {
            get => Flip ? BState : AState;
            set
            {
                if (Flip)
                    BState = value;
                else
                    AState = value;
            }
        }

        public TextureHandle PreviousHandle => Flip ? AHandle : BHandle;
        public ResourceState PreviousState => Flip ? AState : BState;

        public void CommitCurrent(ResourceState state)
        {
            CurrentState = state;
            HasPrevious = true;
            Flip = !Flip;
        }

        public void Reset(IDevice? device, bool waitForGpu)
        {
            if (device == null)
            {
                if (AHandle.IsValid || BHandle.IsValid)
                    throw new InvalidOperationException("ViewHistory reset requires an RHI device after history textures have been created.");
                return;
            }

            if (waitForGpu)
                device.WaitIdle();

            if (AHandle.IsValid)
                device.Destroy(AHandle);
            if (BHandle.IsValid)
                device.Destroy(BHandle);

            AHandle = default;
            BHandle = default;
            AState = default;
            BState = default;
            HasPrevious = false;
            Flip = false;
            Desc = new();
        }
    }
}
