namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Cluster 管线共享的容量常量。
/// 所有 Stage / Pipeline / Feature 统一引用此处。
/// </summary>
public static class ClusterLimits
{
    /// <summary>单帧最大可见 cluster 数。</summary>
    public const uint MaxDraws = 2_500_000;

    /// <summary>Raster bin 最大数量（按材质分组）。</summary>
    public const uint MaxBins = 16;

    /// <summary>每个 bin 最大 cluster 数。</summary>
    public const uint MaxClustersPerBin = MaxDraws / MaxBins;
}
