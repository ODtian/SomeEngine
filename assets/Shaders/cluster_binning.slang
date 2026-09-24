#include "cluster_common.slang"

// ─── Binning Uniforms ───
struct BinningUniforms
{
    uint MaxBins;
    uint SlotCapacity;         // SlotBuffer SOA capacity (even)
    uint BinFieldIndex;        // Field index for this binning pass in SOA
    uint MaxVisibleClusters;
};

struct CombinedBinningUniforms
{
    uint RasterMaxBins;
    uint DeformMaxBins;
    uint SlotCapacity;
    uint RasterBinFieldIndex;
    uint DeformBinFieldIndex;
    uint MaxVisibleClusters;
    uint ResetCacheAllocationState;
    uint Pad0;
};

ConstantBuffer<BinningUniforms> Uniforms;
ConstantBuffer<CombinedBinningUniforms> CombinedUniforms;

// ─── Input ───
StructuredBuffer<uint4> VisibleClusters;

ByteAddressBuffer DrawArgs;                  // IndirectArgs: must stay raw, layout = CullDrawArgs
ByteAddressBuffer ClusterReadOffsetArgs;     // IndirectArgs: must stay raw, layout = CullDrawArgs
StructuredBuffer<uint> SlotBuffer;   // SOA layout, StructuredBuffer<uint>
ByteAddressBuffer PageHeap;                  // Cluster geometry pages (for reading VRBBatchInfo)

// ─── Output ───
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
RWStructuredBuffer<uint2> BinnedClusterIndexBuffer; // .x = visibleClusterIndex, .y = rangeStart<<16 | rangeEnd
RWByteAddressBuffer BinnedDrawArgs;          // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinnedHWDrawArgs;        // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinningDispatchArgs;     // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinnedSWDispatchArgs;    // Per-bin IndirectDispatchArgs: [0..MaxBins) = Total(SW+HW), [MaxBins..2*MaxBins) = SW only
RWStructuredBuffer<uint> ReserveCounters;    // [0] = total batch allocator, [1] = SW batch allocator
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
RWStructuredBuffer<uint2> DeformBinnedClusterIndexBuffer;
RWByteAddressBuffer PreDeformDispatchArgs;
RWStructuredBuffer<uint> DeformReserveCounters;
RWStructuredBuffer<uint> CacheOffsetsWrite;
RWStructuredBuffer<uint> CacheAllocationCounter;

static const uint CACHE_OFFSET_OVERFLOW = 0xFFFFFFFDu;
static const uint CACHE_OFFSET_UNINITIALIZED = 0xFFFFFFFEu;

#include "cluster_bin_io.slang"

// ─── Helper: decode VRB batch count and compute total entries ───
struct VRBDecoded
{
    uint batchCount;     // number of fast batches (≤5)
    uint triCounts[5];   // tri count per fast batch
    uint fastTriTotal;   // total triangles covered by fast batches
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

    return d;
}

bool EmitsSw(bool clusterWasClassifiedSW, uint batchIndex, uint encodedBatchCount)
{
    return clusterWasClassifiedSW && batchIndex < encodedBatchCount;
}

// ─── Helper: read binning payload from PageHeap ───
void ReadClusterData(
    uint4 entry,
    out uint vrbInfo,
    out uint triangleCount,
    out uint packedMaterials,
    out uint packedRanges)
{
    uint pageOffset = entry.x;
    uint clusterID = entry.y;
    uint clustersOff = PageHeap.Load(pageOffset + 16); // header.ClustersOffset
    uint clusterOffset = pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES;
    uint packedCounts = PageHeap.Load(clusterOffset + 44);
    triangleCount = (packedCounts >> 8) & 0xFF;
    packedMaterials = PageHeap.Load(clusterOffset + 48);
    packedRanges = PageHeap.Load(clusterOffset + 52);
    vrbInfo = PageHeap.Load(clusterOffset + 60);
}

