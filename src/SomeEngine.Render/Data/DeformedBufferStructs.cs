using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data;

[StructLayout(LayoutKind.Sequential)]
public struct DeformedClusterAlloc
{
    public uint PositionOffset;  // DeformedPositionBuffer offset (bytes)
    public uint NormalOffset;    // DeformedNormalBuffer offset (bytes)

    public const int SizeInBytes = 8;
}
