#ifndef VISBUFFER_ENCODING_SLANG
#define VISBUFFER_ENCODING_SLANG

static const uint VISBUFFER_EMPTY = 0u;
static const uint VISBUFFER_TRIANGLE_BITS = 7u;
static const uint VISBUFFER_TRIANGLE_MASK = (1u << VISBUFFER_TRIANGLE_BITS) - 1u;
static const uint VISBUFFER_SW_RASTER_MASK = 0x80000000u;
static const uint VISBUFFER_DATA_VALID_MASK = 0x7FFFFFFFu;

uint EncodeVisBuffer(uint visibleClusterIndex, uint triangleID)
{
    return ((visibleClusterIndex + 1u) << VISBUFFER_TRIANGLE_BITS)
        | (triangleID & VISBUFFER_TRIANGLE_MASK);
}

uint StripVisBufferDebugMarker(uint visData)
{
    return visData & VISBUFFER_DATA_VALID_MASK;
}

uint DecodeVisBufferVisibleClusterIndex(uint visData)
{
    return (StripVisBufferDebugMarker(visData) >> VISBUFFER_TRIANGLE_BITS) - 1u;
}

uint DecodeVisBufferTriangleID(uint visData)
{
    return StripVisBufferDebugMarker(visData) & VISBUFFER_TRIANGLE_MASK;
}

bool IsVisBufferEmpty(uint visData)
{
    return visData == VISBUFFER_EMPTY;
}

uint MarkVisBufferSWRaster(uint visData)
{
    return visData | VISBUFFER_SW_RASTER_MASK;
}

bool IsVisBufferSWRaster(uint visData)
{
    return (visData & VISBUFFER_SW_RASTER_MASK) != 0u;
}

#endif
