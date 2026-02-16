using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GPUCluster
{
    public Vector3 Center;
    public float Radius;
    public Vector3 LodCenter;
    public float LodRadius;
    public float LODError;
    public float ParentLODError;
    public uint VertexStart;
    public uint TriangleStart;
    public int GroupId;
    public int ParentGroupId;
    public byte VertexCount;
    public byte TriangleCount;
    public byte LODLevel;
    public byte _Pad1;
}
