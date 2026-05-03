// cluster_deform.slang — Pre-deformation Compute Shader
// 32 threads/group, 1 group = 1 DeformBin batch entry.
// DeformCache is binned by VertexEval; raster remains independently binned.

#ifndef CLUSTER_DEFORM_SLANG
#define CLUSTER_DEFORM_SLANG

#include "cluster_common.slang"
#include "cluster_structures.slang"
#include "common.slang"
#include "vertex_evaluate.slang"

struct DeformUniforms
{
    float3 QuantOrigin;
    float  QuantStep;
    uint   MaxVisibleClusters;
    uint   MaxDeformCacheBytes;
    uint   MaxClusterVertices;
    uint   CurrentBin;
    uint   CacheStrideBytes;
    uint   CacheScanBlockCount0;
    uint   CacheScanBlockCount1;
    uint   ResetCacheAllocationState;
};

ConstantBuffer<DeformUniforms> Uniforms;

ByteAddressBuffer PageHeap;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint2> BinnedClusterIndexBuffer;
StructuredBuffer<DeformBinEntry> DeformBinMeta;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ReadOffsetArgs;

RWByteAddressBuffer      DeformCache;
StructuredBuffer<uint>   CacheOffsets;
RWStructuredBuffer<uint> CacheOffsetsWrite;
RWByteAddressBuffer      DeformDispatchArgs;
RWStructuredBuffer<uint> CacheRequestBytes;
RWStructuredBuffer<uint> CacheBlockSums0;
RWStructuredBuffer<uint> CacheBlockSums1;
RWStructuredBuffer<uint> CacheBlockOffsets0;
RWStructuredBuffer<uint> CacheBlockOffsets1;

static const uint CACHE_OFFSET_OVERFLOW    = 0xFFFFFFFDu;
static const uint CACHE_SCAN_BLOCK_SIZE = 256u;
static const uint CACHE_VISIBLE_THREAD_COUNT = 64u;

groupshared uint CacheScanScratch[CACHE_SCAN_BLOCK_SIZE];

uint DecodeVisibleIdx(uint logicalIdx, out bool valid)
{
    CullDrawArgs args       = DrawArgs.Load<CullDrawArgs>(0);
    CullDrawArgs readOffset = ReadOffsetArgs.Load<CullDrawArgs>(0);
    uint swCount = args.SWCount;
    uint hwCount = args.HWCount;
    uint total   = swCount + hwCount;
    valid = (logicalIdx < total);
    if (!valid) return 0;

    uint swBase = readOffset.SWCount;
    uint hwBase = Uniforms.MaxVisibleClusters - readOffset.HWCount - hwCount;
    bool isSW  = logicalIdx < swCount;
    return isSW ? (swBase + logicalIdx) : (hwBase + logicalIdx - swCount);
}

uint DecodeVisibleThreadIdx(uint3 groupID, uint groupIndex)
{
    return (groupID.x + groupID.y * 65535u) * CACHE_VISIBLE_THREAD_COUNT + groupIndex;
}

uint MaskedBitCount(uint2 bits, uint index)
{
    uint mask = (1u << (index & 31u)) - 1u;
    uint a = (index < 32u) ? bits.x : bits.y;
    uint b = (index < 32u) ? 0u : countbits(bits.x);
    return countbits(a & mask) + b;
}

int FindNthSetBit(uint mask, int n)
{
    int last = int(countbits(mask)) - n - 1;
    uint p = 16u;
    p += (countbits(mask >> p) <= uint(last)) ? -8u : 8u;
    p += (countbits(mask >> p) <= uint(last)) ? -4u : 4u;
    p += (countbits(mask >> p) <= uint(last)) ? -2u : 2u;
    p += (countbits(mask >> p) <= uint(last)) ? -1u : 1u;
    p  = (countbits(mask >> p) == uint(last)) ? (p - 1u) : p;
    return int(p);
}

int FindNthSetBit2(uint2 mask, int n)
{
    int lowPop = int(countbits(mask.x));
    return FindNthSetBit(
        (n < lowPop) ? mask.x : mask.y,
        (n < lowPop) ? n : (n - lowPop)
    ) + ((n < lowPop) ? 0 : 32);
}

uint CountBits2(uint2 v)
{
    return countbits(v.x) + countbits(v.y);
}

uint LoadClusterVertexCount(uint visibleIndex)
{
    uint4 req = VisibleClusters[visibleIndex];
    uint pageOffset = req.x;
    uint clusterID = req.y;

    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint clustersOff = header1.x;
    uint clusterByteOff = pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES;
    uint packedCounts = PageHeap.Load(clusterByteOff + 44);
    return packedCounts & 0xFF;
}

