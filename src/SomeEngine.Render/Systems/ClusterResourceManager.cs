using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using System.Numerics;
namespace SomeEngine.Render.Systems;

public class ClusterResourceManager : IDisposable
{
    private readonly RenderContext _context;

    // Page-Based Streaming Heap

    public BufferDesc PageHeapDesc { get; private set; }
    public BufferDesc PageFaultDesc { get; private set; }
    public BufferDesc PageFaultReadbackDesc { get; private set; }

    // Global BVH Buffer

    public BufferDesc GlobalBVHDesc { get; private set; }
    private uint _bvhNodeCount = 0;
    private const uint BVHMaxNodes = 262144; // 256K nodes * 64B = 16MB

    // Track loaded pages
    public struct PageInfo
    {
        public uint PageID;
        public uint Offset;
        public uint Size;
        public uint ClusterCount;
    }

    // Key: Mesh Name, Value: List of Pages

    public Dictionary<string, List<PageInfo>> PageRegistry { get; } = new();
    public Dictionary<string, uint> MeshBVHRoots { get; } = new();

    // Map PageID to a list of global BVH leaf node indices
    public Dictionary<uint, List<uint>> PageToLeafNodes { get; } = new();
    private readonly Dictionary<uint, uint> _leafNodeToPage = new();
    private readonly Dictionary<uint, byte[]> _pageSourceData = new();
    private readonly HashSet<uint> _residentPages = new();

    public uint PageCount => (uint)_pageOffsets.Count;
    public uint ResidentPageCount => (uint)_residentPages.Count;
    private readonly List<uint> _pageOffsets = new(); // Current resident offset per page ID, or PageFaultMarker when non-resident
    private readonly List<uint> _pageSizes = new(); // Original page payload size per page ID
    public const uint MaxPageFaults = 4096;
    public uint PageFaultBufferSize => 4u + (MaxPageFaults * 4u);

    private const uint HeapSize = 64 * 1024 * 1024; // 64MB
    private const int PageSize = 131072; // 128KB fixed page size as per plan

    // Global quantization parameters (from loaded mesh asset)
    public Vector3 QuantOrigin { get; private set; } = Vector3.Zero;
    public float QuantStep { get; private set; } = 1.0f;

    private struct FreeBlock
    {
        public uint Offset;
        public uint Size;
    }

    private readonly List<FreeBlock> _freeBlocks = new();
    private readonly LinkedList<uint> _residentPageLru = new();
    private readonly Dictionary<uint, LinkedListNode<uint>> _residentPageLruNodes = new();

    public ClusterResourceManager(RenderContext context)
    {
        _context = context;
        InitHeap();
    }

