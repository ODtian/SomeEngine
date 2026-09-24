using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using static SomeEngine.Tests.RenderGraphTestHelpers;

namespace SomeEngine.Tests;

public sealed class ClusterResourceTests
{
    [Fact]
    public void GpuDescriptors()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var manager = new ClusterMeshes();
        using var resources = new ClusterGpuResources(device, manager);
        using var graph = new RenderGraph();

        ClusterBuffers buffers = resources.AddUploadPasses(graph);

        BufferDesc heap = graph.GetBufferDesc(buffers.PageHeap);
        Assert.Equal("Global Page Heap", heap.Name);
        Assert.True(heap.Raw);
        Assert.True(heap.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(heap.BindFlags.HasFlag(BindFlags.IndexBuffer));
        Assert.True(heap.BindFlags.HasFlag(BindFlags.CopyDestination));
        Assert.Equal(ResourceState.Common, heap.InitialState);

        BufferDesc bvh = graph.GetBufferDesc(buffers.GlobalBVH);
        Assert.Equal("Global BVH Buffer", bvh.Name);
        Assert.Equal(64u, bvh.StrideInBytes);
        Assert.True(bvh.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(bvh.BindFlags.HasFlag(BindFlags.UnorderedAccess));
        Assert.True(bvh.BindFlags.HasFlag(BindFlags.CopyDestination));

        BufferDesc faults = graph.GetBufferDesc(buffers.PageFault);
        Assert.Equal("Cluster Page Fault Buffer", faults.Name);
        Assert.True(faults.Raw);
        Assert.True(faults.BindFlags.HasFlag(BindFlags.UnorderedAccess));
        Assert.True(faults.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(faults.BindFlags.HasFlag(BindFlags.CopySource));

        BufferDesc readback = graph.GetBufferDesc(buffers.PageFaultReadback);
        Assert.Equal("Cluster Page Fault Readback", readback.Name);
        Assert.Equal(MemoryClass.CpuReadback, readback.Memory);
        Assert.True(readback.BindFlags.HasFlag(BindFlags.CopyDestination));
    }

    [Fact]
    public void UploadsStartEmpty()
    {
        var manager = new ClusterMeshes();

        var uploads = manager.TakeUploads();

        Assert.Empty(uploads.GlobalBVH);
        Assert.Empty(uploads.PageHeap);
        Assert.Equal(0, manager.PendingPageUploadCount);
        Assert.Equal(0L, manager.PendingPageUploadBytes);
        Assert.Equal(0, manager.PendingPatchCount);
        Assert.Equal(0u, manager.ResidentPageCount);
        Assert.Equal(0u, manager.MissingPageCount);
    }

    [Fact]
    public void BvhOwner()
    {
        string systems = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline");
        string manager = File.ReadAllText(Path.Combine(systems, "ClusterMeshes.cs"));
        string bvh = File.ReadAllText(Path.Combine(systems, "ClusterBvh.cs"));

        Assert.DoesNotContain("ComputeLocalBvhTraverseDepth", manager);
        Assert.DoesNotContain("_bvhNodeCount", manager);
        Assert.DoesNotContain("BVHPatchData", manager);
        Assert.Contains("internal sealed class ClusterBvh", bvh);
        Assert.Contains("FlatDictionary<string, uint> Roots", bvh);
        Assert.Contains("IReadOnlyList<UploadItem> TakeUploads()", bvh);
    }

    [Fact]
    public void LeafDepth()
    {
        var manager = new ClusterMeshes();

        uint root = AddMesh(manager, MeshWithBvh("LeafRoot", Leaf()));

        Assert.True(manager.TryDepth(root, out int depth));
        Assert.Equal(1, depth);
    }

    [Fact]
    public void MultiLevelDepth()
    {
        var manager = new ClusterMeshes();

        uint root = AddMesh(manager, MeshWithBvh(
            "ThreeLevel",
            Leaf(),
            Leaf(),
            Internal(firstChild: 0, childCount: 2),
            Internal(firstChild: 2, childCount: 1)));

        Assert.True(manager.TryDepth(root, out int depth));
        Assert.Equal(3, depth);
    }

    [Fact]
    public void HandlesSeparateMeshes()
    {
        var manager = new ClusterMeshes();

        uint first = AddMesh(manager, MeshWithBvh("SameName", Leaf()), 1);
        uint second = AddMesh(manager, MeshWithBvh("SameName", Leaf()), 2);

        Assert.NotEqual(first, second);
        Assert.Equal(2, manager.MeshCount);
        Assert.Equal(2u, manager.PageCount);
    }

    [Fact]
    public void PageMetaQuant()
    {
        var manager = new ClusterMeshes();
        var header = new MeshPageHeader
        {
            ClusterCount = 7,
            IndicesOffset = MeshPageHeader.Size,
            QuantOriginX = 3.0f,
            QuantOriginY = 5.0f,
            QuantOriginZ = 7.0f,
            QuantStep = 0.25f,
        };
        MeshAsset mesh = MeshWithHeader("QuantPage", header, Leaf());
        Handle<Mesh> handle = MeshHandle(1);

        manager.AddMesh(handle, RuntimeAssetLoader.LoadMesh(mesh));

        PageMeta page = Assert.Single(manager.Pages(handle));
        Assert.Equal(7u, page.ClusterCount);
        Assert.Equal(3.0f, page.QuantOrigin.X);
        Assert.Equal(5.0f, page.QuantOrigin.Y);
        Assert.Equal(7.0f, page.QuantOrigin.Z);
        Assert.Equal(0.25f, page.QuantStep);
    }

    [Fact]
    public void AcceptsEmptyGuid()
    {
        var manager = new ClusterMeshes();
        MeshAsset mesh = MeshWithBvh("NoGuid", string.Empty, [Leaf()]);

        uint root = AddMesh(manager, mesh);

        Assert.NotEqual(uint.MaxValue, root);
        Assert.Equal(1, manager.MeshCount);
    }

    [Fact]
    public void ImportsBuffers()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var manager = new ClusterMeshes();
        using var resources = new ClusterGpuResources(device, manager);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffers = resources.AddUploadPasses(graph);

        Assert.True(resources.GlobalBVH.IsValid);
        Assert.True(resources.PageHeap.IsValid);
        Assert.True(resources.PageFaultReadback.IsValid);
        Assert.True(buffers.GlobalBVH.IsValid);
        Assert.True(buffers.PageHeap.IsValid);
        Assert.True(buffers.PageFault.IsValid);
        Assert.True(buffers.PageFaultReadback.IsValid);

        graph.Execute(device, queue);
    }

    [Fact]
    public void ReusesViews()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var manager = new ClusterMeshes();
        using var graph = new RenderGraph();

        using (var resources = new ClusterGpuResources(device, manager))
        {
            UseResourceViews(graph, resources, device, queue);
            int buffersAfterFirstFrame = LiveHandleCount(device, "Buffers");
            int viewsAfterFirstFrame = LiveHandleCount(device, "BufferViews");

            UseResourceViews(graph, resources, device, queue);

            Assert.Equal(buffersAfterFirstFrame, LiveHandleCount(device, "Buffers"));
            Assert.Equal(viewsAfterFirstFrame, LiveHandleCount(device, "BufferViews"));
        }

        Assert.Equal(0, LiveHandleCount(device, "Buffers"));
        Assert.Equal(0, LiveHandleCount(device, "BufferViews"));
    }

