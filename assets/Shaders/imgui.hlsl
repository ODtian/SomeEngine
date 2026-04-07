
struct VSInput
{
    float2 pos : ATTRIB0;
    float2 uv  : ATTRIB1;
    float4 col : ATTRIB2;
};

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
    float4 col : COLOR0;
};

cbuffer UniformBuffer
{
    float4x4 g_ProjectionMatrix;
};

Texture2D g_Texture;
SamplerState g_Texture_sampler;

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.pos = mul(float4(input.pos.xy, 0.0, 1.0), g_ProjectionMatrix);
    output.uv  = input.uv;
    output.col = input.col;
    return output;
}

// Heatmap: 0=blue, 0.5=green, 1=red
float3 Heatmap(float t)
{
    t = saturate(t);
    float3 c;
    c.r = saturate(1.5 - abs(t - 1.0) * 4.0);
    c.g = saturate(1.5 - abs(t - 0.5) * 4.0);
    c.b = saturate(1.5 - abs(t - 0.0) * 4.0);
    return c;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    float4 texCol = g_Texture.Sample(g_Texture_sampler, input.uv);

    // Detect R32_Float single-channel texture: GPU returns (r, 0, 0, 1)
    if (texCol.g == 0 && texCol.b == 0 && texCol.a == 1.0)
    {
        float d = texCol.r;

        // Cleared / sky (exactly 1.0) -> dark background
        if (d >= 1.0)
            return float4(0.05, 0.05, 0.08, 1.0) * input.col;

        // Standard Z-buffer: near~0, far=1.0.
        // Use 4th root to spread depth values clustered near 1.0.
        float invD = 1.0 - d;
        float vis = saturate(pow(max(invD, 1e-7), 0.25) * 5.0);
        float3 col = Heatmap(vis);  // near geometry=red, far geometry=blue
        return float4(col, 1.0) * input.col;
    }

    return input.col * texCol;
}
