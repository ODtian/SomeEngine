using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data;

[StructLayout(LayoutKind.Sequential)]
public struct GpuInstanceHeader
{
    public uint BVHRootIndex;
    public uint MaterialID;
    public uint MetadataOffset;
    public uint MetadataCount;

    public const int SizeInBytes = 16;
}