    [Fact]
    public void ReusesReadback()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var manager = new ClusterMeshes();

        using (var graph = new RenderGraph())
        {
            using (var resources = new ClusterGpuResources(device, manager))
            {
                UseReadback(graph, resources, PageFaults.ByteCount, device, queue);
                UseReadback(graph, resources, PageFaults.ByteCount, device, queue);
                int buffersAfterWarmFrame = LiveHandleCount(device, "Buffers");

                UseReadback(graph, resources, PageFaults.ByteCount, device, queue);

                Assert.Equal(buffersAfterWarmFrame, LiveHandleCount(device, "Buffers"));
            }
        }

        Assert.Equal(0, LiveHandleCount(device, "Buffers"));
    }

    [Fact]
    public void ParsesFaults()
    {
        uint[] words = [3, 10, 11, 12, 13];
        var faults = new PageFaults();

        ReadOnlySpan<uint> parsed = faults.Read(MemoryMarshal.AsBytes(words.AsSpan()), maxCount: 2);

        Assert.Equal([10u, 11u], parsed.ToArray());

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var readbackDesc = new BufferDesc
        {
            Name = "Cluster Fault Test Readback",
            SizeInBytes = PageFaults.ByteCount,
            Memory = MemoryClass.CpuReadback,
            BindFlags = BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
        };
        BufferHandle readback = device.CreateBuffer(readbackDesc);
        using var graph = new RenderGraph();
        byte[] faultBytes = new byte[PageFaults.ByteCount];
        MemoryMarshal.AsBytes(words.AsSpan()).CopyTo(faultBytes);

        graph.BeginFrame();
        RenderGraphHandle source = graph.CreateBuffer(
            "Cluster Fault Test Source",
            new BufferDesc
            {
                SizeInBytes = PageFaults.ByteCount,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
                Raw = true,
            },
            faultBytes);
        RenderGraphHandle destination = graph.ImportBuffer(
            "Cluster Fault Test Readback",
            readback,
            readbackDesc,
            new ImportDesc(ResourceState.Common)
            {
                AllowWrite = true,
            });
        BufferCopyPasses.AddCopyPass(
            graph,
            "Copy cluster faults to test readback",
            source,
            destination,
            0,
            0,
            PageFaults.ByteCount);
        graph.SetFinalState(destination, ResourceState.CopyDestination);

        graph.Execute(device, queue);

        ReadOnlySpan<uint> readbackFaults = faults.Read(
            device,
            readback,
            checked((int)PageFaults.ByteCount),
            maxCount: 2);
        Assert.Equal([10u, 11u], readbackFaults.ToArray());
        device.Destroy(readback);
    }

    [Fact]
    public async Task StreamLoads()
    {
        var manager = new ClusterMeshes();
        MeshAsset mesh = MeshWithBvh("Stream", Leaf());
        Handle<Mesh> handle = MeshHandle(1);
        manager.AddMesh(handle, RuntimeAssetLoader.LoadMesh(mesh));
        PageMeta page = Assert.Single(manager.Pages(handle));
        uint node = Assert.Single(manager.Leaves(page.PageID));
        ReadOnlyMemory<byte> pageData = await manager.LoadPageAsync(page.PageID);
        Assert.NotEmpty(manager.TakeUploads().PageHeap);
        Assert.Equal(0, manager.PendingPageUploadCount);
        Assert.Equal(1u, manager.ResidentPageCount);
        Assert.Equal(0u, manager.MissingPageCount);
        Assert.Equal(0u, manager.EvictedPageCount);
        Assert.True(manager.EvictPage(page.PageID));
        Assert.Equal(0u, manager.ResidentPageCount);
        Assert.Equal(1u, manager.MissingPageCount);
        Assert.Equal(1u, manager.EvictedPageCount);
        Assert.Equal(1, manager.PendingPatchCount);
        Assert.NotEmpty(manager.TakePatches());
        Assert.Equal(0, manager.PendingPatchCount);
        Assert.Equal(0u, manager.ResidentPageCount);
        int loadAttempts = 0;
        var stream = new PageStream(
            manager,
            pageID =>
            {
                loadAttempts++;
                if (loadAttempts == 1)
                {
                    return new ValueTask<ReadOnlyMemory<byte>>(
                        Task.FromException<ReadOnlyMemory<byte>>(
                            new InvalidOperationException("Simulated page load failure.")));
                }

                return ValueTask.FromResult(pageData);
            });
        ReadOnlyMemory<uint> faults = new[] { node, node };

        stream.Push(faults.Span);
        Assert.Equal(0u, stream.FaultCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.False(manager.IsPageResident(page.PageID));
        Assert.Equal(0u, manager.ResidentPageCount);

        stream.Update();

        Assert.Equal(1u, stream.FaultCount);
        Assert.Equal(1u, stream.RequestedPageCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.Equal(0u, stream.ErrorCount);
        Assert.Equal(1, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.False(manager.IsPageResident(page.PageID));
        Assert.Equal(0u, manager.ResidentPageCount);

        Assert.Equal(1u, stream.FaultCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.False(manager.IsPageResident(page.PageID));
        Assert.Equal(0u, manager.ResidentPageCount);

        stream.Update();

        Assert.Equal(0u, stream.FaultCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.Equal(1u, stream.ErrorCount);
        Assert.IsType<InvalidOperationException>(stream.LastError);
        Assert.Equal(0, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.False(manager.IsPageResident(page.PageID));
        Assert.Equal(0u, manager.ResidentPageCount);
        Assert.Equal(1u, manager.MissingPageCount);

        stream.Push(faults.Span);
        stream.Update();

        Assert.Equal(1u, stream.FaultCount);
        Assert.Equal(1u, stream.RequestedPageCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.Equal(1u, stream.ErrorCount);
        Assert.Equal(1, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.False(manager.IsPageResident(page.PageID));
        Assert.Equal(0u, manager.ResidentPageCount);

        stream.Update();

        Assert.Equal(0u, stream.FaultCount);
        Assert.Equal(1u, stream.LoadedPages);
        Assert.Equal(1u, stream.ErrorCount);
        Assert.Equal(0, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.True(manager.IsPageResident(page.PageID));
        Assert.Equal(1u, manager.ResidentPageCount);
        Assert.Equal(0u, manager.MissingPageCount);
        Assert.Equal(1, manager.PendingPageUploadCount);
        Assert.Equal(1, manager.PendingPatchCount);
        ClusterBvhPatch patch = Assert.Single(manager.TakePatches());
        Assert.Equal(node, patch.NodeIndex);
        Assert.NotEqual(ClusterBVHNode.PageFaultMarker, patch.NewPagePointer);
        Assert.Equal(0, manager.PendingPatchCount);
    }

    private static void UseResourceViews(
        RenderGraph graph,
        ClusterGpuResources resources,
        IDevice device,
        IQueue queue)
    {
        graph.BeginFrame();
        var buffers = resources.AddUploadPasses(graph);
        graph.AddRasterPass(
            "Use persistent cluster resources",
            builder =>
            {
                builder.Read(buffers.GlobalBVH, ResourceState.ShaderResource);
                builder.Read(buffers.PageHeap, ResourceState.ShaderResource);
                builder.Write(buffers.PageFault, ResourceState.UnorderedAccess);
            },
            context =>
            {
                Assert.True(context.GetBufferView(buffers.GlobalBVH, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetBufferView(buffers.PageHeap, ViewKind.ShaderResource, raw: true).IsValid);
                Assert.True(context.GetBufferView(buffers.PageFault, ViewKind.UnorderedAccess, raw: true).IsValid);
            });
        graph.Execute(device, queue);
        graph.BeginFrame();
    }

    private static void UseReadback(
        RenderGraph graph,
        ClusterGpuResources resources,
        ulong pageFaultBufferSize,
        IDevice device,
        IQueue queue)
    {
        graph.BeginFrame();
        var buffers = resources.AddUploadPasses(graph);
        BufferUploadPasses.AddUploadPass(
            graph,
            "Initialize page fault buffer",
            buffers.PageFault,
            0,
            new byte[checked((int)pageFaultBufferSize)]);
        BufferCopyPasses.AddCopyPass(
            graph,
            "Copy page fault to persistent readback",
            buffers.PageFault,
            buffers.PageFaultReadback,
            0,
            0,
            pageFaultBufferSize);

        BufferHandle extracted = default;
        graph.ExtractBuffer(
            buffers.PageFaultReadback,
            ResourceState.CopyDestination,
            (buffer, state) =>
            {
                extracted = buffer;
                Assert.Equal(ResourceState.CopyDestination, state);
            });

        graph.Execute(device, queue);
        graph.BeginFrame();

        Assert.Equal(resources.PageFaultReadback, extracted);
    }

    private static uint AddMesh(ClusterMeshes manager, MeshAsset asset, int handleId = 1)
        => manager.AddMesh(MeshHandle(handleId), RuntimeAssetLoader.LoadMesh(asset));

    private static Handle<Mesh> MeshHandle(int id)
        => new(id, 1);

    private static MeshAsset MeshWithBvh(string name, params ClusterBVHNode[] nodes)
        => MeshWithBvh(name, AssetGuid.New().ToFlatString(), nodes);

    private static MeshAsset MeshWithBvh(string name, string assetGuid, ClusterBVHNode[] nodes)
        => MeshWithHeader(name, assetGuid, new MeshPageHeader
        {
            IndicesOffset = MeshPageHeader.Size,
        }, nodes);

    private static MeshAsset MeshWithHeader(string name, MeshPageHeader header, params ClusterBVHNode[] nodes)
        => MeshWithHeader(name, AssetGuid.New().ToFlatString(), header, nodes);

    private static MeshAsset MeshWithHeader(string name, string assetGuid, MeshPageHeader header, ClusterBVHNode[] nodes)
    {
        byte[] payload = new byte[MeshPageHeader.Size + nodes.Length * Marshal.SizeOf<ClusterBVHNode>()];
        if (header.IndicesOffset == 0)
            header.IndicesOffset = MeshPageHeader.Size;
        MemoryMarshal.Write(payload.AsSpan(0, MeshPageHeader.Size), in header);
        MemoryMarshal.AsBytes(nodes.AsSpan()).CopyTo(payload.AsSpan(MeshPageHeader.Size));
        return new MeshAsset
        {
            AssetGuid = assetGuid,
            Name = name,
            Bounds = new Bounds { Center = new Vec3(), Radius = 1f },
            Payload = payload,
            Attributes = [],
            BvhOffset = MeshPageHeader.Size,
            QuantStep = 1f,
        };
    }

    private static ClusterBVHNode Leaf()
        => new()
        {
            ChildPointer = 0,
            ChildCount = 1u << 12,
            NodeType = 1,
        };

    private static ClusterBVHNode Internal(uint firstChild, uint childCount)
        => new()
        {
            ChildPointer = firstChild,
            ChildCount = childCount,
            NodeType = 0,
        };
}
