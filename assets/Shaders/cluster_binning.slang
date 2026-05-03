#include "cluster_common.slang"

// ─── Binning Uniforms ───
struct BinningUniforms
{
    uint MaxBins;
    uint SlotCapacity;         // MaterialSlotBuffer SOA capacity (even)
    uint BinFieldIndex;        // Field index for this binning pass in SOA
    uint MaxVisibleClusters;
};

ConstantBuffer<BinningUniforms> Uniforms;

// ─── Input ───
StructuredBuffer<uint4> VisibleClusters;

ByteAddressBuffer DrawArgs;                  // IndirectArgs: must stay raw, layout = CullDrawArgs
ByteAddressBuffer ClusterReadOffsetArgs;     // IndirectArgs: must stay raw, layout = CullDrawArgs
StructuredBuffer<uint> MaterialSlotBuffer;   // SOA layout, StructuredBuffer<uint>
ByteAddressBuffer PageHeap;                  // Cluster geometry pages (for reading VRBBatchInfo)

// ─── Output ───
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
RWStructuredBuffer<uint2> BinnedClusterIndexBuffer; // .x = visibleClusterIndex, .y = rangeStart<<16 | rangeEnd
RWByteAddressBuffer BinnedDrawArgs;          // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinnedHWDrawArgs;        // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinningDispatchArgs;     // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinnedSWDispatchArgs;    // Per-bin IndirectDispatchArgs: [0..MaxBins) = Total(SW+HW), [MaxBins..2*MaxBins) = SW only
RWStructuredBuffer<uint> ReserveCounters;    // [0] = total batch allocator, [1] = SW batch allocator

DispatchArgs MakeIndirectDispatchArgs(uint groupCount)
{
    uint dispatchX = min(groupCount, 65535u);
    uint dispatchY = max(1u, (groupCount + 65534u) / 65535u);
    return DispatchArgs(dispatchX, dispatchY, 1);
}

// ─── Helper: decode VRB batch count and compute total entries ───
struct VRBDecoded
{
    uint batchCount;     // number of fast batches (≤5)
    uint triCounts[5];   // tri count per fast batch
    uint fastTriTotal;   // total triangles covered by fast batches
    uint totalEntries;   // total bin entries to emit (fast + slow residual batches)
};

VRBDecoded DecodeVRB(uint vrbInfo, uint triangleCount)
{
    VRBDecoded d;
    d.batchCount = min(((vrbInfo >> 25) & 0x7) + 1, 5u);
    d.fastTriTotal = 0;

    for (uint i = 0; i < 5; i++)
    {
        if (i < d.batchCount)
        {
            d.triCounts[i] = ((vrbInfo >> (i * 5)) & 0x1F) + 1;
            d.fastTriTotal += d.triCounts[i];
        }
        else
        {
            d.triCounts[i] = 0;
        }
    }
    d.fastTriTotal = min(d.fastTriTotal, triangleCount);

    // Slow residual: fixed 32-tri windows for remaining triangles
    uint slowTris = (triangleCount > d.fastTriTotal) ? (triangleCount - d.fastTriTotal) : 0;
    uint slowBatches = (slowTris + 31) / 32;

    d.totalEntries = d.batchCount + slowBatches;
    return d;
}

// ─── Helper: read VRBBatchInfo and triangleCount from PageHeap ───
void ReadClusterVRB(uint4 entry, out uint vrbInfo, out uint triangleCount)
{
    uint pageOffset = entry.x;
    uint clusterID = entry.y;
    uint clustersOff = PageHeap.Load(pageOffset + 16); // header.ClustersOffset
    uint packedCounts = PageHeap.Load(pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES + 44);
    triangleCount = (packedCounts >> 8) & 0xFF;
    vrbInfo = PageHeap.Load(pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES + 60);
}

// ─── Helper: get raster bin key for an instance ───
uint ReadClusterMaterialBinKey(uint instanceID)
{
    uint slotOffset = GetInstanceMaterialSlotOffset(instanceID);
    return GetSlotField(MaterialSlotBuffer, slotOffset, Uniforms.BinFieldIndex, Uniforms.SlotCapacity);
}

// ─── Pass 0: CSBinningClear: clears BinCount ───
[numthreads(64, 1, 1)]
void CSBinningClear(uint3 dtid : SV_DispatchThreadID)
{
    uint binIdx = dtid.x;
    if (binIdx >= Uniforms.MaxBins) return;
    RasterBinMeta[binIdx].BinSWCount = 0;
    RasterBinMeta[binIdx].BinHWCount = 0;
    RasterBinMeta[binIdx].SWCursor = 0;
    RasterBinMeta[binIdx].HWCursor = 0;
}

