using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;
using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.RHI;

internal readonly record struct UniformFrame(
    RenderGraphHandle Buffer,
    int Count,
    ulong SlotBytes)
{
    public bool IsValid => Buffer.IsValid;

    public ulong Offset(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index), "uniform slot index is outside the active range.");

        return checked((ulong)index * SlotBytes);
    }
}

internal sealed class UniformPool<T> : IDisposable
    where T : unmanaged
{
    private readonly IDevice _device;
    private readonly List<UniformGpu<T>> _pool = [];
    private RenderGraph? _graph;
    private int _generation;
    private int _used;
    private bool _disposed;

    public UniformPool(IDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public UniformFrame Add(
        RenderGraph graph,
        ReadOnlySpan<T> values,
        string name)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        if (!ReferenceEquals(_graph, graph) || _generation != graph.Generation)
        {
            _graph = graph;
            _generation = graph.Generation;
            _used = 0;
        }

        if (_used == _pool.Count)
            _pool.Add(new UniformGpu<T>(_device));

        return _pool[_used++].Add(graph, values, name);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        for (int i = 0; i < _pool.Count; i++)
            _pool[i].Dispose();

        _pool.Clear();
        _graph = null;
        _generation = 0;
        _used = 0;
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UniformPool<T>));
    }
}

internal sealed class UniformGpu<T> : IDisposable
    where T : unmanaged
{
    private readonly IDevice _device;
    private readonly ulong _slotBytes;
    private readonly List<(BufferHandle Buffer, FlatDictionary<BufferViewKey, BufferViewHandle> Views)> _retired = [];
    private FlatDictionary<BufferViewKey, BufferViewHandle> _views = new();
    private BufferHandle _buffer;
    private ulong _bufferBytes;
    private RenderGraph? _frameGraph;
    private byte[] _scratch = [];
    private int _frameGeneration;
    private bool _disposed;

    public UniformGpu(IDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _slotBytes = AlignUp(
            checked((ulong)Unsafe.SizeOf<T>()),
            Math.Max(256ul, device.Limits.MinConstantBufferOffsetAlignment));
    }

    public ulong SlotBytes => _slotBytes;

    public UniformFrame Add(
        RenderGraph graph,
        ReadOnlySpan<T> values,
        string name)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (values.IsEmpty)
            throw new ArgumentException("uniform data must contain at least one value.", nameof(values));
        if (ReferenceEquals(_frameGraph, graph)
            && _frameGeneration == graph.Generation)
        {
            throw new InvalidOperationException("uniform gpu buffer has already been imported into this render graph frame.");
        }

        _frameGraph = graph;
        _frameGeneration = graph.Generation;
        int byteCount = Fill(values);
        EnsureBuffer(name, checked((ulong)byteCount));
        Upload(byteCount);
        RenderGraphHandle handle = graph.ImportBuffer(
            name,
            _buffer,
            Desc(name, checked((ulong)byteCount)),
            new ImportDesc(ResourceState.ConstantBuffer),
            _views);

        return new UniformFrame(handle, values.Length, _slotBytes);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _frameGraph = null;
        _frameGeneration = 0;
        DestroyViews(_views);
        _views.Clear();
        if (_buffer.IsValid)
        {
            _device.Destroy(_buffer);
            _buffer = default;
        }

        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            var retired = _retired[i];
            DestroyViews(retired.Views);
            if (retired.Buffer.IsValid)
                _device.Destroy(retired.Buffer);
        }

        _retired.Clear();
        _disposed = true;
    }

    private int Fill(ReadOnlySpan<T> values)
    {
        int byteCount = checked((int)(_slotBytes * (ulong)values.Length));
        if (_scratch.Length < byteCount)
            Array.Resize(ref _scratch, byteCount);

        Span<byte> target = _scratch.AsSpan(0, byteCount);
        target.Clear();
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(values);
        int valueBytes = Unsafe.SizeOf<T>();
        for (int i = 0; i < values.Length; i++)
        {
            source.Slice(i * valueBytes, valueBytes)
                .CopyTo(target.Slice(checked((int)(_slotBytes * (ulong)i)), valueBytes));
        }

        return byteCount;
    }

    private void EnsureBuffer(string name, ulong byteCount)
    {
        if (_buffer.IsValid && _bufferBytes >= byteCount)
            return;

        if (_buffer.IsValid)
        {
            _retired.Add((_buffer, _views));
            _buffer = default;
            _views = new FlatDictionary<BufferViewKey, BufferViewHandle>();
        }

        _bufferBytes = byteCount;
        _buffer = _device.CreateBuffer(Desc(name, byteCount));
    }

    private void Upload(int byteCount)
    {
        var mapped = _device.MapBuffer(_buffer, MapMode.Write, 0, byteCount);
        _scratch.AsSpan(0, byteCount).CopyTo(mapped.Span);
        _device.UnmapBuffer(_buffer);
    }

    private static BufferDesc Desc(string name, ulong byteCount)
        => new()
        {
            Name = name,
            SizeInBytes = byteCount,
            Memory = MemoryClass.CpuUpload,
            BindFlags = BindFlags.ConstantBuffer,
            InitialState = ResourceState.ConstantBuffer,
        };

    private static ulong AlignUp(ulong value, ulong alignment)
        => (value + alignment - 1) / alignment * alignment;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UniformGpu<T>));
    }

    private void DestroyViews(FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        foreach (var pair in views)
        {
            if (pair.Value.IsValid)
                _device.Destroy(pair.Value);
        }
    }
}
