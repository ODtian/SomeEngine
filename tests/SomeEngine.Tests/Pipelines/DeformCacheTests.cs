using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests.Pipelines;

public class DeformCacheTests
{
    // ── ComputeVisibleArgs ──

    [Fact]
    public void ComputeVisibleArgs_ZeroClusters_ReturnsZeroDispatch()
    {
        var (x, y) = DeformDispatchCalc.ComputeVisibleArgs(0, 0);
        Assert.Equal(0u, x);
        Assert.Equal(0u, y);
    }

    [Fact]
    public void ComputeVisibleArgs_SmallCount_SingleRow()
    {
        var (x, y) = DeformDispatchCalc.ComputeVisibleArgs(100, 50);
        Assert.Equal(150u, x);
        Assert.Equal(1u, y);
    }

    [Fact]
    public void ComputeVisibleArgs_ExactlyMaxX_SingleRow()
    {
        var (x, y) = DeformDispatchCalc.ComputeVisibleArgs(65535, 0);
        Assert.Equal(65535u, x);
        Assert.Equal(1u, y);
    }

    [Fact]
    public void ComputeVisibleArgs_OverMaxX_WrapsToMultipleRows()
    {
        // 65536 total → should wrap to 2 rows
        var (x, y) = DeformDispatchCalc.ComputeVisibleArgs(65536, 0);
        Assert.Equal(65535u, x);
        Assert.Equal(2u, y);
    }

    [Fact]
    public void ComputeVisibleArgs_LargeCount_CorrectRowCount()
    {
        // 200000 total → ceil(200000/65535) = 4 rows
        var (x, y) = DeformDispatchCalc.ComputeVisibleArgs(100000, 100000);
        Assert.Equal(65535u, x);
        Assert.Equal(4u, y);
    }

    [Fact]
    public void ComputeVisibleArgs_OnlySW_Works()
    {
        var (x, y) = DeformDispatchCalc.ComputeVisibleArgs(42, 0);
        Assert.Equal(42u, x);
        Assert.Equal(1u, y);
    }

    [Fact]
    public void ComputeVisibleArgs_OnlyHW_Works()
    {
        var (x, y) = DeformDispatchCalc.ComputeVisibleArgs(0, 77);
        Assert.Equal(77u, x);
        Assert.Equal(1u, y);
    }

    // ── DecodeVisibleIdx ──

    [Fact]
    public void DecodeVisibleIdx_OutOfRange_ReturnsFalse()
    {
        var (_, valid) = DeformDispatchCalc.DecodeVisibleIdx(
            logicalIdx: 10, swCount: 5, hwCount: 3,
            swBase: 0, maxVisibleClusters: 1024, hwReadOffset: 0);
        Assert.False(valid);
    }

    [Fact]
    public void DecodeVisibleIdx_SWCluster_MapsToSwBase()
    {
        // SW: indices 0..4, stored at swBase=100
        var (idx, valid) = DeformDispatchCalc.DecodeVisibleIdx(
            logicalIdx: 2, swCount: 5, hwCount: 3,
            swBase: 100, maxVisibleClusters: 1024, hwReadOffset: 0);
        Assert.True(valid);
        Assert.Equal(102u, idx); // swBase + logicalIdx
    }

    [Fact]
    public void DecodeVisibleIdx_HWCluster_MapsToTailEnd()
    {
        // HW: indices 5..7 (logical), stored at tail of buffer
        // hwBase = maxVisible - hwReadOffset - hwCount = 1024 - 0 - 3 = 1021
        // physical = hwBase + (logicalIdx - swCount) = 1021 + (5 - 5) = 1021
        var (idx, valid) = DeformDispatchCalc.DecodeVisibleIdx(
            logicalIdx: 5, swCount: 5, hwCount: 3,
            swBase: 100, maxVisibleClusters: 1024, hwReadOffset: 0);
        Assert.True(valid);
        Assert.Equal(1021u, idx);
    }

    [Fact]
    public void DecodeVisibleIdx_HWWithReadOffset_ShiftsTail()
    {
        // hwBase = 1024 - 10 - 3 = 1011
        var (idx, valid) = DeformDispatchCalc.DecodeVisibleIdx(
            logicalIdx: 5, swCount: 5, hwCount: 3,
            swBase: 0, maxVisibleClusters: 1024, hwReadOffset: 10);
        Assert.True(valid);
        Assert.Equal(1011u, idx);
    }

    [Fact]
    public void DecodeVisibleIdx_BoundaryLastSW()
    {
        // Last SW cluster: logical index = swCount - 1
        var (idx, valid) = DeformDispatchCalc.DecodeVisibleIdx(
            logicalIdx: 4, swCount: 5, hwCount: 3,
            swBase: 200, maxVisibleClusters: 1024, hwReadOffset: 0);
        Assert.True(valid);
        Assert.Equal(204u, idx);
    }

