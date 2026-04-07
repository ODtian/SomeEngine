// cluster_deform.slang — Pre-deformation Compute Shader
// 32 threads/group, 1 group = 1 RasterBin batch entry.
// Deform and Raster share the same BinnedClusterIndex/RasterBin outputs.

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
    uint   MaxDeformVertices;
    uint   MaxRasterBins;
    uint   MaxClusterVertices;
    uint   CurrentBin;
    uint   _pad0;
    uint   _pad1;
    uint   _pad2;
};

ConstantBuffer<DeformUniforms> Uniforms;

ByteAddressBuffer PageHeap;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint2> BinnedClusterIndexBuffer;
StructuredBuffer<RasterBinEntry> RasterBinMeta;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ReadOffsetArgs;

RWByteAddressBuffer      DeformCache;
RWStructuredBuffer<uint> CacheOffsets;
RWByteAddressBuffer      CacheAllocCounter;
RWByteAddressBuffer      DeformDispatchArgs;

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

    // Per-bin dispatch: total count (SW+HW) dispatched at bin*12
    uint clusterOffset = RasterBinMeta[Uniforms.CurrentBin].ClusterOffset;
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

    uint clusterByteOff = pageOffset + clustersOff + clusterID * 64;

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

    uint rangeStart = (packedRange >> 16) & 0xFFFF;
    uint rangeEnd = packedRange & 0xFFFF;
    if (rangeEnd == 0) rangeEnd = triCount;
    uint batchTriCount = rangeEnd - rangeStart;

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

    uint cacheBaseVert = CacheOffsets[visibleIndex];
    if (cacheBaseVert + vertexCount > Uniforms.MaxDeformVertices)
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

    uint stride   = evaluator.getCacheStride();
    uint byteAddr = (cacheBaseVert + localVertIdx) * stride;
    evaluator.writeCache(DeformCache, byteAddr, v);
}

// [shader("compute")]
// [numthreads(32, 1, 1)]
// void CSDeformStatic(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
// {
//     StaticVertexEval eval;
//     DeformKernel(eval, groupID, gtid);
// }

// [shader("compute")]
// [numthreads(32, 1, 1)]
// void CSDeformWave(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
// {
//     WaveVertexEval eval;
//     DeformKernel(eval, groupID, gtid);
// }

[shader("compute")]
[numthreads(1, 1, 1)]
void CSDeformPrepareVisibleArgs()
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    uint dispatchX = min(totalVisible, 65535u);
    uint dispatchY = (totalVisible > 0) ? ((totalVisible + 65535u - 1u) / 65535u) : 0u;
    DeformDispatchArgs.Store(0, dispatchX);
    DeformDispatchArgs.Store(4, dispatchY);
    DeformDispatchArgs.Store(8, 1u);
}

[shader("compute")]
[numthreads(1, 1, 1)]
void CSDeformInitVisible(uint3 groupID : SV_GroupID)
{
    uint logicalIdx = groupID.x + groupID.y * 65535u;

    bool valid;
    uint visibleIndex = DecodeVisibleIdx(logicalIdx, valid);
    if (!valid) return;

    uint4 req = VisibleClusters[visibleIndex];
    uint pageOffset = req.x;
    uint clusterID = req.y;
    uint clustersOff = PageHeap.Load(pageOffset + 16);
    uint clusterByteOff = pageOffset + clustersOff + clusterID * 64;
    uint packedCounts = PageHeap.Load(clusterByteOff + 44);
    uint vertexCount = packedCounts & 0xFF;

    uint baseVert;
    CacheAllocCounter.InterlockedAdd(0, vertexCount, baseVert);
    CacheOffsets[visibleIndex] = baseVert;
}

#endif // CLUSTER_DEFORM_SLANG
