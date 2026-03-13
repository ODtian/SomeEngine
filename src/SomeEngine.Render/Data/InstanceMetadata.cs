using System.Runtime.InteropServices;

namespace SomeEngine.Render.Data;

[StructLayout(LayoutKind.Sequential)]
public struct GpuInstanceHeader
{
    public uint BVHRootIndex;
    public uint MaterialID;
    public uint MetadataOffset;
    public uint MetadataCount;

    // Programmable rasterization reserved fields (Phase 1-3)
    public uint DeformFlags;      // bit0: Skinned, bit1: WPO, bit2: Tessellation
    public float BoundsExpansion;  // Conservative AABB expansion (world space units)
    public uint BoneMatrixOffset;  // Offset into BoneMatrixBuffer
    public uint BoneCount;         // Number of bones

    public const int SizeInBytes = 32;
}