// ─── Helper: get raster bin key for a slot/local material pair ───
uint RasterKey(uint slotOffset, uint localMaterialIndex)
{
    return SlotBin(
        SlotBuffer,
        slotOffset,
        localMaterialIndex,
        Uniforms.BinFieldIndex,
        Uniforms.SlotCapacity,
        Uniforms.MaxBins);
}

void ClearRasterBin(uint binIdx)
{
    ClearRaster(RasterBinMeta, binIdx, Uniforms.MaxBins);
}

void WriteArgs()
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint groupCount = (visibleCount + 63) / 64;
    StoreDispatchArgs(BinningDispatchArgs, 0u, DispatchArgs(groupCount, 1, 1));
}

// ─── Pass 0: CSBinningClear: clears BinCount ───
[numthreads(64, 1, 1)]
void CSBinningClear(uint3 dtid : SV_DispatchThreadID)
{
    ClearRasterBin(dtid.x);
}

// ─── Pass 1: CSBinningPrepare: compute dispatch args from GPU-side count ───
[numthreads(1, 1, 1)]
void CSBinningPrepare(uint3 dtid : SV_DispatchThreadID)
{
    WriteArgs();
}

[numthreads(64, 1, 1)]
void CSBinningClearPrepare(uint3 dtid : SV_DispatchThreadID)
{
    ClearRasterBin(dtid.x);

    if (dtid.x == 0)
    {
        WriteArgs();
        ReserveCounters[0] = 0;
        ReserveCounters[1] = 0;
    }
}

void DecodeVisibleIndex(uint logicalIdx, out uint visibleIndex, out bool isSW)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    CullDrawArgs readOffset = LoadCullDrawArgs(ClusterReadOffsetArgs, 0u);
    uint swCount = args.SWCount;
    uint hwCount = args.HWCount;
    uint swBase = readOffset.SWCount;
    uint hwBase = Uniforms.MaxVisibleClusters - readOffset.HWCount - hwCount;

    isSW = logicalIdx < swCount;
    visibleIndex = isSW ? (swBase + logicalIdx) : (hwBase + logicalIdx - swCount);
}

// ─── Pass 2: CSBinningCount: count per-batch entries per bin ───
// Both SW and HW paths emit per-VRB-batch entries with the slot/local material bin key.
[numthreads(64, 1, 1)]
void CSBinningCount(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];

    uint vrbInfo, triangleCount, packedMaterials, packedRanges;
    ReadClusterData(entry, vrbInfo, triangleCount, packedMaterials, packedRanges);
    if (triangleCount == 0u)
        return;

    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);
    uint slotOffset = GetInstanceSlotOffset(entry.z);

    // Emit per-batch counts with the slot/local material bin key.
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        if (triStart >= triangleCount)
            break;

        uint triEnd = min(triStart + vrb.triCounts[b], triangleCount);
        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint batchBinKey = RasterKey(slotOffset, localMaterialIndex);
        bool emitSW = EmitsSw(isSW, b, vrb.batchCount);
        CountRaster(RasterBinMeta, batchBinKey, emitSW);
        triStart = triEnd;
    }
    // Slow residual batches are not encoded VRB batches, so route them through HW raster.
    while (triStart < triangleCount)
    {
        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint batchBinKey = RasterKey(slotOffset, localMaterialIndex);
        CountRaster(RasterBinMeta, batchBinKey, false);
        triStart = min(triStart + 32u, MaterialEnd(packedRanges, triStart, triangleCount));
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

    ReserveRaster(
        RasterBinMeta,
        BinnedDrawArgs,
        BinnedHWDrawArgs,
        BinnedSWDispatchArgs,
        bin,
        Uniforms.MaxBins,
        RasterReserveTotalBlockBase,
        RasterReserveSWBlockBase,
        totalInclusive,
        swInclusive,
        swCount,
        hwCount);
}

