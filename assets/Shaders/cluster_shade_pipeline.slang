#ifndef CLUSTER_SHADE_PIPELINE_SLANG
#define CLUSTER_SHADE_PIPELINE_SLANG

#include "vertex_evaluate.slang"
#include "material_interfaces.slang"

struct ShadeUniforms
{
    float4x4 ViewProj;
    float4x4 View;
    float4x4 PrevViewProj;
    float4x4 MotionViewProj;
    float4x4 PrevMotionViewProj;
    uint PageTableSize;
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
    uint ShadingBin;
    uint MaterialCount;
    uint LightLayerMask;
    uint Pad1;
    float3 CameraPos;
    float Pad2;
    uint HasPreviousFrame;
    uint WriteMotionVectors;
    uint Pad3;
    uint Pad4;
};

struct LightCountsUniform
{
    uint DirectionalCount;
    uint PointCount;
    uint SpotCount;
    uint Pad0;
};

struct GPULight
{
    float3 Position;
    float Range;
    float3 Direction;
    float InnerConeCos;
    float3 Color;
    float Intensity;
    float OuterConeCos;
    uint LayerMask;
    int CookieIndex;
    float CookieStrength;
    float4x4 WorldToLightCookie;
    float4 CookieScaleOffset;
};

ConstantBuffer<ShadeUniforms> Uniforms;
ConstantBuffer<LightCountsUniform> LightCounts;
StructuredBuffer<GPULight> LightBuffer;
Texture2DArray LightCookieAtlas;
SamplerState LightCookieSampler;

cbuffer LightGridUniforms
{
    uint2 LightGridTileSize;
    uint2 LightGridTileCount;
    float4 LightGridZParams;
    uint DepthSliceCount;
    uint3 LightGridPad0;
};

StructuredBuffer<uint2> ClusterLightGrid;
StructuredBuffer<uint> LightIndexList;

StructuredBuffer<uint> PixelCoordBuffer;
StructuredBuffer<uint> BinOffsets;
StructuredBuffer<uint> BinCounts;

Texture2D<uint> VisBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;

RWTexture2D<float4> OutputColor;
RWTexture2D<float2> OutputMotionVectors;

ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;
StructuredBuffer<GpuTransform> PreviousInstances;

static const uint SHADE_CACHE_OFFSET_INVALID = 0xFFFFFFFDu;
static const uint LIGHT_GRID_INVALID_CLUSTER = 0xFFFFFFFFu;

uint GetClusterIndex(uint2 pixelCoord, float3 surfacePosition)
{
    if (LightGridTileSize.x == 0u ||
        LightGridTileSize.y == 0u ||
        LightGridTileCount.x == 0u ||
        LightGridTileCount.y == 0u ||
        DepthSliceCount == 0u)
    {
        return LIGHT_GRID_INVALID_CLUSTER;
    }

    uint2 tileCoord = pixelCoord / LightGridTileSize;
    if (tileCoord.x >= LightGridTileCount.x || tileCoord.y >= LightGridTileCount.y)
        return LIGHT_GRID_INVALID_CLUSTER;

    float viewDepth = mul(float4(surfacePosition, 1.0), Uniforms.View).z;
    float normalizedDepth = saturate((viewDepth - LightGridZParams.x) * LightGridZParams.z);
    uint sliceIndex = min((uint)(normalizedDepth * (float)DepthSliceCount), DepthSliceCount - 1u);
    return (sliceIndex * LightGridTileCount.y + tileCoord.y) * LightGridTileCount.x + tileCoord.x;
}

struct TriangleInfo
{
    uint visibleClusterIndex;
    uint triangleID;
    uint instanceID;
    uint vi0;
    uint vi1;
    uint vi2;
    uint pageOffset;
    int3 intBase;
    uint vStart;
    uint vertexCount;
    uint totalVertexCount;
    uint posOffset;
    uint attrOffset;
    uint idxOffset;
};

