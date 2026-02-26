using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Systems;

public class ClusterResourceManager : IDisposable
{
    private readonly RenderContext _context;

    // Page-Based Streaming Heap

    public IBuffer? PageHeap { get; private set; }
    public IBuffer? PageFaultBuffer { get; private set; }
    public IBuffer? PageFaultReadbackBuffer { get; private set; }

    // Global BVH Buffer

    public IBuffer? GlobalBVHBuffer { get; private set; }
    private uint _bvhNodeCount = 0;
    private const uint BVHMaxNodes = 262144; // 256K nodes * 64B = 16MB

    // Patching resources
    private IPipelineState? _patchPSO;
    private IShaderResourceBinding? _patchSRB;
    private ShaderAsset? _patchShaderAsset;
    private IBuffer? _patchUniformsBuffer;
    private IBuffer? _patchNodeIndicesBuffer;
    private bool _patchInitialized = false;

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
    private readonly List<uint> _pageOffsets = new(); // Current resident offset per page ID, or PageFaultMarker when non-resident
    private readonly List<uint> _pageSizes = new(); // Original page payload size per page ID
    public const uint MaxPageFaults = 4096;
    public uint PageFaultBufferSize => 4u + (MaxPageFaults * 4u);

    private const uint HeapSize = 64 * 1024 * 1024; // 64MB
    private const int PageSize = 131072; // 128KB fixed page size as per plan

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

        BufferDesc pageFaultDesc = new BufferDesc
        {
            Name = "Cluster Page Fault Buffer",
            Size = PageFaultBufferSize,
            Usage = Usage.Default,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4
        };
        PageFaultBuffer = _context.Device.CreateBuffer(pageFaultDesc);

        BufferDesc pageFaultReadbackDesc = new BufferDesc
        {
            Name = "Cluster Page Fault Readback",
            Size = PageFaultBufferSize,
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        };
        PageFaultReadbackBuffer = _context.Device.CreateBuffer(pageFaultReadbackDesc);

        // Global BVH Buffer (Structured)
        BufferDesc bvhDesc = new BufferDesc
        {
            Name = "Global BVH Buffer",
            Size = BVHMaxNodes * 64, // 64 bytes per node
            Usage = Usage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            Mode = BufferMode.Structured,
            ElementByteStride = 64
        };
        GlobalBVHBuffer = _context.Device.CreateBuffer(bvhDesc);

        _freeBlocks.Clear();
        _freeBlocks.Add(new FreeBlock { Offset = 0, Size = HeapSize });

        // Patch Uniforms
        _patchUniformsBuffer = _context.Device.CreateBuffer(new BufferDesc
        {
            Name = "Patch Uniforms",
            Size = 16,
            Usage = Usage.Default,
            BindFlags = BindFlags.UniformBuffer
        });

        // Dynamic buffer for node indices