// ─── Pass 4: CSBinningScatter: scatter per-batch entries ───
// Both SW and HW paths scatter per-VRB-batch entries with the slot/local material bin key.
[numthreads(64, 1, 1)]
void CSBinningScatter(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];

    uint vrbInfo, triangleCount, packedMaterials, packedRanges;
    ReadClusterData(entry, vrbInfo, triangleCount, packedMaterials, packedRanges);
    if (triangleCount == 0u)
        return;

    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);
    uint slotOffset = GetInstanceSlotOffset(entry.z);

    // Fast VRB batches
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        uint triEnd = triStart + vrb.triCounts[b];
        triEnd = min(triEnd, triangleCount);

        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint batchBinKey = RasterKey(slotOffset, localMaterialIndex);
        bool emitSW = EmitsSw(isSW, b, vrb.batchCount);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            batchBinKey,
            visibleIndex,
            triStart,
            triEnd,
            emitSW);
        triStart = triEnd;
    }

    // Slow residual batches are not encoded VRB batches, so route them through HW raster.
    while (triStart < triangleCount)
    {
        uint triEnd = min(triStart + 32u, MaterialEnd(packedRanges, triStart, triangleCount));

        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint batchBinKey = RasterKey(slotOffset, localMaterialIndex);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            batchBinKey,
            visibleIndex,
            triStart,
            triEnd,
            false);
        triStart = triEnd;
    }
}

uint ComboRasterKey(uint slotOffset, uint localMaterialIndex)
{
    return SlotBin(
        SlotBuffer,
        slotOffset,
        localMaterialIndex,
        CombinedUniforms.RasterBinFieldIndex,
        CombinedUniforms.SlotCapacity,
        CombinedUniforms.RasterMaxBins);
}

uint ComboDeformKey(uint slotOffset, uint localMaterialIndex)
{
    return SlotBin(
        SlotBuffer,
        slotOffset,
        localMaterialIndex,
        CombinedUniforms.DeformBinFieldIndex,
        CombinedUniforms.SlotCapacity,
        CombinedUniforms.DeformMaxBins);
}

void ClearComboRaster(uint binIdx)
{
    ClearRaster(RasterBinMeta, binIdx, CombinedUniforms.RasterMaxBins);
}

void ClearComboDeform(uint binIdx)
{
    ClearDeform(DeformBinMeta, binIdx, CombinedUniforms.DeformMaxBins);
}

void WriteComboArgs()
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint groupCount = (visibleCount + 63) / 64;
    StoreDispatchArgs(BinningDispatchArgs, 0u, DispatchArgs(groupCount, 1, 1));
}

void DecodeComboVis(uint logicalIdx, out uint visibleIndex, out bool isSW)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    CullDrawArgs readOffset = LoadCullDrawArgs(ClusterReadOffsetArgs, 0u);
    uint swCount = args.SWCount;
    uint hwCount = args.HWCount;
    uint swBase = readOffset.SWCount;
    uint hwBase = CombinedUniforms.MaxVisibleClusters - readOffset.HWCount - hwCount;

    isSW = logicalIdx < swCount;
    visibleIndex = isSW ? (swBase + logicalIdx) : (hwBase + logicalIdx - swCount);
}

[numthreads(64, 1, 1)]
void CSRasterDeformBinClearPrepare(uint3 dtid : SV_DispatchThreadID)
{
    ClearComboRaster(dtid.x);
    ClearComboDeform(dtid.x);

    if (dtid.x == 0)
    {
        WriteComboArgs();
        ReserveCounters[0] = 0;
        ReserveCounters[1] = 0;
        DeformReserveCounters[0] = 0;
        if (CombinedUniforms.ResetCacheAllocationState != 0u)
            CacheAllocationCounter[0] = 0;
    }
}

void CountComboRaster(
    uint slotOffset,
    uint vrbInfo,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges,
    bool isSW)
{
    if (triangleCount == 0u)
        return;

    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        if (triStart >= triangleCount)
            break;

        uint triEnd = min(triStart + vrb.triCounts[b], triangleCount);
        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = ComboRasterKey(slotOffset, localMaterialIndex);
        bool emitSW = EmitsSw(isSW, b, vrb.batchCount);
        CountRaster(RasterBinMeta, binKey, emitSW);
        triStart = triEnd;
    }

    while (triStart < triangleCount)
    {
        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = ComboRasterKey(slotOffset, localMaterialIndex);
        CountRaster(RasterBinMeta, binKey, false);
        triStart = min(triStart + 32u, MaterialEnd(packedRanges, triStart, triangleCount));
    }
}

