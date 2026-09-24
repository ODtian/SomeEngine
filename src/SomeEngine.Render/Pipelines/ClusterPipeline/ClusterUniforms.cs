using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Pipelines;

[StructLayout(LayoutKind.Sequential)]
internal struct CullingUniforms
{
    public Matrix4x4 ViewProj;
    public Vector3 CameraPos;
    public float LodThreshold;
    public float LodScale;
    public uint MaxQueueNodes;
    public uint MaxCandidates;
    public uint MaxTraversalDepth;
    public int ForcedLODLevel;
    public uint InstanceCount;
    public uint DebugMode;
    public uint VisualiseBVH;
    public int DebugBVHDepth;
    public uint CurrentDepth;
    public uint Pad5;
    public uint Pad6;

    public Matrix4x4 PrevViewProj;
    public uint HasPrevHistory;
    public uint HiZMipCount;
    public Vector2 HiZInvSize;

    public Matrix4x4 View;
    public float P00;
    public float P11;
    public uint ScreenWidth;
    public uint ScreenHeight;

    public Matrix4x4 PrevView;
    public float PrevP00;
    public float PrevP11;
    public Vector2 Pad8;

    public static CullingUniforms Create(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        Vector3 cameraPos,
        float lodThreshold,
        float lodScale,
        int forcedLODLevel,
        uint instanceCount,
        bool bypassCulling,
        in Matrix4x4 prevViewProjT,
        bool hasPrevHistory,
        uint hizMipCount,
        Vector2 hizInvSize,
        in Matrix4x4 prevView,
        in Matrix4x4 prevProj,
        uint screenWidth = 0,
        uint screenHeight = 0)
        => new()
        {
            ViewProj = Matrix4x4.Transpose(view * proj),
            CameraPos = cameraPos,
            LodThreshold = lodThreshold,
            LodScale = lodScale,
            MaxQueueNodes = 4 * 1024 * 1024u,
            MaxCandidates = ClusterLimits.MaxDraws,
            MaxTraversalDepth = ClusterLimits.DefaultTraverseDepth,
            ForcedLODLevel = forcedLODLevel,
            InstanceCount = instanceCount,
            DebugMode = bypassCulling ? 1u : 0u,
            CurrentDepth = 0,
            PrevViewProj = Matrix4x4.Transpose(Matrix4x4.Transpose(prevViewProjT)),
            HasPrevHistory = hasPrevHistory ? 1u : 0u,
            HiZMipCount = hizMipCount,
            HiZInvSize = hizInvSize,
            View = Matrix4x4.Transpose(view),
            P00 = proj.M11,
            P11 = proj.M22,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            PrevView = Matrix4x4.Transpose(prevView),
            PrevP00 = prevProj.M11,
            PrevP11 = prevProj.M22,
        };
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrawUniforms
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;
    public uint PageTableSize;
    public uint DebugMode;
    public uint ScreenWidth;
    public uint ScreenHeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrawDispatchUniforms
{
    public uint DrawArgsByteOffset;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SwRasterUniforms
{
    public Matrix4x4 ViewProj;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint MaxBins;
    public uint DebugDump;
    public uint CurrentBin;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BinningUniforms
{
    public uint MaxBins;
    public uint SlotCapacity;
    public uint BinFieldIndex;
    public uint MaxVisibleClusters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ShadeBinUniforms
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint MaterialCount;
    public uint SlotCapacity;
    public uint BinFieldIndex;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ShadeUniforms
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 View;
    public Matrix4x4 PrevViewProj;
    public Matrix4x4 MotionViewProj;
    public Matrix4x4 PrevMotionViewProj;
    public uint PageTableSize;
    public uint DebugMode;
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint ShadingBin;
    public uint MaterialCount;
    public uint LightLayerMask;
    public uint Pad1;
    public Vector3 CameraPos;
    public float Pad2;
    public uint HasPreviousFrame;
    public uint WriteMotionVectors;
    public uint Pad3;
    public uint Pad4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LightCounts
{
    public uint DirectionalCount;
    public uint PointCount;
    public uint SpotCount;
    public uint Pad0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LightGridUniforms
{
    public uint LightGridTileSizeX;
    public uint LightGridTileSizeY;
    public uint LightGridTileCountX;
    public uint LightGridTileCountY;
    public Vector4 LightGridZParams;
    public uint DepthSliceCount;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}
