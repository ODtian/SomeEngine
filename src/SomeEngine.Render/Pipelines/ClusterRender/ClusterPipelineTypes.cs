using System.Numerics;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

// ────────────────────────────────────────────────────────────
//  Output types (Stage 产出 — positional records 支持解构)
// ────────────────────────────────────────────────────────────

/// <summary>
/// Upload Stage 产出：全局共享的 GPU 资源 Handle。
/// 所有 View/Pass 共享同一份。
/// </summary>
public readonly record struct ClusterGlobalResources(
    RenderGraphHandle GlobalBVH,
    RenderGraphHandle PageHeap,
    RenderGraphHandle GlobalTransform,
    RenderGraphHandle GlobalInstanceHeader,
    RenderGraphHandle InstanceDataHeap
);

/// <summary>
/// BVH Traverse Stage 产出：候选 Cluster 列表。
/// </summary>
public readonly record struct ClusterTraverseOutput(
    RenderGraphHandle CandidateClusters,
    RenderGraphHandle CandidateArgs,
    RenderGraphHandle CandidateCount,
    RenderGraphHandle CullingUniforms
);

/// <summary>
/// Cull Stage 产出：可见 Cluster 列表 + 间接绘制参数。
/// </summary>
public readonly record struct ClusterCullOutput(
    RenderGraphHandle VisibleClusters,
    RenderGraphHandle DrawArgs,
    RenderGraphHandle Phase2DrawArgs,
    RenderGraphHandle Phase2CandidateCount,
    RenderGraphHandle Phase2CandidateClusters,
    RenderGraphHandle Phase2CandidateArgs,
    RenderGraphHandle DebugHiZOutput
);

/// <summary>
/// Raster Binning Stage 产出：按 RasterBinKey 分组的 Cluster 索引。
/// </summary>
public readonly record struct ClusterRasterBinOutput(
    RenderGraphHandle BinnedClusterIndex,
    RenderGraphHandle BinnedDrawArgs,
    RenderGraphHandle RasterBinMeta,
    RenderGraphHandle BinningDispatchArgs
);

/// <summary>
/// Draw/Rasterize Stage 产出：VisBuffer 和深度。
/// </summary>
public readonly record struct ClusterRasterOutput(
    RenderGraphHandle VisBuffer,
    RenderGraphHandle DepthTarget
);

/// <summary>
/// Shade Binning Stage 产出：按 MaterialID 分组的像素坐标。
/// </summary>
public readonly record struct ClusterShadeBinOutput(
    RenderGraphHandle PixelCoordBuffer,
    RenderGraphHandle BinOffsets,
    RenderGraphHandle BinCounts,
    RenderGraphHandle BinIndirectArgs
);

/// <summary>
/// Material Shade Stage 产出：着色后颜色。
/// </summary>
public readonly record struct ClusterShadeOutput(
    RenderGraphHandle OutputColor
);

// ────────────────────────────────────────────────────────────
//  Camera data (打包相机参数，减少 stage 传参数量)
// ────────────────────────────────────────────────────────────

/// <summary>
/// 相机参数打包。所有 Stage 通过此结构获取相机信息，不再单独传递多个标量。
/// </summary>
public readonly record struct ClusterCameraData
{
    public Matrix4x4 View { get; init; }
    public Matrix4x4 Proj { get; init; }
    public Vector3 CameraPos { get; init; }
    public float LodThreshold { get; init; }
    public float LodScale { get; init; }
    public int ForcedLODLevel { get; init; }
    public uint ScreenWidth { get; init; }
    public uint ScreenHeight { get; init; }

    /// <summary>HiZ 跨帧需要的前一帧矩阵。</summary>
    public Matrix4x4 PrevViewProj { get; init; }
    public Matrix4x4 PrevView { get; init; }
    public Matrix4x4 PrevProj { get; init; }

    public static ClusterCameraData Default(
        Matrix4x4 view, Matrix4x4 proj, Vector3 cameraPos,
        uint screenWidth, uint screenHeight
    ) => new()
    {
        View = view,
        Proj = proj,
        CameraPos = cameraPos,
        LodThreshold = 1.0f,
        LodScale = 500.0f,
        ForcedLODLevel = -1,
        ScreenWidth = screenWidth,
        ScreenHeight = screenHeight,
        PrevViewProj = Matrix4x4.Identity,
        PrevView = Matrix4x4.Identity,
        PrevProj = Matrix4x4.Identity,
    };
}

// ────────────────────────────────────────────────────────────
//  Config types (init 属性 + 预设工厂 + with 支持)
// ────────────────────────────────────────────────────────────

/// <summary>
/// Upload Stage 配置。
/// </summary>
public readonly record struct ClusterUploadConfig
{
    public Matrix4x4 View { get; init; }
    public Matrix4x4 Proj { get; init; }
    public Vector3 CameraPos { get; init; }
    public float LodThreshold { get; init; }
    public float LodScale { get; init; }
    public int ForcedLODLevel { get; init; }
    public bool BypassCulling { get; init; }
    public uint DebugMode { get; init; }
    public uint ScreenWidth { get; init; }
    public uint ScreenHeight { get; init; }

    /// <summary>HiZ 相关参数（由 CullStage 跨帧状态提供）。</summary>
    public Matrix4x4 PrevViewProj { get; init; }
    public Matrix4x4 PrevView { get; init; }
    public Matrix4x4 PrevProj { get; init; }
    public bool HasPrevHistory { get; init; }
    public uint HiZMipCount { get; init; }
    public Vector2 HiZInvSize { get; init; }

    /// <summary>是否 dump 当前帧调试数据。</summary>
    public bool DumpNextFrame { get; init; }
    public bool DebugShowHiZAABBs { get; init; }
    public static ClusterUploadConfig Default(
        Matrix4x4 view, Matrix4x4 proj, Vector3 cameraPos,
        uint screenWidth, uint screenHeight
    ) => new()
    {
        View = view,
        Proj = proj,
        CameraPos = cameraPos,
        LodThreshold = 1.0f,
        LodScale = 500.0f,
        ForcedLODLevel = -1,
        ScreenWidth = screenWidth,
        ScreenHeight = screenHeight,
        PrevViewProj = Matrix4x4.Identity,
        PrevView = Matrix4x4.Identity,
        PrevProj = Matrix4x4.Identity,
    };
}

