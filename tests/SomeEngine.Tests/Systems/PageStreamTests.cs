using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests.Systems;

public sealed class PageStreamTests
{
    [Fact]
    public void HoldsDuplicateFaultsDuringLoad()
    {
        var (manager, page, node, pageData) = MissingPage();
        int loadCount = 0;
        var pageLoad = new TaskCompletionSource<ReadOnlyMemory<byte>>();
        var stream = new PageStream(
            manager,
            _ =>
            {
                loadCount++;
                return new ValueTask<ReadOnlyMemory<byte>>(pageLoad.Task);
            });

        uint[] faultWords = [2, node, node];
        var pageFaults = new PageFaults();
        ReadOnlySpan<uint> repeatedFaults = pageFaults.Read(
            MemoryMarshal.AsBytes(faultWords.AsSpan()),
            PageFaults.MaxCount);

        stream.Push(repeatedFaults);
        stream.Update();

        Assert.Equal(1, loadCount);
        Assert.Equal(1, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.Equal(1u, stream.FaultCount);
        Assert.Equal(1u, stream.RequestedPageCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.False(manager.IsPageResident(page.PageID));

        ReadOnlyMemory<uint> samePageFault = new[] { node };
        stream.Push(samePageFault.Span);
        stream.Update();

        Assert.Equal(1, loadCount);
        Assert.Equal(1, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.Equal(1u, stream.FaultCount);
        Assert.Equal(1u, stream.RequestedPageCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.False(manager.IsPageResident(page.PageID));

        pageLoad.SetResult(pageData);
    }

    [Fact]
    public void RecoversAfterLoadFailure()
    {
        var (manager, page, node, pageData) = MissingPage();
        int loadCount = 0;
        var stream = new PageStream(
            manager,
            _ =>
            {
                loadCount++;
                return loadCount == 1
                    ? new ValueTask<ReadOnlyMemory<byte>>(Task.FromException<ReadOnlyMemory<byte>>(
                        new InvalidOperationException("transient page load failure")))
                    : ValueTask.FromResult(pageData);
            });

        ReadOnlyMemory<uint> fault = new[] { node };
        stream.Push(fault.Span);
        stream.Update();
        stream.Update();

        Assert.Equal(1, loadCount);
        Assert.Equal(0, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.Equal(0u, stream.LoadedPages);
        Assert.Equal(1u, stream.ErrorCount);
        Assert.NotNull(stream.LastError);
        Assert.False(manager.IsPageResident(page.PageID));
        Assert.Equal(1u, manager.MissingPageCount);

        stream.Push(fault.Span);
        stream.Update();
        stream.Update();

        Assert.Equal(2, loadCount);
        Assert.Equal(0, stream.InFlightCount);
        Assert.Equal(0, stream.QueuedPageCount);
        Assert.Equal(1u, stream.LoadedPages);
        Assert.Equal(1u, stream.ErrorCount);
        Assert.True(manager.IsPageResident(page.PageID));
        Assert.Equal(0u, manager.MissingPageCount);
        Assert.Equal(1, manager.PendingPageUploadCount);
        Assert.Equal(1, manager.PendingPatchCount);
        ClusterBvhPatch patch = Assert.Single(manager.TakePatches());
        Assert.Equal(node, patch.NodeIndex);
        Assert.NotEqual(ClusterBVHNode.PageFaultMarker, patch.NewPagePointer);
        Assert.Equal(0, manager.PendingPatchCount);
    }

    private static (ClusterMeshes Manager, PageMeta Page, uint Node, ReadOnlyMemory<byte> Data) MissingPage()
    {
        var manager = new ClusterMeshes();
        Handle<Mesh> handle = MeshHandle(1);
        MeshAsset asset = MeshWithBvh("PageStream", Leaf());
        manager.AddMesh(handle, RuntimeAssetLoader.LoadMesh(asset));
        Assert.NotEmpty(manager.TakeUploads().PageHeap);
        Assert.Equal(0, manager.PendingPageUploadCount);
        PageMeta page = Assert.Single(manager.Pages(handle));
        uint node = Assert.Single(manager.Leaves(page.PageID));
        ReadOnlyMemory<byte> pageData = PageData();

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
        Assert.False(manager.IsPageResident(page.PageID));
        return (manager, page, node, pageData);
    }

    private static Handle<Mesh> MeshHandle(int id)
        => new(id, 1);

    private static MeshAsset MeshWithBvh(string name, params ClusterBVHNode[] nodes)
    {
        byte[] payload = new byte[MeshPageHeader.Size + nodes.Length * Marshal.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            IndicesOffset = MeshPageHeader.Size,
        };
        MemoryMarshal.Write(payload.AsSpan(0, MeshPageHeader.Size), in header);
        MemoryMarshal.AsBytes(nodes.AsSpan()).CopyTo(payload.AsSpan(MeshPageHeader.Size));
        return new MeshAsset
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
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

    private static ReadOnlyMemory<byte> PageData()
    {
        byte[] data = new byte[MeshPageHeader.Size];
        var header = new MeshPageHeader
        {
            IndicesOffset = MeshPageHeader.Size,
        };
        MemoryMarshal.Write(data.AsSpan(), in header);
        return data;
    }
}