    private void InitHeap()
    {
        PageHeapDesc = new BufferDesc
        {
            Name = "Global Page Heap",
            Size = HeapSize,
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.IndexBuffer,
            Mode = BufferMode.Raw,
        };

        PageFaultDesc = new BufferDesc
        {
            Name = "Cluster Page Fault Buffer",
            Size = PageFaultBufferSize,
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        };

        PageFaultReadbackDesc = new BufferDesc
        {
            Name = "Cluster Page Fault Readback",
            Size = PageFaultBufferSize,
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        };

        // Global BVH Buffer (Structured)
        GlobalBVHDesc = new BufferDesc
        {
            Name = "Global BVH Buffer",
            Size = BVHMaxNodes * 64, // 64 bytes per node
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            Mode = BufferMode.Structured,
            ElementByteStride = 64,
        };

        _freeBlocks.Clear();
        _freeBlocks.Add(new FreeBlock { Offset = 0, Size = HeapSize });
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BVHPatchData
    {
        public uint NodeIndex;
        public uint NewPagePointer;
    }

    private struct PendingPatch
    {
        public uint PageID;
        public uint ByteOffset;
        public bool Resident;
    }

    private readonly List<PendingPatch> _pendingPatches = new();

    private struct PendingUploadBVH
    {
        public uint Offset;
        public ClusterBVHNode[] Data;
    }

    private readonly List<PendingUploadBVH> _pendingUploadBVH = new();

    private struct PendingUploadData
    {
        public uint Offset;
        public byte[] Data;
    }

    private readonly List<PendingUploadData> _pendingUploadData = new();

    public void PatchBVHLeafNodes(uint pageID, uint byteOffset, bool resident)
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

    public IReadOnlyList<BVHPatchData> ExtractPendingPatches()
    {
        if (_pendingPatches.Count == 0)
            return Array.Empty<BVHPatchData>();

        var patchList = new List<BVHPatchData>();
        foreach (var patch in _pendingPatches)
        {
            if (!PageToLeafNodes.TryGetValue(patch.PageID, out var nodes) || nodes.Count == 0)
                continue;

            uint offsetVal = patch.Resident ? patch.ByteOffset : ClusterBVHNode.PageFaultMarker;
            for (int i = 0; i < nodes.Count; i++)
            {
                patchList.Add(
                    new BVHPatchData { NodeIndex = nodes[i], NewPagePointer = offsetVal }
                );
            }

            if (patch.Resident)
                _residentPages.Add(patch.PageID);
            else
                _residentPages.Remove(patch.PageID);
        }

        _pendingPatches.Clear();
        return patchList;
    }

    public bool TryGetPageForLeafNode(uint nodeIndex, out uint pageID)
    {
        return _leafNodeToPage.TryGetValue(nodeIndex, out pageID);
    }

    public bool IsPageResident(uint pageID)
    {
        return _residentPages.Contains(pageID);
    }

    public bool TryGetPageOffset(uint pageID, out uint byteOffset)
    {
        if (pageID >= _pageOffsets.Count)
        {
            byteOffset = 0;
            return false;
        }

        byteOffset = _pageOffsets[(int)pageID];
        return byteOffset != ClusterBVHNode.PageFaultMarker;
    }

    public bool TryLoadPage(uint pageID, out uint byteOffset)
    {
        if (IsPageResident(pageID) && TryGetPageOffset(pageID, out byteOffset))
        {
            TouchPage(pageID);
            return true;
        }

        if (pageID >= _pageSizes.Count)
        {
            byteOffset = 0;
            return false;
        }

        if (!_pageSourceData.TryGetValue(pageID, out var data))
        {
            byteOffset = 0;
            return false;
        }

        uint pageSize = _pageSizes[(int)pageID];
        byteOffset = AllocateHeap(pageSize, pageID);

        UploadData(byteOffset, data);
        _pageOffsets[(int)pageID] = byteOffset;
        _residentPages.Add(pageID);
        TouchPage(pageID);
        return true;
    }

    public bool MarkPageNonResident(uint pageID)
    {
        if (!_residentPages.Contains(pageID))
            return false;

        if (pageID >= _pageOffsets.Count || pageID >= _pageSizes.Count)
            return false;

        uint offset = _pageOffsets[(int)pageID];
        if (offset == ClusterBVHNode.PageFaultMarker)
            return false;

        PatchBVHLeafNodes(pageID, ClusterBVHNode.PageFaultMarker, false);
        FreeHeap(offset, _pageSizes[(int)pageID]);
        _pageOffsets[(int)pageID] = ClusterBVHNode.PageFaultMarker;
        _residentPages.Remove(pageID);
        RemoveFromLru(pageID);
        return true;
    }

    public void TouchPage(uint pageID)
    {
        if (!_residentPages.Contains(pageID))
            return;

        if (_residentPageLruNodes.TryGetValue(pageID, out var node))
        {
            _residentPageLru.Remove(node);
            _residentPageLru.AddLast(node);
            return;
        }

        var newNode = _residentPageLru.AddLast(pageID);
        _residentPageLruNodes[pageID] = newNode;
    }

    public uint AddMesh(MeshAsset mesh)
    {
        if (!mesh.Payload.HasValue || mesh.Payload.Value.IsEmpty)
            return uint.MaxValue;

        string meshName = mesh.Name ?? "Unnamed";
        if (PageRegistry.ContainsKey(meshName))
            return MeshBVHRoots.GetValueOrDefault(meshName, uint.MaxValue);

        var payload = mesh.Payload.Value;
        int payloadLength = payload.Length;
        int pageDataEnd = (mesh.BvhOffset > 0) ? (int)mesh.BvhOffset : payloadLength;

        // Store global quantization parameters
        if (mesh.QuantOrigin != null)
        {
            QuantOrigin = new Vector3(mesh.QuantOrigin.X, mesh.QuantOrigin.Y, mesh.QuantOrigin.Z);
        }
        QuantStep = mesh.QuantStep > 0 ? mesh.QuantStep : 1.0f;

        var pageList = new List<PageInfo>();
        PageRegistry[meshName] = pageList;

        int offset = 0;
        uint meshStartPageID = (uint)_pageOffsets.Count;

        while (offset < pageDataEnd)
        {
            // Read Header (44 bytes)
            // ClusterCount(0), VertexCount(4), TriangleCount(8), QuantOriginX(12),
            // ClustersOff(16), PosOff(20), AttrOff(24), IdxOff(28),
            // QuantOriginY(32), QuantOriginZ(36), QuantStep(40)
            if (offset + MeshPageHeader.Size > pageDataEnd)
                break;

            var headerSpan = payload.Span.Slice(offset, MeshPageHeader.Size);
            uint clusterCount = MemoryMarshal.Read<uint>(headerSpan.Slice(0, 4));
            uint totalTriangleCount = MemoryMarshal.Read<uint>(headerSpan.Slice(8, 4));
            uint indicesOffset = MemoryMarshal.Read<uint>(headerSpan.Slice(28, 4));

            // Reconstruct page size: IndicesOffset + TotalTriangleCount * 3 (u8 indices)
            uint pageSize = indicesOffset + totalTriangleCount * 3;

            if (offset + pageSize > pageDataEnd)
                pageSize = (uint)(pageDataEnd - offset);

            // Allocate
            uint heapOffset = AllocateHeap(pageSize);

            byte[] pageData = payload.Span.Slice(offset, (int)pageSize).ToArray();

            // Upload

            UploadData(heapOffset, pageData);

            // Register Page

            uint pageId = (uint)_pageOffsets.Count;
            _pageOffsets.Add(heapOffset);
            _pageSizes.Add(pageSize);
            PageToLeafNodes[pageId] = new List<uint>();
            _pageSourceData[pageId] = pageData;
            _residentPages.Add(pageId);
            TouchPage(pageId);

            pageList.Add(
                new PageInfo
                {
                    PageID = pageId,
                    Offset = heapOffset,
                    Size = pageSize,
                    ClusterCount = clusterCount,
                }
            );

            offset += (int)pageSize;
        }

        uint bvhRootIndex = uint.MaxValue;

        // Load BVH
        if (mesh.BvhOffset > 0 && (int)mesh.BvhOffset < payloadLength)
        {
            int bvhStart = (int)mesh.BvhOffset;
            var bvhSpan = payload.Span.Slice(bvhStart);
            int nodeCount = bvhSpan.Length / 64; // 64 bytes per node

            if (nodeCount > 0)
            {
                uint globalNodeBase = _bvhNodeCount;
                // TODO: Check buffer overflow

                var nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(bvhSpan);
                ClusterBVHNode[] patchedNodes = new ClusterBVHNode[nodeCount];
                nodes.CopyTo(patchedNodes);

                for (int i = 0; i < nodeCount; i++)
                {
                    if (patchedNodes[i].NodeType == 0) // Internal
                    {
                        patchedNodes[i].ChildPointer += globalNodeBase;
                    }
                    else // Leaf
                    {
                        // ChildPointer currently holds the local page index
                        uint localPageIdx = patchedNodes[i].ChildPointer;
                        uint globalPageIdx = localPageIdx + meshStartPageID;

                        uint globalNodeIdx = globalNodeBase + (uint)i;
                        PageToLeafNodes[globalPageIdx].Add(globalNodeIdx);
                        _leafNodeToPage[globalNodeIdx] = globalPageIdx;

                        uint pageOffset = _pageOffsets[(int)globalPageIdx];
                        patchedNodes[i].ChildPointer = pageOffset;
                        // ChildCount remains as packed (ClusterCount and ClusterStart)
                    }
                }

                UploadBVH(globalNodeBase * 64, patchedNodes);

                _bvhNodeCount += (uint)nodeCount;

                // Root is the last node in the list
                bvhRootIndex = globalNodeBase + (uint)nodeCount - 1;
                MeshBVHRoots[meshName] = bvhRootIndex;
            }
        }

        return bvhRootIndex;
    }

    private void UploadBVH(uint offset, ClusterBVHNode[] data)
    {
        _pendingUploadBVH.Add(new PendingUploadBVH { Offset = offset, Data = data });
    }

    private uint AllocateHeap(uint size)
    {
        return AllocateHeap(size, uint.MaxValue);
    }

    private uint AllocateHeap(uint size, uint protectedPageID)
    {
        uint alignedSize = AlignTo16(size);

        if (TryAllocateHeap(alignedSize, out uint offset))
            return offset;

        if (
            TryEvictPagesForSize(alignedSize, protectedPageID)
            && TryAllocateHeap(alignedSize, out offset)
        )
            return offset;

        throw new Exception(
            $"Cluster Page Heap OOM. Requested {alignedSize}, LargestFreeBlock {GetLargestFreeBlockSize()}"
        );
    }

    private static uint AlignTo16(uint size)
    {
        return (size + 15) & ~15u;
    }

    private bool TryAllocateHeap(uint alignedSize, out uint offset)
    {
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            var block = _freeBlocks[i];
            if (block.Size < alignedSize)
                continue;

            offset = block.Offset;

            if (block.Size == alignedSize)
            {
                _freeBlocks.RemoveAt(i);
            }
            else
            {
                _freeBlocks[i] = new FreeBlock
                {
                    Offset = block.Offset + alignedSize,
                    Size = block.Size - alignedSize,
                };
            }

            return true;
        }

        offset = 0;
        return false;
    }

