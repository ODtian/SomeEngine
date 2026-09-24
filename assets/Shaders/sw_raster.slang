// sw_raster.slang — Software Rasterizer Compute Shader
// Zero LDS, zero barriers. Nanite-style vertex reuse batch + WaveQueue pixel distribution.
//
// 32 threads per group, 1 wave = 1 cluster.
// Stage 1: Vertex Dedup + Transform (wave intrinsics, no LDS)
// Stage 2: Triangle Setup (subpixel edge equations)
// Stage 3: WaveQueue Pixel Distribution (IWaveTask)

#ifndef SW_RASTER_SLANG
#define SW_RASTER_SLANG

#include "cluster_structures.slang"
#include "common.slang"
#include "wave_queue.slang"
#include "vertex_evaluate.slang"

// ─── Uniforms ───
struct SWRasterUniforms
{
    float4x4 ViewProj;
    uint ScreenWidth;
    uint ScreenHeight;
    uint MaxBins;
    uint DebugDump;  // non-zero = dump debug data this frame
    uint CurrentBin; // Per-bin dispatch: which bin this dispatch covers
    uint _pad0;
    uint _pad1;
    uint _pad2;
};

cbuffer PushConstants : register(b100, space0)
{
    SWRasterUniforms Uniforms;
};

ByteAddressBuffer PageHeap;
// Instances is declared in cluster_common.slang (via vertex_evaluate.slang)
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint2> BinnedClusterIndexBuffer; // .x = visibleClusterIndex, .y = rangeStart<<16 | rangeEnd
StructuredBuffer<RasterBinEntry> RasterBinMeta;

Texture2D<float> DepthTarget;
RWTexture2D<uint> VisBuffer;    // packed: (depth_high24 << 8) | unused for now; shade uses vis encoding
RWTexture2D<uint> DepthUAV;

ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;

// Debug: per-cluster SW raster dump. Layout:
// [0]: atomic counter (uint)
// [4 + i*24 + 0]:  binnedIndex
// [4 + i*24 + 4]:  originalIndex
// [4 + i*24 + 8]:  clusterID
// [4 + i*24 + 12]: triangleCount
// [4 + i*24 + 16]: binIdx
// [4 + i*24 + 20]: vertexCount
// Max 8192 entries → buffer size = 4 + 8192*24 = 196612 bytes
RWByteAddressBuffer DebugSWOutput;

// ─── Subpixel Constants ───
static const uint SUBPIXEL_BITS  = 8;
static const uint SUBPIXEL_SCALE = 1u << SUBPIXEL_BITS; // 256
static const float SUBPIXEL_SCALE_F = float(SUBPIXEL_SCALE);
static const uint SW_CACHE_OFFSET_INVALID = 0xFFFFFFFDu;

// ─── FRasterTri ───
struct FRasterTri
{
    float2 Edge01; // Edge vectors for stepping
    float2 Edge12;
    float2 Edge20;
    float  C0, C1, C2;     // Half-edge constants (rebased off MinPixel)
    float3 DepthPlane;      // z0, dz1, dz2 (scaled to unit barycentrics)
    int2   MinPixel;
    int2   MaxPixel;
    bool   bIsValid;
};

// ─── Vertex Deduplication (Wave-based, zero LDS) ───

// MaskedBitCount: count set bits in mask below position `index`
uint MaskedBitCount(uint2 bits, uint index)
{
    uint mask = (1u << (index & 31u)) - 1u;
    uint a = (index < 32u) ? bits.x : bits.y;
    uint b = (index < 32u) ? 0u : countbits(bits.x);
    return countbits(a & mask) + b;
}

// FindNthSetBit: find position of the N-th set bit (0-indexed) in a 32-bit mask
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
    uint  laneVertIndex;   // Which vertex this lane should transform
    uint3 cornerLanes;     // Compacted lane indices for this triangle's 3 corners
};

