#include "common.hlsl"

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

#define PI 3.14159265359

float3 GetSphereVertex(uint sector, uint stack, uint R, uint S)
{
    float u = (float)sector / (float)R;
    float v = (float)stack / (float)S;
    
    float theta = u * 2.0 * PI;
    float phi = v * PI;
    
    float x = sin(phi) * cos(theta);
    float y = cos(phi);
    float z = sin(phi) * sin(theta);
    
    return float3(x, y, z);
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
    
    const uint R = 16; // Sectors
    const uint S = 16; // Stacks
    
    uint triID = vertexID / 3;
    uint vertInTri = vertexID % 3;
    
    uint quadID = triID / 2;
    uint triInQuad = triID % 2;
    
    uint stack = quadID / R;
    uint sector = quadID % R;
    
    uint s0 = stack;
    uint s1 = stack + 1;
    uint r0 = sector;
    uint r1 = sector + 1;
    
    float3 localPos;
    if (triInQuad == 0)
    {
        // Triangle 1: (r0, s0), (r1, s0), (r0, s1)
        if (vertInTri == 0) localPos = GetSphereVertex(r0, s0, R, S);
        else if (vertInTri == 1) localPos = GetSphereVertex(r1, s0, R, S);
        else localPos = GetSphereVertex(r0, s1, R, S);
    }
    else
    {
        // Triangle 2: (r1, s0), (r1, s1), (r0, s1)
        if (vertInTri == 0) localPos = GetSphereVertex(r1, s0, R, S);
        else if (vertInTri == 1) localPos = GetSphereVertex(r1, s1, R, S);
        else localPos = GetSphereVertex(r0, s1, R, S);
    }
    
    // Load Request
    DrawRequest req = RequestBuffer[instanceID];
    uint pageOffset = req.PageOffset;
    uint clusterID = req.ClusterID;
    
    // Load Cluster Center/Radius
    // Header: 16 bytes offset to ClustersOffset
    uint clustersStartOffset = PageHeap.Load(pageOffset + 16);
    
    // GPUCluster stride is 52 bytes
    uint clusterStride = 52;
    uint clusterOffset = pageOffset + clustersStartOffset + clusterID * clusterStride; 

    // Load LOD Center/Radius (Offset 16)
    float4 v0 = asfloat(PageHeap.Load4(clusterOffset + 16));
    float3 center = v0.xyz;
    float radius = v0.w;
    
    float3 worldPos = center + localPos * radius;
    output.Pos = mul(float4(worldPos, 1.0), ViewProj);
    output.Color = float3(0.0, 1.0, 0.0); // Green
    
    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    return float4(input.Color, 0.5);
}
