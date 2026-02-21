#include "common.hlsl"

struct GPUPageHeader
{
    uint ClusterCount;
    uint TotalVertexCount;
    uint TotalTriangleCount;
    uint _Pad0;
    uint ClustersOffset;
    uint PositionsOffset;
    uint AttributesOffset;
    uint IndicesOffset;
};

// Matched to C# struct Layout (Pack=1) -> 60 bytes
struct GPUCluster
{
    float3 Center;
    float Radius;
    float3 LodCenter;
    float LodRadius;
    float LODError;
    float ParentLODError;
    uint VertexStart;
    uint TriangleStart;
    int GroupId;
    int ParentGroupId;
    uint PackedCounts; // [VertexCount:8][TriangleCount:8][LODLevel:8][Pad:8]
};

struct DrawArg
{
    uint VertexCountPerInstance;
    uint InstanceCount;
    uint StartVertexLocation;
    uint StartInstanceLocation;
};

cbuffer CullingUniforms : register(b0)
{
    float4x4 ViewProj;
    float3 CameraPos;
    uint PageOffset; // Offset in ByteAddressBuffer
    float LodThreshold;
    float LodScale;
    uint PageID; // ID of the page
    uint BaseInstanceID;
    int ForcedLODLevel;
};

StructuredBuffer<GpuTransform> InstanceData : register(t1);

struct DrawRequest
{
    uint PageID;
    uint ClusterID;
    uint InstanceID;
    uint Pad;
};

ByteAddressBuffer PageBuffer : register(t0);
RWByteAddressBuffer IndirectDrawArgs : register(u0);
RWStructuredBuffer<DrawRequest> RequestBuffer : register(u1);

// Helper to load 60-byte cluster
GPUCluster LoadCluster(uint offset)
{
    GPUCluster c;
    // Load 3 float3 (60 bytes)
    float4 v0 = asfloat(PageBuffer.Load4(offset));
    c.Center = v0.xyz;
    c.Radius = v0.w;

    float4 v1 = asfloat(PageBuffer.Load4(offset + 16));
    c.LodCenter = v1.xyz;
    c.LodRadius = v1.w;

    float4 v2 = asfloat(PageBuffer.Load4(offset + 32));
    c.LODError = v2.x;
    c.ParentLODError = v2.y;
    c.VertexStart = asuint(v2.z);
    c.TriangleStart = asuint(v2.w);

    c.GroupId = asint(PageBuffer.Load(offset + 48));
    c.ParentGroupId = asint(PageBuffer.Load(offset + 52));
    c.PackedCounts = PageBuffer.Load(offset + 56);

    return c;
}

bool IsVisible(float3 center, float radius)
{
    float4 clip = mul(float4(center, 1.0), ViewProj);
    if (clip.w <= 0.0001)
    {
        return false;
    }

    float3 ndc = clip.xyz / clip.w;
    float dist = max(distance(center, CameraPos) - radius, 0.001);
    float radiusNdc = radius / dist;

    return ndc.x >= -1.0 - radiusNdc && ndc.x <= 1.0 + radiusNdc &&
           ndc.y >= -1.0 - radiusNdc && ndc.y <= 1.0 + radiusNdc &&
           ndc.z >= -radiusNdc && ndc.z <= 1.0 + radiusNdc;
}

bool IsLodSelected(GPUCluster cluster)
{
    // Extract LOD Level
    uint lodLevel = (cluster.PackedCounts >> 16) & 0xFF;

    if (ForcedLODLevel >= 0)
    {
        return int(lodLevel) == ForcedLODLevel;
    }

    // Use cluster.Center/Radius for SelfError (matches children's parentDist)
    float selfDist = max(distance(cluster.Center, CameraPos) - cluster.Radius, 0.001);
    float projectedError = (cluster.LODError * LodScale) / selfDist;

    // Use cluster.LodCenter/Radius for ParentError (matches parent's selfDist)
    float parentDist = max(distance(cluster.LodCenter, CameraPos) - cluster.LodRadius, 0.001);
    float projectedParentError = (cluster.ParentLODError * LodScale) / parentDist;

    return projectedParentError > LodThreshold && projectedError <= LodThreshold;
}

[numthreads(64, 1, 1)] void main(uint3 DTid : SV_DispatchThreadID) {
    // 1. Load Header
    // Manual load since ByteAddressBuffer.Load<T> is not standard
    uint4 h1 = PageBuffer.Load4(PageOffset);
    GPUPageHeader header;
    header.ClusterCount = h1.x;
    header.TotalVertexCount = h1.y;
    header.TotalTriangleCount = h1.z;
    // _Pad0 = h1.w
    uint4 h2 = PageBuffer.Load4(PageOffset + 16);
    header.ClustersOffset = h2.x;
    header.PositionsOffset = h2.y;
    header.AttributesOffset = h2.z;
    header.IndicesOffset = h2.w;

    uint clusterId = DTid.x;
    if (clusterId >= header.ClusterCount)
        return;

    // Instance Logic
    uint instanceIdx = DTid.y;
    uint globalInstanceId = BaseInstanceID + instanceIdx;

    // 2. Load Cluster
    // Stride 60 bytes
    uint clusterOffset = PageOffset + header.ClustersOffset + clusterId * 60;

    // helper function LoadCluster is defined above and works correct with Load4
    GPUCluster cluster = LoadCluster(clusterOffset);

    // 3. Culling + Runtime LOD Cut
    bool visible =
        true && IsLodSelected(cluster);

    if (visible)
    {
        // 4. Append Draw Argument
        // We use Single DrawInstancedIndirect.
        // Increment InstanceCount in IndirectDrawArgs (Offset 4)

        uint requestIndex;
        IndirectDrawArgs.InterlockedAdd(4, 1, requestIndex);

        // Write Request
        DrawRequest req;
        req.PageID = PageID;
        req.ClusterID = clusterId;
        req.InstanceID = globalInstanceId;
        req.Pad = 0;
        RequestBuffer[requestIndex] = req;
    }
}