VertexDedup DeduplicateVertIndexes(uint3 vertIndexes, uint laneIndex, bool bTriValid)
{
    VertexDedup result;

    // Find smallest referenced vertex across all active lanes (must consider ALL 3 corners)
    uint minVert = bTriValid ? min(min(vertIndexes.x, vertIndexes.y), vertIndexes.z) : 0xFFFFFFFFu;
    uint baseVertIndex = WaveActiveMin(minVert);

    // Rebase to [0..63] range
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
        // Invalid lanes: keep vertIndexes at 0 so cornerLanes = 0 (safe for WaveReadLaneAt)
        vertIndexes = uint3(0, 0, 0);
    }

    // OR across wave to get full used-vertex bitmask
    uint2 usedVertMask;
    usedVertMask.x = WaveActiveBitOr(triVertMask.x);
    usedVertMask.y = WaveActiveBitOr(triVertMask.y);

    // Map each triangle corner to its compacted lane index
    result.cornerLanes.x = MaskedBitCount(usedVertMask, vertIndexes.x);
    result.cornerLanes.y = MaskedBitCount(usedVertMask, vertIndexes.y);
    result.cornerLanes.z = MaskedBitCount(usedVertMask, vertIndexes.z);

    // Map this lane to the vertex it should transform
    result.laneVertIndex = uint(FindNthSetBit2(usedVertMask, int(laneIndex))) + baseVertIndex;
    result.numUniqueVerts = CountBits2(usedVertMask);

    return result;
}

// ─── Vertex Fetch (from PageHeap, same as cluster_draw.slang) ───

void LoadClusterGeometry(uint pageOffset, uint clusterID,
    out int3 intBase, out uint vStart, out uint tStart,
    out uint vertexCount, out uint triangleCount,
    out uint posOffset, out uint idxOffset,
    out uint attrOffset, out uint totalVertexCount)
{
    totalVertexCount = PageHeap.Load(pageOffset + 4);
    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint clustersOff = header1.x;
    posOffset = header1.y;
    attrOffset = header1.z;
    idxOffset = header1.w;

    uint clusterOffset = pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES;

    intBase.x = asint(PageHeap.Load(clusterOffset + 0));
    intBase.y = asint(PageHeap.Load(clusterOffset + 4));
    intBase.z = asint(PageHeap.Load(clusterOffset + 8));

    uint packedLodVStart = PageHeap.Load(clusterOffset + 36);
    vStart = (packedLodVStart >> 16) & 0xFFFF;

    uint packedTStartGroup = PageHeap.Load(clusterOffset + 40);
    tStart = packedTStartGroup & 0xFFFF;

    uint packedCounts = PageHeap.Load(clusterOffset + 44);
    vertexCount  = packedCounts & 0xFF;
    triangleCount = (packedCounts >> 8) & 0xFF;
}

uint FetchVertexIndex(uint pageOffset, uint idxOffset, uint tStart, uint triIdx, uint corner)
{
    uint byteAddr = pageOffset + idxOffset + tStart + triIdx * 3 + corner;
    uint aligned = byteAddr & ~3u;
    uint shift = (byteAddr & 3u) * 8u;
    return (PageHeap.Load(aligned) >> shift) & 0xFFu;
}

VertexEvalContext MakeSWRasterVertexEvalContext(
    uint pageOffset,
    uint posOffset,
    uint attrOffset,
    uint idxOffset,
    uint totalVertexCount,
    uint vStart,
    int3 intBase,
    uint localVertIdx,
    uint instanceID,
    GpuTransform transform)
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
    ctx.IsPreviousFrame = 0u;
    return ctx;
}

float4 EvaluateSWRasterClipPosition<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    bool useDeformCache,
    uint visibleClusterIndex,
    uint vertexCount,
    VertexEvalContext ctx)
{
    TVE.DeformedVertex vertex;
    if (useDeformCache)
    {
        uint cacheBaseByte = CacheOffsets[visibleClusterIndex];
        if (cacheBaseByte >= SW_CACHE_OFFSET_INVALID)
            vertex = evaluator.evaluate(ctx);
        else
            vertex = evaluator.readCache(DeformCache, cacheBaseByte, vertexCount, ctx.localVertIdx);
    }
    else
    {
        vertex = evaluator.evaluate(ctx);
    }

    return mul(float4(evaluator.getPosition(vertex), 1.0), Uniforms.ViewProj);
}

// ─── Triangle Setup ───

