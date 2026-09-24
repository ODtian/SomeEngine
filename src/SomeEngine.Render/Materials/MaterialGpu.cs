using SomeEngine.Core.Collections;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Materials;

internal sealed class MaterialGpu : IDisposable
{
    private readonly IDevice _device;
    private readonly Dictionary<Material, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ScalarLayout, Entry> _layoutEntries = new(ReferenceEqualityComparer.Instance);
    private readonly Entry _empty = new();
    private readonly Material _defaultMaterial = new();
    private byte[] _scratch = [];
    private bool _disposed;

    public MaterialGpu(IDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public int Count => _entries.Count + _layoutEntries.Count + (_empty.Buffer.IsValid ? 1 : 0);

    public RenderGraphHandle Add(
        RenderGraph graph,
        Material? material,
        string name = "MaterialScalarRegion")
        => Add(graph, material, ScalarLayout.Empty, name);

    public RenderGraphHandle Add(
        RenderGraph graph,
        Material? material,
        ScalarLayout scalarLayout,
        string name = "MaterialScalarRegion")
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(scalarLayout);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Entry entry = material != null ? GetEntry(material) : GetEntry(scalarLayout);
        uint version = Version(material, scalarLayout);
        if (ReferenceEquals(entry.FrameGraph, graph)
            && entry.FrameGeneration == graph.Generation
            && entry.FrameHandle.IsValid)
        {
            if (entry.Version != version)
            {
                throw new InvalidOperationException(
                    "material scalar region changed after it was imported into the current render graph frame.");
            }

            return entry.FrameHandle;
        }

        int bytes = ByteSize(material, scalarLayout);
        bool dirty = Sync(entry, bytes, version, name);
        RenderGraphHandle handle = Import(graph, entry, name);
        entry.FrameGraph = graph;
        entry.FrameGeneration = graph.Generation;
        entry.FrameHandle = handle;

        if (dirty)
            Upload(graph, handle, entry, material, scalarLayout, name);

        return handle;
    }

    public BufferHandle Get(Material? material)
        => material != null && _entries.TryGetValue(material, out Entry? entry)
            ? entry.Buffer
            : _empty.Buffer;

    public void Remove(Material material)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(material);
        if (!_entries.Remove(material, out Entry? entry))
            return;

        _device.WaitIdle();
        Destroy(entry);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _device.WaitIdle();
        Destroy(_empty);
        foreach (Entry entry in _entries.Values)
            Destroy(entry);
        foreach (Entry entry in _layoutEntries.Values)
            Destroy(entry);
        _entries.Clear();
        _layoutEntries.Clear();
        _disposed = true;
    }

    private Entry GetEntry(Material material)
    {
        if (!_entries.TryGetValue(material, out Entry? entry))
        {
            entry = new Entry();
            _entries.Add(material, entry);
        }

        return entry;
    }

    private Entry GetEntry(ScalarLayout scalarLayout)
    {
        if (scalarLayout == ScalarLayout.Empty)
            return _empty;

        if (!_layoutEntries.TryGetValue(scalarLayout, out Entry? entry))
        {
            entry = new Entry();
            _layoutEntries.Add(scalarLayout, entry);
        }

        return entry;
    }

    private bool Sync(Entry entry, int bytes, uint version, string name)
    {
        bool grow = entry.Bytes < bytes;
        bool create = !entry.Buffer.IsValid;
        if (grow && entry.Buffer.IsValid)
        {
            _device.WaitIdle();
            Destroy(entry);
        }

        if (!entry.Buffer.IsValid)
        {
            entry.Bytes = bytes;
            entry.State = ResourceState.Common;
            entry.Buffer = _device.CreateBuffer(Desc(name, bytes));
            create = true;
        }

        bool dirty = create || grow || entry.Version != version;
        if (dirty)
            entry.Version = version;

        return dirty;
    }

    private static RenderGraphHandle Import(RenderGraph graph, Entry entry, string name)
    {
        RenderGraphHandle handle = graph.ImportBuffer(
            name,
            entry.Buffer,
            Desc(name, entry.Bytes),
            new ImportDesc(entry.State)
            {
                AllowWrite = true,
            },
            entry.Views);
        graph.ExtractBuffer(
            handle,
            ResourceState.ShaderResource,
            (_, state) => entry.State = state);
        return handle;
    }

    private void Upload(
        RenderGraph graph,
        RenderGraphHandle handle,
        Entry entry,
        Material? material,
        ScalarLayout scalarLayout,
        string name)
    {
        Fill(entry.Bytes, material, scalarLayout);
        BufferUploadPasses.AddUploadPass(
            graph,
            $"{name} Upload",
            handle,
            0,
            _scratch.AsSpan(0, entry.Bytes));
    }

    private void Fill(int bytes, Material? material, ScalarLayout scalarLayout)
    {
        if (_scratch.Length < bytes)
            Array.Resize(ref _scratch, bytes);

        Span<byte> target = _scratch.AsSpan(0, bytes);
        target.Clear();
        if (material != null)
        {
            material.WriteScalarRegion(target);
            return;
        }

        if (scalarLayout == ScalarLayout.Empty)
            return;

        _defaultMaterial.SetScalarLayout(scalarLayout);
        _defaultMaterial.WriteScalarRegion(target);
    }

    private static int ByteSize(Material? material, ScalarLayout scalarLayout)
    {
        int bytes = Math.Max(
            ScalarLayout.HeaderByteSize,
            material?.ScalarRegionByteSize ?? scalarLayout.ByteSize);
        return AlignUp(bytes, ScalarLayout.PayloadAlignment);
    }

    private static uint Version(Material? material, ScalarLayout scalarLayout)
        => material?.ScalarVersion ?? (scalarLayout.LayoutHash == 0 ? 1u : scalarLayout.LayoutHash);

    private static BufferDesc Desc(string name, int bytes)
        => new()
        {
            Name = name,
            SizeInBytes = checked((ulong)bytes),
            Memory = MemoryClass.DeviceLocal,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            Raw = true,
        };

    private static int AlignUp(int value, int alignment)
        => ((value + alignment - 1) / alignment) * alignment;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MaterialGpu));
    }

    private void Destroy(Entry entry)
    {
        ClearBindings(entry);
        DestroyViews(entry.Views);
        if (entry.Buffer.IsValid)
            _device.Destroy(entry.Buffer);

        entry.Buffer = default;
        entry.FrameHandle = default;
        entry.FrameGraph = null;
        entry.FrameGeneration = 0;
        entry.State = ResourceState.Common;
        entry.Bytes = 0;
        entry.Version = 0;
    }

    private static void ClearBindings(Entry entry)
    {
        if (entry.Views.Count == 0)
            return;

        RenderGraph? graph = entry.FrameGraph;
        if (graph is { IsDisposed: false })
            graph.ClearBindSets(waitForGpu: true);
    }

    private void DestroyViews(FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        foreach (var pair in views)
            _device.Destroy(pair.Value);
        views.Clear();
    }

    private sealed class Entry
    {
        public BufferHandle Buffer;
        public readonly FlatDictionary<BufferViewKey, BufferViewHandle> Views = new();
        public RenderGraphHandle FrameHandle;
        public RenderGraph? FrameGraph;
        public int FrameGeneration;
        public ResourceState State = ResourceState.Common;
        public int Bytes;
        public uint Version;
    }
}
