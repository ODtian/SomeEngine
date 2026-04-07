#ifndef CLUSTER_SHADE_PIPELINE_SLANG
#define CLUSTER_SHADE_PIPELINE_SLANG

#include "vertex_evaluate.slang"
#include "material_interfaces.slang"

struct ShadeUniforms
{
    float4x4 ViewProj;
    float4x4 View;
    uint PageTableSize;
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
    float3 QuantOrigin;
    float QuantStep;
    uint ShadingBin;        // Current shading bin being dispatched
    uint MaterialCount;
    uint Pad0;
    uint Pad1;
    float3 LightDir;        // Simple directional light
    float LightIntensity;
    float3 AmbientColor;
    float Pad2;
    float3 CameraPos;       // World-space camera position (for specular)
    float Pad3;
};

ConstantBuffer<ShadeUniforms> Uniforms;

// Binning data
StructuredBuffer<uint> PixelCoordBuffer;
StructuredBuffer<uint> BinOffsets;
StructuredBuffer<uint> BinCounts;

// VisBuffer + scene data
Texture2D<uint> VisBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;

// Output
RWTexture2D<float4> OutputColor;

// ── DeformCache resources (optional, for cached shade path) ──
ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;

// ── 三角形解码结果，供后续 position fetch + buildPixelContext 共用 ──
struct TriangleInfo
{
    uint visibleClusterIndex;
    uint triangleID;
    uint instanceID;
    uint vi0, vi1, vi2;
    uint pageOffset;
    int3 intBase;
    uint vStart;
    uint totalVertexCount;
    uint posOffset;
    uint attrOffset;
    uint idxOffset;
};

