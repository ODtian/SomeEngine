#include "cluster_common.slang"

struct BinningUniforms
{
    uint MaxBins;
    uint SlotCapacity;
    uint BinFieldIndex;
    uint MaxVisibleClusters;
    uint ResetCacheAllocationState;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<BinningUniforms> Uniforms;

StructuredBuffer<uint4> VisibleClusters;
ByteAddressBuffer ReadOffsetArgs;
ByteAddressBuffer DrawArgs;

RWByteAddressBuffer DeformBinningDispatchArgs;
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
RWStructuredBuffer<uint2> BinnedClusterIndexBuffer;
RWStructuredBuffer<uint> CacheOffsetsWrite;
RWStructuredBuffer<uint> CacheAllocationCounter;
StructuredBuffer<uint> SlotBuffer;
ByteAddressBuffer PageHeap;
RWStructuredBuffer<uint> ReserveCounters;

static const uint CACHE_OFFSET_UNINITIALIZED = 0xFFFFFFFEu;

#include "cluster_bin_io.slang"

void DecodeVisibleIndex(uint logicalIdx, out uint visibleIndex, out bool isSW)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    CullDrawArgs readOffset = LoadCullDrawArgs(ReadOffsetArgs, 0u);
    uint swCount = args.SWCount;
    uint hwCount = args.HWCount;
    uint swBase = readOffset.SWCount;
    uint hwBase = Uniforms.MaxVisibleClusters - readOffset.HWCount - hwCount;

    isSW = logicalIdx < swCount;
    visibleIndex = isSW ? (swBase + logicalIdx) : (hwBase + logicalIdx - swCount);
}

struct VRBDecoded
{
    uint batchCount;
    uint triCounts[5];
    uint fastTriTotal;
    uint totalEntries;
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

    uint slowTris = (triangleCount > d.fastTriTotal) ? (triangleCount - d.fastTriTotal) : 0;
    uint slowBatches = (slowTris + 31) / 32;

    d.totalEntries = d.batchCount + slowBatches;
    return d;
}

void ReadClusterVRB(uint4 entry, out uint vrbInfo, out uint triangleCount)
{
    uint pageOffset = entry.x;
    uint clusterID = entry.y;
    uint clustersOff = PageHeap.Load(pageOffset + 16);
    uint packedCounts = PageHeap.Load(pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES + 44);
    triangleCount = (packedCounts >> 8) & 0xFF;
    vrbInfo = PageHeap.Load(pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES + 60);
}

uint DeformKey(uint instanceID)
{
    return SlotBin(
        SlotBuffer,
        instanceID,
        Uniforms.BinFieldIndex,
        Uniforms.SlotCapacity,
        Uniforms.MaxBins);
}

void ClearBin(uint tid)
{
    ClearDeform(DeformBinMeta, tid, Uniforms.MaxBins);
}

void WriteArgs()
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint totalVisible = args.SWCount + args.HWCount;
    StoreDispatchArgs(DeformBinningDispatchArgs, 0u, DispatchArgs((totalVisible + 63) / 64, 1, 1));
}

[numthreads(64, 1, 1)]
void CSDeformBinClear(uint3 dtid : SV_DispatchThreadID)
{
    ClearBin(dtid.x);
}

[numthreads(1, 1, 1)]
void CSDeformBinPrepare(uint3 dtid : SV_DispatchThreadID)
{
    WriteArgs();
}

[numthreads(64, 1, 1)]
void CSDeformBinClearPrepare(uint3 dtid : SV_DispatchThreadID)
{
    ClearBin(dtid.x);

    if (dtid.x == 0)
    {
        WriteArgs();
        ReserveCounters[0] = 0;
        if (Uniforms.ResetCacheAllocationState != 0u)
            CacheAllocationCounter[0] = 0;
    }
}

void EmitDeform(uint binKey, uint visibleIndex, bool doScatter)
{
    if (!doScatter)
    {
        CountDeform(DeformBinMeta, binKey);
        return;
    }

    ScatterDeform(
        DeformBinMeta,
        BinnedClusterIndexBuffer,
        CacheOffsetsWrite,
        binKey,
        visibleIndex,
        CACHE_OFFSET_UNINITIALIZED);
}

void ProcessCluster(uint visibleIndex, bool doScatter)
{
    uint4 entry = VisibleClusters[visibleIndex];

    uint vrbInfo, triangleCount;
    ReadClusterVRB(entry, vrbInfo, triangleCount);
    if (triangleCount == 0u)
        return;

    uint binKey = DeformKey(entry.z);
    EmitDeform(binKey, visibleIndex, doScatter);
}

[numthreads(64, 1, 1)]
void CSDeformBinCount(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint totalVisible = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= totalVisible) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    ProcessCluster(visibleIndex, false);
}

#define DEFORM_BIN_RESERVE_BLOCK_SIZE 128

groupshared uint DeformReservePrefix[DEFORM_BIN_RESERVE_BLOCK_SIZE];
groupshared uint DeformReserveBlockBase;

[numthreads(DEFORM_BIN_RESERVE_BLOCK_SIZE, 1, 1)]
void CSDeformBinReserve(uint3 dtid : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint bin = dtid.x;
    uint count = bin < Uniforms.MaxBins ? DeformBinMeta[bin].BinCount : 0;

    DeformReservePrefix[groupIndex] = count;
    GroupMemoryBarrierWithGroupSync();

    for (uint scanOffset = 1; scanOffset < DEFORM_BIN_RESERVE_BLOCK_SIZE; scanOffset <<= 1)
    {
        uint addend = groupIndex >= scanOffset ? DeformReservePrefix[groupIndex - scanOffset] : 0;
        GroupMemoryBarrierWithGroupSync();
        DeformReservePrefix[groupIndex] += addend;
        GroupMemoryBarrierWithGroupSync();
    }

    uint inclusiveOffset = DeformReservePrefix[groupIndex];
    uint blockSum = DeformReservePrefix[DEFORM_BIN_RESERVE_BLOCK_SIZE - 1];

    if (groupIndex == 0)
    {
        InterlockedAdd(ReserveCounters[0], blockSum, DeformReserveBlockBase);
    }
    GroupMemoryBarrierWithGroupSync();

    if (bin >= Uniforms.MaxBins) return;

    ReserveDeform(
        DeformBinMeta,
        DeformBinningDispatchArgs,
        bin,
        Uniforms.MaxBins,
        DeformReserveBlockBase,
        inclusiveOffset,
        count);
}

[numthreads(64, 1, 1)]
void CSDeformBinReserveDispatchWrite(uint3 dtid : SV_DispatchThreadID)
{
    uint tid = dtid.x;
    if (tid >= Uniforms.MaxBins) return;

    uint count = DeformBinMeta[tid].BinCount;
    WriteDispatch(DeformBinningDispatchArgs, tid, count);
}

[numthreads(64, 1, 1)]
void CSDeformBinScatter(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint totalVisible = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= totalVisible) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    ProcessCluster(visibleIndex, true);
}
