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
    var x = GetExportedValue(123);
    output.Normal += x.xxx;
    output.Normal += GetExportedValue((uint)123);
    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    float3 lightDir = normalize(float3(1.0, 1.0, -1.0));
    float diff = max(dot(input.Normal, lightDir), 0.2);
    return g_Constants.Color * diff;
}

export float16_t GetExportedValue<T>(T value)
    where T : IArithmetic
{
    return 42.0h;
}
