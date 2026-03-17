#include "cluster_structures.slang"
#include "common.slang"

// ─── Binning Uniforms ───
struct BinningUniforms
{
    uint MaxBins;
    uint MaxClustersPerBin;
    uint Pad0;
    uint Pad1;
};

ConstantBuffer<BinningUniforms> Uniforms;

// ─── Input ───
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<GpuInstanceHeader> InstanceHeaders;
ByteAddressBuffer DrawArgs;                  // [4] = visible count for this phase
ByteAddressBuffer ClusterReadOffsetArgs;     // [4] = base offset into VisibleClusters (0 for Legacy/Phase1, N1 for Phase2)

// ─── Output ───
RWByteAddressBuffer RasterBinMeta;           // 16B per bin: (ClusterOffset, BinCapacity, BinCount, Pad)
RWStructuredBuffer<uint> BinnedClusterIndexBuffer;
RWByteAddressBuffer BinnedDrawArgs;          // 16B per bin: DrawInstanced args
RWByteAddressBuffer BinningDispatchArgs;     // 12B: (GroupX, GroupY, GroupZ) for DispatchIndirect

// ─── CSBinningPrepare: compute dispatch args from GPU-side count ───
[numthreads(1, 1, 1)]
void CSBinningPrepare(uint3 dtid : SV_DispatchThreadID)
{
    uint visibleCount = DrawArgs.Load(4);  // InstanceCount for this phase
    uint groupCount = (visibleCount + 63) / 64;
    BinningDispatchArgs.Store(0, groupCount);
    BinningDispatchArgs.Store(4, 1);
    BinningDispatchArgs.Store(8, 1);
}

// ─── CSBinningInit: initialize per-bin metadata + draw args ───
[numthreads(64, 1, 1)]
void CSBinningInit(uint3 dtid : SV_DispatchThreadID)
{
    uint binIdx = dtid.x;
    if (binIdx >= Uniforms.MaxBins)
        return;

    uint binMetaAddr = binIdx * 16;
    uint clusterOffset = binIdx * Uniforms.MaxClustersPerBin;

    RasterBinMeta.Store(binMetaAddr + 0, clusterOffset);
    RasterBinMeta.Store(binMetaAddr + 4, Uniforms.MaxClustersPerBin);
    RasterBinMeta.Store(binMetaAddr + 8, 0);  // BinCount = 0
    RasterBinMeta.Store(binMetaAddr + 12, 0);

    uint drawArgsAddr = binIdx * 16;
    BinnedDrawArgs.Store(drawArgsAddr + 0, 372);  // VertexCountPerInstance = 124 * 3
    BinnedDrawArgs.Store(drawArgsAddr + 4, 0);    // InstanceCount = 0
    BinnedDrawArgs.Store(drawArgsAddr + 8, 0);
    BinnedDrawArgs.Store(drawArgsAddr + 12, 0);
}

// ─── CSBinning: scatter visible clusters into bins ───
[numthreads(64, 1, 1)]
void CSBinning(uint3 dtid : SV_DispatchThreadID)
{
    uint visibleCount = DrawArgs.Load(4);
    uint baseOffset = ClusterReadOffsetArgs.Load(4);  // 0 for Legacy/Phase1, N1 for Phase2
    uint idx = dtid.x;
    if (idx >= visibleCount)
        return;

    uint4 entry = VisibleClusters[baseOffset + idx];
    uint instanceID = entry.z;

    GpuInstanceHeader header = InstanceHeaders[instanceID];
    uint binKey = header.RasterBinKey;
    if (binKey >= Uniforms.MaxBins)
        binKey = 0;

    uint binMetaAddr = binKey * 16;
    uint clusterOffset = RasterBinMeta.Load(binMetaAddr + 0);
    uint binCapacity = RasterBinMeta.Load(binMetaAddr + 4);

    uint localIdx;
    RasterBinMeta.InterlockedAdd(binMetaAddr + 8, 1, localIdx);

    if (localIdx < binCapacity)
    {
        BinnedClusterIndexBuffer[clusterOffset + localIdx] = baseOffset + idx;

        uint drawArgsAddr = binKey * 16;
        BinnedDrawArgs.InterlockedMax(drawArgsAddr + 4, localIdx + 1);
    }
}
