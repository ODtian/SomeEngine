#ifndef CLUSTER_STRUCTURES_SLANG
#define CLUSTER_STRUCTURES_SLANG

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

struct GPUCluster
{
    float3 Center;
    float Radius;
    float3 LODCenter;
    float LODRadius;
    float LODError;
    uint VertexStart;
    uint TriangleStart;
    int GroupId;
    uint PackedCounts; // [VertexCount:8][TriangleCount:8][LODLevel:8][Pad:8]
};

struct ClusterBVHNode
{
    float4 BoundMin;
    float4 BoundMax;
    float4 LODSphere; // xyz: Center, w: Radius
    float LODError;
    uint ChildPointer;
    uint ChildCount;
    uint NodeType; // 0=Internal, 1=Leaf
};

struct GpuInstanceHeader
{
    uint BVHRootIndex;
    uint MaterialID;
    uint MetadataOffset;
    uint MetadataCount;
};

struct AABB
{
    float3 Min;
    float3 Max;
    uint Color; // Packed RGBA
    uint Pad;
};

#endif
