using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data;

[StructLayout(LayoutKind.Sequential)]
public struct ClusterInfo
{
    public Vector3 Center;
    public float Radius;

    public Vector3 ConeAxis;
    public float ConeCutoff;

    public uint IndexStart;
    public uint IndexCount;
    public uint Padding0;
    public uint Padding1;
}