/// <summary>
/// BVH Traverse Stage 配置。
/// </summary>
public readonly record struct ClusterTraverseConfig
{
    /// <summary>BVH 最大遍历深度。</summary>
    public int MaxDepth { get; init; }

    public static ClusterTraverseConfig Default() => new()
    {
        MaxDepth = 12,
    };
}

/// <summary>
/// Cull Stage 配置。
/// </summary>
public readonly record struct ClusterCullConfig
{
    public HiZDebugMode HiZMode { get; init; }

    /// <summary>前一帧 HiZ 纹理（Phase1 遮挡剔除用）。Invalid = 不使用。</summary>
    public RenderGraphHandle HiZTexture { get; init; }
    /// <summary>是否有有效的前一帧 HiZ 历史。</summary>
    public bool HasPrevHistory { get; init; }
    /// <summary>HiZ mip 数量（用于 uniform 构建）。</summary>
    public uint HiZMipCount { get; init; }
    /// <summary>HiZ 纹理逆尺寸。</summary>
    public Vector2 HiZInvSize { get; init; }
    /// <summary>是否输出 HiZ debug AABB 数据。</summary>
    public bool DebugShowHiZAABBs { get; init; }
    /// <summary>是否 dump 当前帧数据。</summary>
    public bool DumpNextFrame { get; init; }

    public static ClusterCullConfig Default() => new()
    {
        HiZMode = HiZDebugMode.Full2Phase,
    };
}



/// <summary>
/// Draw/Rasterize Stage 配置。
/// </summary>
public readonly record struct ClusterDrawConfig
{
    /// <summary>
    /// Draw request 元数据缓冲。当前架构下用于传入可见 cluster 读取基址等扩展信息。
    /// Invalid = Stage 内部回退为 0 偏移缓冲。
    /// </summary>
    public RenderGraphHandle VisibleClusterMeta { get; init; }

    /// <summary>是否写深度。false = 透明模式（只读深度测试）。</summary>
    public bool DepthWrite { get; init; }

    /// <summary>Draw 前是否 clear 目标。</summary>
    public bool ClearTargets { get; init; }

    /// <summary>
    /// 绘制哪个 Raster Bin。-1 = 所有 bin（使用未 binned 的 DrawArgs），≥0 = 特定 bin。
    /// </summary>
    public int BinIndex { get; init; }

    /// <summary>是否使用 VisBuffer 模式（R32_UInt）。false = forward shading color RT。</summary>
    public bool UseVisBuffer { get; init; }

    /// <summary>Debug 模式标志。</summary>
    public bool Wireframe { get; init; }
    public bool Overdraw { get; init; }
    public ClusterDebugMode DebugMode { get; init; }

    /// <summary>资源命名前缀（用于区分多个 Draw 实例的 RG 资源名）。</summary>
    public string? Tag { get; init; }

    public static ClusterDrawConfig Opaque() => new()
    {
        DepthWrite = true,
        ClearTargets = true,
        BinIndex = -1,
        UseVisBuffer = true,
    };
}

/// <summary>
/// Shade Binning Stage 配置。
/// </summary>
public readonly record struct ClusterShadeBinConfig
{
    public static ClusterShadeBinConfig Default() => new();
}

/// <summary>
/// Material Shade Stage 配置。
/// </summary>
public readonly record struct ClusterShadeConfig
{
    /// <summary>输出目标。Invalid = 自动创建 ResolveTarget。</summary>
    public RenderGraphHandle OutputColor { get; init; }

    /// <summary>是否将结果 copy 到 BackBuffer。</summary>
    public bool CopyToBackBuffer { get; init; }

    /// <summary>BackBuffer handle（仅 CopyToBackBuffer=true 时使用）。</summary>
    public RenderGraphHandle BackBuffer { get; init; }

    /// <summary>是否为 resolve-only debug 模式（ClusterID/LOD 可视化）。</summary>
    public bool UseResolveDebug { get; init; }
    public uint DebugMode { get; init; }

    // ─── Shade Uniform 参数 ───
    public Matrix4x4 ViewProj { get; init; }
    public Matrix4x4 View { get; init; }
    public uint PageTableSize { get; init; }
    public Vector3 QuantOrigin { get; init; }
    public float QuantStep { get; init; }
    public Vector3 LightDir { get; init; }
    public float LightIntensity { get; init; }
    public Vector3 AmbientColor { get; init; }
    public Vector3 CameraPos { get; init; }

    public static ClusterShadeConfig Default(RenderGraphHandle backBuffer) => new()
    {
        CopyToBackBuffer = true,
        BackBuffer = backBuffer,
        LightDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.3f)),
        LightIntensity = 1.0f,
        AmbientColor = new Vector3(0.15f, 0.15f, 0.15f),
    };
}