FRasterTri SetupTriangle(float4 clipVerts[3], int4 scissorRect)
{
    FRasterTri tri;
    tri.bIsValid = true;

    // W cull — any vertex behind camera? Must check BEFORE perspective divide.
    if (clipVerts[0].w <= 0 || clipVerts[1].w <= 0 || clipVerts[2].w <= 0)
    {
        tri.bIsValid = false;
        return tri;
    }

    // Perspective divide → NDC → screen → subpixel
    float3 invW = float3(1.0 / clipVerts[0].w, 1.0 / clipVerts[1].w, 1.0 / clipVerts[2].w);
    float2 screenScale = float2(0.5, -0.5) * float2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float2 screenBias  = float2(0.5, 0.5) * float2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);

    // Clip-space to subpixel coordinates
    float2 v0 = (clipVerts[0].xy * invW.x * screenScale + screenBias) * SUBPIXEL_SCALE_F;
    float2 v1 = (clipVerts[1].xy * invW.y * screenScale + screenBias) * SUBPIXEL_SCALE_F;
    float2 v2 = (clipVerts[2].xy * invW.z * screenScale + screenBias) * SUBPIXEL_SCALE_F;

    // Edge vectors (subpixel)
    tri.Edge01 = v0 - v1;
    tri.Edge12 = v1 - v2;
    tri.Edge20 = v2 - v0;

    // Backface cull (CCW = front, Det > 0 = backface)
    float det = tri.Edge01.y * tri.Edge20.x - tri.Edge01.x * tri.Edge20.y;
    if (det >= 0.0)
    {
        tri.bIsValid = false;
        return tri;
    }

    // Bounding box in pixel space
    float2 minSub = min(min(v0, v1), v2);
    float2 maxSub = max(max(v0, v1), v2);

    // Round to nearest pixel center — matches Nanite NaniteRasterizer.ush L65-66
    tri.MinPixel = int2(floor((minSub + (SUBPIXEL_SCALE / 2)) * (1.0 / SUBPIXEL_SCALE_F)));
    tri.MaxPixel = int2(floor((maxSub - (SUBPIXEL_SCALE / 2)) * (1.0 / SUBPIXEL_SCALE_F)));

    // Scissor clamp
    tri.MinPixel = max(tri.MinPixel, scissorRect.xy);
    tri.MaxPixel = min(tri.MaxPixel, scissorRect.zw - 1);

    // Clamp to 64×64 pixel bounding box — SW raster is for small triangles
    // tri.MaxPixel = min(tri.MaxPixel, tri.MinPixel + 63);

    if (any(tri.MinPixel > tri.MaxPixel))
    {
        tri.bIsValid = false;
        return tri;
    }

    // Rebase vertices off MinPixel with half-pixel offset
    float2 baseSub = float2(tri.MinPixel) * SUBPIXEL_SCALE_F + (SUBPIXEL_SCALE / 2);
    v0 -= baseSub;
    v1 -= baseSub;
    v2 -= baseSub;

    // Half-edge constants
    tri.C0 = tri.Edge12.y * v1.x - tri.Edge12.x * v1.y;
    tri.C1 = tri.Edge20.y * v2.x - tri.Edge20.x * v2.y;
    tri.C2 = tri.Edge01.y * v0.x - tri.Edge01.x * v0.y;

    float scaleToUnit = SUBPIXEL_SCALE_F / (tri.C0 + tri.C1 + tri.C2);

    // Top-left fill rule
    tri.C0 -= saturate(tri.Edge12.y + saturate(1.0 - tri.Edge12.x));
    tri.C1 -= saturate(tri.Edge20.y + saturate(1.0 - tri.Edge20.x));
    tri.C2 -= saturate(tri.Edge01.y + saturate(1.0 - tri.Edge01.x));

    // Scale C down by SUBPIXEL_SCALE for pixel-space stepping
    // (edges stay in subpixel space — stepping formula accounts for this)
    tri.C0 *= (1.0 / SUBPIXEL_SCALE_F);
    tri.C1 *= (1.0 / SUBPIXEL_SCALE_F);
    tri.C2 *= (1.0 / SUBPIXEL_SCALE_F);

    // Depth plane: z = z0 + dz1*bary1 + dz2*bary2
    float z0 = clipVerts[0].z * invW.x;
    float z1 = clipVerts[1].z * invW.y;
    float z2 = clipVerts[2].z * invW.z;
    tri.DepthPlane = float3(z0, (z1 - z0) * scaleToUnit, (z2 - z0) * scaleToUnit);

    return tri;
}

