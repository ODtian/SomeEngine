using SomeEngine.Core.Collections;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Render.Assets;
using SomeEngine.Render.RHI;
using System.Numerics;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterMeshes : IDisposable
{
    private readonly PageHeap _heap = new();
    private readonly ClusterBvh _bvh = new();
    private readonly MeshPages _pages = new();

    public uint PageCount => _pages.Count;
    public uint ResidentPageCount => _pages.ResidentCount;
    public uint MissingPageCount => _pages.MissingCount;
    public uint EvictedPageCount { get; private set; }
    public uint PageHeapFreeBytes => _heap.FreeBytes;
    public uint PageHeapUsedBytes => _heap.UsedBytes;
    public uint PageHeapLargestFreeBlock => _heap.Largest();
    public int PageHeapFreeBlockCount => _heap.FreeBlockCount;
    public int PendingPageUploadCount => _pageUploads.Count;
    public long PendingPageUploadBytes => _pageUploads.ByteCount;
    public int PendingPatchCount => _pendingPatches.Count;
    public int MeshCount => _bvh.Roots.Count;
    public IEnumerable<KeyValuePair<string, uint>> Roots => _bvh.Roots;

    private struct PendingPatch
    {
        public uint PageID;
        public uint ByteOffset;
        public bool Resident;
    }

    private readonly List<PendingPatch> _pendingPatches = new();

    private readonly UploadPack _pageUploads = new();

    public void PatchLeaves(uint pageID, uint byteOffset, bool resident)
    {
        _pendingPatches.Add(
            new PendingPatch
            {
                PageID = pageID,
                ByteOffset = byteOffset,
                Resident = resident,
            }
        );
    }

    public IReadOnlyList<ClusterBvhPatch> TakePatches()
    {
        if (_pendingPatches.Count == 0)
            return Array.Empty<ClusterBvhPatch>();

        var patchList = new List<ClusterBvhPatch>();
        foreach (var patch in _pendingPatches)
        {
            IReadOnlyList<uint> nodes = _pages.Leaves(patch.PageID);
            if (nodes.Count == 0)
                continue;

            uint offsetVal = patch.Resident ? patch.ByteOffset : ClusterBVHNode.PageFaultMarker;
            for (int i = 0; i < nodes.Count; i++)
            {
                patchList.Add(
                    new ClusterBvhPatch { NodeIndex = nodes[i], NewPagePointer = offsetVal }
                );
            }

            if (patch.Resident)
                _pages.MakeResident(patch.PageID, patch.ByteOffset);
            else
                _pages.MakeMissing(patch.PageID, out _, out _);
        }

        _pendingPatches.Clear();
        return patchList;
    }

    public bool TryPage(uint nodeIndex, out uint pageID)
    {
        return _pages.TryLeaf(nodeIndex, out pageID);
    }

    public bool IsPageResident(uint pageID)
    {
        return _pages.IsResident(pageID);
    }

    public bool TryOffset(uint pageID, out uint byteOffset)
    {
        return _pages.TryOffset(pageID, out byteOffset);
    }

    public ValueTask<ReadOnlyMemory<byte>> LoadPageAsync(uint pageID)
    {
        if (!_pages.TrySource(pageID, out _, out ReadOnlyMemory<byte> source))
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        return ValueTask.FromResult(source);
    }

    public bool TryLoad(uint pageID, out uint byteOffset)
    {
        if (IsPageResident(pageID) && TryOffset(pageID, out byteOffset))
        {
            Touch(pageID);
            return true;
        }

        if (!_pages.TrySource(pageID, out _, out ReadOnlyMemory<byte> data))
        {
            byteOffset = 0;
            return false;
        }

        return TryLoad(pageID, data, out byteOffset);
    }

    public bool TryLoad(uint pageID, ReadOnlyMemory<byte> data, out uint byteOffset)
    {
        if (IsPageResident(pageID) && TryOffset(pageID, out byteOffset))
        {
            Touch(pageID);
            return true;
        }

        if (data.IsEmpty)
        {
            byteOffset = 0;
            return false;
        }

        byteOffset = Alloc(checked((uint)data.Length), pageID);

        UploadData(byteOffset, data);
        _pages.MakeResident(pageID, byteOffset);
        return true;
    }

    public bool EvictPage(uint pageID)
    {
        if (!_pages.MakeMissing(pageID, out uint offset, out uint size))
            return false;

        PatchLeaves(pageID, ClusterBVHNode.PageFaultMarker, false);
        _heap.Free(offset, size);
        EvictedPageCount++;
        return true;
    }

    public void Touch(uint pageID)
    {
        _pages.Touch(pageID);
    }

    public uint AddMesh(Handle<Mesh> handle, Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Payload.IsEmpty)
            return uint.MaxValue;

        string meshId = MeshPages.Key(handle);
        if (_pages.Has(meshId))
            return _bvh.Roots.GetValueOrDefault(meshId, uint.MaxValue);

        ReadOnlyMemory<byte> payload = mesh.Payload;
        int payloadLength = payload.Length;
        int pageDataEnd = mesh.BvhOffset > 0 ? checked((int)mesh.BvhOffset) : payloadLength;

        _pages.TryAddMesh(handle, out meshId);

        int offset = 0;
        uint meshStartPageID = _pages.Count;

        while (offset < pageDataEnd)
        {
            // Read Header (44 bytes)
            // ClusterCount(0), VertexCount(4), TriangleCount(8), QuantOriginX(12),
            // ClustersOff(16), PosOff(20), AttrOff(24), IdxOff(28),
            // QuantOriginY(32), QuantOriginZ(36), QuantStep(40)
            if (offset + MeshPageHeader.Size > pageDataEnd)
                break;

            var headerSpan = payload.Span.Slice(offset, MeshPageHeader.Size);
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(headerSpan);
            uint clusterCount = header.ClusterCount;
            uint totalTriangleCount = header.TotalTriangleCount;
            uint indicesOffset = header.IndicesOffset;
            Vector3 quantOrigin = new(header.QuantOriginX, header.QuantOriginY, header.QuantOriginZ);
            float quantStep = header.QuantStep;

            // Reconstruct page size: IndicesOffset + TotalTriangleCount * 3 (u8 indices)
            uint pageSize = indicesOffset + totalTriangleCount * 3;

            if (offset + pageSize > pageDataEnd)
                pageSize = (uint)(pageDataEnd - offset);

            // Allocate
            uint heapOffset = Alloc(pageSize);

            PageMeta page = _pages.AddPage(
                meshId,
                heapOffset,
                clusterCount,
                quantOrigin,
                quantStep,
                payload.Span.Slice(offset, (int)pageSize),
                out ReadOnlyMemory<byte> pageData);

            // Upload

            UploadData(heapOffset, pageData);

            offset += (int)page.Size;
        }

        uint bvhRootIndex = uint.MaxValue;

        if (mesh.BvhOffset > 0 && checked((int)mesh.BvhOffset) < payloadLength)
        {
            int bvhStart = checked((int)mesh.BvhOffset);
            bvhRootIndex = _bvh.Add(
                meshId,
                payload.Span.Slice(bvhStart),
                meshStartPageID,
                _pages);
        }

        return bvhRootIndex;
    }

    public bool TryDepth(uint bvhRootIndex, out int depth)
        => _bvh.TryDepth(bvhRootIndex, out depth);

    public IReadOnlyList<PageMeta> Pages(Handle<Mesh> mesh)
    {
        string meshId = MeshPages.Key(mesh);
        return _pages.Registry.TryGetValue(meshId, out List<PageMeta>? pages)
            ? pages
            : Array.Empty<PageMeta>();
    }

    public IReadOnlyList<uint> Leaves(uint pageID)
        => _pages.Leaves(pageID);

    private uint Alloc(uint size)
        => Alloc(size, uint.MaxValue);

    private uint Alloc(uint size, uint protectedPageID)
    {
        if (_heap.TryAlloc(size, out uint offset))
            return offset;

        if (TryEvict(size, protectedPageID) && _heap.TryAlloc(size, out offset))
            return offset;

        throw new Exception(
            $"Cluster Page Heap OOM. Requested {size}, LargestFreeBlock {_heap.Largest()}"
        );
    }

    private bool TryEvict(uint size, uint protectedPageID)
    {
        while (!_heap.Has(size))
        {
            if (!TryEvictPage(protectedPageID))
                return false;
        }

        return true;
    }

    private bool TryEvictPage(uint protectedPageID)
    {
        return _pages.TryVictim(protectedPageID, out uint pageID)
            && EvictPage(pageID);
    }

    private void UploadData(uint offset, ReadOnlyMemory<byte> data)
        => _pageUploads.Add(offset, data);

    public (IReadOnlyList<UploadItem> GlobalBVH, IReadOnlyList<UploadItem> PageHeap) TakeUploads()
        => (_bvh.TakeUploads(), _pageUploads.Take());

    public void Dispose() { }
}
