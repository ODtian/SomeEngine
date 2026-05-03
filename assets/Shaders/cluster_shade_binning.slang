#include "cluster_common.slang"

struct ShadeBinUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint MaterialCount;        // Active material count this frame
    uint SlotCapacity;         // MaterialSlotBuffer SOA capacity (even)
    uint BinFieldIndex;        // Field index for this binning pass in SOA
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2D<uint> VisBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
StructuredBuffer<uint> MaterialSlotBuffer;
ByteAddressBuffer PageHeap;

// ─── Pass 1: Count ─── Dispatch: ceil(W/8), ceil(H/8), 1 ───

RWStructuredBuffer<uint> BinCounts;

[shader("compute")]
[numthreads(8, 8, 1)]
void CSBinCount(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= Uniforms.ScreenWidth || tid.y >= Uniforms.ScreenHeight) return;
    uint visData = VisBuffer[tid.xy];
    if (visData == 0) return;
    uint visDataClean = visData & 0x7FFFFFFFu;

    // VisBuffer → DrawRequest → PageHeap cluster → MaterialSlotBuffer → ShadingBin
    uint visClusterIdx = (visDataClean >> 7) - 1;
    DrawRequest req = VisibleClusters[visClusterIdx];

    uint slotOffset = GetInstanceMaterialSlotOffset(req.InstanceID);
    uint shadingBin = GetSlotField(MaterialSlotBuffer, slotOffset, Uniforms.BinFieldIndex, Uniforms.SlotCapacity);

    uint dummy;
    InterlockedAdd(BinCounts[shadingBin], 1, dummy);
}

// ─── Pass 2: Reserve ─── Dispatch: 1, 1, 1 ───

RWStructuredBuffer<uint> BinOffsets;
RWByteAddressBuffer BinIndirectArgs;
RWStructuredBuffer<uint> BinScatterCount;
RWStructuredBuffer<uint> ReserveCounters;

#define SHADE_BIN_RESERVE_BLOCK_SIZE 128

groupshared uint ShadeReservePrefix[SHADE_BIN_RESERVE_BLOCK_SIZE];
groupshared uint ShadeReserveBlockBase;

[shader("compute")]
[numthreads(SHADE_BIN_RESERVE_BLOCK_SIZE, 1, 1)]
void CSBinReserve(uint3 tid : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint bin = tid.x;
    uint count = bin < Uniforms.MaterialCount ? BinCounts[bin] : 0;

    ShadeReservePrefix[groupIndex] = count;
    GroupMemoryBarrierWithGroupSync();

    for (uint scanOffset = 1; scanOffset < SHADE_BIN_RESERVE_BLOCK_SIZE; scanOffset <<= 1)
    {
        uint addend = groupIndex >= scanOffset ? ShadeReservePrefix[groupIndex - scanOffset] : 0;
        GroupMemoryBarrierWithGroupSync();
        ShadeReservePrefix[groupIndex] += addend;
        GroupMemoryBarrierWithGroupSync();
    }

    uint inclusiveOffset = ShadeReservePrefix[groupIndex];
    uint blockSum = ShadeReservePrefix[SHADE_BIN_RESERVE_BLOCK_SIZE - 1];

    if (groupIndex == 0)
    {
        InterlockedAdd(ReserveCounters[0], blockSum, ShadeReserveBlockBase);
    }
    GroupMemoryBarrierWithGroupSync();

    if (bin >= Uniforms.MaterialCount) return;

    uint offset = ShadeReserveBlockBase + inclusiveOffset - count;
    BinOffsets[bin] = offset;
    BinScatterCount[bin] = 0;

    // Write indirect dispatch args: {GroupX, 1, 1}
    uint groups = (count + 63) / 64;
    BinIndirectArgs.Store<DispatchArgs>(bin * 12, DispatchArgs(groups, 1, 1));
}

// ─── Pass 3: Scatter ─── Dispatch: ceil(W/8), ceil(H/8), 1 ───

RWStructuredBuffer<uint> PixelCoordBuffer;

[shader("compute")]
[numthreads(8, 8, 1)]
void CSBinScatter(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= Uniforms.ScreenWidth || tid.y >= Uniforms.ScreenHeight) return;
    uint visData = VisBuffer[tid.xy];
    if (visData == 0) return;
    uint visDataClean = visData & 0x7FFFFFFFu;

    uint visClusterIdx = (visDataClean >> 7) - 1;
    DrawRequest req = VisibleClusters[visClusterIdx];

    uint slotOffset = GetInstanceMaterialSlotOffset(req.InstanceID);
    uint shadingBin = GetSlotField(MaterialSlotBuffer, slotOffset, Uniforms.BinFieldIndex, Uniforms.SlotCapacity);

    uint writeIdx;
    InterlockedAdd(BinScatterCount[shadingBin], 1, writeIdx);
    PixelCoordBuffer[BinOffsets[shadingBin] + writeIdx] = (tid.y << 16) | tid.x;
}
