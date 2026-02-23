using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Data;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Systems;

public class ClusterResourceManager : IDisposable
{
    private readonly RenderContext _context;
    
    // Page-Based Streaming Heap
    public IBuffer? PageHeap { get; private set; }
    
    // Global BVH Buffer
    public IBuffer? GlobalBVHBuffer { get; private set; }
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

    // Page Table
    public IBuffer? PageTableBuffer { get; private set; }
    public uint PageCount => (uint)_pageOffsets.Count;
    private List<uint> _pageOffsets = new();
    private bool _pageTableDirty = false;

    // Simple bump allocator for now
    private uint _heapOffset = 0;
    private const uint HeapSize = 64 * 1024 * 1024; // 64MB
    private const int PageSize = 131072; // 128KB fixed page size as per plan

    public ClusterResourceManager(RenderContext context)
    {
        _context = context;
        InitHeap();
    }
    
    private void InitHeap()
    {
        if (_context.Device == null) return;
        
        BufferDesc heapDesc = new BufferDesc
        {
            Name = "Global Page Heap",
            Size = HeapSize,
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.IndexBuffer,
            Mode = BufferMode.Raw
        };
        
        PageHeap = _context.Device.CreateBuffer(heapDesc);

        // Global BVH Buffer (Structured)
        BufferDesc bvhDesc = new BufferDesc
        {
            Name = "Global BVH Buffer",
            Size = BVHMaxNodes * 64, // 64 bytes per node
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 64
        };
        GlobalBVHBuffer = _context.Device.CreateBuffer(bvhDesc);

        // Initial Page Table (size 1024, growable later)
        BufferDesc tableDesc = new BufferDesc
        {
            Name = "Page Table",
            Size = 1024 * 4, // 1024 pages
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4
        };
        PageTableBuffer = _context.Device.CreateBuffer(tableDesc);
    }

    public IBuffer? GetIndexBuffer() => PageHeap;

    public void CommitPageTable()
    {
        if (!_pageTableDirty || _context.ImmediateContext == null || PageTableBuffer == null) return;

        // Resize if needed
        uint requiredSize = (uint)(_pageOffsets.Count * 4);
        if (PageTableBuffer.GetDesc().Size < requiredSize)
        {
            PageTableBuffer.Dispose();
            BufferDesc tableDesc = new BufferDesc
            {
                Name = "Page Table",
                Size = requiredSize * 2, // Grow x2
                Usage = Usage.Default,
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 4
            };
            PageTableBuffer = _context.Device!.CreateBuffer(tableDesc);
        }

        unsafe
        {
            var span = CollectionsMarshal.AsSpan(_pageOffsets);
            fixed (uint* ptr = span)
            {
                _context.ImmediateContext!.UpdateBuffer(PageTableBuffer, 0, requiredSize, (IntPtr)ptr, ResourceStateTransitionMode.Transition);
            }
        }
        _pageTableDirty = false;
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

        var pageList = new List<PageInfo>();
        PageRegistry[meshName] = pageList;

        int offset = 0;
        uint meshStartPageID = (uint)_pageOffsets.Count;

        while (offset < pageDataEnd)
        {
            // Read Header to get PageSize
            // Header: ClusterCount(0), VertexCount(4), IndexCount(8), PageSize(12)
            if (offset + 16 > pageDataEnd) break;
            
            var headerSpan = payload.Span.Slice(offset, 16);
            uint pageSize = MemoryMarshal.Read<uint>(headerSpan.Slice(12, 4));
            uint clusterCount = MemoryMarshal.Read<uint>(headerSpan.Slice(0, 4));
            
            // Fallback for old assets or if PageSize was 0 (pad)
            if (pageSize == 0) pageSize = 131072; 

            if (offset + pageSize > pageDataEnd) pageSize = (uint)(pageDataEnd - offset);

            // Allocate
            uint heapOffset = AllocateHeap(pageSize);
            
            // Upload
            UploadData(heapOffset, payload.Span.Slice(offset, (int)pageSize));
            
            // Update Page Table
            uint pageId = (uint)_pageOffsets.Count;
            _pageOffsets.Add(heapOffset);
            
            pageList.Add(new PageInfo 
            {
                PageID = pageId,
                Offset = heapOffset,
                Size = pageSize,
                ClusterCount = clusterCount
            });
            
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
                         uint packed = patchedNodes[i].ChildPointer;
                         uint localPageIdx = packed >> 12;
                         uint clusterStart = packed & 0xFFF;
                         uint globalPageIdx = localPageIdx + meshStartPageID;
                         patchedNodes[i].ChildPointer = (globalPageIdx << 12) | clusterStart;
                     }
                 }
                 
                 UploadBVH(globalNodeBase * 64, patchedNodes);
                 
                 _bvhNodeCount += (uint)nodeCount;
                 
                 // Root is the last node in the list
                 bvhRootIndex = globalNodeBase + (uint)nodeCount - 1;
                 MeshBVHRoots[meshName] = bvhRootIndex;
             }
        }
        
        _pageTableDirty = true;
        return bvhRootIndex;
    }

    private void UploadBVH(uint offset, ClusterBVHNode[] data)
    {
        if (_context.ImmediateContext == null || GlobalBVHBuffer == null) return;
        unsafe
        {
            fixed (ClusterBVHNode* ptr = data)
            {
                _context.ImmediateContext.UpdateBuffer(GlobalBVHBuffer, offset, (uint)(data.Length * 64), (IntPtr)ptr, ResourceStateTransitionMode.Transition);
            }
        }
    }

    private uint AllocateHeap(uint size)
    {
        // Align to 16 bytes for ByteAddressBuffer performance
        uint alignedSize = (size + 15) & ~15u;
        
        if (_heapOffset + alignedSize > HeapSize)
        {
            // TODO: Simple OOM handling
             throw new Exception($"Cluster Page Heap OOM. Requested {alignedSize}, Available {HeapSize - _heapOffset}");
        }
             
        uint offset = _heapOffset;
        _heapOffset += alignedSize;
        return offset;
    }

    private void UploadData(uint offset, ReadOnlySpan<byte> data)
    {
        if (_context.ImmediateContext == null || PageHeap == null) return;
        
        unsafe
        {
            fixed (byte* ptr = data)
            {
                _context.ImmediateContext.UpdateBuffer(PageHeap, offset, (uint)data.Length, (IntPtr)ptr, ResourceStateTransitionMode.Transition);
            }
        }
    }

    public void Dispose()
    {
        PageHeap?.Dispose();
        PageTableBuffer?.Dispose();
        GlobalBVHBuffer?.Dispose();
    }
}
