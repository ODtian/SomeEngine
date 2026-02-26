#include "cluster_structures.slang"
#include "common.slang"

struct CullingUniforms
{
    float4x4 ViewProj;
    float3 CameraPos;
    float LodThreshold;

    float LodScale;
    uint MaxQueueNodes;
    uint MaxCandidates;
    uint Pad2;

    int ForcedLODLevel;
    uint InstanceCount;
    uint DebugMode;
    uint Pad3;
};

ConstantBuffer<CullingUniforms> Uniforms;

StructuredBuffer<GpuTransform> Instances;
StructuredBuffer<GpuInstanceHeader> InstanceHeaders;
ByteAddressBuffer PageHeap;
StructuredBuffer<uint3> CandidateClusters;
RWByteAddressBuffer CandidateCount;
RWByteAddressBuffer CandidateArgs;
RWByteAddressBuffer DrawArgs;
RWStructuredBuffer<uint4> VisibleClusters;

GPUCluster LoadCluster(uint pageOffset, uint clusterIndex)
{
    uint headerOffset = pageOffset;
    uint clustersOffset = PageHeap.Load(headerOffset + 16);
    uint offset = headerOffset + clustersOffset + (clusterIndex * 52);
    GPUCluster c;

    uint4 u1 = PageHeap.Load<uint4>(offset);
    c.Center = asfloat(u1.xyz);
    c.Radius = asfloat(u1.w);

    uint4 uLOD = PageHeap.Load<uint4>(offset + 16);
    c.LODCenter = asfloat(uLOD.xyz);
    c.LODRadius = asfloat(uLOD.w);

    uint4 u2 = PageHeap.Load<uint4>(offset + 32);
    c.LODError = asfloat(u2.x);
    c.VertexStart = u2.y;
    c.TriangleStart = u2.z;
    c.GroupId = (int)u2.w;

    c.PackedCounts = PageHeap.Load(offset + 48);
    return c;
}

[numthreads(1, 1, 1)]
void UpdateIndirectArgs(uint3 id: SV_DispatchThreadID)
{
    uint count = CandidateCount.Load(0);
    uint groups = (count + 63) / 64;
    CandidateArgs.Store(0, groups);
    CandidateArgs.Store(4, 1);
    CandidateArgs.Store(8, 1);
}

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint count = CandidateCount.Load(0);
    uint threadId = dispatchThreadID.x;
    if (threadId >= count)
        return;

    uint3 candidate = CandidateClusters[threadId];
    uint pageOffset = candidate.x;
    uint clusterID = candidate.y;
    uint instanceID = candidate.z;

    GPUCluster cluster = LoadCluster(pageOffset, clusterID);
    GpuTransform t = Instances[instanceID];

    bool visible = true;

    if (Uniforms.ForcedLODLevel >= 0)
    {
        uint lod = (cluster.PackedCounts >> 16) & 0xFF;
        if (lod != uint(Uniforms.ForcedLODLevel))
            visible = false;
    }
    else if (Uniforms.DebugMode == 0)
    {
        float3 worldCenter = ApplyTransform(cluster.LODCenter, t);
        float maxScale = t.Scale * max(max(t.Stretch.x, t.Stretch.y), t.Stretch.z);
        float worldRadius = cluster.LODRadius * maxScale;

        float clusterDist = max(0.0, distance(worldCenter, Uniforms.CameraPos) - worldRadius);
        if (cluster.LODError * Uniforms.LodScale > Uniforms.LodThreshold * clusterDist)
            visible = false;
    }

    if (visible)
    {
        uint outIdx;
        DrawArgs.InterlockedAdd(4, 1, outIdx);
        if (outIdx < Uniforms.MaxCandidates)
        {
            VisibleClusters[outIdx] = uint4(pageOffset, clusterID, instanceID, 0);
        }
    }
}