// ─── Pass 1: CSBinningPrepare: compute dispatch args from GPU-side count ───
[numthreads(1, 1, 1)]
void CSBinningPrepare(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint visibleCount = args.SWCount + args.HWCount;
    uint groupCount = (visibleCount + 63) / 64;
    BinningDispatchArgs.Store<DispatchArgs>(0, DispatchArgs(groupCount, 1, 1));
}

void DecodeVisibleIndex(uint logicalIdx, out uint visibleIndex, out bool isSW)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    CullDrawArgs readOffset = ClusterReadOffsetArgs.Load<CullDrawArgs>(0);
    uint swCount = args.SWCount;
    uint hwCount = args.HWCount;
    uint swBase = readOffset.SWCount;
    uint hwBase = Uniforms.MaxVisibleClusters - readOffset.HWCount - hwCount;

    isSW = logicalIdx < swCount;
    visibleIndex = isSW ? (swBase + logicalIdx) : (hwBase + logicalIdx - swCount);
}

// ─── Pass 2: CSBinningCount: count per-batch entries per bin ───
// Both SW and HW paths emit per-VRB-batch entries with the instance bin key.
[numthreads(64, 1, 1)]
void CSBinningCount(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];

    uint vrbInfo, triangleCount;
    ReadClusterVRB(entry, vrbInfo, triangleCount);
    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);

    // Emit per-batch counts with the instance bin key.
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        uint batchBinKey = ReadClusterMaterialBinKey(entry.z);
        if (batchBinKey >= Uniforms.MaxBins) batchBinKey = 0;
        uint unused;
        if (isSW)
            InterlockedAdd(RasterBinMeta[batchBinKey].BinSWCount, 1, unused);
        else
            InterlockedAdd(RasterBinMeta[batchBinKey].BinHWCount, 1, unused);
        triStart += vrb.triCounts[b];
    }
    // Slow residual batches (fixed 32-tri windows)
    while (triStart < triangleCount)
    {
        uint batchBinKey = ReadClusterMaterialBinKey(entry.z);
        if (batchBinKey >= Uniforms.MaxBins) batchBinKey = 0;
        uint unused;
        if (isSW)
            InterlockedAdd(RasterBinMeta[batchBinKey].BinSWCount, 1, unused);
        else
            InterlockedAdd(RasterBinMeta[batchBinKey].BinHWCount, 1, unused);
        triStart = min(triStart + 32, triangleCount);
    }
}

// ─── Pass 3: CSBinningReserve: Prefix Scan ───
#define RASTER_BIN_RESERVE_BLOCK_SIZE 128

groupshared uint RasterReserveTotalPrefix[RASTER_BIN_RESERVE_BLOCK_SIZE];
groupshared uint RasterReserveSWPrefix[RASTER_BIN_RESERVE_BLOCK_SIZE];
groupshared uint RasterReserveTotalBlockBase;
groupshared uint RasterReserveSWBlockBase;

