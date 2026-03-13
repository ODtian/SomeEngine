using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

/// <summary>
/// Page header (44 bytes). Contains stream offsets and global quantization parameters.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MeshPageHeader
{
    public uint ClusterCount;        //  0
    public uint TotalVertexCount;    //  4
    public uint TotalTriangleCount;  //  8
    public float QuantOriginX;       // 12 (was PageSize)
    public uint ClustersOffset;      // 16
    public uint PositionsOffset;     // 20
    public uint AttributesOffset;    // 24
    public uint IndicesOffset;       // 28
    public float QuantOriginY;       // 32
    public float QuantOriginZ;       // 36
    public float QuantStep;          // 40

    public const int Size = 44;
    public const int MaxPageSize = 131072;
}
