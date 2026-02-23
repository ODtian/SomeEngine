#include "common.slang"
#include "cluster_structures.slang"

struct DrawUniforms
{
    float4x4 ViewProj;
    float4x4 View;
    uint PageTableSize;
    uint DebugMode;
    float2 Pad;
};

ConstantBuffer<DrawUniforms> Uniforms;
StructuredBuffer<AABB> DebugAABBs;

struct VSOutput
{
    float4 Pos : SV_POSITION;
    float4 Color : COLOR;
};

static const float3 BoxVerts[8] = {
    float3(-1, -1, -1), float3( 1, -1, -1),
    float3( 1,  1, -1), float3(-1,  1, -1),
    float3(-1, -1,  1), float3( 1, -1,  1),
    float3( 1,  1,  1), float3(-1,  1,  1)
};

static const uint BoxIndices[24] = {
    0,1, 1,2, 2,3, 3,0, // Bottom
    4,5, 5,6, 6,7, 7,4, // Top
    0,4, 1,5, 2,6, 3,7  // Sides
};

[shader("vertex")]
VSOutput VSMain(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    AABB box = DebugAABBs[instanceID];
    uint index = BoxIndices[vertexID % 24];
    float3 v = BoxVerts[index];
    
    // Remap -1..1 to 0..1 then scale to size + offset
    float3 size = box.Max - box.Min;
    float3 center = (box.Max + box.Min) * 0.5;
    float3 halfSize = size * 0.5;
    
    float3 worldPos = center + v * halfSize;

    VSOutput output;
    output.Pos = mul(float4(worldPos, 1.0), Uniforms.ViewProj);
    
    // Unpack color
    float r = float((box.Color >> 0) & 0xFF) / 255.0;
    float g = float((box.Color >> 8) & 0xFF) / 255.0;
    float b = float((box.Color >> 16) & 0xFF) / 255.0;
    float a = float((box.Color >> 24) & 0xFF) / 255.0;
    
    output.Color = float4(r, g, b, a);
    return output;
}

[shader("pixel")]
float4 PSMain(VSOutput input) : SV_TARGET
{
    return input.Color;
}
