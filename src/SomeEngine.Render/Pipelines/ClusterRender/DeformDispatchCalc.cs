namespace SomeEngine.Render.Pipelines;

/// <summary>
/// DeformCache 的纯 CPU 辅助逻辑，镜像自 cluster_deform.slang。
/// </summary>
public static class DeformDispatchCalc
{
    /// <summary>
    /// 镜像 CSDeformPrepareVisibleArgs：从 SW+HW 可见数量计算 dispatch 参数。
    /// <para>
    /// GPU 的 DispatchCompute 最大 X 维度为 65535，超出时折行到 Y 维度。
    /// </para>
    /// </summary>
    public static (uint DispatchX, uint DispatchY) ComputeVisibleArgs(uint swCount, uint hwCount)
    {
        uint total = swCount + hwCount;
        uint dispatchX = Math.Min(total, 65535u);
        uint dispatchY = total > 0 ? (total + 65534u) / 65535u : 0u;
        return (dispatchX, dispatchY);
    }

    /// <summary>
    /// 镜像 DecodeVisibleIdx：将线性 logical index 映射到 VisibleClusters 数组中的物理索引。
    /// <para>
    /// SW cluster 从 swBase 开始正向排列，HW cluster 从 maxVisible 尾部反向排列。
    /// </para>
    /// </summary>
    /// <param name="logicalIdx">线性索引（0-based，跨 SW+HW）</param>
    /// <param name="swCount">SW 可见 cluster 数量</param>
    /// <param name="hwCount">HW 可见 cluster 数量</param>
    /// <param name="swBase">readOffset.SWCount — SW 段在 VisibleClusters 中的起始偏移</param>
    /// <param name="maxVisibleClusters">VisibleClusters 缓冲区总容量</param>
    /// <param name="hwReadOffset">readOffset.HWCount — HW 段已消费的偏移</param>
    /// <returns>(物理索引, 是否有效)</returns>
    public static (uint VisibleIndex, bool Valid) DecodeVisibleIdx(
        uint logicalIdx, uint swCount, uint hwCount,
        uint swBase, uint maxVisibleClusters, uint hwReadOffset)
    {
        uint total = swCount + hwCount;
        if (logicalIdx >= total)
            return (0, false);

        uint hwBase = maxVisibleClusters - hwReadOffset - hwCount;
        bool isSW = logicalIdx < swCount;
        uint visibleIndex = isSW
            ? swBase + logicalIdx
            : hwBase + logicalIdx - swCount;
        return (visibleIndex, true);
    }

    /// <summary>
    /// 镜像 CSDeformInitVisible 中的 CacheAllocCounter.InterlockedAdd + CacheOffsets 写回。
    /// 返回总分配顶点数。
    /// </summary>
    public static uint AllocateCacheOffsets(
        ReadOnlySpan<uint> visibleIndices,
        ReadOnlySpan<uint> vertexCounts,
        Span<uint> cacheOffsets)
    {
        if (visibleIndices.Length != vertexCounts.Length)
            throw new ArgumentException("visibleIndices and vertexCounts must have the same length.");

        uint allocCounter = 0;
        for (int i = 0; i < visibleIndices.Length; i++)
        {
            uint visibleIndex = visibleIndices[i];
            if (visibleIndex >= cacheOffsets.Length)
                throw new ArgumentOutOfRangeException(nameof(visibleIndices), "visibleIndex exceeds cacheOffsets length.");

            cacheOffsets[(int)visibleIndex] = allocCounter;
            allocCounter += vertexCounts[i];
        }

        return allocCounter;
    }

    /// <summary>
    /// 镜像 shader 中的容量边界：如果 cacheBaseVert + vertexCount 超过 MaxDeformVertices，
    /// 则 cached path 不可用，应回退到 inline path。
    /// </summary>
    public static bool CanUseCachedPath(uint cacheBaseVert, uint vertexCount, uint maxDeformVertices)
    {
        return cacheBaseVert + vertexCount <= maxDeformVertices;
    }

    /// <summary>
    /// 镜像 cached path 的字节寻址：byteAddr = (cacheBaseVert + localVertIdx) * stride。
    /// </summary>
    public static uint ComputeCacheByteAddress(uint cacheBaseVert, uint localVertIdx, uint stride)
    {
        return (cacheBaseVert + localVertIdx) * stride;
    }

    /// <summary>
    /// 从 2D group ID 恢复线性 flat index（同 shader 中 groupID.x + groupID.y * 65535）。
    /// </summary>
    public static uint FlatIndexFromGroupID(uint groupX, uint groupY)
    {
        return groupX + groupY * 65535u;
    }
}
