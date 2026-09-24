#ifndef CLUSTER_BIN_IO_SLANG
#define CLUSTER_BIN_IO_SLANG

DispatchArgs BinDispatch(uint count)
{
    uint x = min(count, 65535u);
    uint y = max(1u, (count + 65534u) / 65535u);
    return DispatchArgs(x, y, 1);
}

bool IsBin(uint bin)
{
    return bin != BIN_INVALID;
}

uint ValidBin(uint key, uint maxBins)
{
    return key != SLOT_INVALID && key < maxBins ? key : BIN_INVALID;
}

uint SlotBin(
    StructuredBuffer<uint> slots,
    uint slotOffset,
    uint localMaterialIndex,
    uint field,
    uint capacity,
    uint maxBins)
{
    uint key = GetSlotField(slots, slotOffset, localMaterialIndex, field, capacity);
    return ValidBin(key, maxBins);
}

uint SlotBin(
    StructuredBuffer<uint> slots,
    uint instanceID,
    uint field,
    uint capacity,
    uint maxBins)
{
    uint slotOffset = GetInstanceSlotOffset(instanceID);
    return SlotBin(slots, slotOffset, 0u, field, capacity, maxBins);
}

void WriteDispatch(
    RWByteAddressBuffer args,
    uint row,
    uint count)
{
    StoreDispatchArgs(args, row * 12u, BinDispatch(count));
}

void ClearRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    uint bin,
    uint maxBins)
{
    if (bin >= maxBins) return;

    bins[bin].BinSWCount = 0;
    bins[bin].BinHWCount = 0;
    bins[bin].SWCursor = 0;
    bins[bin].HWCursor = 0;
}

void ClearDeform(
    RWStructuredBuffer<DeformBinEntry> bins,
    uint bin,
    uint maxBins)
{
    if (bin >= maxBins) return;

    bins[bin].ClusterOffset = 0;
    bins[bin].BinCount = 0;
    bins[bin].Cursor = 0;
    bins[bin].Pad = 0;
}

void CountRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    uint bin,
    bool sw)
{
    if (!IsBin(bin)) return;

    uint unused;
    if (sw)
        InterlockedAdd(bins[bin].BinSWCount, 1, unused);
    else
        InterlockedAdd(bins[bin].BinHWCount, 1, unused);
}

void CountDeform(
    RWStructuredBuffer<DeformBinEntry> bins,
    uint bin)
{
    if (!IsBin(bin)) return;

    uint unused;
    InterlockedAdd(bins[bin].BinCount, 1, unused);
}

void CountShade(
    RWStructuredBuffer<uint> counts,
    uint bin)
{
    if (!IsBin(bin)) return;

    uint unused;
    InterlockedAdd(counts[bin], 1, unused);
}

void ReserveRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    RWByteAddressBuffer drawArgs,
    RWByteAddressBuffer hwArgs,
    RWByteAddressBuffer swArgs,
    uint bin,
    uint maxBins,
    uint totalBase,
    uint swBase,
    uint totalInc,
    uint swInc,
    uint swCount,
    uint hwCount)
{
    if (bin >= maxBins) return;

    uint total = swCount + hwCount;
    uint totalOffset = totalBase + totalInc - total;
    uint swOffset = swBase + swInc - swCount;

    bins[bin].ClusterOffset = totalOffset;
    bins[bin].BinCapacity = total;
    bins[bin].SWCursor = 0;
    bins[bin].HWCursor = 0;
    bins[bin].SWOffset = swOffset;

    StoreDrawInstancedArgs(
        drawArgs,
        bin * 16u,
        DrawInstancedArgs(96, total, 0, totalOffset));
    StoreDrawInstancedArgs(
        hwArgs,
        bin * 16u,
        DrawInstancedArgs(96, hwCount, 0, totalOffset + swCount));
    WriteDispatch(swArgs, bin, total);
    WriteDispatch(swArgs, maxBins + bin, swCount);
}

void ReserveDeform(
    RWStructuredBuffer<DeformBinEntry> bins,
    RWByteAddressBuffer args,
    uint bin,
    uint maxBins,
    uint baseOffset,
    uint inclusive,
    uint count)
{
    if (bin >= maxBins) return;

    bins[bin].ClusterOffset = baseOffset + inclusive - count;
    bins[bin].Cursor = 0;
    WriteDispatch(args, bin, count);
}

void ScatterRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    RWStructuredBuffer<uint2> indices,
    uint bin,
    uint visible,
    uint triStart,
    uint triEnd,
    bool sw)
{
    if (!IsBin(bin)) return;

    uint offset = bins[bin].ClusterOffset;
    if (sw)
    {
        uint local;
        InterlockedAdd(bins[bin].SWCursor, 1, local);
        indices[offset + local] = uint2(visible, (triStart << 16) | triEnd);
        return;
    }

    uint swCount = bins[bin].BinSWCount;
    uint local;
    InterlockedAdd(bins[bin].HWCursor, 1, local);
    indices[offset + swCount + local] = uint2(visible, (triStart << 16) | triEnd);
}

void ScatterDeform(
    RWStructuredBuffer<DeformBinEntry> bins,
    RWStructuredBuffer<uint2> indices,
    RWStructuredBuffer<uint> offsets,
    uint bin,
    uint visible,
    uint sentinel)
{
    if (!IsBin(bin)) return;

    uint offset = bins[bin].ClusterOffset;
    uint local;
    InterlockedAdd(bins[bin].Cursor, 1, local);
    offsets[visible] = sentinel;
    indices[offset + local] = uint2(visible, 0u);
}

void ReserveShade(
    RWStructuredBuffer<uint> offsets,
    RWStructuredBuffer<uint> scatter,
    RWByteAddressBuffer args,
    uint bin,
    uint maxBins,
    uint baseOffset,
    uint inclusive,
    uint count)
{
    if (bin >= maxBins) return;

    offsets[bin] = baseOffset + inclusive - count;
    scatter[bin] = 0;
    WriteDispatch(args, bin, count);
}

void ScatterShade(
    RWStructuredBuffer<uint> coords,
    RWStructuredBuffer<uint> offsets,
    RWStructuredBuffer<uint> scatter,
    uint bin,
    uint packedCoord)
{
    if (!IsBin(bin)) return;

    uint writeIdx;
    InterlockedAdd(scatter[bin], 1, writeIdx);
    coords[offsets[bin] + writeIdx] = packedCoord;
}

#endif
