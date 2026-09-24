using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterSlotGpu : IDisposable
{
    private readonly IDevice _device;
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _views = new();
    private BufferHandle _buffer;
    private RenderGraph? _frameGraph;
    private ResourceState _state = ResourceState.Common;
    private int _bytes;
    private bool _disposed;

    public ClusterSlotGpu(IDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public BufferHandle Buffer => _buffer;
    public int ByteCount => _bytes;

    public RenderGraphHandle Add(RenderGraph graph, ClusterSlotBuffer slots)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(slots);

        ReadOnlySpan<ushort> data = slots.GetData();
        if (data.IsEmpty)
            throw new InvalidOperationException("material slot buffer has no uploadable data.");

        int bytes = slots.Layout.ByteCount;
        bool created = Ensure(bytes);
        RenderGraphHandle handle = graph.ImportBuffer(
            "SlotBuffer",
            _buffer,
            Desc(_bytes),
            new ImportDesc(_state)
            {
                AllowWrite = true,
            },
            _views);
        graph.ExtractBuffer(
            handle,
            ResourceState.ShaderResource,
            (_, state) => _state = state);
        _frameGraph = graph;

        if (AddUploads(graph, handle, slots, data, created))
            slots.ClearDirty();

        return handle;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _device.WaitIdle();
        ClearBindings();
        Destroy();
        _disposed = true;
    }

    private bool Ensure(int bytes)
    {
        if (_buffer.IsValid && _bytes >= bytes)
            return false;

        if (_buffer.IsValid)
        {
            _device.WaitIdle();
            ClearBindings();
            Destroy();
        }

        _bytes = bytes;
        _state = ResourceState.Common;
        _buffer = _device.CreateBuffer(Desc(_bytes));
        return true;
    }

    private static bool AddUploads(
        RenderGraph graph,
        RenderGraphHandle handle,
        ClusterSlotBuffer slots,
        ReadOnlySpan<ushort> data,
        bool full)
    {
        if (full || slots.NeedsFull)
        {
            BufferUploadPasses.AddUploadPass(
                graph,
                "SlotBuffer Upload",
                handle,
                0,
                MemoryMarshal.AsBytes(data));
            return true;
        }

        BufferUploadBatch? batch = null;
        ClusterSlotLayout layout = slots.Layout;
        for (int field = 0; field < layout.Fields; field++)
        {
            if (!slots.TryDirty(field, out int min, out int max))
                continue;

            ClusterSlotSpan span = layout.Span(field, min, max);
            ReadOnlySpan<ushort> dirty = data.Slice(span.Offset, span.Count);
            batch ??= new BufferUploadBatch(graph, "SlotBuffer Upload");
            batch.AddUpload(
                handle,
                checked((ulong)span.Offset * sizeof(ushort)),
                MemoryMarshal.AsBytes(dirty));
        }

        if (batch == null)
            return false;

        batch.AddPass();
        return true;
    }

    private static BufferDesc Desc(int bytes)
        => new()
        {
            Name = "SlotBuffer",
            SizeInBytes = checked((ulong)bytes),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            StrideInBytes = 4,
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ClusterSlotGpu));
    }

    private void Destroy()
    {
        DestroyViews();
        if (_buffer.IsValid)
            _device.Destroy(_buffer);

        _buffer = default;
        _frameGraph = null;
        _state = ResourceState.Common;
        _bytes = 0;
    }

    private void ClearBindings()
    {
        if (_views.Count == 0)
            return;

        RenderGraph? graph = _frameGraph;
        if (graph is { IsDisposed: false })
            graph.ClearBindSets(waitForGpu: true);
    }

    private void DestroyViews()
    {
        foreach (var pair in _views)
            _device.Destroy(pair.Value);
        _views.Clear();
    }
}