/// 从 VisBuffer 像素解码三角形信息。返回 false 表示该像素应跳过。
bool decodeTriangle(uint2 pixelCoord, out TriangleInfo tri)
{
    tri = {};
    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return false;

    uint visData = VisBuffer[pixelCoord];
    if (visData == 0)
        return false;

    tri.visibleClusterIndex = (visData >> 7) - 1;
    tri.triangleID = visData & 0x7F;

    DrawRequest req = VisibleClusters[tri.visibleClusterIndex];
    tri.pageOffset = req.PageOffset;
    tri.instanceID = req.InstanceID;

    uint clusterID = req.ClusterID;
    uint tStart; uint packedCounts;
    LoadClusterInfo(PageHeap, tri.pageOffset, clusterID, tri.intBase, tri.vStart, tStart, packedCounts);

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

/// Inline 路径：从 PageHeap 反量化 + evaluate → 3 个变形后世界空间位置
void evaluateTrianglePositions<TVE : IVertexEvaluate>(
    TVE evaluator, TriangleInfo tri,
    out float3 wp0, out float3 wp1, out float3 wp2)
{
    float3 pos0 = FetchVertexPosition(PageHeap, tri.pageOffset, tri.posOffset, tri.vStart, tri.vi0, tri.intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
    float3 pos1 = FetchVertexPosition(PageHeap, tri.pageOffset, tri.posOffset, tri.vStart, tri.vi1, tri.intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
    float3 pos2 = FetchVertexPosition(PageHeap, tri.pageOffset, tri.posOffset, tri.vStart, tri.vi2, tri.intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);

    GpuTransform t = Instances[tri.instanceID];
    float3 wpos0 = ApplyTransform(pos0, t);
    float3 wpos1 = ApplyTransform(pos1, t);
    float3 wpos2 = ApplyTransform(pos2, t);

    VertexEvalContext ec;
    ec.instanceID = tri.instanceID; ec.transform = t;
    ec.pageHeap = PageHeap; ec.pageOffset = tri.pageOffset;
    ec.vStart = tri.vStart; ec.totalVertexCount = tri.totalVertexCount;
    ec.attrOffset = tri.attrOffset;

    ec.objectPos = pos0; ec.worldPos = wpos0; ec.localVertIdx = tri.vi0;
    wp0 = evaluator.getPosition(evaluator.evaluate(ec));
    ec.objectPos = pos1; ec.worldPos = wpos1; ec.localVertIdx = tri.vi1;
    wp1 = evaluator.getPosition(evaluator.evaluate(ec));
    ec.objectPos = pos2; ec.worldPos = wpos2; ec.localVertIdx = tri.vi2;
    wp2 = evaluator.getPosition(evaluator.evaluate(ec));
}

/// Cached 路径：从 DeformCache 读取 3 个变形后世界空间位置
void fetchCachedTrianglePositions<TVE : IVertexEvaluate>(
    TVE evaluator, TriangleInfo tri,
    ByteAddressBuffer cache, StructuredBuffer<uint> offsets,
    out float3 wp0, out float3 wp1, out float3 wp2)
{
    uint cacheBase = offsets[tri.visibleClusterIndex];
    uint stride = evaluator.getCacheStride();
    wp0 = evaluator.getPosition(evaluator.readCache(cache, (cacheBase + tri.vi0) * stride));
    wp1 = evaluator.getPosition(evaluator.readCache(cache, (cacheBase + tri.vi1) * stride));
    wp2 = evaluator.getPosition(evaluator.readCache(cache, (cacheBase + tri.vi2) * stride));
}

/// 从三角形信息 + 3 个世界空间位置 → 构建完整 PixelContext。
/// 位置来源（inline evaluate 或 cache read）由调用方决定。
bool buildPixelContext(
    TriangleInfo tri,
    float3 dwp0, float3 dwp1, float3 dwp2,
    uint2 pixelCoord,
    out PixelContext ctx)
{
    ctx = {};
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = tri.instanceID;

    // Barycentric coordinates (using deformed positions)
    float3 sp0 = ProjectToScreen(dwp0, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float3 sp1 = ProjectToScreen(dwp1, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float3 sp2 = ProjectToScreen(dwp2, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);

    float2 pixelCenter = float2(pixelCoord) + 0.5;
    float3 bary = ComputeBarycentric(pixelCenter, sp0.xy, sp1.xy, sp2.xy);

    ctx.worldPos = bary.x * dwp0 + bary.y * dwp1 + bary.z * dwp2;

    // SoA attribute fetch — stream order: NORMAL(3B) → UV(4B)
    GpuTransform t = Instances[tri.instanceID];
    StreamCursor cursor = { tri.pageOffset + tri.attrOffset, tri.totalVertexCount };
    uint normalBase = cursor.advance(3);
    uint uvBase = cursor.advance(4);

    float3 n0 = FetchNormal3(PageHeap, normalBase, tri.vStart, tri.vi0);
    float3 n1 = FetchNormal3(PageHeap, normalBase, tri.vStart, tri.vi1);
    float3 n2 = FetchNormal3(PageHeap, normalBase, tri.vStart, tri.vi2);
    float3 interpNormal = normalize(bary.x * n0 + bary.y * n1 + bary.z * n2);

    // Transform normal to world space (quaternion rotation)
    float3 qvec = t.Rotation.xyz;
    float qw = t.Rotation.w;
    float3 uv_n = cross(qvec, interpNormal);
    float3 uuv_n = cross(qvec, uv_n);
    ctx.worldNormal = normalize(interpNormal + ((uv_n * qw) + uuv_n) * 2.0);

    float3 N = ctx.worldNormal;
    float3 up = abs(N.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 worldTangentDir = normalize(cross(up, N));
    ctx.worldTangent = float4(worldTangentDir, 1.0);

    float2 uv0 = FetchUV(PageHeap, uvBase, tri.vStart, tri.vi0);
    float2 uv1 = FetchUV(PageHeap, uvBase, tri.vStart, tri.vi1);
    float2 uv2 = FetchUV(PageHeap, uvBase, tri.vStart, tri.vi2);
    ctx.uv0 = bary.x * uv0 + bary.y * uv1 + bary.z * uv2;

    return true;
}

void handleDebugMode<TVE : IVertexEvaluate>(TVE evaluator, uint2 pixelCoord, uint debugMode)
{
    uint visData = VisBuffer[pixelCoord];
    if (visData == 0) return;

    uint visibleClusterIndex = (visData >> 7) - 1;
    uint triangleID = visData & 0x7F;
    DrawRequest req = VisibleClusters[visibleClusterIndex];

    float3 color;
    if (debugMode == 1) // ClusterID
    {
        uint h = PCGHash(req.ClusterID);
        color = ColorFromHash(h);
    }
    else if (debugMode == 7) // ShadingBin (per-dispatch)
    {
        uint h = PCGHash(Uniforms.ShadingBin);
        color = ColorFromHash(h);
    }
    else if (debugMode == 8) // Barycentric
    {
        TriangleInfo tri;
        if (decodeTriangle(pixelCoord, tri))
        {
            float3 wp0, wp1, wp2;
            evaluateTrianglePositions(evaluator, tri, wp0, wp1, wp2);
            float3 sp0 = ProjectToScreen(wp0, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
            float3 sp1 = ProjectToScreen(wp1, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
            float3 sp2 = ProjectToScreen(wp2, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
            color = ComputeBarycentric(float2(pixelCoord) + 0.5, sp0.xy, sp1.xy, sp2.xy);
        }
        else color = float3(0.0);
    }
    else if (debugMode == 9) // Normal
    {
        TriangleInfo tri;
        if (decodeTriangle(pixelCoord, tri))
        {
            float3 wp0, wp1, wp2;
            evaluateTrianglePositions(evaluator, tri, wp0, wp1, wp2);
            PixelContext debugCtx;
            buildPixelContext(tri, wp0, wp1, wp2, pixelCoord, debugCtx);
            color = debugCtx.worldNormal * 0.5 + 0.5;
        }
        else color = float3(0.0);
    }
    else if (debugMode == 10) // UV
    {
        TriangleInfo tri;
        if (decodeTriangle(pixelCoord, tri))
        {
            float3 wp0, wp1, wp2;
            evaluateTrianglePositions(evaluator, tri, wp0, wp1, wp2);
            PixelContext uvCtx;
            buildPixelContext(tri, wp0, wp1, wp2, pixelCoord, uvCtx);
            float2 uv = uvCtx.uv0;
            float checker = step(0.5, frac(uv.x * 8.0)) != step(0.5, frac(uv.y * 8.0)) ? 0.8 : 0.2;
            color = float3(uv.x, uv.y, checker);
        }
        else color = float3(0.0);
    }
    else if (debugMode == 11) // DIAG: Deformation offset
    {
        TriangleInfo tri;
        if (decodeTriangle(pixelCoord, tri))
        {
            // Only need first vertex for delta visualization
            float3 pos0 = FetchVertexPosition(PageHeap, tri.pageOffset, tri.posOffset, tri.vStart, tri.vi0, tri.intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
            GpuTransform t = Instances[tri.instanceID];
            float3 wpos0 = ApplyTransform(pos0, t);

            VertexEvalContext ec;
            ec.objectPos = pos0; ec.worldPos = wpos0; ec.localVertIdx = tri.vi0;
            ec.instanceID = tri.instanceID; ec.transform = t;
            ec.pageHeap = PageHeap; ec.pageOffset = tri.pageOffset;
            ec.vStart = tri.vStart; ec.totalVertexCount = tri.totalVertexCount;
            ec.attrOffset = tri.attrOffset;
            float3 dwp0 = evaluator.getPosition(evaluator.evaluate(ec));

            float3 delta = dwp0 - wpos0;
            color = float3(abs(delta.x) * 3.0, abs(delta.y) * 3.0, abs(delta.z) * 3.0);
        }
        else color = float3(0.0);
    }
    else color = float3(1, 0, 1);

    OutputColor[pixelCoord] = float4(color, 1.0);
}

void CSShade<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate>(uniform TVE vertexEval, uniform TMaterial material, uint3 tid)
{
    if (tid.x >= BinCounts[Uniforms.ShadingBin])
        return;

    uint pixelIdx = BinOffsets[Uniforms.ShadingBin] + tid.x;
    uint packedCoord = PixelCoordBuffer[pixelIdx];
    uint2 pixelCoord = uint2(packedCoord & 0xFFFF, packedCoord >> 16);

    // ── Debug modes (bypass material evaluation) ──
    if (Uniforms.DebugMode != 0)
    {
        if (pixelCoord.x < Uniforms.ScreenWidth && pixelCoord.y < Uniforms.ScreenHeight)
        {
            handleDebugMode(vertexEval, pixelCoord, Uniforms.DebugMode);
        }
        return;
    }

    // ── Material evaluation ──
    TriangleInfo tri;
    if (!decodeTriangle(pixelCoord, tri))
        return;

    float3 wp0, wp1, wp2;
    evaluateTrianglePositions(vertexEval, tri, wp0, wp1, wp2);

    PixelContext ctx;
    if (!buildPixelContext(tri, wp0, wp1, wp2, pixelCoord, ctx))
        return;

    material.evaluateSurface(ctx, OutputColor);
}

/// Cached variant: reads deformed positions from DeformCache instead of evaluating.
void CSShadeFromCache<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate>(uniform TVE vertexEval, uniform TMaterial material, uint3 tid)
{
    if (tid.x >= BinCounts[Uniforms.ShadingBin])
        return;

    uint pixelIdx = BinOffsets[Uniforms.ShadingBin] + tid.x;
    uint packedCoord = PixelCoordBuffer[pixelIdx];
    uint2 pixelCoord = uint2(packedCoord & 0xFFFF, packedCoord >> 16);

    if (Uniforms.DebugMode != 0)
    {
        if (pixelCoord.x < Uniforms.ScreenWidth && pixelCoord.y < Uniforms.ScreenHeight)
            handleDebugMode(vertexEval, pixelCoord, Uniforms.DebugMode);
        return;
    }

    TriangleInfo tri;
    if (!decodeTriangle(pixelCoord, tri))
        return;

    float3 wp0, wp1, wp2;
    fetchCachedTrianglePositions(vertexEval, tri, DeformCache, CacheOffsets, wp0, wp1, wp2);

    PixelContext ctx;
    if (!buildPixelContext(tri, wp0, wp1, wp2, pixelCoord, ctx))
        return;

    material.evaluateSurface(ctx, OutputColor);
}

#endif
