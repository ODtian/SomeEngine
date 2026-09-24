struct GpuTransform
{
    float4 Rotation;
    float3 Position;
    float Scale;
    float3 Stretch;
    float Padding;
};

StructuredBuffer<GpuTransform> Transforms;

struct PSInput
{
    float4 Pos : SV_POSITION;
    float4 Color : COLOR;
};

[shader("vertex")]
PSInput VSMain(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    float2 positions[3];
    positions[0] = float2(-0.05, -0.05);
    positions[1] = float2(0.0, 0.05);
    positions[2] = float2(0.05, -0.05);

    GpuTransform transform = Transforms[instanceID];
    float3 position = float3(positions[vertexID], 0.0) + transform.Position;

    PSInput output;
    output.Pos = float4(position, 1.0);
    output.Color = float4(transform.Position.x + 0.5, transform.Position.y + 0.5, 0.5, 1.0);
    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_Target
{
    return input.Color;
}
