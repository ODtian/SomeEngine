using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MeshPageHeader
{
    public uint ClusterCount;
    public uint TotalVertexCount;
    public uint TotalTriangleCount;
    public uint PageSize;
    public uint ClustersOffset;
    public uint PositionsOffset;
    public uint AttributesOffset;
    public uint IndicesOffset;

    public const int Size = 32;
    public const int MaxPageSize = 131072;
}
