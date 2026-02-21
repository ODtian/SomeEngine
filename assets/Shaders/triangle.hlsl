struct GpuTransform
{
    float4 Rotation;
    float3 Position;
    float Scale;
    float3 Stretch;
    float Padding;
};

StructuredBuffer<GpuTransform> Transforms : register(t0);

struct PSInput
{
    float4 Pos : SV_POSITION;
    float4 Color : COLOR;
};

[shader("vertex")]
PSInput VSMain(uint VertID : SV_VertexID, uint InstID : SV_InstanceID)
{
    PSInput ps;
    float2 pos[3];
    pos[0] = float2(-0.05, -0.05); // Smaller triangle
    pos[1] = float2( 0.00,  0.05);
    pos[2] = float2( 0.05, -0.05);
    
    // Read transform
    GpuTransform t = Transforms[InstID];
    
    // Simple 2D offset for test
    float3 p = float3(pos[VertID], 0.0) + t.Position;
    
    ps.Pos = float4(p, 1.0);
    // Visualize position as color
    ps.Color = float4(t.Position.x + 0.5, t.Position.y + 0.5, 0.5, 1.0);
    return ps;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    return input.Color;
}
