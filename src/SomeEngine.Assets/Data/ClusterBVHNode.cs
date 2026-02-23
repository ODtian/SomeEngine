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
}