    private void FreeHeap(uint offset, uint size)
    {
        uint alignedSize = AlignTo16(size);
        if (alignedSize == 0)
            return;

        FreeBlock newBlock = new FreeBlock { Offset = offset, Size = alignedSize };

        int insertIndex = 0;
        while (insertIndex < _freeBlocks.Count && _freeBlocks[insertIndex].Offset < newBlock.Offset)
            insertIndex++;

        _freeBlocks.Insert(insertIndex, newBlock);

        if (insertIndex > 0)
        {
            var prev = _freeBlocks[insertIndex - 1];
            var cur = _freeBlocks[insertIndex];
            if (prev.Offset + prev.Size == cur.Offset)
            {
                _freeBlocks[insertIndex - 1] = new FreeBlock
                {
                    Offset = prev.Offset,
                    Size = prev.Size + cur.Size,
                };
                _freeBlocks.RemoveAt(insertIndex);
                insertIndex--;
            }
        }

        if (insertIndex + 1 < _freeBlocks.Count)
        {
            var cur = _freeBlocks[insertIndex];
            var next = _freeBlocks[insertIndex + 1];
            if (cur.Offset + cur.Size == next.Offset)
            {
                _freeBlocks[insertIndex] = new FreeBlock
                {
                    Offset = cur.Offset,
                    Size = cur.Size + next.Size,
                };
                _freeBlocks.RemoveAt(insertIndex + 1);
            }
        }
    }

