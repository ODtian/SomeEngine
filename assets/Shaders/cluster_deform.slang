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
    uint   MaxVisibleClusters;
    uint   MaxDeformCacheBytes;
    uint   MaxClusterVertices;
    uint   CurrentBin;
    uint   Reserved0;
    uint   ResetCacheAllocationState;
};

cbuffer PushConstants : register(b100, space0)
{
    DeformUniforms Uniforms;
};

ByteAddressBuffer PageHeap;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint2> BinnedClusterIndexBuffer;
StructuredBuffer<DeformBinEntry> DeformBinMeta;
StructuredBuffer<GpuTransform> PreviousInstances;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ReadOffsetArgs;

RWByteAddressBuffer      DeformCache;
StructuredBuffer<uint>   CacheOffsets;
RWStructuredBuffer<uint> CacheOffsetsWrite;
RWStructuredBuffer<uint> CacheAllocationCounter;
RWByteAddressBuffer      DeformDispatchArgs;

static const uint CACHE_OFFSET_OVERFLOW    = 0xFFFFFFFDu;
static const uint CACHE_OFFSET_UNINITIALIZED = 0xFFFFFFFEu;

groupshared uint DeformKernelCacheBaseByte;

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

VertexEvalContext MakeDeformVertexEvalContext(
    uint pageOffset,
    uint posOffset,
    uint attrOffset,
    uint idxOffset,
    uint totalVertexCount,
    uint vStart,
    int3 intBase,
    uint localVertIdx,
    uint instanceID,
    GpuTransform transform,
    uint isPreviousFrame)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    ctx.pageOffset = pageOffset;
    ctx.posOffset = posOffset;
    ctx.attrOffset = attrOffset;
    ctx.idxOffset = idxOffset;
    ctx.vStart = vStart;
    ctx.totalVertexCount = totalVertexCount;
    ctx.localVertIdx = localVertIdx;
    ctx.intBase = intBase;
    ctx.instanceID = instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = isPreviousFrame;
    return ctx;
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
    uint flatIndex = groupID.x + groupID.y * 65535u;

    // Per-bin dispatch: one compute group per visible-cluster DeformBin entry.
    DeformBinEntry binMeta = DeformBinMeta[Uniforms.CurrentBin];
    if (flatIndex >= binMeta.BinCount)
        return;

    uint clusterOffset = binMeta.ClusterOffset;
    uint binnedIndex = clusterOffset + flatIndex;
    uint2 binEntry = BinnedClusterIndexBuffer[binnedIndex];
    uint visibleIndex = binEntry.x;

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

    uint packedCounts = PageHeap.Load(clusterByteOff + 44);
    uint vertexCount  = packedCounts & 0xFF;
    if (vertexCount == 0u || vertexCount > Uniforms.MaxClusterVertices)
        return;

    if (groupThreadIndex == 0u)
    {
        uint requestBytesRaw = evaluator.getCacheByteSize(vertexCount);
        uint requestBytes =
            requestBytesRaw != 0u && ((requestBytesRaw & 3u) == 0u)
                ? requestBytesRaw
                : 0u;

        uint cacheBaseByte = CACHE_OFFSET_OVERFLOW;
        if (requestBytes != 0u && requestBytes <= Uniforms.MaxDeformCacheBytes)
        {
            uint allocatedBaseByte;
            InterlockedAdd(CacheAllocationCounter[0], requestBytes, allocatedBaseByte);
            bool overflow =
                allocatedBaseByte > Uniforms.MaxDeformCacheBytes ||
                requestBytes > Uniforms.MaxDeformCacheBytes - allocatedBaseByte;
            cacheBaseByte = overflow ? CACHE_OFFSET_OVERFLOW : allocatedBaseByte;
        }

        CacheOffsetsWrite[visibleIndex] = cacheBaseByte;
        DeformKernelCacheBaseByte = cacheBaseByte;
    }
    GroupMemoryBarrierWithGroupSync();

    uint cacheBaseByte = DeformKernelCacheBaseByte;
    if (cacheBaseByte >= CACHE_OFFSET_OVERFLOW)
        return;

    uint localVertIdx = groupThreadIndex;
    if (localVertIdx >= vertexCount || localVertIdx >= Uniforms.MaxClusterVertices)
        return;

    GpuTransform transform = Instances[realInstanceID];
    GpuTransform previousTransform = PreviousInstances[realInstanceID];

    VertexEvalContext ctx = MakeDeformVertexEvalContext(
        pageOffset,
        posOffset,
        attrOffset,
        idxOffset,
        totalVertexCount,
        vStart,
        intBase,
        localVertIdx,
        realInstanceID,
        transform,
        0u);
    TVE.DeformedVertex v = evaluator.evaluate(ctx);

    VertexEvalContext previousCtx = MakeDeformVertexEvalContext(
        pageOffset,
        posOffset,
        attrOffset,
        idxOffset,
        totalVertexCount,
        vStart,
        intBase,
        localVertIdx,
        realInstanceID,
        previousTransform,
        1u);
    TVE.DeformedVertex previousV = evaluator.evaluate(previousCtx);

    evaluator.writeCache(DeformCache, cacheBaseByte, vertexCount, localVertIdx, v, previousV);
}

[MaterialTarget("cluster.deform.eval")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSDeformStatic(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
{
    StaticVertexEval eval;
    DeformKernel(eval, groupID, gtid);
}

[MaterialTarget("cluster.deform.eval")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSDeformWave(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
{
    WaveVertexEval eval;
    DeformKernel(eval, groupID, gtid);
}

#endif // CLUSTER_DEFORM_SLANG
