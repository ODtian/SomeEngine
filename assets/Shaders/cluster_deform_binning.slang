#include "cluster_common.slang"
#include "cluster_cull_structures.slang"

// ─── Definitions ───
struct DeformBinMeta
{
    uint BinCount;       // Number of material ranges in this bin
    uint Cursor;         // Counter during scatter
    uint ClusterOffset;  // Global offset in index buffer (prefix sum of BinCount)
    uint Padding;
};

// [0] = ThreadGroupCountX, [1] = Y, [2] = Z (DispatchComputeIndirect args)
struct DispatchIndirectArgs
{
    uint X;
    uint Y;
    uint Z;
}

// ─── Uniforms (reusing BinningUniforms struct) ───
struct BinningUniforms
{
    uint MaxBins;
    uint SlotCapacity;         // MaterialSlotBuffer capacity
    uint BinFieldIndex;        // VertexEval field index
    uint MaxVisibleClusters;
};

// ─── Bindings ───
[[vk::binding(0, 0)]] ConstantBuffer<BinningUniforms> Uniforms;

[[vk::binding(1, 0)]] StructuredBuffer<uint4> VisibleClusters;

[[vk::binding(3, 0)]] ByteAddressBuffer DrawArgs; 

// [BinCount] x 3 uints for DispatchComputeIndirect
[[vk::binding(4, 0)]] RWStructuredBuffer<DispatchIndirectArgs> DeformBinningDispatchArgs;

[[vk::binding(5, 0)]] RWStructuredBuffer<DeformBinMeta> RasterBinMeta; // Reusing name for convenience, actually DeformBinMeta

[[vk::binding(6, 0)]] RWStructuredBuffer<uint2> BinnedClusterIndexBuffer; // (visibleIndex, rangeStart<<16 | rangeEnd)
[[vk::binding(7, 0)]] StructuredBuffer<uint> MaterialSlotBuffer;
[[vk::binding(8, 0)]] ByteAddressBuffer PageHeap;

// ─── Helper: get deform bin key for a specific material range ───
uint ReadDeformMaterialBinKey(uint4 entry, uint localMatIdx, uint instanceID)
{
    GpuInstanceHeader header = InstanceHeaders[instanceID];
    uint slotOffset = header.MaterialSlotOffset + localMatIdx;
    return GetSlotField(MaterialSlotBuffer, slotOffset, Uniforms.BinFieldIndex, Uniforms.SlotCapacity);
}

// ─── Pass 0: Clear ───
[numthreads(64, 1, 1)]
void CSDeformBinClear(uint3 dtid : SV_DispatchThreadID)
{
    uint tid = dtid.x;
    if (tid >= Uniforms.MaxBins) return;
    RasterBinMeta[tid].BinCount = 0;
    RasterBinMeta[tid].Cursor = 0;
    RasterBinMeta[tid].ClusterOffset = 0;
    RasterBinMeta[tid].Padding = 0;
}

// ─── Pass 1: Prepare ───
[numthreads(1, 1, 1)]
void CSDeformBinPrepare(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    DeformBinningDispatchArgs[0].X = (totalVisible + 63) / 64;
    DeformBinningDispatchArgs[0].Y = 1;
    DeformBinningDispatchArgs[0].Z = 1;
}

