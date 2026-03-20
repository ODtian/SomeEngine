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
StructuredBuffer<GpuInstanceHeader> InstanceHeaders;
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

    // VisBuffer → DrawRequest → PageHeap cluster → MaterialSlotBuffer → ShadingBin
    uint visClusterIdx = (visData >> 7) - 1;
    uint triIdx = visData & 0x7F;

    DrawRequest req = VisibleClusters[visClusterIdx];

    uint packedMaterials, packedRanges;
    LoadClusterMaterialInfo(PageHeap, req.PageOffset, req.ClusterID, packedMaterials, packedRanges);

    uint localMatIdx = GetLocalMaterialIndexRaw(packedMaterials, packedRanges, triIdx);
    uint slotOffset = InstanceHeaders[req.InstanceID].MaterialSlotOffset + localMatIdx;
    uint shadingBin = GetSlotField(MaterialSlotBuffer, slotOffset, Uniforms.BinFieldIndex, Uniforms.SlotCapacity);

    uint dummy;
    InterlockedAdd(BinCounts[shadingBin], 1, dummy);
}

// ─── Pass 2: Reserve ─── Dispatch: 1, 1, 1 ───

#define MAX_MATERIALS 256

RWStructuredBuffer<uint> BinOffsets;
RWByteAddressBuffer BinIndirectArgs;
RWStructuredBuffer<uint> BinScatterCount;

// Simple prefix sum for up to MAX_MATERIALS entries
groupshared uint sPrefix[MAX_MATERIALS];

[shader("compute")]
[numthreads(MAX_MATERIALS, 1, 1)]
void CSBinReserve(uint tid : SV_GroupThreadID)
{
    uint count = (tid < Uniforms.MaterialCount) ? BinCounts[tid] : 0;

    // Inclusive scan via shared memory
    sPrefix[tid] = count;
    GroupMemoryBarrierWithGroupSync();

    // Up-sweep (reduce)
    [unroll] for (uint s = 1; s < MAX_MATERIALS; s <<= 1)
    {
        GroupMemoryBarrierWithGroupSync();
        uint val = 0;
        if (tid >= s)
            val = sPrefix[tid - s];
        GroupMemoryBarrierWithGroupSync();
        sPrefix[tid] += val;
    }
    GroupMemoryBarrierWithGroupSync();

    // sPrefix[tid] is now inclusive prefix sum; convert to exclusive
    uint exclusiveSum = sPrefix[tid] - count;

    if (tid >= Uniforms.MaterialCount) return;

    BinOffsets[tid] = exclusiveSum;
    BinScatterCount[tid] = 0;

    // Write indirect dispatch args: {GroupX, 1, 1}
    uint groups = (count + 63) / 64;
    BinIndirectArgs.Store(tid * 12 + 0, groups);
    BinIndirectArgs.Store(tid * 12 + 4, 1);
    BinIndirectArgs.Store(tid * 12 + 8, 1);
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

    uint visClusterIdx = (visData >> 7) - 1;
    uint triIdx = visData & 0x7F;

    DrawRequest req = VisibleClusters[visClusterIdx];

    uint packedMaterials, packedRanges;
    LoadClusterMaterialInfo(PageHeap, req.PageOffset, req.ClusterID, packedMaterials, packedRanges);

    uint localMatIdx = GetLocalMaterialIndexRaw(packedMaterials, packedRanges, triIdx);
    uint slotOffset = InstanceHeaders[req.InstanceID].MaterialSlotOffset + localMatIdx;
    uint shadingBin = GetSlotField(MaterialSlotBuffer, slotOffset, Uniforms.BinFieldIndex, Uniforms.SlotCapacity);

    uint writeIdx;
    InterlockedAdd(BinScatterCount[shadingBin], 1, writeIdx);
    PixelCoordBuffer[BinOffsets[shadingBin] + writeIdx] = (tid.y << 16) | tid.x;
}
