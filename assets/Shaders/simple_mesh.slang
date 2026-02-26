struct Constants
{
    float4x4 WorldViewProj;
    float4 Color;
};

ConstantBuffer<Constants> g_Constants;

struct VSInput
{
    float3 Pos : ATTRIB0;
};

struct PSInput
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
};

[shader("vertex")]
PSInput VSMain(VSInput input)
{
    PSInput output;
    output.Pos = mul(float4(input.Pos, 1.0), g_Constants.WorldViewProj);
    output.Normal = normalize(input.Pos);
    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    float3 lightDir = normalize(float3(1.0, 1.0, -1.0));
    float diff = max(dot(input.Normal, lightDir), 0.2);
    return g_Constants.Color * diff;
}
