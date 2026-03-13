// Debug 2D projection overlay
// Reads DebugHiZOutput buffer and draws screen-space rectangles via SV_VertexID.
// Each entry = 48 bytes: minUv(8B), maxUv(8B), nearDepth(4B), hizDepth(4B), mip(4B), occluded(4B), ...
// 4 edges × 2 vertices = 8 vertices per entry (LINE LIST)

ByteAddressBuffer DebugHiZInput;

struct VSOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR;
};

// Edge table for a 2D rectangle: 4 edges, each pair of corner indices
// Corners: 0=min,min  1=max,min  2=max,max  3=min,max
static const int2 kRectEdges[4] = {
    int2(0, 1), // bottom: min,min -> max,min
    int2(1, 2), // right:  max,min -> max,max
    int2(2, 3), // top:    max,max -> min,max
    int2(3, 0), // left:   min,max -> min,min
};

[shader("vertex")]
VSOutput VSMain(uint vertexID : SV_VertexID)
{
    VSOutput output;

    uint entryIdx = vertexID / 8;
    uint localVtx = vertexID % 8;
    uint edgeIdx = localVtx / 2;
    uint endpointIdx = localVtx % 2;

    uint count = DebugHiZInput.Load(0);
    if (entryIdx >= count || entryIdx >= 4096)
    {
        output.Position = float4(0, 0, 0, 0);
        output.Color = float4(0, 0, 0, 0);
        return output;
    }

    uint base = 4 + entryIdx * 48;
    float minU = asfloat(DebugHiZInput.Load(base + 0));
    float minV = asfloat(DebugHiZInput.Load(base + 4));
    float maxU = asfloat(DebugHiZInput.Load(base + 8));
    float maxV = asfloat(DebugHiZInput.Load(base + 12));
    uint occluded = DebugHiZInput.Load(base + 28);

    // Get corner index (0-3)
    int cornerIdx = (endpointIdx == 0) ? kRectEdges[edgeIdx].x : kRectEdges[edgeIdx].y;

    // Compute corner UV
    float u = (cornerIdx == 1 || cornerIdx == 2) ? maxU : minU;
    float v = (cornerIdx == 2 || cornerIdx == 3) ? maxV : minV;

    // Convert UV [0,1] to NDC [-1,1] (Y flipped)
    float ndcX = u * 2.0 - 1.0;
    float ndcY = -(v * 2.0 - 1.0); // flip Y: UV=0 is top, NDC=1 is top

    // Place at depth 0 (closest in reverse-Z)
    output.Position = float4(ndcX, ndcY, 0.0, 1.0);

    if (occluded != 0)
        output.Color = float4(0.0, 0.8, 0.0, 1.0);  // green = occluded
    else
        output.Color = float4(1.0, 0.2, 0.2, 1.0);  // red = visible

    return output;
}

[shader("pixel")]
float4 PSMain(VSOutput input) : SV_Target
{
    return input.Color;
}
