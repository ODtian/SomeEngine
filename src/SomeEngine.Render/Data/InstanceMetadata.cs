using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data;

[StructLayout(LayoutKind.Sequential)]
public struct GpuInstanceHeader
{
    public uint BVHRootIndex;
    public uint MaterialSlotOffset; // Index into MaterialSlotBuffer
    public uint MetadataOffset;
    public uint MetadataCount;

    public float BoundsExpansion;   // Conservative AABB expansion (world space)
    public uint Pad1;
    public uint Pad2;
    public uint Pad3;

    public const int SizeInBytes = 32;
}
