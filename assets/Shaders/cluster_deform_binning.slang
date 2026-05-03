#include "cluster_common.slang"

struct BinningUniforms
{
    uint MaxBins;
    uint SlotCapacity;
    uint BinFieldIndex;
    uint MaxVisibleClusters;
};

ConstantBuffer<BinningUniforms> Uniforms;

StructuredBuffer<uint4> VisibleClusters;
ByteAddressBuffer ReadOffsetArgs;
ByteAddressBuffer DrawArgs;

RWByteAddressBuffer DeformBinningDispatchArgs;
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
RWStructuredBuffer<uint2> BinnedClusterIndexBuffer;
StructuredBuffer<uint> MaterialSlotBuffer;
ByteAddressBuffer PageHeap;
RWStructuredBuffer<uint> ReserveCounters;

DispatchArgs MakeIndirectDispatchArgs(uint groupCount)
{
    uint dispatchX = min(groupCount, 65535u);
    uint dispatchY = max(1u, (groupCount + 65534u) / 65535u);
    return DispatchArgs(dispatchX, dispatchY, 1);
}

void DecodeVisibleIndex(uint logicalIdx, out uint visibleIndex, out bool isSW)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    CullDrawArgs readOffset = ReadOffsetArgs.Load<CullDrawArgs>(0);
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

uint ReadDeformBinKey(uint instanceID)
{
    uint slotOffset = GetInstanceMaterialSlotOffset(instanceID);
    return GetSlotField(MaterialSlotBuffer, slotOffset, Uniforms.BinFieldIndex, Uniforms.SlotCapacity);
}

[numthreads(64, 1, 1)]
void CSDeformBinClear(uint3 dtid : SV_DispatchThreadID)
{
    uint tid = dtid.x;
    if (tid >= Uniforms.MaxBins) return;

    DeformBinMeta[tid].ClusterOffset = 0;
    DeformBinMeta[tid].BinCount = 0;
    DeformBinMeta[tid].Cursor = 0;
    DeformBinMeta[tid].Pad = 0;
}

[numthreads(1, 1, 1)]
void CSDeformBinPrepare(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    DeformBinningDispatchArgs.Store<DispatchArgs>(0, DispatchArgs((totalVisible + 63) / 64, 1, 1));
}

void EmitDeformBinEntry(uint binKey, uint visibleIndex, uint triStart, uint triEnd, bool doScatter)
{
    if (binKey >= Uniforms.MaxBins) binKey = 0;

    if (!doScatter)
    {
        uint unused;
        InterlockedAdd(DeformBinMeta[binKey].BinCount, 1, unused);
        return;
    }

    uint offset = DeformBinMeta[binKey].ClusterOffset;
    uint localIdx;
    InterlockedAdd(DeformBinMeta[binKey].Cursor, 1, localIdx);
    BinnedClusterIndexBuffer[offset + localIdx] = uint2(visibleIndex, (triStart << 16) | triEnd);
}

void ProcessClusterRanges(uint visibleIndex, bool doScatter)
{
    uint4 entry = VisibleClusters[visibleIndex];

    uint vrbInfo, triangleCount;
    ReadClusterVRB(entry, vrbInfo, triangleCount);
    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);

    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        uint triEnd = min(triStart + vrb.triCounts[b], triangleCount);
        uint binKey = ReadDeformBinKey(entry.z);
        EmitDeformBinEntry(binKey, visibleIndex, triStart, triEnd, doScatter);
        triStart = triEnd;
    }

    while (triStart < triangleCount)
    {
        uint triEnd = min(triStart + 32, triangleCount);
        uint binKey = ReadDeformBinKey(entry.z);
        EmitDeformBinEntry(binKey, visibleIndex, triStart, triEnd, doScatter);
        triStart = triEnd;
    }
}

[numthreads(64, 1, 1)]
void CSDeformBinCount(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= totalVisible) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    ProcessClusterRanges(visibleIndex, false);
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

    DeformBinMeta[bin].ClusterOffset = DeformReserveBlockBase + inclusiveOffset - count;
    DeformBinMeta[bin].Cursor = 0;
}

[numthreads(64, 1, 1)]
void CSDeformBinReserveDispatchWrite(uint3 dtid : SV_DispatchThreadID)
{
    uint tid = dtid.x;
    if (tid >= Uniforms.MaxBins) return;

    uint count = DeformBinMeta[tid].BinCount;
    DeformBinningDispatchArgs.Store<DispatchArgs>(tid * 12, MakeIndirectDispatchArgs(count));
}

[numthreads(64, 1, 1)]
void CSDeformBinScatter(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= totalVisible) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);

    ProcessClusterRanges(visibleIndex, true);
}
