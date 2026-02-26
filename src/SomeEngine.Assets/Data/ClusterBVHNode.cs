using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct ClusterBVHNode
{
    public Vector4 BoundMin; // w is padding
    public Vector4 BoundMax; // w is padding
    public Vector4 LODSphere;
    public float LODError;
    public uint ChildPointer;
    public uint ChildCount;
    public uint NodeType; // 0 = Internal, 1 = Leaf

    public const uint PageFaultMarker = 0xFFFFFFFF;

    // Helper for encoding leaf data node counts
    public void SetLeafData(uint clusterStart, uint clusterCount)
    {
        ChildCount = (clusterCount << 12) | (clusterStart & 0xFFF);
    }

    public readonly void GetLeafData(out uint clusterStart, out uint clusterCount)
    {
        clusterStart = ChildCount & 0xFFF;
        clusterCount = ChildCount >> 12;
    }
}