        _patchNodeIndicesBuffer = _context.Device.CreateBuffer(new BufferDesc
        {
            Name = "Patch Node Indices",
            Size = 16384 * 4,
            Usage = Usage.Dynamic,

            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            Mode = BufferMode.Structured,
            ElementByteStride = 4
        });


    }

    public IBuffer? GetIndexBuffer() => PageHeap;



    private void InitPatchPSO()
    {
        if (_patchInitialized || _context.Device == null) return;


        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/bvh_patch.slang"));
        _patchShaderAsset = SlangShaderImporter.Import(path);


        var ci = new ComputePipelineStateCreateInfo();
        ci.PSODesc.Name = "BVH Patch PSO";
        ci.PSODesc.PipelineType = PipelineType.Compute;
        using var cs = _patchShaderAsset.CreateShader(_context, "main");
        ci.Cs = cs;
        ci.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Mutable;
        ci.PSODesc.ResourceLayout.Variables = _patchShaderAsset.GetResourceVariables(_context, static (name, cat) => null);

        _patchPSO = _context.Device.CreateComputePipelineState(ci);
        if (_patchPSO != null)
        {
            _patchSRB = _patchPSO.CreateShaderResourceBinding(false);
            _patchSRB.GetVariable(_context, _patchShaderAsset, ShaderType.Compute, "GlobalBVH")?.Set(GlobalBVHBuffer?.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
            _patchSRB.GetVariable(_context, _patchShaderAsset, ShaderType.Compute, "Uniforms")?.Set(_patchUniformsBuffer, SetShaderResourceFlags.None);
            _patchSRB.GetVariable(_context, _patchShaderAsset, ShaderType.Compute, "NodeIndices")?.Set(_patchNodeIndicesBuffer?.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        }


        _patchInitialized = true;
    }

    struct PatchUniforms
    {
        public uint NodeCount;
        public uint NewPagePointer;
        public uint Pad0;
        public uint Pad1;
    }

    public void PatchBVHLeafNodes(uint pageID, uint byteOffset, bool resident)
    {
        if (!PageToLeafNodes.TryGetValue(pageID, out var nodes) || nodes.Count == 0) return;
        if (_context.ImmediateContext == null || _patchNodeIndicesBuffer == null || _patchUniformsBuffer == null) return;


        InitPatchPSO();
        if (_patchPSO == null || _patchSRB == null) return;

        // 1. Update Uniforms
        uint offsetVal = resident ? byteOffset : ClusterBVHNode.PageFaultMarker;
        var uniforms = new PatchUniforms { NodeCount = (uint)nodes.Count, NewPagePointer = offsetVal, Pad0 = 0, Pad1 = 0 };
        _context.ImmediateContext.UpdateBuffer(_patchUniformsBuffer, 0, new PatchUniforms[] { uniforms }.AsSpan(), ResourceStateTransitionMode.Transition);

        // 2. Map & Write Node Indices
        unsafe
        {
            // Reallocate dynamic buffer if too small
            if (_patchNodeIndicesBuffer.GetDesc().Size < (ulong)nodes.Count * 4)
            {
                _patchNodeIndicesBuffer.Dispose();
                _patchNodeIndicesBuffer = _context.Device!.CreateBuffer(new BufferDesc
                {
                    Name = "Patch Node Indices",
                    Size = (uint)nodes.Count * 4 * 2,
                    Usage = Usage.Dynamic,

                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                    Mode = BufferMode.Structured,
                    ElementByteStride = 4
                });
                _patchSRB.GetVariable(_context, _patchShaderAsset, ShaderType.Compute, "NodeIndices")?.Set(_patchNodeIndicesBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
            }

            var map = _context.ImmediateContext.MapBuffer<uint>(_patchNodeIndicesBuffer, MapType.Write, MapFlags.Discard);
            for (int i = 0; i < nodes.Count; i++) map[i] = nodes[i];
            _context.ImmediateContext.UnmapBuffer(_patchNodeIndicesBuffer, MapType.Write);
        }

        // 3. Dispatch
        _context.ImmediateContext.SetPipelineState(_patchPSO);
        _context.ImmediateContext.CommitShaderResources(_patchSRB, ResourceStateTransitionMode.Transition);
        uint groups = ((uint)nodes.Count + 63) / 64;
        _context.ImmediateContext.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = groups, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

        if (resident)
            _residentPages.Add(pageID);
        else
            _residentPages.Remove(pageID);
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
        return AllocateHeap(size, uint.MaxValue);
    }

    private uint AllocateHeap(uint size, uint protectedPageID)
    {
        uint alignedSize = AlignTo16(size);

        if (TryAllocateHeap(alignedSize, out uint offset))
            return offset;

        if (TryEvictPagesForSize(alignedSize, protectedPageID) && TryAllocateHeap(alignedSize, out offset))
            return offset;

        throw new Exception($"Cluster Page Heap OOM. Requested {alignedSize}, LargestFreeBlock {GetLargestFreeBlockSize()}");
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
                    Size = block.Size - alignedSize
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
                    Size = prev.Size + cur.Size
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
                    Size = cur.Size + next.Size
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
        PageFaultBuffer?.Dispose();
        PageFaultReadbackBuffer?.Dispose();
        GlobalBVHBuffer?.Dispose();
        _patchPSO?.Dispose();
        _patchSRB?.Dispose();
        _patchUniformsBuffer?.Dispose();
        _patchNodeIndicesBuffer?.Dispose();
    }
}
