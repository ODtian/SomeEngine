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
    uint packedCounts = PageHeap.Load(pageOffset + clustersOff + clusterID * 64 + 44);
    triangleCount = (packedCounts >> 8) & 0xFF;
    vrbInfo = PageHeap.Load(pageOffset + clustersOff + clusterID * 64 + 60);
}

// ─── Helper: get raster bin key for a specific triangle range's material ───
uint ReadClusterMaterialBinKey(uint4 entry, uint rangeStart, uint instanceID)
{
    uint pageOffset = entry.x;
    uint clusterID = entry.y;
    uint clustersOff = PageHeap.Load(pageOffset + 16);
    uint clusterOffset = pageOffset + clustersOff + clusterID * 64;

    uint packedMaterials = PageHeap.Load(clusterOffset + 48);
    uint packedRanges = PageHeap.Load(clusterOffset + 52);

    uint localMatIdx = GetLocalMaterialIndexRaw(packedMaterials, packedRanges, rangeStart);

    GpuInstanceHeader header = InstanceHeaders[instanceID];
    uint slotOffset = header.MaterialSlotOffset + localMatIdx;
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
// Both SW and HW paths emit per-VRB-batch entries with per-batch material binKey.
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

    // Emit per-batch counts with per-batch material binKey
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        uint batchBinKey = ReadClusterMaterialBinKey(entry, triStart, entry.z);
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
        uint batchBinKey = ReadClusterMaterialBinKey(entry, triStart, entry.z);
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
#define MAX_BINS 256
groupshared uint sPrefixTotal[MAX_BINS];
groupshared uint sPrefixSW[MAX_BINS];

[numthreads(MAX_BINS, 1, 1)]
void CSBinningReserve(uint tid : SV_GroupThreadID)
{
    uint swCount = (tid < Uniforms.MaxBins) ? RasterBinMeta[tid].BinSWCount : 0;
    uint hwCount = (tid < Uniforms.MaxBins) ? RasterBinMeta[tid].BinHWCount : 0;
    uint count = swCount + hwCount;

    // Inclusive scan via shared memory for Total and SW
    sPrefixTotal[tid] = count;
    sPrefixSW[tid] = swCount;
    GroupMemoryBarrierWithGroupSync();

    [unroll] for (uint s = 1; s < MAX_BINS; s <<= 1)
    {
        GroupMemoryBarrierWithGroupSync();
        uint valTotal = 0;
        uint valSW = 0;
        if (tid >= s)
        {
            valTotal = sPrefixTotal[tid - s];
            valSW = sPrefixSW[tid - s];
        }
        GroupMemoryBarrierWithGroupSync();
        sPrefixTotal[tid] += valTotal;
        sPrefixSW[tid] += valSW;
    }
    GroupMemoryBarrierWithGroupSync();

    uint exclusiveSumTotal = sPrefixTotal[tid] - count;
    uint exclusiveSumSW    = sPrefixSW[tid] - swCount;

    if (tid >= Uniforms.MaxBins) return;

    RasterBinMeta[tid].ClusterOffset = exclusiveSumTotal;
    RasterBinMeta[tid].BinCapacity = count;
    RasterBinMeta[tid].SWCursor = 0;
    RasterBinMeta[tid].HWCursor = 0;
    RasterBinMeta[tid].SWOffset = exclusiveSumSW;

    BinnedDrawArgs.Store<DrawInstancedArgs>(tid * 16,
        DrawInstancedArgs(96, swCount, 0, exclusiveSumTotal));

    BinnedHWDrawArgs.Store<DrawInstancedArgs>(tid * 16,
        DrawInstancedArgs(96, hwCount, 0, exclusiveSumTotal + swCount));

    // Per-bin Total dispatch args (SW+HW, for deform: offset bin*12)
    BinnedSWDispatchArgs.Store<DispatchArgs>(tid * 12,
        DispatchArgs(count, 1, 1));

    // Per-bin SW-only dispatch args (for SW raster: offset (MaxBins+bin)*12)
    BinnedSWDispatchArgs.Store<DispatchArgs>((Uniforms.MaxBins + tid) * 12,
        DispatchArgs(swCount, 1, 1));
}

// ─── Pass 4: CSBinningScatter: scatter per-batch entries ───
// Both SW and HW paths scatter per-VRB-batch entries with per-batch material binKey.
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

        uint batchBinKey = ReadClusterMaterialBinKey(entry, triStart, entry.z);
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

        uint batchBinKey = ReadClusterMaterialBinKey(entry, triStart, entry.z);
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