[numthreads(RASTER_BIN_RESERVE_BLOCK_SIZE, 1, 1)]
void CSBinningReserve(uint3 tid : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint bin = tid.x;
    uint swCount = 0;
    uint hwCount = 0;

    if (bin < Uniforms.MaxBins)
    {
        swCount = RasterBinMeta[bin].BinSWCount;
        hwCount = RasterBinMeta[bin].BinHWCount;
    }

    uint count = swCount + hwCount;
    RasterReserveTotalPrefix[groupIndex] = count;
    RasterReserveSWPrefix[groupIndex] = swCount;
    GroupMemoryBarrierWithGroupSync();

    for (uint scanOffset = 1; scanOffset < RASTER_BIN_RESERVE_BLOCK_SIZE; scanOffset <<= 1)
    {
        uint totalAddend = groupIndex >= scanOffset ? RasterReserveTotalPrefix[groupIndex - scanOffset] : 0;
        uint swAddend = groupIndex >= scanOffset ? RasterReserveSWPrefix[groupIndex - scanOffset] : 0;
        GroupMemoryBarrierWithGroupSync();
        RasterReserveTotalPrefix[groupIndex] += totalAddend;
        RasterReserveSWPrefix[groupIndex] += swAddend;
        GroupMemoryBarrierWithGroupSync();
    }

    uint totalInclusive = RasterReserveTotalPrefix[groupIndex];
    uint swInclusive = RasterReserveSWPrefix[groupIndex];
    uint totalBlockSum = RasterReserveTotalPrefix[RASTER_BIN_RESERVE_BLOCK_SIZE - 1];
    uint swBlockSum = RasterReserveSWPrefix[RASTER_BIN_RESERVE_BLOCK_SIZE - 1];

    if (groupIndex == 0)
    {
        InterlockedAdd(ReserveCounters[0], totalBlockSum, RasterReserveTotalBlockBase);
        InterlockedAdd(ReserveCounters[1], swBlockSum, RasterReserveSWBlockBase);
    }
    GroupMemoryBarrierWithGroupSync();

    if (bin >= Uniforms.MaxBins) return;

    uint totalOffset = RasterReserveTotalBlockBase + totalInclusive - count;
    uint swOffset = RasterReserveSWBlockBase + swInclusive - swCount;

    RasterBinMeta[bin].ClusterOffset = totalOffset;
    RasterBinMeta[bin].BinCapacity = count;
    RasterBinMeta[bin].SWCursor = 0;
    RasterBinMeta[bin].HWCursor = 0;
    RasterBinMeta[bin].SWOffset = swOffset;

    BinnedDrawArgs.Store<DrawInstancedArgs>(bin * 16,
        DrawInstancedArgs(96, count, 0, totalOffset));

    BinnedHWDrawArgs.Store<DrawInstancedArgs>(bin * 16,
        DrawInstancedArgs(96, hwCount, 0, totalOffset + swCount));

    // Per-bin Total dispatch args (SW+HW, for deform: offset bin*12)
    BinnedSWDispatchArgs.Store<DispatchArgs>(bin * 12,
        MakeIndirectDispatchArgs(count));

    // Per-bin SW-only dispatch args (for SW raster: offset (MaxBins+bin)*12)
    BinnedSWDispatchArgs.Store<DispatchArgs>((Uniforms.MaxBins + bin) * 12,
        MakeIndirectDispatchArgs(swCount));
}

// ─── Pass 4: CSBinningScatter: scatter per-batch entries ───
// Both SW and HW paths scatter per-VRB-batch entries with the instance bin key.
[numthreads(64, 1, 1)]
void CSBinningScatter(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];

    uint vrbInfo, triangleCount;
    ReadClusterVRB(entry, vrbInfo, triangleCount);
    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);

    // Fast VRB batches
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        uint triEnd = triStart + vrb.triCounts[b];
        triEnd = min(triEnd, triangleCount);

        uint batchBinKey = ReadClusterMaterialBinKey(entry.z);
        if (batchBinKey >= Uniforms.MaxBins) batchBinKey = 0;
        uint batchClusterOffset = RasterBinMeta[batchBinKey].ClusterOffset;

        if (isSW)
        {
            uint localIdx;
            InterlockedAdd(RasterBinMeta[batchBinKey].SWCursor, 1, localIdx);
            BinnedClusterIndexBuffer[batchClusterOffset + localIdx] = uint2(visibleIndex, (triStart << 16) | triEnd);
        }
        else
        {
            uint batchSWCount = RasterBinMeta[batchBinKey].BinSWCount;
            uint hwLocalIdx;
            InterlockedAdd(RasterBinMeta[batchBinKey].HWCursor, 1, hwLocalIdx);
            BinnedClusterIndexBuffer[batchClusterOffset + batchSWCount + hwLocalIdx] = uint2(visibleIndex, (triStart << 16) | triEnd);
        }
        triStart = triEnd;
    }

    // Slow residual batches (fixed 32-tri windows)
    while (triStart < triangleCount)
    {
        uint triEnd = min(triStart + 32, triangleCount);

        uint batchBinKey = ReadClusterMaterialBinKey(entry.z);
        if (batchBinKey >= Uniforms.MaxBins) batchBinKey = 0;
        uint batchClusterOffset = RasterBinMeta[batchBinKey].ClusterOffset;

        if (isSW)
        {
            uint localIdx;
            InterlockedAdd(RasterBinMeta[batchBinKey].SWCursor, 1, localIdx);
            BinnedClusterIndexBuffer[batchClusterOffset + localIdx] = uint2(visibleIndex, (triStart << 16) | triEnd);
        }
        else
        {
            uint batchSWCount = RasterBinMeta[batchBinKey].BinSWCount;
            uint hwLocalIdx;
            InterlockedAdd(RasterBinMeta[batchBinKey].HWCursor, 1, hwLocalIdx);
            BinnedClusterIndexBuffer[batchClusterOffset + batchSWCount + hwLocalIdx] = uint2(visibleIndex, (triStart << 16) | triEnd);
        }
        triStart = triEnd;
    }
}
