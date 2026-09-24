struct PSInput
{
    float4 Pos : SV_POSITION;
    float4 Color : COLOR;
};

[shader("vertex")]
PSInput VSMain(uint vertexID : SV_VertexID)
{
    float2 positions[3];
    positions[0] = float2(-0.5, -0.5);
    positions[1] = float2(0.0, 0.5);
    positions[2] = float2(0.5, -0.5);

    float4 colors[3];
    colors[0] = float4(1.0, 0.2, 0.2, 1.0);
    colors[1] = float4(0.2, 1.0, 0.2, 1.0);
    colors[2] = float4(0.2, 0.4, 1.0, 1.0);

    PSInput output;
    output.Pos = float4(positions[vertexID], 0.0, 1.0);
    output.Color = colors[vertexID];
    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_Target
{
    return input.Color;
}
