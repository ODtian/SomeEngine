// depth_merge.slang — Merge SW raster depth into HW depth target
// Fullscreen triangle pass: reads SW DepthUAV (R32_UINT reversed-Z)
// and outputs SV_Depth for HW depth buffer injection.

Texture2D<uint> SWDepthUAV;

struct VSOutput
{
    float4 Pos : SV_POSITION;
    float2 UV  : TEXCOORD0;
};

[shader("vertex")]
VSOutput VSFullscreen(uint vertexID : SV_VertexID)
{
    VSOutput output;
    // Fullscreen triangle: 3 vertices covering the entire screen
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    output.Pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    output.UV = float2(uv.x, 1.0 - uv.y);
    return output;
}

struct PSOutput
{
    // No color output — depth only
};

[shader("pixel")]
void PSDepthMerge(VSOutput input, out float depth : SV_Depth)
{
    uint2 pixelCoord = uint2(input.Pos.xy);
    uint swDepthBits = SWDepthUAV[pixelCoord];

    // DepthUAV stores 0 for unwritten pixels (clear value).
    // 0 in reversed-Z means "far plane" — skip to avoid overwriting.
    if (swDepthBits == 0)
        discard;

    // SW raster stored: asuint(1.0 - normalZ) = asuint(reversedZ)
    // Convert back: reversedZ = asfloat(bits), normalZ = 1.0 - reversedZ
    float reversedZ = asfloat(swDepthBits);
    float normalZ = 1.0 - reversedZ;

    depth = normalZ;
}