void CountComboDeform(uint slotOffset, uint triangleCount, uint packedMaterials, uint packedRanges)
{
    if (triangleCount == 0u)
        return;

    uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, 0u);
    uint binKey = ComboDeformKey(slotOffset, localMaterialIndex);
    CountDeform(DeformBinMeta, binKey);
}

[numthreads(64, 1, 1)]
void CSRasterDeformBinCount(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeComboVis(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];
    CacheOffsetsWrite[visibleIndex] = CACHE_OFFSET_OVERFLOW;
    uint vrbInfo, triangleCount, packedMaterials, packedRanges;
    ReadClusterData(entry, vrbInfo, triangleCount, packedMaterials, packedRanges);
    uint slotOffset = GetInstanceSlotOffset(entry.z);

    CountComboRaster(slotOffset, vrbInfo, triangleCount, packedMaterials, packedRanges, isSW);
    CountComboDeform(slotOffset, triangleCount, packedMaterials, packedRanges);
}

#define COMBINED_BIN_RESERVE_BLOCK_SIZE 128

groupshared uint CombinedRasterReserveTotalPrefix[COMBINED_BIN_RESERVE_BLOCK_SIZE];
groupshared uint CombinedRasterReserveSWPrefix[COMBINED_BIN_RESERVE_BLOCK_SIZE];
groupshared uint CombinedDeformReservePrefix[COMBINED_BIN_RESERVE_BLOCK_SIZE];
groupshared uint CombinedRasterReserveTotalBlockBase;
groupshared uint CombinedRasterReserveSWBlockBase;
groupshared uint CombinedDeformReserveBlockBase;

[numthreads(COMBINED_BIN_RESERVE_BLOCK_SIZE, 1, 1)]
void CSRasterDeformBinReserve(uint3 tid : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint bin = tid.x;
    uint rasterSWCount = 0;
    uint rasterHWCount = 0;
    uint deformCount = 0;

    if (bin < CombinedUniforms.RasterMaxBins)
    {
        rasterSWCount = RasterBinMeta[bin].BinSWCount;
        rasterHWCount = RasterBinMeta[bin].BinHWCount;
    }
    if (bin < CombinedUniforms.DeformMaxBins)
    {
        deformCount = DeformBinMeta[bin].BinCount;
    }

    uint rasterTotalCount = rasterSWCount + rasterHWCount;
    CombinedRasterReserveTotalPrefix[groupIndex] = rasterTotalCount;
    CombinedRasterReserveSWPrefix[groupIndex] = rasterSWCount;
    CombinedDeformReservePrefix[groupIndex] = deformCount;
    GroupMemoryBarrierWithGroupSync();

    for (uint scanOffset = 1; scanOffset < COMBINED_BIN_RESERVE_BLOCK_SIZE; scanOffset <<= 1)
    {
        uint rasterTotalAddend = groupIndex >= scanOffset ? CombinedRasterReserveTotalPrefix[groupIndex - scanOffset] : 0;
        uint rasterSWAddend = groupIndex >= scanOffset ? CombinedRasterReserveSWPrefix[groupIndex - scanOffset] : 0;
        uint deformAddend = groupIndex >= scanOffset ? CombinedDeformReservePrefix[groupIndex - scanOffset] : 0;
        GroupMemoryBarrierWithGroupSync();
        CombinedRasterReserveTotalPrefix[groupIndex] += rasterTotalAddend;
        CombinedRasterReserveSWPrefix[groupIndex] += rasterSWAddend;
        CombinedDeformReservePrefix[groupIndex] += deformAddend;
        GroupMemoryBarrierWithGroupSync();
    }

    uint rasterTotalInclusive = CombinedRasterReserveTotalPrefix[groupIndex];
    uint rasterSWInclusive = CombinedRasterReserveSWPrefix[groupIndex];
    uint deformInclusive = CombinedDeformReservePrefix[groupIndex];
    uint rasterTotalBlockSum = CombinedRasterReserveTotalPrefix[COMBINED_BIN_RESERVE_BLOCK_SIZE - 1];
    uint rasterSWBlockSum = CombinedRasterReserveSWPrefix[COMBINED_BIN_RESERVE_BLOCK_SIZE - 1];
    uint deformBlockSum = CombinedDeformReservePrefix[COMBINED_BIN_RESERVE_BLOCK_SIZE - 1];

    if (groupIndex == 0)
    {
        InterlockedAdd(ReserveCounters[0], rasterTotalBlockSum, CombinedRasterReserveTotalBlockBase);
        InterlockedAdd(ReserveCounters[1], rasterSWBlockSum, CombinedRasterReserveSWBlockBase);
        InterlockedAdd(DeformReserveCounters[0], deformBlockSum, CombinedDeformReserveBlockBase);
    }
    GroupMemoryBarrierWithGroupSync();

    ReserveRaster(
        RasterBinMeta,
        BinnedDrawArgs,
        BinnedHWDrawArgs,
        BinnedSWDispatchArgs,
        bin,
        CombinedUniforms.RasterMaxBins,
        CombinedRasterReserveTotalBlockBase,
        CombinedRasterReserveSWBlockBase,
        rasterTotalInclusive,
        rasterSWInclusive,
        rasterSWCount,
        rasterHWCount);

    ReserveDeform(
        DeformBinMeta,
        PreDeformDispatchArgs,
        bin,
        CombinedUniforms.DeformMaxBins,
        CombinedDeformReserveBlockBase,
        deformInclusive,
        deformCount);
}

