
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

float4 PSMain(PSInput input) : SV_TARGET
{
    return input.col * g_Texture.Sample(g_Texture_sampler, input.uv);
}
