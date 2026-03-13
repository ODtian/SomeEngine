using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

/// <summary>
/// Compressed GPU cluster (48 bytes).
/// Positions are decoded using global quantization: float(IntBase + localOffset) * QuantStep + QuantOrigin.
/// Center/Radius for culling are packed as u16 offsets from IntBase.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GPUCluster
{
    // 0: int3 IntBase (12 bytes) — Cluster integer base for vertex decode
    public int IntBaseX;
    public int IntBaseY;
    public int IntBaseZ;

    // 12: uint PackedCenterXY — CenterOffsetX:16 | CenterOffsetY:16
    public uint PackedCenterXY;

    // 16: float3 LODCenter (12 bytes)
    public Vector3 LODCenter;

    // 28: float LODRadius
    public float LODRadius;

    // 32: uint PackedCenterZRadius — CenterOffsetZ:16 | RadiusQuant:16
    public uint PackedCenterZRadius;

    // 36: ushort LODErrorHalf (float16)
    public ushort LODErrorHalf;

    // 38: ushort VertexStart
    public ushort VertexStart;

    // 40: ushort TriangleStart
    public ushort TriangleStart;

    // 42: short GroupId
    public short GroupId;

    // 44: uint PackedCounts — [VertexCount:8][TriangleCount:8][LODLevel:8][Pad:8]
    public uint PackedCounts;

    // Total: 48 bytes

    // Helper to pack CenterOffset and RadiusQuant
    public static uint PackU16Pair(ushort a, ushort b) => (uint)a | ((uint)b << 16);
    public static (ushort, ushort) UnpackU16Pair(uint packed) => ((ushort)(packed & 0xFFFF), (ushort)(packed >> 16));
}