// ─── WritePixel helper ───
void WritePixel(int2 pixel, float depth, uint visData)
{
    float sceneDepth = DepthTarget.Load(int3(pixel, 0));
    if (depth >= sceneDepth)
        return;

    // Normal-Z depth (near=0, far=1) flipped to reversed-Z for InterlockedMax + clear-to-0.
    float depthReversed = 1.0 - depth;
    uint depthBits = asuint(depthReversed);
    // SW/HW debug view: set bit 31 to mark as SW-rasterized pixel
    if ((Uniforms.DebugDump & 0x2) != 0)
        visData = MarkVisBufferSWRaster(visData);
    uint oldDepth;
    InterlockedMax(DepthUAV[pixel], depthBits, oldDepth);
    if (depthBits > oldDepth)
    {
        VisBuffer[pixel] = visData;
    }
}

// ─── Pixel Rasterization Task (WaveQueue Consumer) ───

struct PixelRasterTask : IWaveTask
{
    FRasterTri tri;
    uint visData;
    uint pixelCount;

    [mutating] uint GetTaskCount()
    {
        return pixelCount;
    }

    [mutating] void ExecuteTask(uint srcLane, uint localIdx, bool bActive)
    {
        // All WaveReadLaneAt calls must execute uniformly (all lanes active)
        // to avoid reading UNDEFINED data from inactive producers.
        FRasterTri srcTri;
        srcTri.Edge01  = float2(WaveReadLaneAt(tri.Edge01.x, srcLane),  WaveReadLaneAt(tri.Edge01.y, srcLane));
        srcTri.Edge12  = float2(WaveReadLaneAt(tri.Edge12.x, srcLane),  WaveReadLaneAt(tri.Edge12.y, srcLane));
        srcTri.Edge20  = float2(WaveReadLaneAt(tri.Edge20.x, srcLane),  WaveReadLaneAt(tri.Edge20.y, srcLane));
        srcTri.C0      = WaveReadLaneAt(tri.C0, srcLane);
        srcTri.C1      = WaveReadLaneAt(tri.C1, srcLane);
        srcTri.C2      = WaveReadLaneAt(tri.C2, srcLane);
        srcTri.MinPixel = int2(WaveReadLaneAt(tri.MinPixel.x, srcLane), WaveReadLaneAt(tri.MinPixel.y, srcLane));
        srcTri.MaxPixel = int2(WaveReadLaneAt(tri.MaxPixel.x, srcLane), WaveReadLaneAt(tri.MaxPixel.y, srcLane));
        srcTri.DepthPlane = float3(
            WaveReadLaneAt(tri.DepthPlane.x, srcLane),
            WaveReadLaneAt(tri.DepthPlane.y, srcLane),
            WaveReadLaneAt(tri.DepthPlane.z, srcLane));
        uint srcVisData = WaveReadLaneAt(visData, srcLane);

        if (!bActive)
            return;

        int bboxW = srcTri.MaxPixel.x - srcTri.MinPixel.x + 1;
        int rx = int(localIdx) % bboxW;
        int ry = int(localIdx) / bboxW;

        float cx0 = srcTri.C0 - srcTri.Edge12.y * float(rx) + srcTri.Edge12.x * float(ry);
        float cx1 = srcTri.C1 - srcTri.Edge20.y * float(rx) + srcTri.Edge20.x * float(ry);
        float cx2 = srcTri.C2 - srcTri.Edge01.y * float(rx) + srcTri.Edge01.x * float(ry);

        if (cx0 < 0.0 || cx1 < 0.0 || cx2 < 0.0)
            return;

        int px = srcTri.MinPixel.x + rx;
        int py = srcTri.MinPixel.y + ry;
        float depth = srcTri.DepthPlane.x + srcTri.DepthPlane.y * cx1 + srcTri.DepthPlane.z * cx2;
        depth = saturate(depth);

        float sceneDepth = DepthTarget.Load(int3(px, py, 0));
        if (depth >= sceneDepth)
            return;

        float depthReversed = 1.0 - depth;
        uint depthBits = asuint(depthReversed);
        uint oldDepth;
        // SW/HW debug view: set bit 31 to mark as SW-rasterized pixel
        uint finalVisData = srcVisData;
        if ((Uniforms.DebugDump & 0x2) != 0)
            finalVisData = MarkVisBufferSWRaster(finalVisData);
        InterlockedMax(DepthUAV[int2(px, py)], depthBits, oldDepth);
        if (depthBits > oldDepth)
        {
            VisBuffer[int2(px, py)] = finalVisData;
        }
    }
};