bool decodeTriangle(uint2 pixelCoord, out TriangleInfo tri)
{
    tri = {};
    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return false;

    uint visData = VisBuffer[pixelCoord];
    if (IsVisBufferEmpty(visData))
        return false;

    tri.visibleClusterIndex = DecodeVisBufferVisibleClusterIndex(visData);
    tri.triangleID = DecodeVisBufferTriangleID(visData);

    DrawRequest req = VisibleClusters[tri.visibleClusterIndex];
    tri.pageOffset = req.PageOffset;
    tri.instanceID = req.InstanceID;

    uint tStart;
    uint packedCounts;
    LoadClusterInfo(PageHeap, tri.pageOffset, req.ClusterID, tri.intBase, tri.vStart, tStart, packedCounts);
    tri.vertexCount = packedCounts & 0xFFu;

    tri.totalVertexCount = PageHeap.Load(tri.pageOffset + 4);
    uint4 header1 = PageHeap.Load4(tri.pageOffset + 16);
    tri.posOffset = header1.y;
    tri.attrOffset = header1.z;
    tri.idxOffset = header1.w;

    tri.vi0 = FetchVertexIndex(PageHeap, tri.pageOffset, tri.idxOffset, tStart, tri.triangleID, 0);
    tri.vi1 = FetchVertexIndex(PageHeap, tri.pageOffset, tri.idxOffset, tStart, tri.triangleID, 1);
    tri.vi2 = FetchVertexIndex(PageHeap, tri.pageOffset, tri.idxOffset, tStart, tri.triangleID, 2);
    return true;
}

VertexEvalContext makeVertexEvalContext(
    TriangleInfo tri,
    GpuTransform transform,
    uint isPreviousFrame,
    uint localVertIdx)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    ctx.pageOffset = tri.pageOffset;
    ctx.posOffset = tri.posOffset;
    ctx.attrOffset = tri.attrOffset;
    ctx.idxOffset = tri.idxOffset;
    ctx.vStart = tri.vStart;
    ctx.totalVertexCount = tri.totalVertexCount;
    ctx.localVertIdx = localVertIdx;
    ctx.intBase = tri.intBase;
    ctx.instanceID = tri.instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = isPreviousFrame;
    return ctx;
}

void evaluateTriangleVerticesWithTransform<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    GpuTransform transform,
    uint isPreviousFrame,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    v0 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi0));
    v1 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi1));
    v2 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi2));
}

void evaluateTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    evaluateTriangleVerticesWithTransform(
        evaluator,
        tri,
        Instances[tri.instanceID],
        0,
        v0,
        v1,
        v2);
}

void evaluatePreviousTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    evaluateTriangleVerticesWithTransform(
        evaluator,
        tri,
        PreviousInstances[tri.instanceID],
        1,
        v0,
        v1,
        v2);
}

void readCachedTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    ByteAddressBuffer cache,
    StructuredBuffer<uint> offsets,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    uint cacheBaseByte = offsets[tri.visibleClusterIndex];
    if (cacheBaseByte >= SHADE_CACHE_OFFSET_INVALID)
    {
        evaluateTriangleVertices(evaluator, tri, v0, v1, v2);
        return;
    }

    v0 = evaluator.readCache(cache, cacheBaseByte, tri.vertexCount, tri.vi0);
    v1 = evaluator.readCache(cache, cacheBaseByte, tri.vertexCount, tri.vi1);
    v2 = evaluator.readCache(cache, cacheBaseByte, tri.vertexCount, tri.vi2);
}

void readCachedPreviousTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    ByteAddressBuffer cache,
    StructuredBuffer<uint> offsets,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    uint cacheBaseByte = offsets[tri.visibleClusterIndex];
    if (cacheBaseByte >= SHADE_CACHE_OFFSET_INVALID)
    {
        evaluatePreviousTriangleVertices(evaluator, tri, v0, v1, v2);
        return;
    }

    v0 = evaluator.readPreviousCache(cache, cacheBaseByte, tri.vertexCount, tri.vi0);
    v1 = evaluator.readPreviousCache(cache, cacheBaseByte, tri.vertexCount, tri.vi1);
    v2 = evaluator.readPreviousCache(cache, cacheBaseByte, tri.vertexCount, tri.vi2);
}

void loadTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    bool useDeformCache,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    if (useDeformCache)
    {
        readCachedTriangleVertices(evaluator, tri, DeformCache, CacheOffsets, v0, v1, v2);
        return;
    }

    evaluateTriangleVertices(evaluator, tri, v0, v1, v2);
}

void loadPreviousTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    bool useDeformCache,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    if (useDeformCache)
    {
        readCachedPreviousTriangleVertices(evaluator, tri, DeformCache, CacheOffsets, v0, v1, v2);
        return;
    }

    evaluatePreviousTriangleVertices(evaluator, tri, v0, v1, v2);
}

float2 clipToUv(float4 clip)
{
    float2 ndc = clip.xy / clip.w;
    return float2(ndc.x * 0.5 + 0.5, 1.0 - (ndc.y * 0.5 + 0.5));
}

bool shouldWriteMotionVectors()
{
    return Uniforms.WriteMotionVectors != 0 && Uniforms.HasPreviousFrame != 0;
}