// ─── Logic: Emit Ranges ───
void ProcessClusterRanges(uint visibleIndex, bool doScatter)
{
    uint4 entry = VisibleClusters[visibleIndex];
    uint pageOffset = entry.x;
    uint clusterID = entry.y;
    uint realInstanceID = entry.z;
    
    uint clustersOff = PageHeap.Load(pageOffset + 16);
    uint clusterOffset = pageOffset + clustersOff + clusterID * 64;

    uint packedMaterials = PageHeap.Load(clusterOffset + 48);
    uint packedRanges = PageHeap.Load(clusterOffset + 52);
    
    uint packedCounts = PageHeap.Load(clusterOffset + 44);
    uint triCount = (packedCounts >> 8) & 0xFF;

    bool isSlowPath = ((packedMaterials >> 24) & 0xFF) == 0xFF;
    
    if (isSlowPath)
    {
        uint materialTableOffset = PageHeap.Load(clusterOffset + 56);
        // TODO: enumerate ranges from external table
        // Fallback for now: treat entire cluster as single range with mat 0
        uint binKey = ReadDeformMaterialBinKey(entry, 0, realInstanceID);
        if (binKey >= Uniforms.MaxBins) binKey = 0;
        
        if (!doScatter)
        {
            uint unused;
            InterlockedAdd(RasterBinMeta[binKey].BinCount, 1, unused);
        }
        else
        {
            uint offset = RasterBinMeta[binKey].ClusterOffset;
            uint localIdx;
            InterlockedAdd(RasterBinMeta[binKey].Cursor, 1, localIdx);
            BinnedClusterIndexBuffer[offset + localIdx] = uint2(visibleIndex, (0 << 16) | triCount);
        }
    }
    else
    {
        // Fast path: up to 3 ranges
        uint range0End = packedRanges & 0xFF;
        uint range1End = (packedRanges >> 8) & 0xFF;
        
        uint ends[3];
        ends[0] = range0End;
        ends[1] = range1End;
        ends[2] = triCount;
        
        uint matIdx[3];
        matIdx[0] = packedMaterials & 0xFF;
        matIdx[1] = (packedMaterials >> 8) & 0xFF;
        matIdx[2] = (packedMaterials >> 16) & 0xFF;
        
        uint start = 0;
        for (int i = 0; i < 3; i++)
        {
            uint end = min(ends[i], triCount);
            if (end > start)
            {
                uint binKey = ReadDeformMaterialBinKey(entry, matIdx[i], realInstanceID);
                if (binKey >= Uniforms.MaxBins) binKey = 0;
                
                if (!doScatter)
                {
                    uint unused;
                    InterlockedAdd(RasterBinMeta[binKey].BinCount, 1, unused);
                }
                else
                {
                    uint offset = RasterBinMeta[binKey].ClusterOffset;
                    uint localIdx;
                    InterlockedAdd(RasterBinMeta[binKey].Cursor, 1, localIdx);
                    BinnedClusterIndexBuffer[offset + localIdx] = uint2(visibleIndex, (start << 16) | end);
                }
            }
            start = end;
        }
    }
}

// ─── Pass 2: Count ───
[numthreads(64, 1, 1)]
void CSDeformBinCount(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= totalVisible) return;
    
    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);
    
    ProcessClusterRanges(visibleIndex, false);
}

// ─── Pass 3: Reserve ───
// Prefix sum implementation for DeformBinMeta.ClusterOffset
#define MAX_BINS 256
groupshared uint sPrefixTotal[MAX_BINS];

[numthreads(MAX_BINS, 1, 1)]
void CSDeformBinReserve(uint3 dtid : SV_DispatchThreadID)
{
    uint tid = dtid.x;
    
    uint count = (tid < Uniforms.MaxBins) ? RasterBinMeta[tid].BinCount : 0;
    sPrefixTotal[tid] = count;
    GroupMemoryBarrierWithGroupSync();
    
    [unroll] for (uint s = 1; s < MAX_BINS; s <<= 1)
    {
        GroupMemoryBarrierWithGroupSync();
        uint valTotal = 0;
        if (tid >= s)
        {
            valTotal = sPrefixTotal[tid - s];
        }
        GroupMemoryBarrierWithGroupSync();
        
        if (tid >= s)
        {
            sPrefixTotal[tid] += valTotal;
        }
    }
    GroupMemoryBarrierWithGroupSync();
    
    uint exclusiveSumTotal = sPrefixTotal[tid] - count;
    
    if (tid >= Uniforms.MaxBins) return;
    
    RasterBinMeta[tid].ClusterOffset = exclusiveSumTotal;
    RasterBinMeta[tid].Cursor = 0; // reset for scatter
    
    // Write DispatchComputeIndirect args: 1 thread group per material range entry
    // Each group will process up to 128 vertices (the whole material range)
    // DeformBinningDispatchArgs follows BinnedDrawArgs convention: indexed by BinKey.
    // Note: the dispatch buffer offset is +1 from the base if we used it with offset, 
    // but here we just write an array of DispatchIndirectArgs.
}

[numthreads(64, 1, 1)]
void CSDeformBinReserveDispatchWrite(uint3 dtid : SV_DispatchThreadID)
{
    uint tid = dtid.x;
    if (tid >= Uniforms.MaxBins) return;
    
    uint count = RasterBinMeta[tid].BinCount;
    DeformBinningDispatchArgs[tid].X = count; // 1 group per entry
    DeformBinningDispatchArgs[tid].Y = 1;
    DeformBinningDispatchArgs[tid].Z = 1;
}

// ─── Pass 4: Scatter ───
[numthreads(64, 1, 1)]
void CSDeformBinScatter(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = DrawArgs.Load<CullDrawArgs>(0);
    uint totalVisible = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= totalVisible) return;
    
    uint visibleIndex;
    bool isSW;
    DecodeVisibleIndex(idx, visibleIndex, isSW);
    
    ProcessClusterRanges(visibleIndex, true);
}