uint ScanExclusive256(uint value, uint groupIndex, out uint inclusive)
{
    CacheScanScratch[groupIndex] = value;
    GroupMemoryBarrierWithGroupSync();

    for (uint scanOffset = 1u; scanOffset < CACHE_SCAN_BLOCK_SIZE; scanOffset <<= 1u)
    {
        uint addend = groupIndex >= scanOffset ? CacheScanScratch[groupIndex - scanOffset] : 0u;
        GroupMemoryBarrierWithGroupSync();
        CacheScanScratch[groupIndex] += addend;
        GroupMemoryBarrierWithGroupSync();
    }

    inclusive = CacheScanScratch[groupIndex];
    return inclusive - value;
}

struct VertexDedup
{
    uint  numUniqueVerts;
    uint  laneVertIndex;
    uint3 cornerLanes;
};

VertexDedup DeduplicateVertIndexes(uint3 vertIndexes, uint laneIndex, bool bTriValid)
{
    VertexDedup result;

    uint minVert = bTriValid ? min(min(vertIndexes.x, vertIndexes.y), vertIndexes.z) : 0xFFFFFFFFu;
    uint baseVertIndex = WaveActiveMin(minVert);

    uint2 triVertMask = uint2(0, 0);
    if (bTriValid)
    {
        vertIndexes -= baseVertIndex;
        for (uint i = 0; i < 3; i++)
        {
            bool bLow = vertIndexes[i] < 32u;
            uint dstMask = 1u << (vertIndexes[i] & 31u);
            if (bLow) triVertMask.x |= dstMask;
            else      triVertMask.y |= dstMask;
        }
    }
    else
    {
        vertIndexes = uint3(0, 0, 0);
    }

    uint2 usedVertMask;
    usedVertMask.x = WaveActiveBitOr(triVertMask.x);
    usedVertMask.y = WaveActiveBitOr(triVertMask.y);

    result.cornerLanes.x = MaskedBitCount(usedVertMask, vertIndexes.x);
    result.cornerLanes.y = MaskedBitCount(usedVertMask, vertIndexes.y);
    result.cornerLanes.z = MaskedBitCount(usedVertMask, vertIndexes.z);
    result.laneVertIndex = uint(FindNthSetBit2(usedVertMask, int(laneIndex))) + baseVertIndex;
    result.numUniqueVerts = CountBits2(usedVertMask);

    return result;
}

