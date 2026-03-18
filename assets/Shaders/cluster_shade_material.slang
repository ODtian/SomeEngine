#include "cluster_common.slang"
#include "standard_pbr.slang"

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

// VisBuffer + scene data
Texture2D<uint> VisBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;
StructuredBuffer<GpuTransform> Instances;
StructuredBuffer<GpuInstanceHeader> InstanceHeaders;
ByteAddressBuffer InstanceDataHeap;

// Material textures (bound per-material via SRB)
Texture2D AlbedoMap;
Texture2D NormalMap;
Texture2D ARMMap;
SamplerState MaterialSampler;

float4 getInstancePropertyFloat4(PixelContext ctx, uint relativeOffset, float4 defaultValue)
{
    uint baseOffset = InstanceHeaders[ctx.instanceID].MetadataOffset;
    uint count = InstanceHeaders[ctx.instanceID].MetadataCount;
    if (count > 0)
    {
        return asfloat(InstanceDataHeap.Load4(baseOffset + relativeOffset));
    }
    return defaultValue;
}

// Output
RWTexture2D<float4> OutputColor;

/// Build a PixelContext from VisBuffer data at the given pixel.
/// Returns false if the pixel should be skipped (empty or out of bounds).
bool buildPixelContext(
    uint2 pixelCoord,
    out PixelContext ctx)
{
    ctx = {};
    ctx.pixelCoord = pixelCoord;

    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return false;

    uint visData = VisBuffer[pixelCoord];
    if (visData == 0)
        return false;

    // Decode VisBuffer
    uint visibleClusterIndex = (visData >> 7) - 1;
    uint triangleID = visData & 0x7F;

    DrawRequest req = VisibleClusters[visibleClusterIndex];
    uint pageOffset = req.PageOffset;
    uint clusterID = req.ClusterID;
    uint realInstanceID = req.InstanceID;

    ctx.instanceID = realInstanceID;

    // Load cluster info
    int3 intBase;
    uint vStart;
    uint tStart;
    uint packedCounts;
    LoadClusterInfo(PageHeap, pageOffset, clusterID, intBase, vStart, tStart, packedCounts);

    // Load header offsets
    uint totalVertexCount = PageHeap.Load(pageOffset + 4);
    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint posOffset = header1.y;
    uint attrOffset = header1.z;
    uint idxOffset = header1.w;

    // Fetch triangle vertex indices
    uint vi0 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 0);
    uint vi1 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 1);
    uint vi2 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 2);

    // Fetch positions (object space, dequantized)
    float3 pos0 = FetchVertexPosition(PageHeap, pageOffset, posOffset, vStart, vi0, intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
    float3 pos1 = FetchVertexPosition(PageHeap, pageOffset, posOffset, vStart, vi1, intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
    float3 pos2 = FetchVertexPosition(PageHeap, pageOffset, posOffset, vStart, vi2, intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);

    // Transform to world space
    GpuTransform t = Instances[realInstanceID];
    float3 wp0 = ApplyTransform(pos0, t);
    float3 wp1 = ApplyTransform(pos1, t);
    float3 wp2 = ApplyTransform(pos2, t);

    // Barycentric coordinates
    float3 sp0 = ProjectToScreen(wp0, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float3 sp1 = ProjectToScreen(wp1, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float3 sp2 = ProjectToScreen(wp2, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);

    float2 pixelCenter = float2(pixelCoord) + 0.5;
    float3 bary = ComputeBarycentric(pixelCenter, sp0.xy, sp1.xy, sp2.xy);

    // Interpolate world position
    ctx.worldPos = bary.x * wp0 + bary.y * wp1 + bary.z * wp2;

    // SoA attribute fetch — stream order: NORMAL(3B) → TANGENT(4B) → UV(4B)
    StreamCursor cursor = { pageOffset + attrOffset, totalVertexCount };
    uint normalBase = cursor.advance(3);   // NORMAL: 3 bytes/vertex
    cursor.advance(4);                      // TANGENT: 4 bytes/vertex (skip)
    uint uvBase = cursor.advance(4);        // UV: 4 bytes/vertex (Float16x2)

    float3 n0 = FetchNormal3(PageHeap, normalBase, vStart, vi0);
    float3 n1 = FetchNormal3(PageHeap, normalBase, vStart, vi1);
    float3 n2 = FetchNormal3(PageHeap, normalBase, vStart, vi2);
    float3 interpNormal = normalize(bary.x * n0 + bary.y * n1 + bary.z * n2);

    // Transform normal to world space (quaternion rotation)
    float3 qvec = t.Rotation.xyz;
    float qw = t.Rotation.w;
    float3 uv_n = cross(qvec, interpNormal);
    float3 uuv_n = cross(qvec, uv_n);
    ctx.worldNormal = normalize(interpNormal + ((uv_n * qw) + uuv_n) * 2.0);

    // UV interpolation
    float2 uv0 = FetchUV(PageHeap, uvBase, vStart, vi0);
    float2 uv1 = FetchUV(PageHeap, uvBase, vStart, vi1);
    float2 uv2 = FetchUV(PageHeap, uvBase, vStart, vi2);
    ctx.uv0 = bary.x * uv0 + bary.y * uv1 + bary.z * uv2;

    return true;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShade(uint3 tid : SV_DispatchThreadID)
{
    uint pixelIdx = BinOffsets[Uniforms.ShadingBin] + tid.x;
    uint packedCoord = PixelCoordBuffer[pixelIdx];
    uint2 pixelCoord = uint2(packedCoord & 0xFFFF, packedCoord >> 16);

    // ── Debug modes (bypass material evaluation) ──
    if (Uniforms.DebugMode != 0)
    {
        if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
            return;

        uint visData = VisBuffer[pixelCoord];
        if (visData == 0)
            return;

        uint visibleClusterIndex = (visData >> 7) - 1;
        uint triangleID = visData & 0x7F;
        DrawRequest req = VisibleClusters[visibleClusterIndex];

        float3 color;
        if (Uniforms.DebugMode == 1) // ClusterID
        {
            uint h = PCGHash(req.ClusterID);
            color = ColorFromHash(h);
        }
        else if (Uniforms.DebugMode == 7) // ShadingBin (per-dispatch)
        {
            uint h = PCGHash(Uniforms.ShadingBin);
            color = ColorFromHash(h);
        }
        else if (Uniforms.DebugMode == 8) // Barycentric
        {
            // Quick bary for debug (recompute)
            PixelContext debugCtx;
            if (buildPixelContext(pixelCoord, debugCtx))
            {
                // Recompute bary from screen positions for debug display
                int3 intBase; uint vStart; uint tStart; uint packedCounts;
                LoadClusterInfo(PageHeap, req.PageOffset, req.ClusterID, intBase, vStart, tStart, packedCounts);
                uint4 header1 = PageHeap.Load4(req.PageOffset + 16);
                uint posOff = header1.y; uint idxOff = header1.w;
                uint vi0 = FetchVertexIndex(PageHeap, req.PageOffset, idxOff, tStart, triangleID, 0);
                uint vi1 = FetchVertexIndex(PageHeap, req.PageOffset, idxOff, tStart, triangleID, 1);
                uint vi2 = FetchVertexIndex(PageHeap, req.PageOffset, idxOff, tStart, triangleID, 2);
                float3 p0 = FetchVertexPosition(PageHeap, req.PageOffset, posOff, vStart, vi0, intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
                float3 p1 = FetchVertexPosition(PageHeap, req.PageOffset, posOff, vStart, vi1, intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
                float3 p2 = FetchVertexPosition(PageHeap, req.PageOffset, posOff, vStart, vi2, intBase, Uniforms.QuantOrigin, Uniforms.QuantStep);
                GpuTransform t = Instances[req.InstanceID];
                float3 wp0 = ApplyTransform(p0, t); float3 wp1 = ApplyTransform(p1, t); float3 wp2 = ApplyTransform(p2, t);
                float3 sp0 = ProjectToScreen(wp0, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
                float3 sp1 = ProjectToScreen(wp1, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
                float3 sp2 = ProjectToScreen(wp2, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
                color = ComputeBarycentric(float2(pixelCoord) + 0.5, sp0.xy, sp1.xy, sp2.xy);
            }
            else
            {
                color = float3(0.0);
            }
        }
        else if (Uniforms.DebugMode == 9) // Normal
        {
            PixelContext debugCtx;
            if (buildPixelContext(pixelCoord, debugCtx))
                color = debugCtx.worldNormal * 0.5 + 0.5;
            else
                color = float3(0.0);
        }
        else if (Uniforms.DebugMode == 10) // UV
        {
            PixelContext uvCtx;
            if (buildPixelContext(pixelCoord, uvCtx))
            {
                // UV as color + checkerboard pattern
                float2 uv = uvCtx.uv0;
                float checker = step(0.5, frac(uv.x * 8.0)) != step(0.5, frac(uv.y * 8.0)) ? 0.8 : 0.2;
                color = float3(uv.x, uv.y, checker);
            }
            else
            {
                color = float3(0.0);
            }
        }
        else
        {
            color = float3(1, 0, 1); // fallback magenta
        }
        OutputColor[pixelCoord] = float4(color, 1.0);
        return;
    }

    // ── Material evaluation ──
    PixelContext ctx;
    if (!buildPixelContext(pixelCoord, ctx))
        return;

    // Sample material textures
    StandardPBRMaterial material;
    float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
    
    float4 tint = getInstancePropertyFloat4(ctx, 0, float4(1.0));
    
    material.baseColor = albedoSample.rgb * tint.rgb;
    material.metallic  = 0.0;
    material.roughness = 0.5;
    material.light.lightDir       = Uniforms.LightDir;
    material.light.lightIntensity = Uniforms.LightIntensity;
    material.light.ambientColor   = Uniforms.AmbientColor;
    material.light.cameraPos      = Uniforms.CameraPos;

    material.evaluateSurface(ctx, OutputColor);
}