void ScatterComboRaster(
    uint visibleIndex,
    uint slotOffset,
    uint vrbInfo,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges,
    bool isSW)
{
    if (triangleCount == 0u)
        return;

    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        if (triStart >= triangleCount)
            break;

        uint triEnd = triStart + vrb.triCounts[b];
        triEnd = min(triEnd, triangleCount);

        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = ComboRasterKey(slotOffset, localMaterialIndex);
        bool emitSW = EmitsSw(isSW, b, vrb.batchCount);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            binKey,
            visibleIndex,
            triStart,
            triEnd,
            emitSW);
        triStart = triEnd;
    }

    while (triStart < triangleCount)
    {
        uint triEnd = min(triStart + 32u, MaterialEnd(packedRanges, triStart, triangleCount));

        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = ComboRasterKey(slotOffset, localMaterialIndex);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            binKey,
            visibleIndex,
            triStart,
            triEnd,
            false);
        triStart = triEnd;
    }
}

void ScatterComboDeform(
    uint visibleIndex,
    uint slotOffset,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges)
{
    if (triangleCount == 0u)
        return;

    uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, 0u);
    uint binKey = ComboDeformKey(slotOffset, localMaterialIndex);
    ScatterDeform(
        DeformBinMeta,
        DeformBinnedClusterIndexBuffer,
        CacheOffsetsWrite,
        binKey,
        visibleIndex,
        CACHE_OFFSET_UNINITIALIZED);
}

[numthreads(64, 1, 1)]
void CSRasterDeformBinScatter(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeComboVis(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];
    uint vrbInfo, triangleCount, packedMaterials, packedRanges;
    ReadClusterData(entry, vrbInfo, triangleCount, packedMaterials, packedRanges);
    uint slotOffset = GetInstanceSlotOffset(entry.z);

    ScatterComboRaster(visibleIndex, slotOffset, vrbInfo, triangleCount, packedMaterials, packedRanges, isSW);
    ScatterComboDeform(visibleIndex, slotOffset, triangleCount, packedMaterials, packedRanges);
}
