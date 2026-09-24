using SomeEngine.Render.Graph;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct ClusterTraverseOutput(
    RenderGraphHandle CandidateClusters,
    RenderGraphHandle CandidateCount,
    RenderGraphHandle CandidateArgs,
    RenderGraphHandle CullingUniforms,
    RenderGraphHandle IndirectDrawArgs);

internal readonly record struct ClusterCullOutput(
    RenderGraphHandle VisibleClusters,
    RenderGraphHandle DrawArgs,
    RenderGraphHandle Phase2DrawArgs,
    RenderGraphHandle Phase2CandidateClusters,
    RenderGraphHandle Phase2CandidateCount,
    RenderGraphHandle Phase2CandidateArgs);

internal readonly record struct RasterBinFrame(
    RenderGraphHandle BinnedClusterIndex,
    RenderGraphHandle BinnedDrawArgs,
    RenderGraphHandle BinnedHWDrawArgs,
    RenderGraphHandle RasterBinMeta,
    RenderGraphHandle BinningDispatchArgs,
    RenderGraphHandle BinnedSWDispatchArgs)
{
    public uint DrawBinCount { get; init; }
}

internal readonly record struct DeformBinFrame(
    RenderGraphHandle DeformBinnedClusterIndex,
    RenderGraphHandle DeformBinMeta,
    RenderGraphHandle PreDeformDispatchArgs);

internal readonly record struct ClusterRasterOutput(
    RenderGraphHandle VisBuffer,
    RenderGraphHandle DepthTarget,
    RenderGraphHandle RasterDepth);

internal readonly record struct ShadeBinFrame(
    RenderGraphHandle PixelCoordBuffer,
    RenderGraphHandle BinOffsets,
    RenderGraphHandle BinCounts,
    RenderGraphHandle BinIndirectArgs);

internal readonly record struct ClusterShadeOutput(
    RenderGraphHandle OutputColor,
    RenderGraphHandle MotionVectors)
{
    public ClusterShadeOutput(RenderGraphHandle outputColor)
        : this(outputColor, RenderGraphHandle.Invalid)
    {
    }
}
