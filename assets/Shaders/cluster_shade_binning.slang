#include "cluster_common.slang"

struct ShadeBinUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint MaterialCount;        // Active material count this frame
    uint SlotCapacity;         // SlotBuffer SOA capacity (even)
    uint BinFieldIndex;        // Field index for this binning pass in SOA
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2D<uint> VisBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
StructuredBuffer<uint> SlotBuffer;
ByteAddressBuffer PageHeap;

#include "cluster_bin_io.slang"

uint ShadeKey(DrawRequest req, uint localTri)
{
    uint clustersOff = PageHeap.Load(req.PageOffset + 16);
    uint clusterOffset = req.PageOffset + clustersOff + req.ClusterID * GPU_CLUSTER_STRIDE_BYTES;
    uint packedMaterials = PageHeap.Load(clusterOffset + 48);
    uint packedRanges = PageHeap.Load(clusterOffset + 52);
    uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, localTri);
    uint slotOffset = GetInstanceSlotOffset(req.InstanceID);

    return SlotBin(
        SlotBuffer,
        slotOffset,
        localMaterialIndex,
        Uniforms.BinFieldIndex,
        Uniforms.SlotCapacity,
        Uniforms.MaterialCount);
}

// ─── Pass 1: Count ─── Dispatch: ceil(W/8), ceil(H/8), 1 ───

RWStructuredBuffer<uint> BinCounts;

[shader("compute")]
[numthreads(8, 8, 1)]
void CSBinCount(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= Uniforms.ScreenWidth || tid.y >= Uniforms.ScreenHeight) return;
    uint visData = VisBuffer[tid.xy];
    if (IsVisBufferEmpty(visData)) return;

    // VisBuffer -> DrawRequest -> PageHeap cluster material -> SlotBuffer -> ShadingBin
    uint visClusterIdx = DecodeVisBufferVisibleClusterIndex(visData);
    uint localTri = DecodeVisBufferTriangleID(visData);
    DrawRequest req = VisibleClusters[visClusterIdx];

    uint shadingBin = ShadeKey(req, localTri);
    CountShade(BinCounts, shadingBin);
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
void CSBinClearPrepare(uint3 tid : SV_DispatchThreadID)
{
    uint bin = tid.x;
    if (bin < Uniforms.MaterialCount)
    {
        BinCounts[bin] = 0;
        BinScatterCount[bin] = 0;
    }

    if (bin == 0)
        ReserveCounters[0] = 0;
}

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

    ReserveShade(
        BinOffsets,
        BinScatterCount,
        BinIndirectArgs,
        bin,
        Uniforms.MaterialCount,
        ShadeReserveBlockBase,
        inclusiveOffset,
        count);
}

// ─── Pass 3: Scatter ─── Dispatch: ceil(W/8), ceil(H/8), 1 ───

RWStructuredBuffer<uint> PixelCoordBuffer;

[shader("compute")]
[numthreads(8, 8, 1)]
void CSBinScatter(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= Uniforms.ScreenWidth || tid.y >= Uniforms.ScreenHeight) return;
    uint visData = VisBuffer[tid.xy];
    if (IsVisBufferEmpty(visData)) return;

    uint visClusterIdx = DecodeVisBufferVisibleClusterIndex(visData);
    uint localTri = DecodeVisBufferTriangleID(visData);
    DrawRequest req = VisibleClusters[visClusterIdx];

    uint shadingBin = ShadeKey(req, localTri);

    ScatterShade(
        PixelCoordBuffer,
        BinOffsets,
        BinScatterCount,
        shadingBin,
        (tid.y << 16) | tid.x);
}
