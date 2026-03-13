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

    float d = DepthTexture.Load(int3(coord, 0));
    HiZMip0[coord] = d;
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

    uint2 baseCoord = coord * 2;

    // For the last texel in each dimension, extend to cover any extra
    // row/column left over from odd source dimensions.
    // Normal texels sample a 2×2 region [base, base+1].
    // Edge texels sample [base, srcDim-1] which can be 2×2 or 3×2/2×3/3×3.
    uint endX = (coord.x == width  - 1) ? srcWidth  - 1 : baseCoord.x + 1;
    uint endY = (coord.y == height - 1) ? srcHeight - 1 : baseCoord.y + 1;

    float result = 0.0;
    for (uint y = baseCoord.y; y <= endY; y++)
        for (uint x = baseCoord.x; x <= endX; x++)
            result = max(result, SrcMip[uint2(x, y)]);

    DstMip[coord] = result;
}
