using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data;

[StructLayout(LayoutKind.Sequential)]
public struct GpuInstanceHeader
{
    public uint BVHRootIndex;
    public uint MaterialID;
    public uint MetadataOffset;
    public uint MetadataCount;

    public uint RasterBinKey;      // Material-driven rasterization bin key
    public float BoundsExpansion;   // Conservative AABB expansion (world space)
    public uint Pad2;
    public uint Pad3;

    public const int SizeInBytes = 32;
}
