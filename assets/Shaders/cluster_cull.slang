#include "common.slang"

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

struct CullingUniforms
{
    float4x4 ViewProj;
    float3 CameraPos;
    uint PageOffset; // Offset in ByteAddressBuffer
    float LodThreshold;
    float LodScale;
    uint PageID; // ID of the page
    uint BaseInstanceID;
    int ForcedLODLevel;
    uint InstanceCount;
    float2 Pad;
};

cbuffer CullingUniformsBuffer
{
    CullingUniforms Uniforms;
}

StructuredBuffer<GpuTransform> InstanceData;

struct DrawRequest
{
    uint PageID;
    uint ClusterID;
    uint InstanceID;
    uint Pad;
};

ByteAddressBuffer PageBuffer;
RWByteAddressBuffer IndirectDrawArgs;
RWStructuredBuffer<DrawRequest> RequestBuffer;

// Helper to load 60-byte cluster
GPUCluster LoadCluster(uint offset)
{
    GPUCluster c;
    
    // Load 3 float3 (60 bytes)
    uint4 u0 = PageBuffer.Load<uint4>(offset);
    c.Center = asfloat(u0.xyz);
    c.Radius = asfloat(u0.w);

    uint4 u1 = PageBuffer.Load<uint4>(offset + 16);
    c.LodCenter = asfloat(u1.xyz);
    c.LodRadius = asfloat(u1.w);

    uint4 u2 = PageBuffer.Load<uint4>(offset + 32);
    c.LODError = asfloat(u2.x);
    c.ParentLODError = asfloat(u2.y);
    c.VertexStart = u2.z;
    c.TriangleStart = u2.w;

    // Remaining 12 bytes
    uint3 u3 = PageBuffer.Load<uint3>(offset + 48);
    c.GroupId = (int)u3.x;
    c.ParentGroupId = (int)u3.y;
    c.PackedCounts = u3.z;

    return c;
}

bool IsVisible(float3 center, float radius)
{
    // CullingUniforms is accessed via Uniforms
    float4 clip = mul(float4(center, 1.0), Uniforms.ViewProj);
    // Basic frustum culling
    // Check if behind camera?
    // Proper frustum culling involves checking against all planes
    // Simplified: Check NDC bounds
    
    if (clip.w <= 0.0001)
    {
         // Behind near plane (assuming w=z for perspective)
         // But need to be careful with large radius.
         // Conservative check
         return false; 
    }

    // Perspective divide
    float3 ndc = clip.xyz / clip.w;
    
    // Expand bounds by radius in NDC?
    // Approximate radius in NDC
    // Dist to camera
    float dist = max(distance(center, Uniforms.CameraPos) - radius, 0.001);
    float radiusNdc = radius / dist; // Rough approximation

    return ndc.x >= -1.0 - radiusNdc && ndc.x <= 1.0 + radiusNdc &&
           ndc.y >= -1.0 - radiusNdc && ndc.y <= 1.0 + radiusNdc &&
           ndc.z >= 0.0 - radiusNdc && ndc.z <= 1.0 + radiusNdc; // Direct3D Z: [0, 1]
}

bool IsLodSelected(float3 center, float radius, float3 lodCenter, float lodRadius, float lodError, float parentLodError, uint packedCounts)
{
    // Extract LOD Level
    uint lodLevel = (packedCounts >> 16) & 0xFF;

    if (Uniforms.ForcedLODLevel >= 0)
    {
        return int(lodLevel) == Uniforms.ForcedLODLevel;
    }

    // Use center/radius for SelfError (matches children's parentDist)
    float selfDist = max(distance(center, Uniforms.CameraPos) - radius, 0.001);
    float projectedError = (lodError * Uniforms.LodScale) / selfDist;

    // Use lodCenter/lodRadius for ParentError (matches parent's selfDist)
    float parentDist = max(distance(lodCenter, Uniforms.CameraPos) - lodRadius, 0.001);
    float projectedParentError = (parentLodError * Uniforms.LodScale) / parentDist;

    return projectedParentError > Uniforms.LodThreshold && projectedError <= Uniforms.LodThreshold;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void main(uint3 DTid : SV_DispatchThreadID)
{
    // 1. Load Header
    uint4 h1 = PageBuffer.Load<uint4>(Uniforms.PageOffset);
    GPUPageHeader header;
    header.ClusterCount = h1.x;
    header.TotalVertexCount = h1.y;
    header.TotalTriangleCount = h1.z;
    // _Pad0 = h1.w
    uint4 h2 = PageBuffer.Load<uint4>(Uniforms.PageOffset + 16);
    header.ClustersOffset = h2.x;
    header.PositionsOffset = h2.y;
    header.AttributesOffset = h2.z;
    header.IndicesOffset = h2.w;

    uint clusterId = DTid.x;
    if (clusterId >= header.ClusterCount)
        return;

    // Instance Logic
    uint instanceIdx = DTid.y;
    if (instanceIdx >= Uniforms.InstanceCount)
        return;

    uint globalInstanceId = Uniforms.BaseInstanceID + instanceIdx;

    // 2. Load Cluster
    uint clusterOffset = Uniforms.PageOffset + header.ClustersOffset + clusterId * 60;
    GPUCluster cluster = LoadCluster(clusterOffset);

    // 3. Apply Instance Transform
    GpuTransform transform = InstanceData[globalInstanceId];
    
    // Transform Center and LodCenter
    float3 worldCenter = ApplyTransform(cluster.Center, transform);
    float3 worldLodCenter = ApplyTransform(cluster.LodCenter, transform);
    
    // Scale Radius (Uniform scale assumption for now, take max scale)
    float maxScale = transform.Scale * max(transform.Stretch.x, max(transform.Stretch.y, transform.Stretch.z));
    float worldRadius = cluster.Radius * maxScale;
    float worldLodRadius = cluster.LodRadius * maxScale;

    // 4. Culling + Runtime LOD Cut
    bool visible = IsVisible(worldCenter, worldRadius);
    
    if (visible)
    {
        visible = IsLodSelected(worldCenter, worldRadius, worldLodCenter, worldLodRadius, cluster.LODError * maxScale, cluster.ParentLODError * maxScale, cluster.PackedCounts);
    }

    if (visible)
    {
        // 5. Append Draw Argument
        uint originalValue;
        IndirectDrawArgs.InterlockedAdd(4, 1, originalValue);

        // Write Request
        DrawRequest req;
        req.PageID = Uniforms.PageID;
        req.ClusterID = clusterId;
        req.InstanceID = globalInstanceId;
        req.Pad = 0;
        
        RequestBuffer[originalValue] = req;
    }
}


