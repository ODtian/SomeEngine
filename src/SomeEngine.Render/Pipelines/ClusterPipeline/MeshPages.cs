using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Core.Collections;
using SomeEngine.Render.Assets;
using System.Numerics;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct PageMeta(
    uint PageID,
    uint Offset,
    uint Size,
    uint ClusterCount,
    Vector3 QuantOrigin,
    float QuantStep);

internal sealed class MeshPages
{
    private readonly FlatDictionary<uint, uint> _leafNodeToPage = new();
    private readonly FlatDictionary<uint, byte[]> _sourceData = new();
    private readonly HashSet<uint> _residentPages = new();
    private readonly List<uint> _offsets = [];
    private readonly List<uint> _sizes = [];
    private readonly LinkedList<uint> _residentLru = new();
    private readonly FlatDictionary<uint, LinkedListNode<uint>> _residentLruNodes = new();

    public FlatDictionary<string, List<PageMeta>> Registry { get; } = new();
    public FlatDictionary<uint, List<uint>> LeafNodes { get; } = new();
    public uint Count => (uint)_offsets.Count;
    public uint ResidentCount => (uint)_residentPages.Count;
    public uint MissingCount => Count - ResidentCount;

    public static string Key(Handle<Mesh> mesh)
    {
        if (!mesh.IsValid)
            throw new InvalidOperationException("Runtime mesh handle must be valid before cluster page registration.");

        return mesh.ToString();
    }

    public bool Has(string meshId)
        => Registry.ContainsKey(meshId);

    public bool TryAddMesh(Handle<Mesh> mesh, out string meshId)
    {
        meshId = Key(mesh);
        if (Registry.ContainsKey(meshId))
            return false;

        Registry[meshId] = [];
        return true;
    }

    public PageMeta AddPage(
        string meshId,
        uint offset,
        uint clusterCount,
        Vector3 quantOrigin,
        float quantStep,
        ReadOnlySpan<byte> source,
        out ReadOnlyMemory<byte> data)
    {
        if (!Registry.TryGetValue(meshId, out List<PageMeta>? pages))
            throw new InvalidOperationException($"Mesh '{meshId}' is not registered.");
        if (source.IsEmpty)
            throw new ArgumentException("Mesh page source data must not be empty.", nameof(source));

        uint pageId = Count;
        uint size = checked((uint)source.Length);
        byte[] sourceBytes = source.ToArray();
        data = sourceBytes;

        _offsets.Add(offset);
        _sizes.Add(size);
        _sourceData[pageId] = sourceBytes;
        _residentPages.Add(pageId);
        LeafNodes[pageId] = [];

        var page = new PageMeta(pageId, offset, size, clusterCount, quantOrigin, quantStep);
        pages.Add(page);
        Touch(pageId);
        return page;
    }

    public void AddLeaf(uint pageId, uint nodeIndex)
    {
        Ensure(pageId);
        if (!LeafNodes.TryGetValue(pageId, out List<uint>? nodes))
            throw new InvalidOperationException($"Mesh page {pageId} does not have a leaf-node list.");

        nodes.Add(nodeIndex);
        _leafNodeToPage[nodeIndex] = pageId;
    }

    public IReadOnlyList<uint> Leaves(uint pageId)
        => LeafNodes.TryGetValue(pageId, out List<uint>? nodes) ? nodes : Array.Empty<uint>();

    public bool TryLeaf(uint nodeIndex, out uint pageId)
        => _leafNodeToPage.TryGetValue(nodeIndex, out pageId);

    public bool IsResident(uint pageId)
        => _residentPages.Contains(pageId);

    public uint Offset(uint pageId)
    {
        Ensure(pageId);
        return _offsets[(int)pageId];
    }

    public bool TryOffset(uint pageId, out uint offset)
    {
        if (pageId >= _offsets.Count)
        {
            offset = 0;
            return false;
        }

        offset = _offsets[(int)pageId];
        return offset != ClusterBVHNode.PageFaultMarker;
    }

    public bool TrySource(uint pageId, out uint size, out ReadOnlyMemory<byte> source)
    {
        if (pageId >= _sizes.Count || !_sourceData.TryGetValue(pageId, out byte[]? data))
        {
            size = 0;
            source = default;
            return false;
        }

        size = _sizes[(int)pageId];
        source = data;
        return true;
    }

    public void MakeResident(uint pageId, uint offset)
    {
        Ensure(pageId);
        _offsets[(int)pageId] = offset;
        _residentPages.Add(pageId);
        Touch(pageId);
    }

    public bool MakeMissing(uint pageId, out uint offset, out uint size)
    {
        if (!_residentPages.Contains(pageId) || pageId >= _offsets.Count || pageId >= _sizes.Count)
        {
            offset = 0;
            size = 0;
            return false;
        }

        offset = _offsets[(int)pageId];
        size = _sizes[(int)pageId];
        if (offset == ClusterBVHNode.PageFaultMarker)
            return false;

        _offsets[(int)pageId] = ClusterBVHNode.PageFaultMarker;
        _residentPages.Remove(pageId);
        Remove(pageId);
        return true;
    }

    public void Touch(uint pageId)
    {
        if (!_residentPages.Contains(pageId))
            return;

        if (_residentLruNodes.TryGetValue(pageId, out LinkedListNode<uint>? node))
        {
            _residentLru.Remove(node);
            _residentLru.AddLast(node);
            return;
        }

        LinkedListNode<uint> newNode = _residentLru.AddLast(pageId);
        _residentLruNodes[pageId] = newNode;
    }

    public bool TryVictim(uint protectedPageId, out uint pageId)
    {
        LinkedListNode<uint>? node = _residentLru.First;
        while (node != null)
        {
            pageId = node.Value;
            if (pageId != protectedPageId)
                return true;

            node = node.Next;
        }

        pageId = 0;
        return false;
    }

    private void Ensure(uint pageId)
    {
        if (pageId >= _offsets.Count)
            throw new ArgumentOutOfRangeException(nameof(pageId), pageId, "Mesh page id is outside the registered page range.");
    }

    private void Remove(uint pageId)
    {
        if (!_residentLruNodes.TryGetValue(pageId, out LinkedListNode<uint>? node))
            return;

        _residentLru.Remove(node);
        _residentLruNodes.Remove(pageId);
    }
}
