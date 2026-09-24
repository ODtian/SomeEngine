using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;
using SomeEngine.Core.Collections;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
internal struct ClusterBvhPatch
{
    public uint NodeIndex;
    public uint NewPagePointer;
}

internal sealed class ClusterBvh
{
    internal const uint MaxNodes = 262144;
    internal const uint NodeBytes = 64;
    internal const ulong BufferBytes = (ulong)MaxNodes * NodeBytes;

    private readonly FlatDictionary<uint, int> _depths = new();
    private readonly UploadPack _uploads = new();
    private uint _count;

    public FlatDictionary<string, uint> Roots { get; } = new();

    public uint Add(
        string meshId,
        ReadOnlySpan<byte> data,
        uint firstPage,
        MeshPages pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (string.IsNullOrWhiteSpace(meshId))
            throw new ArgumentException("Mesh id must not be empty.", nameof(meshId));

        int nodeCount = data.Length / 64;
        if (nodeCount <= 0)
            return uint.MaxValue;

        uint firstNode = _count;
        if (firstNode > MaxNodes || (ulong)firstNode + (ulong)nodeCount > MaxNodes)
        {
            throw new InvalidOperationException(
                $"Global BVH node capacity exceeded: current={firstNode}, incoming={nodeCount}, max={MaxNodes}.");
        }

        ReadOnlySpan<ClusterBVHNode> nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(data);
        ReadOnlySpan<ClusterBVHNode> source = nodes[..nodeCount];
        int depth = Depth(source);
        ClusterBVHNode[] patched = new ClusterBVHNode[nodeCount];
        source.CopyTo(patched);
        PatchNodes(patched, firstNode, firstPage, pages);

        Upload(firstNode * NodeBytes, patched);
        _count += checked((uint)nodeCount);

        uint root = firstNode + checked((uint)nodeCount) - 1;
        Roots[meshId] = root;
        _depths[root] = depth;
        return root;
    }

    public bool TryDepth(uint root, out int depth)
        => _depths.TryGetValue(root, out depth);

    public IReadOnlyList<UploadItem> TakeUploads()
        => _uploads.Take();

    private static void PatchNodes(
        ClusterBVHNode[] nodes,
        uint firstNode,
        uint firstPage,
        MeshPages pages)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].NodeType == 0)
            {
                nodes[i].ChildPointer += firstNode;
                continue;
            }

            uint pageId = nodes[i].ChildPointer + firstPage;
            uint nodeId = firstNode + checked((uint)i);
            pages.AddLeaf(pageId, nodeId);
            nodes[i].ChildPointer = pages.Offset(pageId);
        }
    }

    private static int Depth(ReadOnlySpan<ClusterBVHNode> nodes)
    {
        if (nodes.IsEmpty)
            return 0;

        int[] depths = new int[nodes.Length];
        bool[] visiting = new bool[nodes.Length];
        return DepthAt(nodes, nodes.Length - 1, depths, visiting);
    }

    private static int DepthAt(
        ReadOnlySpan<ClusterBVHNode> nodes,
        int node,
        int[] depths,
        bool[] visiting)
    {
        if ((uint)node >= (uint)nodes.Length)
            throw new InvalidOperationException($"BVH node index {node} is outside the local node range.");

        if (depths[node] != 0)
            return depths[node];

        if (visiting[node])
            throw new InvalidOperationException($"BVH node {node} participates in a cycle.");

        visiting[node] = true;
        ClusterBVHNode value = nodes[node];
        int depth;
        if (value.NodeType == 1)
        {
            depth = 1;
        }
        else if (value.NodeType == 0)
        {
            uint firstChild = value.ChildPointer;
            uint childCount = value.ChildCount;
            if (childCount == 0)
                throw new InvalidOperationException($"BVH internal node {node} has no children.");
            uint length = checked((uint)nodes.Length);
            if (firstChild >= length || childCount > length - firstChild)
                throw new InvalidOperationException($"BVH internal node {node} child range is outside the local node range.");

            int maxDepth = 0;
            for (uint child = 0; child < childCount; child++)
                maxDepth = Math.Max(maxDepth, DepthAt(nodes, checked((int)(firstChild + child)), depths, visiting));
            depth = checked(maxDepth + 1);
        }
        else
        {
            throw new InvalidOperationException($"BVH node {node} has unsupported node type {value.NodeType}.");
        }

        visiting[node] = false;
        depths[node] = depth;
        return depth;
    }

    private void Upload(uint offset, ReadOnlySpan<ClusterBVHNode> nodes)
        => _uploads.Copy(offset, MemoryMarshal.AsBytes(nodes));
}