void DeformKernel<TVE : IVertexEvaluate>(
    TVE evaluator,
    uint3 groupID,
    uint groupThreadIndex)
{
    uint waveSize = WaveGetLaneCount();
    if (waveSize != 32u)
        return;

    uint flatIndex = groupID.x + groupID.y * 65535u;

    // Per-bin dispatch: one compute group per DeformBin entry.
    DeformBinEntry binMeta = DeformBinMeta[Uniforms.CurrentBin];
    if (flatIndex >= binMeta.BinCount)
        return;

    uint clusterOffset = binMeta.ClusterOffset;
    uint binnedIndex = clusterOffset + flatIndex;
    uint2 binEntry = BinnedClusterIndexBuffer[binnedIndex];
    uint visibleIndex = binEntry.x;
    uint packedRange = binEntry.y;

    uint4 req = VisibleClusters[visibleIndex];
    uint pageOffset     = req.x;
    uint clusterID      = req.y;
    uint realInstanceID = req.z;

    uint totalVertexCount = PageHeap.Load(pageOffset + 4);
    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint clustersOff = header1.x;
    uint posOffset   = header1.y;
    uint attrOffset  = header1.z;
    uint idxOffset   = header1.w;

    uint clusterByteOff = pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES;

    int3 intBase;
    intBase.x = asint(PageHeap.Load(clusterByteOff + 0));
    intBase.y = asint(PageHeap.Load(clusterByteOff + 4));
    intBase.z = asint(PageHeap.Load(clusterByteOff + 8));

    uint packedLodVStart = PageHeap.Load(clusterByteOff + 36);
    uint vStart = (packedLodVStart >> 16) & 0xFFFF;

    uint packedTStartGroup = PageHeap.Load(clusterByteOff + 40);
    uint tStart = packedTStartGroup & 0xFFFF;

    uint packedCounts = PageHeap.Load(clusterByteOff + 44);
    uint vertexCount  = packedCounts & 0xFF;
    uint triCount     = (packedCounts >> 8) & 0xFF;
    if (vertexCount == 0u || vertexCount > Uniforms.MaxClusterVertices)
        return;

    uint stride = evaluator.getCacheStride();
    uint cacheBaseByte = 0;
    if (WaveGetLaneIndex() == 0)
    {
        cacheBaseByte =
            stride == Uniforms.CacheStrideBytes && stride != 0u && ((stride & 3u) == 0u)
                ? CacheOffsets[visibleIndex]
                : CACHE_OFFSET_OVERFLOW;
    }
    cacheBaseByte = WaveReadLaneFirst(cacheBaseByte);
    if (cacheBaseByte >= CACHE_OFFSET_OVERFLOW)
        return;

    uint rangeStart = (packedRange >> 16) & 0xFFFF;
    uint rangeEnd = packedRange & 0xFFFF;
    uint batchTriCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0;

    uint localTriIdx = rangeStart + groupThreadIndex;
    bool bTriValid = groupThreadIndex < batchTriCount;

    uint3 vertIndices = uint3(0, 0, 0);
    if (bTriValid)
    {
        vertIndices.x = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, localTriIdx, 0);
        vertIndices.y = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, localTriIdx, 1);
        vertIndices.z = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, localTriIdx, 2);
    }

    VertexDedup dedup = DeduplicateVertIndexes(vertIndices, WaveGetLaneIndex(), bTriValid);

    if (WaveGetLaneIndex() >= min(dedup.numUniqueVerts, waveSize))
        return;

    uint localVertIdx = dedup.laneVertIndex;
    if (localVertIdx >= vertexCount || localVertIdx >= Uniforms.MaxClusterVertices)
        return;

    GpuTransform transform = Instances[realInstanceID];

    uint posByteAddr = pageOffset + posOffset + (vStart + localVertIdx) * 6;
    uint alignedAddr = posByteAddr & ~3u;
    uint byteOff     = posByteAddr & 3u;
    uint2 raw = PageHeap.Load2(alignedAddr);

    uint p0, p1, p2;
    if (byteOff == 0)
    {
        p0 = raw.x & 0xFFFF;
        p1 = (raw.x >> 16) & 0xFFFF;
        p2 = raw.y & 0xFFFF;
    }
    else
    {
        p0 = (raw.x >> 16) & 0xFFFF;
        p1 = raw.y & 0xFFFF;
        p2 = (raw.y >> 16) & 0xFFFF;
    }

    int3 globalIntPos = int3(int(p0), int(p1), int(p2)) + intBase;
    float3 objectPos = float3(globalIntPos) * Uniforms.QuantStep + Uniforms.QuantOrigin;
    float3 worldPos  = ApplyTransform(objectPos, transform);

    VertexEvalContext ctx;
    ctx.objectPos        = objectPos;
    ctx.worldPos         = worldPos;
    ctx.localVertIdx     = localVertIdx;
    ctx.instanceID       = realInstanceID;
    ctx.transform        = transform;
    ctx.pageHeap         = PageHeap;
    ctx.pageOffset       = pageOffset;
    ctx.vStart           = vStart;
    ctx.totalVertexCount = totalVertexCount;
    ctx.attrOffset       = attrOffset;

    TVE.DeformedVertex v = evaluator.evaluate(ctx);

    uint byteAddr = cacheBaseByte + localVertIdx * stride;
    evaluator.writeCache(DeformCache, byteAddr, v);
}

