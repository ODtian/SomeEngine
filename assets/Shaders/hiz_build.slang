Texture2D<float> DepthTexture;
RWTexture2D<float> HiZMip0;
RWTexture2D<float> HiZMip1;

RWTexture2D<float> SrcMip;
RWTexture2D<float> DstMip;
RWTexture2D<float> DstMip1;
RWTexture2D<float> DstMip2;

uint CoveredEnd(uint dstCoord, uint dstSize, uint srcSize)
{
    return (dstCoord == dstSize - 1) ? srcSize - 1 : dstCoord * 2 + 1;
}

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
void BuildMip0And1(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint2 coord = dispatchThreadID.xy;

    uint dstWidth;
    uint dstHeight;
    HiZMip1.GetDimensions(dstWidth, dstHeight);

    if (coord.x >= dstWidth || coord.y >= dstHeight)
        return;

    uint srcWidth;
    uint srcHeight;
    HiZMip0.GetDimensions(srcWidth, srcHeight);

    uint2 baseCoord = coord * 2;
    uint endX = CoveredEnd(coord.x, dstWidth, srcWidth);
    uint endY = CoveredEnd(coord.y, dstHeight, srcHeight);

    float result = 0.0;
    for (uint y = baseCoord.y; y <= endY; y++)
    {
        for (uint x = baseCoord.x; x <= endX; x++)
        {
            float d = DepthTexture.Load(int3(x, y, 0));
            HiZMip0[uint2(x, y)] = d;
            result = max(result, d);
        }
    }

    HiZMip1[coord] = result;
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
    uint endX = CoveredEnd(coord.x, width, srcWidth);
    uint endY = CoveredEnd(coord.y, height, srcHeight);

    float result = 0.0;
    for (uint y = baseCoord.y; y <= endY; y++)
        for (uint x = baseCoord.x; x <= endX; x++)
            result = max(result, SrcMip[uint2(x, y)]);

    DstMip[coord] = result;
}

[numthreads(8, 8, 1)]
void DownsampleTwoMips(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint2 coord = dispatchThreadID.xy;

    uint dstWidth;
    uint dstHeight;
    DstMip2.GetDimensions(dstWidth, dstHeight);

    if (coord.x >= dstWidth || coord.y >= dstHeight)
        return;

    uint midWidth;
    uint midHeight;
    DstMip1.GetDimensions(midWidth, midHeight);

    uint srcWidth;
    uint srcHeight;
    SrcMip.GetDimensions(srcWidth, srcHeight);

    uint2 midBase = coord * 2;
    uint midEndX = CoveredEnd(coord.x, dstWidth, midWidth);
    uint midEndY = CoveredEnd(coord.y, dstHeight, midHeight);

    float result = 0.0;
    for (uint midY = midBase.y; midY <= midEndY; midY++)
    {
        for (uint midX = midBase.x; midX <= midEndX; midX++)
        {
            uint2 midCoord = uint2(midX, midY);
            uint2 srcBase = midCoord * 2;
            uint srcEndX = CoveredEnd(midCoord.x, midWidth, srcWidth);
            uint srcEndY = CoveredEnd(midCoord.y, midHeight, srcHeight);

            float midResult = 0.0;
            for (uint srcY = srcBase.y; srcY <= srcEndY; srcY++)
                for (uint srcX = srcBase.x; srcX <= srcEndX; srcX++)
                    midResult = max(midResult, SrcMip[uint2(srcX, srcY)]);

            DstMip1[midCoord] = midResult;
            result = max(result, midResult);
        }
    }

    DstMip2[coord] = result;
}