void writeZeroMotionVector(uint2 pixelCoord)
{
    OutputMotionVectors[pixelCoord] = float2(0.0);
}

float3 computeTriangleBarycentric<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    uint2 pixelCoord,
    TVE.DeformedVertex v0,
    TVE.DeformedVertex v1,
    TVE.DeformedVertex v2)
{
    float3 p0 = evaluator.getPosition(v0);
    float3 p1 = evaluator.getPosition(v1);
    float3 p2 = evaluator.getPosition(v2);
    ScreenProjection sp0 = ProjectToScreenProjection(p0, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp1 = ProjectToScreenProjection(p1, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp2 = ProjectToScreenProjection(p2, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    return ComputePerspectiveBarycentric(float2(pixelCoord) + 0.5, sp0, sp1, sp2);
}

void writeMotionVector<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    uint2 pixelCoord,
    TVE.DeformedVertex v0,
    TVE.DeformedVertex v1,
    TVE.DeformedVertex v2,
    TVE.DeformedVertex previousV0,
    TVE.DeformedVertex previousV1,
    TVE.DeformedVertex previousV2)
{
    if (!shouldWriteMotionVectors())
    {
        writeZeroMotionVector(pixelCoord);
        return;
    }

    float3 bary = computeTriangleBarycentric(evaluator, pixelCoord, v0, v1, v2);
    float3 currentPosition =
        bary.x * evaluator.getPosition(v0) +
        bary.y * evaluator.getPosition(v1) +
        bary.z * evaluator.getPosition(v2);
    float3 previousPosition =
        bary.x * evaluator.getPosition(previousV0) +
        bary.y * evaluator.getPosition(previousV1) +
        bary.z * evaluator.getPosition(previousV2);

    float4 currentClip = mul(float4(currentPosition, 1.0), Uniforms.MotionViewProj);
    float4 previousClip = mul(float4(previousPosition, 1.0), Uniforms.PrevMotionViewProj);

    if (abs(currentClip.w) < 1e-6 || abs(previousClip.w) < 1e-6)
    {
        writeZeroMotionVector(pixelCoord);
        return;
    }

    OutputMotionVectors[pixelCoord] = clipToUv(currentClip) - clipToUv(previousClip);
}

void CSShadeCore<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate<TVE>>(
    uniform TVE vertexEval,
    uniform TMaterial material,
    uint3 tid,
    bool useDeformCache)
{
    if (tid.x >= BinCounts[Uniforms.ShadingBin])
        return;

    uint pixelIdx = BinOffsets[Uniforms.ShadingBin] + tid.x;
    uint packedCoord = PixelCoordBuffer[pixelIdx];
    uint2 pixelCoord = uint2(packedCoord & 0xFFFFu, packedCoord >> 16);

    TriangleInfo tri;
    if (!decodeTriangle(pixelCoord, tri))
        return;

    TVE.DeformedVertex v0;
    TVE.DeformedVertex v1;
    TVE.DeformedVertex v2;
    loadTriangleVertices(vertexEval, tri, useDeformCache, v0, v1, v2);

    float3 bary = computeTriangleBarycentric(vertexEval, pixelCoord, v0, v1, v2);

    if (Uniforms.DebugMode != 0)
    {
        material.evaluateDebug(
            vertexEval,
            Uniforms.DebugMode,
            pixelCoord,
            tri.instanceID,
            bary,
            v0,
            v1,
            v2,
            OutputColor);
        writeZeroMotionVector(pixelCoord);
        return;
    }

    material.evaluateSurface(
        vertexEval,
        pixelCoord,
        tri.instanceID,
        bary,
        v0,
        v1,
        v2,
        OutputColor);

    if (shouldWriteMotionVectors())
    {
        TVE.DeformedVertex previousV0;
        TVE.DeformedVertex previousV1;
        TVE.DeformedVertex previousV2;
        loadPreviousTriangleVertices(vertexEval, tri, useDeformCache, previousV0, previousV1, previousV2);
        writeMotionVector(vertexEval, pixelCoord, v0, v1, v2, previousV0, previousV1, previousV2);
    }
    else
    {
        writeZeroMotionVector(pixelCoord);
    }
}

void CSShade<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate<TVE>>(
    uniform TVE vertexEval,
    uniform TMaterial material,
    uint3 tid)
{
    CSShadeCore(vertexEval, material, tid, false);
}

void CSShadeFromCache<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate<TVE>>(
    uniform TVE vertexEval,
    uniform TMaterial material,
    uint3 tid)
{
    CSShadeCore(vertexEval, material, tid, true);
}

#endif
