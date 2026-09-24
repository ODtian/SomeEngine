
struct VSInput
{
    float2 pos : ATTRIB0;
    float2 uv  : ATTRIB1;
    float4 col : ATTRIB2;
};

struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float4 col : COLOR0;
};

cbuffer UniformBuffer : register(b0, space0)
{
    float4 g_Transform;
};

Texture2D g_Texture : register(t0, space0);
SamplerState g_Texture_sampler : register(s0, space0);

[shader("vertex")]
PSInput VSMain(VSInput input)
{
    PSInput output;
    float2 clipPos = input.pos * g_Transform.xy + g_Transform.zw;
    output.pos = float4(clipPos, 0.0, 1.0);
    output.uv = input.uv;
    output.col = input.col;
    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_Target
{
    float4 texCol = g_Texture.Sample(g_Texture_sampler, input.uv);
    return input.col * texCol;
}
