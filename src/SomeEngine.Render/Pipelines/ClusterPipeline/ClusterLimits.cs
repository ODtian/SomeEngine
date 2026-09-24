namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Cluster 管线共享的容量常量。
/// 所有 Stage / Pipeline / Feature 统一引用此处。
/// </summary>
internal static class ClusterLimits
{
    /// <summary>Default BVH traversal depth for the cluster pass chain.</summary>
    public const int DefaultTraverseDepth = 12;

    /// <summary>单帧最大可见 cluster 数。</summary>
    public const uint MaxDraws = 2_500_000;

    /// <summary>Cluster payload encodes vertex count in the low 8 bits; current builder caps it at 64.</summary>
    public const uint MaxClusterVertices = 64;

    /// <summary>Current builder caps cluster triangle count at 124.</summary>
    public const uint MaxClusterTriangles = 124;

    /// <summary>Cluster payload encodes at most five fast VRB batches; remaining triangles emit 32-triangle slow batches.</summary>
    public const uint MaxEncodedVRBBatches = 5;

    /// <summary>Maximum uint2 bin entries emitted for one visible cluster by raster/deform binning.</summary>
    public const uint MaxBinnedEntriesPerCluster =
        MaxEncodedVRBBatches + ((MaxClusterTriangles - MaxEncodedVRBBatches + 31) / 32);

    /// <summary>Default arena budget for per-frame DeformCache payload bytes.</summary>
    public const ulong DefaultDeformBytes = 128UL * 1024UL * 1024UL;
}
