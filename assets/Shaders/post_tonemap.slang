#include "fullscreen_triangle.slang"

Texture2D<float4> SceneColor;

[shader("pixel")]
float4 PSMain(FullscreenVSOut input) : SV_Target
{
    int2 pixel = int2(input.Position.xy);
    float4 hdr = SceneColor.Load(int3(pixel, 0));

    float3 color = max(hdr.rgb, float3(0.0, 0.0, 0.0));
    float3 mapped = color / (color + float3(1.0, 1.0, 1.0));
    mapped = pow(saturate(mapped), 1.0 / 2.2);

    return float4(mapped, 1.0);
}