    private bool TryEvictPagesForSize(uint alignedSize, uint protectedPageID)
    {
        while (!HasBlockForSize(alignedSize))
        {
            if (!TryEvictLeastRecentlyUsed(protectedPageID))
                return false;
        }

        return true;
    }

    private bool HasBlockForSize(uint alignedSize)
    {
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            if (_freeBlocks[i].Size >= alignedSize)
                return true;
        }

        return false;
    }

    private bool TryEvictLeastRecentlyUsed(uint protectedPageID)
    {
        var node = _residentPageLru.First;
        while (node != null)
        {
            uint pageID = node.Value;
            node = node.Next;

            if (pageID == protectedPageID)
                continue;

            if (MarkPageNonResident(pageID))
                return true;
        }

        return false;
    }

    private uint GetLargestFreeBlockSize()
    {
        uint largest = 0;
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            if (_freeBlocks[i].Size > largest)
                largest = _freeBlocks[i].Size;
        }

        return largest;
    }

    private void RemoveFromLru(uint pageID)
    {
        if (!_residentPageLruNodes.TryGetValue(pageID, out var node))
            return;

        _residentPageLru.Remove(node);
        _residentPageLruNodes.Remove(pageID);
    }

    private void UploadData(uint offset, ReadOnlySpan<byte> data)
    {
        _pendingUploadData.Add(new PendingUploadData { Offset = offset, Data = data.ToArray() });
    }

    public void ExecutePendingUploads(
        RenderContext renderContext,
        IBuffer globalBVHBuffer,
        IBuffer pageHeapBuffer
    )
    {
        var ctx = renderContext.ImmediateContext;
        if (ctx == null)
            return;

        foreach (var upload in _pendingUploadBVH)
        {
            ctx.UpdateBuffer(
                globalBVHBuffer,
                upload.Offset,
                new ReadOnlySpan<ClusterBVHNode>(upload.Data),
                ResourceStateTransitionMode.Verify
            );
        }
        _pendingUploadBVH.Clear();

        foreach (var upload in _pendingUploadData)
        {
            ctx.UpdateBuffer(
                pageHeapBuffer,
                upload.Offset,
                new ReadOnlySpan<byte>(upload.Data),
                ResourceStateTransitionMode.Verify
            );
        }
        _pendingUploadData.Clear();
    }

    public void Dispose() { }
}
