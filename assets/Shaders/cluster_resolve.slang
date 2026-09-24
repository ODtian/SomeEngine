#include "cluster_common.slang"

struct ResolveUniforms
{
    float4x4 ViewProj;
    float4x4 View;
    uint PageTableSize;
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
};

ConstantBuffer<ResolveUniforms> Uniforms;

Texture2D<uint> VisBuffer;
Texture2D<float> DepthBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;

RWTexture2D<float4> OutputColor;

[shader("compute")]
[numthreads(8, 8, 1)]
void CSResolve(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pixelCoord = dispatchThreadID.xy;

    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return;

    uint visData = VisBuffer[pixelCoord];

    // 0 means no geometry (clear value)
    if (IsVisBufferEmpty(visData))
    {
        OutputColor[pixelCoord] = float4(0, 0, 0, 1);
        return;
    }

    uint visibleClusterIndex = DecodeVisBufferVisibleClusterIndex(visData);
    uint triangleID = DecodeVisBufferTriangleID(visData);

    // Look up the draw request
    DrawRequest req = VisibleClusters[visibleClusterIndex];
    uint pageOffset = req.PageOffset;
    uint clusterID = req.ClusterID;
    uint realInstanceID = req.InstanceID;

    // Load cluster info
    int3 intBase;
    uint vStart;
    uint tStart;
    uint packedCounts;
    LoadClusterInfo(PageHeap, pageOffset, clusterID, intBase, vStart, tStart, packedCounts);

    uint lodLevel = (packedCounts >> 16) & 0xFF;

    // Load header offsets
    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint posOffset = header1.y;
    uint idxOffset = header1.w;

    // Fetch 3 vertex indices for this triangle
    uint vi0 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 0);
    uint vi1 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 1);
    uint vi2 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 2);

    // Fetch 3 vertex positions (object space, dequantized)
    float3 pos0 = FetchVertexPosition(PageHeap, pageOffset, posOffset, vStart, vi0, intBase);
    float3 pos1 = FetchVertexPosition(PageHeap, pageOffset, posOffset, vStart, vi1, intBase);
    float3 pos2 = FetchVertexPosition(PageHeap, pageOffset, posOffset, vStart, vi2, intBase);

    // Transform to world space
    GpuTransform t = Instances[realInstanceID];
    float3 wp0 = ApplyTransform(pos0, t);
    float3 wp1 = ApplyTransform(pos1, t);
    float3 wp2 = ApplyTransform(pos2, t);

    // Compute face normal from world-space triangle
    float3 edge1 = wp1 - wp0;
    float3 edge2 = wp2 - wp0;
    float3 faceNormal = normalize(cross(edge1, edge2));

    // Output based on debug mode
    float3 color;
    if (Uniforms.DebugMode == 1) // ClusterID visualization
    {
        uint h = PCGHash(clusterID);
        color = ColorFromHash(h);
    }
    else if (Uniforms.DebugMode == 2) // LOD visualization
    {
        float l = float(lodLevel) / 10.0;
        color = float3(l, 1.0 - l, 0.0);
    }
    else if (Uniforms.DebugMode == 3) // SW/HW raster debug view
    {
        bool isSW = IsVisBufferSWRaster(visData);
        // Pure classification color: blue = SW rasterized, red = HW rasterized.
        color = isSW ? float3(0.05, 0.25, 1.0) : float3(1.0, 0.10, 0.05);
    }
    else if (Uniforms.DebugMode == 0) // Normal shading (face normal fallback)
    {
        color = faceNormal * 0.5 + 0.5;
    }
    else
    {
        // Material debug modes are handled by the material shade pass.
        // Don't write here; let the shade pass handle them.
        return;
    }

    OutputColor[pixelCoord] = float4(color, 1.0);
}
