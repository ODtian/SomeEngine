#include "cluster_structures.slang"

struct DrawRequest
{
    uint PageOffset;
    uint ClusterID;
    uint InstanceID;
    uint Pad;
};

StructuredBuffer<DrawRequest> RequestBuffer;
ByteAddressBuffer PageHeap;

cbuffer DrawUniforms
{
    float4x4 ViewProj;
    float4x4 View;
    uint PageTableSize;
    uint DebugMode;
};

static const float PI = 3.14159265359;
static const uint SphereSectors = 16;
static const uint SphereStacks = 16;

float3 GetSphereVertex(uint sector, uint stack)
{
    float u = float(sector) / float(SphereSectors);
    float v = float(stack) / float(SphereStacks);

    float theta = u * 2.0 * PI;
    float phi = v * PI;

    return float3(
        sin(phi) * cos(theta),
        cos(phi),
        sin(phi) * sin(theta));
}

struct PSInput
{
    float4 Pos : SV_POSITION;
    float3 Color : COLOR;
};

[shader("vertex")]
PSInput VSMain(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    PSInput output;

    uint triID = vertexID / 3;
    uint vertInTri = vertexID % 3;
    uint quadID = triID / 2;
    uint triInQuad = triID % 2;
    uint stack = quadID / SphereSectors;
    uint sector = quadID % SphereSectors;

    uint s0 = stack;
    uint s1 = stack + 1;
    uint r0 = sector;
    uint r1 = sector + 1;

    float3 localPos;
    if (triInQuad == 0)
    {
        if (vertInTri == 0) localPos = GetSphereVertex(r0, s0);
        else if (vertInTri == 1) localPos = GetSphereVertex(r1, s0);
        else localPos = GetSphereVertex(r0, s1);
    }
    else
    {
        if (vertInTri == 0) localPos = GetSphereVertex(r1, s0);
        else if (vertInTri == 1) localPos = GetSphereVertex(r1, s1);
        else localPos = GetSphereVertex(r0, s1);
    }

    DrawRequest req = RequestBuffer[instanceID];
    uint pageOffset = req.PageOffset;
    uint clusterID = req.ClusterID;
    uint clustersStartOffset = PageHeap.Load(pageOffset + 16);

    uint clusterStride = GPU_CLUSTER_STRIDE_BYTES;
    uint clusterOffset = pageOffset + clustersStartOffset + clusterID * clusterStride;

    float4 v0 = asfloat(PageHeap.Load4(clusterOffset + 16));
    float3 center = v0.xyz;
    float radius = v0.w;

    float3 worldPos = center + localPos * radius;
    output.Pos = mul(float4(worldPos, 1.0), ViewProj);
    output.Color = float3(0.0, 1.0, 0.0);

    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_Target
{
    return float4(input.Color, 0.5);
}
