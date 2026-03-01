Texture2D<float> DepthTexture;
RWTexture2D<float> HiZMip0;

RWTexture2D<float> SrcMip;
RWTexture2D<float> DstMip;

[numthreads(8, 8, 1)]
void BuildMip0(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint2 coord = dispatchThreadID.xy;

    uint width;
    uint height;
    HiZMip0.GetDimensions(width, height);

    if (coord.x >= width || coord.y >= height)
        return;

    HiZMip0[coord] = DepthTexture.Load(int3(coord, 0));
}

[numthreads(8, 8, 1)]
void DownsampleMip(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint2 coord = dispatchThreadID.xy;

    uint width;
    uint height;
    DstMip.GetDimensions(width, height);

    if (coord.x >= width || coord.y >= height)
        return;

    uint srcWidth;
    uint srcHeight;
    SrcMip.GetDimensions(srcWidth, srcHeight);

    uint2 maxCoord = uint2(srcWidth - 1, srcHeight - 1);
    uint2 baseCoord = coord * 2;

    uint2 c0 = min(baseCoord, maxCoord);
    uint2 c1 = min(baseCoord + uint2(1, 0), maxCoord);
    uint2 c2 = min(baseCoord + uint2(0, 1), maxCoord);
    uint2 c3 = min(baseCoord + uint2(1, 1), maxCoord);

    float d0 = SrcMip[c0];
    float d1 = SrcMip[c1];
    float d2 = SrcMip[c2];
    float d3 = SrcMip[c3];

    DstMip[coord] = max(max(d0, d1), max(d2, d3));
}