// ─── Generic SW Rasterizer Kernel ───

void SWRasterKernel<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    bool useDeformCache,
    uint3 groupID,
    uint groupThreadIndex)
{
    uint waveLane = WaveGetLaneIndex();
    uint waveSize = WaveGetLaneCount();

    if (waveSize != 32u)
        return;

    uint flatClusterIdx = groupID.x + groupID.y * 65535u;

    // Per-bin dispatch: CPU dispatches per bin, CurrentBin tells us which bin
    uint binIdx = Uniforms.CurrentBin;
    uint binSWCount = RasterBinMeta[binIdx].BinSWCount;
    if (flatClusterIdx >= binSWCount) return;

    uint clusterOffset = RasterBinMeta[binIdx].ClusterOffset;
    uint binCount      = binSWCount;

    uint binnedIndex = clusterOffset + flatClusterIdx;
    uint2 binEntry = BinnedClusterIndexBuffer[binnedIndex];
    uint originalIndex = binEntry.x;

    uint4 req = VisibleClusters[originalIndex];
    uint pageOffset    = req.x;
    uint clusterID     = req.y;
    uint realInstanceID = req.z;

    // ─── Load Cluster Geometry ───
    int3 intBase;
    uint vStart, tStart, vertexCount, triangleCount, posOffset, idxOffset;
    uint attrOffset, totalVertexCount;
    LoadClusterGeometry(pageOffset, clusterID,
        intBase, vStart, tStart, vertexCount, triangleCount,
        posOffset, idxOffset, attrOffset, totalVertexCount);

    GpuTransform transform = Instances[realInstanceID];

    // ─── Debug Dump (lane 0, after all early-outs) ───
    if (Uniforms.DebugDump != 0 && groupThreadIndex == 0)
    {
        uint debugIdx;
        DebugSWOutput.InterlockedAdd(0, 1, debugIdx);
        if (debugIdx < 8192)
        {
            uint dbase = 4 + debugIdx * 24;
            DebugSWOutput.Store(dbase + 0,  binnedIndex);
            DebugSWOutput.Store(dbase + 4,  originalIndex);
            DebugSWOutput.Store(dbase + 8,  clusterID);
            DebugSWOutput.Store(dbase + 12, triangleCount);
            DebugSWOutput.Store(dbase + 16, binCount);       // total clusters in bin
            DebugSWOutput.Store(dbase + 20, vertexCount);
        }
    }

    int4 scissor = int4(0, 0, int(Uniforms.ScreenWidth), int(Uniforms.ScreenHeight));
    uint visibleIndexPlusOne = originalIndex + 1;

    // ─── Read batch range from binned entry (pre-computed by binning shader) ───
    uint packedRange = binEntry.y;
    uint rangeStart = (packedRange >> 16) & 0xFFFF;
    uint rangeEnd = packedRange & 0xFFFF;
    uint batchTriCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0;

    {
        uint localTriIdx = rangeStart + groupThreadIndex;
        bool bTriValid = groupThreadIndex < batchTriCount;

        // Decode 3 vertex indices
        uint3 vertIndices = uint3(0, 0, 0);
        if (bTriValid)
        {
            vertIndices.x = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 0);
            vertIndices.y = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 1);
            vertIndices.z = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 2);
        }

        // ─── Stage 1: Vertex Dedup + Transform ───
        VertexDedup dedup = DeduplicateVertIndexes(vertIndices, waveLane, bTriValid);

        // --- DIAGNOSTIC: Check if we overflow the 32-lane limit for unique vertices ---
        if (bTriValid && dedup.numUniqueVerts > waveSize)
        {
            if (Uniforms.DebugDump != 0)
            {
                uint debugIdx;
                DebugSWOutput.InterlockedAdd(0, 1, debugIdx);
                if (debugIdx < 8192)
                {
                    uint baseAddr = 4 + debugIdx * 24;
                    DebugSWOutput.Store(baseAddr + 0,  binnedIndex);
                    DebugSWOutput.Store(baseAddr + 4,  originalIndex);
                    DebugSWOutput.Store(baseAddr + 8,  0xBAD00002); // Overflow marker
                    DebugSWOutput.Store(baseAddr + 12, triangleCount);
                    DebugSWOutput.Store(baseAddr + 16, dedup.numUniqueVerts);
                    DebugSWOutput.Store(baseAddr + 20, waveSize);
                }
            }
        }

        float4 transformedPos = float4(0, 0, 0, 1);
        if (waveLane < min(dedup.numUniqueVerts, waveSize))
        {
            VertexEvalContext ctx = MakeSWRasterVertexEvalContext(
                pageOffset,
                posOffset,
                attrOffset,
                idxOffset,
                totalVertexCount,
                vStart,
                intBase,
                dedup.laneVertIndex,
                realInstanceID,
                transform);
            transformedPos = EvaluateSWRasterClipPosition(
                evaluator,
                useDeformCache,
                originalIndex,
                vertexCount,
                ctx);
        }

        float4 clipVerts[3];
        clipVerts[0] = float4(
            WaveReadLaneAt(transformedPos.x, dedup.cornerLanes.x),
            WaveReadLaneAt(transformedPos.y, dedup.cornerLanes.x),
            WaveReadLaneAt(transformedPos.z, dedup.cornerLanes.x),
            WaveReadLaneAt(transformedPos.w, dedup.cornerLanes.x));
        clipVerts[1] = float4(
            WaveReadLaneAt(transformedPos.x, dedup.cornerLanes.y),
            WaveReadLaneAt(transformedPos.y, dedup.cornerLanes.y),
            WaveReadLaneAt(transformedPos.z, dedup.cornerLanes.y),
            WaveReadLaneAt(transformedPos.w, dedup.cornerLanes.y));
        clipVerts[2] = float4(
            WaveReadLaneAt(transformedPos.x, dedup.cornerLanes.z),
            WaveReadLaneAt(transformedPos.y, dedup.cornerLanes.z),
            WaveReadLaneAt(transformedPos.z, dedup.cornerLanes.z),
            WaveReadLaneAt(transformedPos.w, dedup.cornerLanes.z));

        // ─── Stage 2: Triangle Setup ───
        FRasterTri rasterTri = {};
        if (bTriValid)
            rasterTri = SetupTriangle(clipVerts, scissor);

        // ─── Stage 3: WaveQueue Pixel Distribution ───
        PixelRasterTask pixelTask;
        pixelTask.tri = rasterTri;
        if (bTriValid && rasterTri.bIsValid)
        {
            pixelTask.visData = EncodeVisBuffer(visibleIndexPlusOne - 1u, localTriIdx);
            int bboxW = rasterTri.MaxPixel.x - rasterTri.MinPixel.x + 1;
            int bboxH = rasterTri.MaxPixel.y - rasterTri.MinPixel.y + 1;
            pixelTask.pixelCount = uint(bboxW * bboxH);
        }
        else
        {
            pixelTask.visData = 0;
            pixelTask.pixelCount = 0;
        }

        WaveQueue::Distribute(pixelTask);

    }
}

// ─── Default Entry Point (Static mesh, inline vertex eval) ───

[MaterialTarget("cluster.raster.sw")]
[shader("compute")]
[numthreads(32, 1, 1)]
void CSSWRaster(uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    StaticVertexEval eval;
    SWRasterKernel(eval, false, groupID, groupThreadIndex);
}

[MaterialTarget("cluster.raster.sw.cached")]
[shader("compute")]
[numthreads(32, 1, 1)]
void CSSWRasterCached(uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    StaticVertexEval eval;
    SWRasterKernel(eval, true, groupID, groupThreadIndex);
}

#endif // SW_RASTER_SLANG
