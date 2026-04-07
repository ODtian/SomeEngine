namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Cluster 管线共享的容量常量。
/// 所有 Stage / Pipeline / Feature 统一引用此处。
/// </summary>
public static class ClusterLimits
{
    /// <summary>单帧最大可见 cluster 数。</summary>
    public const uint MaxDraws = 2_500_000;
}
