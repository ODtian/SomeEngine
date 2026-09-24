struct FullscreenVSOut
{
    float4 Position : SV_POSITION;
};

[shader("vertex")]
FullscreenVSOut VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };

    FullscreenVSOut output;
    output.Position = float4(positions[vertexId], 0.0, 1.0);
    return output;
}