[ClusterDeform]
[shader("compute")]
[numthreads(32, 1, 1)]
void CSDeformStatic(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
{
    StaticVertexEval eval;
    DeformKernel(eval, groupID, gtid);
}

[ClusterDeform]
[shader("compute")]
[numthreads(32, 1, 1)]
void CSDeformWave(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
{
    WaveVertexEval eval;
    DeformKernel(eval, groupID, gtid);
}

[shader("compute")]
[numthreads(1, 1, 1)]
void CSDeformPrepareVisibleArgs()
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    uint groupCount = (totalVisible + CACHE_VISIBLE_THREAD_COUNT - 1u) / CACHE_VISIBLE_THREAD_COUNT;
    uint dispatchX = min(groupCount, 65535u);
    uint dispatchY = (groupCount > 0) ? ((groupCount + 65535u - 1u) / 65535u) : 0u;
    DeformDispatchArgs.Store(0, dispatchX);
    DeformDispatchArgs.Store(4, dispatchY);
    DeformDispatchArgs.Store(8, 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSDeformInitVisible(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint logicalIdx = DecodeVisibleThreadIdx(groupID, groupIndex);

    bool valid;
    uint visibleIndex = DecodeVisibleIdx(logicalIdx, valid);
    if (!valid) return;

    CacheOffsetsWrite[visibleIndex] = CACHE_OFFSET_OVERFLOW;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSDeformCacheRequest(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint logicalIdx = DecodeVisibleThreadIdx(groupID, groupIndex);

    bool valid;
    uint visibleIndex = DecodeVisibleIdx(logicalIdx, valid);
    if (!valid) return;

    uint vertexCount = LoadClusterVertexCount(visibleIndex);
    uint stride = Uniforms.CacheStrideBytes;
    uint requestBytes =
        vertexCount > 0u && vertexCount <= Uniforms.MaxClusterVertices &&
        stride != 0u && ((stride & 3u) == 0u)
            ? vertexCount * stride
            : 0u;

    CacheRequestBytes[logicalIdx] = requestBytes;
    CacheOffsetsWrite[visibleIndex] = CACHE_OFFSET_OVERFLOW;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void CSDeformCacheScanVisible(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint logicalIdx = groupID.x * CACHE_SCAN_BLOCK_SIZE + groupIndex;

    bool valid;
    uint visibleIndex = DecodeVisibleIdx(logicalIdx, valid);
    uint requestBytes = valid ? CacheRequestBytes[logicalIdx] : 0u;

    uint inclusive;
    uint localOffset = ScanExclusive256(requestBytes, groupIndex, inclusive);

    if (valid)
        CacheOffsetsWrite[visibleIndex] = localOffset;

    if (groupIndex == CACHE_SCAN_BLOCK_SIZE - 1u)
        CacheBlockSums0[groupID.x] = inclusive;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void CSDeformCacheScanBlocks0(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint blockIndex = groupID.x * CACHE_SCAN_BLOCK_SIZE + groupIndex;
    uint value = blockIndex < Uniforms.CacheScanBlockCount0 ? CacheBlockSums0[blockIndex] : 0u;

    uint inclusive;
    uint localOffset = ScanExclusive256(value, groupIndex, inclusive);

    if (blockIndex < Uniforms.CacheScanBlockCount0)
        CacheBlockOffsets0[blockIndex] = localOffset;

    if (groupIndex == CACHE_SCAN_BLOCK_SIZE - 1u)
        CacheBlockSums1[groupID.x] = inclusive;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void CSDeformCacheScanBlocks1(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint blockIndex = groupID.x * CACHE_SCAN_BLOCK_SIZE + groupIndex;
    uint value = blockIndex < Uniforms.CacheScanBlockCount1 ? CacheBlockSums1[blockIndex] : 0u;

    uint inclusive;
    uint localOffset = ScanExclusive256(value, groupIndex, inclusive);

    if (blockIndex < Uniforms.CacheScanBlockCount1)
        CacheBlockOffsets1[blockIndex] = localOffset;

    if (groupID.x == 0u && groupIndex == 0u && Uniforms.ResetCacheAllocationState != 0u)
        CacheBlockOffsets1[Uniforms.CacheScanBlockCount1] = 0u;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSDeformCacheApplyBlockOffsets(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint logicalIdx = DecodeVisibleThreadIdx(groupID, groupIndex);

    bool valid;
    uint visibleIndex = DecodeVisibleIdx(logicalIdx, valid);
    if (!valid) return;

    uint requestBytes = CacheRequestBytes[logicalIdx];
    uint block0 = logicalIdx / CACHE_SCAN_BLOCK_SIZE;
    uint block1 = block0 / CACHE_SCAN_BLOCK_SIZE;
    uint localBaseByte =
        CacheOffsetsWrite[visibleIndex] +
        CacheBlockOffsets0[block0] +
        CacheBlockOffsets1[block1];
    uint phaseBaseByte = CacheBlockOffsets1[Uniforms.CacheScanBlockCount1];
    uint baseByte = phaseBaseByte + localBaseByte;

    bool overflow =
        requestBytes == 0u ||
        requestBytes > Uniforms.MaxDeformCacheBytes ||
        localBaseByte > 0xFFFFFFFFu - phaseBaseByte ||
        baseByte > Uniforms.MaxDeformCacheBytes - requestBytes;

    CacheOffsetsWrite[visibleIndex] = overflow ? CACHE_OFFSET_OVERFLOW : baseByte;
}

[shader("compute")]
[numthreads(1, 1, 1)]
void CSDeformCacheCommitAllocation()
{
    uint lastBlock = Uniforms.CacheScanBlockCount1 - 1u;
    uint totalRequestBytes = CacheBlockOffsets1[lastBlock] + CacheBlockSums1[lastBlock];
    uint stateIndex = Uniforms.CacheScanBlockCount1;
    uint currentBase = CacheBlockOffsets1[stateIndex];
    uint nextBase =
        totalRequestBytes > 0xFFFFFFFFu - currentBase
            ? 0xFFFFFFFFu
            : currentBase + totalRequestBytes;
    CacheBlockOffsets1[stateIndex] = nextBase;
}

#endif // CLUSTER_DEFORM_SLANG