    [Fact]
    public void DecodeVisibleIdx_BoundaryFirstHW()
    {
        // First HW cluster: logical index = swCount
        var (idx, valid) = DeformDispatchCalc.DecodeVisibleIdx(
            logicalIdx: 5, swCount: 5, hwCount: 3,
            swBase: 200, maxVisibleClusters: 1024, hwReadOffset: 0);
        Assert.True(valid);
        // hwBase = 1024 - 0 - 3 = 1021
        Assert.Equal(1021u, idx);
    }

    [Fact]
    public void DecodeVisibleIdx_BoundaryLastHW()
    {
        // Last HW cluster: logical index = swCount + hwCount - 1
        var (idx, valid) = DeformDispatchCalc.DecodeVisibleIdx(
            logicalIdx: 7, swCount: 5, hwCount: 3,
            swBase: 200, maxVisibleClusters: 1024, hwReadOffset: 0);
        Assert.True(valid);
        // hwBase = 1021, offset = 7 - 5 = 2 → 1023
        Assert.Equal(1023u, idx);
    }

    // ── FlatIndexFromGroupID ──

    [Fact]
    public void FlatIndex_SingleRow_EqualsGroupX()
    {
        Assert.Equal(42u, DeformDispatchCalc.FlatIndexFromGroupID(42, 0));
    }

    [Fact]
    public void FlatIndex_MultiRow_WrapsCorrectly()
    {
        // groupID = (1, 1) → 1 + 1 * 65535 = 65536
        Assert.Equal(65536u, DeformDispatchCalc.FlatIndexFromGroupID(1, 1));
    }

    [Fact]
    public void FlatIndex_RoundTrip_WithComputeVisibleArgs()
    {
        // 确认 ComputeVisibleArgs 产生的 dispatch 维度能覆盖所有 cluster
        uint total = 131070u; // 2 * 65535
        var (dispatchX, dispatchY) = DeformDispatchCalc.ComputeVisibleArgs(total, 0);

        // 最后一个有效 cluster 的 flat index
        uint lastGroupX = dispatchX - 1;
        uint lastGroupY = dispatchY - 1;
        uint lastFlat = DeformDispatchCalc.FlatIndexFromGroupID(lastGroupX, lastGroupY);
        Assert.True(lastFlat >= total - 1, "Dispatch dimensions must cover all clusters");
    }

    // ── CacheAllocCounter / CacheOffsets ──

    [Fact]
    public void AllocateCacheOffsets_AssignsSequentialBaseOffsets()
    {
        uint[] visibleIndices = [5u, 1021u, 7u];
        uint[] vertexCounts = [10u, 4u, 8u];
        uint[] cacheOffsets = new uint[2048];

        uint totalAllocated = DeformDispatchCalc.AllocateCacheOffsets(
            visibleIndices, vertexCounts, cacheOffsets);

        Assert.Equal(0u, cacheOffsets[5]);
        Assert.Equal(10u, cacheOffsets[1021]);
        Assert.Equal(14u, cacheOffsets[7]);
        Assert.Equal(22u, totalAllocated);
    }

    [Fact]
    public void AllocateCacheOffsets_LengthMismatch_Throws()
    {
        uint[] visibleIndices = [1u, 2u];
        uint[] vertexCounts = [3u];
        uint[] cacheOffsets = new uint[16];

        Assert.Throws<ArgumentException>(() =>
            DeformDispatchCalc.AllocateCacheOffsets(visibleIndices, vertexCounts, cacheOffsets));
    }

    [Fact]
    public void AllocateCacheOffsets_OutOfRangeVisibleIndex_Throws()
    {
        uint[] visibleIndices = [99u];
        uint[] vertexCounts = [3u];
        uint[] cacheOffsets = new uint[8];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeformDispatchCalc.AllocateCacheOffsets(visibleIndices, vertexCounts, cacheOffsets));
    }

    // ── Cached path boundary ──

    [Fact]
    public void CanUseCachedPath_ExactFit_ReturnsTrue()
    {
        Assert.True(DeformDispatchCalc.CanUseCachedPath(cacheBaseVert: 60, vertexCount: 4, maxDeformVertices: 64));
    }

    [Fact]
    public void CanUseCachedPath_Overflow_ReturnsFalse()
    {
        Assert.False(DeformDispatchCalc.CanUseCachedPath(cacheBaseVert: 60, vertexCount: 5, maxDeformVertices: 64));
    }

    [Fact]
    public void ComputeCacheByteAddress_MatchesShaderFormula()
    {
        uint byteAddr = DeformDispatchCalc.ComputeCacheByteAddress(
            cacheBaseVert: 10, localVertIdx: 3, stride: 24);

        Assert.Equal(312u, byteAddr); // (10 + 3) * 24
    }
}
