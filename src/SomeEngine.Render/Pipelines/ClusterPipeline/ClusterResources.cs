using SomeEngine.Core.Collections;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct ClusterBuffers(
    RenderGraphHandle GlobalBVH,
    RenderGraphHandle PageHeap,
    RenderGraphHandle PageFault,
    RenderGraphHandle PageFaultReadback);

internal sealed class ClusterGpuResources : IDisposable
{
    private readonly IDevice _device;
    private readonly ClusterMeshes _meshes;
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _globalBVHViews = new();
    private readonly FlatDictionary<BufferViewKey, BufferViewHandle> _pageHeapViews = new();
    private BufferHandle _pageFaultReadback;
    private BufferHandle _globalBVH;
    private BufferHandle _pageHeap;
    private RenderGraph? _frameGraph;
    private ResourceState _globalBVHState = ResourceState.Common;
    private ResourceState _pageHeapState = ResourceState.Common;
    private ResourceState _pageFaultReadbackState = ResourceState.Common;
    private bool _disposed;

    public ClusterGpuResources(IDevice device, ClusterMeshes meshes)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _meshes = meshes ?? throw new ArgumentNullException(nameof(meshes));
        EnsureCreated();
    }

    public BufferHandle GlobalBVH => _globalBVH;
    public BufferHandle PageHeap => _pageHeap;
    public BufferHandle PageFaultReadback => _pageFaultReadback;

    public ClusterBuffers AddUploadPasses(RenderGraph graph)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graph);

        using (Profiler.BeginScope("ClusterGpuResources.Ensure"))
        {
            EnsureCreated();
            EnsureValid();
        }

        RenderGraphHandle globalBVH;
        RenderGraphHandle pageHeap;
        RenderGraphHandle pageFaultReadback;
        using (Profiler.BeginScope("ClusterGpuResources.ImportBuffers"))
        {
            globalBVH = graph.ImportBuffer(
                "Cluster GlobalBVH",
                _globalBVH,
                BvhDesc(),
                new ImportDesc(_globalBVHState)
                {
                    AllowWrite = true,
                },
                _globalBVHViews);
            pageHeap = graph.ImportBuffer(
                "Cluster PageHeap",
                _pageHeap,
                HeapDesc(),
                new ImportDesc(_pageHeapState)
                {
                    AllowWrite = true,
                },
                _pageHeapViews);
            pageFaultReadback = graph.ImportBuffer(
                "Cluster PageFaultReadback",
                _pageFaultReadback,
                FaultReadDesc(),
                new ImportDesc(_pageFaultReadbackState)
                {
                    AllowWrite = true,
                });
            graph.ExtractBuffer(
                globalBVH,
                ResourceState.ShaderResource,
                (_, state) => _globalBVHState = state);
            graph.ExtractBuffer(
                pageHeap,
                ResourceState.ShaderResource,
                (_, state) => _pageHeapState = state);
            graph.ExtractBuffer(
                pageFaultReadback,
                ResourceState.CopyDestination,
                (_, state) => _pageFaultReadbackState = state);
        }
        _frameGraph = graph;

        var pending = _meshes.TakeUploads();
        using (Profiler.BeginScope("ClusterGpuResources.UploadCopies"))
        {
            AddUploadCopies(graph, "Cluster GlobalBVH Upload", pending.GlobalBVH, globalBVH);
            AddUploadCopies(graph, "Cluster PageHeap Upload", pending.PageHeap, pageHeap);
        }

        RenderGraphHandle pageFault = graph.CreateBuffer(
            "Cluster PageFault",
            FaultDesc(),
            ResourceLifetime.Transient);

        return new ClusterBuffers(globalBVH, pageHeap, pageFault, pageFaultReadback);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _device.WaitIdle();
        ClearBindings();
        DestroyBuffer(ref _globalBVH, _globalBVHViews);
        DestroyBuffer(ref _pageHeap, _pageHeapViews);
        if (_pageFaultReadback.IsValid)
        {
            _device.Destroy(_pageFaultReadback);
            _pageFaultReadback = default;
        }
        _frameGraph = null;

        _disposed = true;
    }

    private void EnsureCreated()
    {
        if (!_globalBVH.IsValid)
            _globalBVH = _device.CreateBuffer(BvhDesc());
        if (!_pageHeap.IsValid)
            _pageHeap = _device.CreateBuffer(HeapDesc());
        if (!_pageFaultReadback.IsValid)
            _pageFaultReadback = _device.CreateBuffer(FaultReadDesc());
    }

    private void EnsureValid()
    {
        if (!_globalBVH.IsValid || !_pageHeap.IsValid || !_pageFaultReadback.IsValid)
            throw new InvalidOperationException("cluster upload resources failed to create required buffers.");
    }

    private static void AddUploadCopies(
        RenderGraph graph,
        string baseName,
        IReadOnlyList<UploadItem> uploads,
        RenderGraphHandle destination)
    {
        BufferUploadBatch? batch = null;
        for (int i = 0; i < uploads.Count; i++)
        {
            var upload = uploads[i];
            if (upload.Data.IsEmpty)
                continue;

            batch ??= new BufferUploadBatch(graph, $"{baseName} Uploads");
            batch.AddMemory(destination, upload.Offset, upload.Data);
        }

        batch?.AddPass();
    }

    private static BufferDesc BvhDesc()
        => new()
        {
            Name = "Global BVH Buffer",
            SizeInBytes = ClusterBvh.BufferBytes,
            BindFlags = BindFlags.ShaderResource
                | BindFlags.UnorderedAccess
                | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            StrideInBytes = ClusterBvh.NodeBytes,
        };

    private static BufferDesc HeapDesc()
        => new()
        {
            Name = "Global Page Heap",
            SizeInBytes = SomeEngine.Render.Pipelines.PageHeap.CapacityBytes,
            BindFlags = BindFlags.ShaderResource
                | BindFlags.IndexBuffer
                | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            Raw = true,
        };

    private static BufferDesc FaultDesc()
        => new()
        {
            Name = "Cluster Page Fault Buffer",
            SizeInBytes = PageFaults.ByteCount,
            BindFlags = BindFlags.UnorderedAccess
                | BindFlags.ShaderResource
                | BindFlags.CopySource
                | BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
            Raw = true,
        };

    private static BufferDesc FaultReadDesc()
        => new()
        {
            Name = "Cluster Page Fault Readback",
            SizeInBytes = PageFaults.ByteCount,
            Memory = MemoryClass.CpuReadback,
            BindFlags = BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ClusterGpuResources));
    }

    private void ClearBindings()
    {
        if (_globalBVHViews.Count == 0
            && _pageHeapViews.Count == 0)
        {
            return;
        }

        RenderGraph? graph = _frameGraph;
        if (graph is { IsDisposed: false })
            graph.ClearBindSets(waitForGpu: true);
    }

    private void DestroyBuffer(
        ref BufferHandle buffer,
        FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        DestroyViews(views);

        if (buffer.IsValid)
        {
            _device.Destroy(buffer);
            buffer = default;
        }
    }

    private void DestroyViews(FlatDictionary<BufferViewKey, BufferViewHandle> views)
    {
        foreach (var pair in views)
            _device.Destroy(pair.Value);
        views.Clear();
    }
}
