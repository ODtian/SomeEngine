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
    uint VisualiseBVH;
    int DebugBVHDepth;
    uint CurrentDepth;
    uint Pad4;
    uint Pad5;

    float4x4 PrevViewProj;
    uint HasPrevHistory;
    uint HiZMipCount;
    float2 HiZInvSize;
};

ConstantBuffer<CullingUniforms> Uniforms;

StructuredBuffer<GpuTransform> Instances;
ByteAddressBuffer PageHeap;
StructuredBuffer<uint3> CandidateClusters;
RWByteAddressBuffer CandidateCount;
RWByteAddressBuffer CandidateArgs;
RWByteAddressBuffer DrawArgs;
RWStructuredBuffer<uint4> VisibleClusters;
Texture2D<float> HiZTexture;
RWStructuredBuffer<uint3> Phase2CandidateClusters;
RWByteAddressBuffer Phase2CandidateCount;

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

bool PassesLODFilter(GPUCluster cluster, GpuTransform t)
{
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

    return visible;
}

float2 ComputeNdcDelta(float4 clipCenter, float4 clipOffset)
{
    float w2 = max(abs(clipCenter.w * clipCenter.w), 1e-6);
    float2 num = clipOffset.xy * clipCenter.w - clipCenter.xy * clipOffset.w;
    return num / w2;
}

bool BuildScreenBoundsAndNearDepth(
    float3 worldCenter,
    float worldRadius,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel
)
{
    minUv = 0.0.xx;
    maxUv = 1.0.xx;
    nearDepth = 0.0;
    mipLevel = 0;

    if (Uniforms.HiZMipCount == 0)
        return false;

    float4 clipCenter = mul(float4(worldCenter, 1.0), Uniforms.ViewProj);
    if (clipCenter.w <= 1e-5)
        return false;

    float invW = 1.0 / clipCenter.w;
    float2 ndcCenter = clipCenter.xy * invW;
    float centerDepth = saturate(clipCenter.z * invW);

    float4 clipOffsetX = mul(float4(worldRadius, 0.0, 0.0, 0.0), Uniforms.ViewProj);
    float4 clipOffsetY = mul(float4(0.0, worldRadius, 0.0, 0.0), Uniforms.ViewProj);
    float4 clipOffsetZ = mul(float4(0.0, 0.0, worldRadius, 0.0), Uniforms.ViewProj);

    float2 ndcDeltaX = ComputeNdcDelta(clipCenter, clipOffsetX);
    float2 ndcDeltaY = ComputeNdcDelta(clipCenter, clipOffsetY);
    float2 ndcDeltaZ = ComputeNdcDelta(clipCenter, clipOffsetZ);

    float2 ndcExtent = abs(ndcDeltaX) + abs(ndcDeltaY) + abs(ndcDeltaZ);
    float ndcRadius = max(ndcExtent.x, ndcExtent.y);

    float2 centerUv = ndcCenter * float2(0.5, -0.5) + 0.5.xx;
    float2 radiusUv = ndcRadius * 0.5.xx;

    minUv = saturate(centerUv - radiusUv);
    maxUv = saturate(centerUv + radiusUv);

    float2 onePixel = max(Uniforms.HiZInvSize, 1e-6.xx);
    maxUv = min(1.0.xx, max(maxUv, minUv + onePixel));

    float pixelDiameterX = (maxUv.x - minUv.x) / onePixel.x;
    float pixelDiameterY = (maxUv.y - minUv.y) / onePixel.y;
    float pixelDiameter = max(1.0, max(pixelDiameterX, pixelDiameterY));

    float mipF = floor(log2(pixelDiameter));
    uint maxMip = Uniforms.HiZMipCount - 1;
    mipLevel = (uint)clamp((int)mipF, 0, (int)maxMip);

    float w2 = max(abs(clipCenter.w * clipCenter.w), 1e-6);
    float depthDeltaX = abs((clipOffsetX.z * clipCenter.w - clipCenter.z * clipOffsetX.w) / w2);
    float depthDeltaY = abs((clipOffsetY.z * clipCenter.w - clipCenter.z * clipOffsetY.w) / w2);
    float depthDeltaZ = abs((clipOffsetZ.z * clipCenter.w - clipCenter.z * clipOffsetZ.w) / w2);
    float depthRadius = depthDeltaX + depthDeltaY + depthDeltaZ;

    nearDepth = saturate(centerDepth - depthRadius - 1e-4);
    return true;
}

float SampleHiZMax(float2 minUv, float2 maxUv, uint mipLevel)
{
    float2 fullSize = 1.0.xx / max(Uniforms.HiZInvSize, 1e-6.xx);
    float2 mipSize = max(1.0.xx, fullSize / exp2((float)mipLevel));
    float2 maxCoord = mipSize - 1.0.xx;

    float2 uv01 = float2(minUv.x, minUv.y);
    float2 uv11 = float2(maxUv.x, minUv.y);
    float2 uv10 = float2(minUv.x, maxUv.y);
    float2 uv00 = float2(maxUv.x, maxUv.y);

    int2 p0 = int2(clamp(uv00 * mipSize, 0.0.xx, maxCoord));
    int2 p1 = int2(clamp(uv01 * mipSize, 0.0.xx, maxCoord));
    int2 p2 = int2(clamp(uv10 * mipSize, 0.0.xx, maxCoord));
    int2 p3 = int2(clamp(uv11 * mipSize, 0.0.xx, maxCoord));

    float d0 = HiZTexture.Load(int3(p0, mipLevel));
    float d1 = HiZTexture.Load(int3(p1, mipLevel));
    float d2 = HiZTexture.Load(int3(p2, mipLevel));
    float d3 = HiZTexture.Load(int3(p3, mipLevel));

    return max(max(d0, d1), max(d2, d3));
}

bool IsOccludedByHiZ(GPUCluster cluster, GpuTransform t)
{
    float3 worldCenter = ApplyTransform(cluster.LODCenter, t);
    float maxScale = t.Scale * max(max(t.Stretch.x, t.Stretch.y), t.Stretch.z);
    float worldRadius = cluster.LODRadius * maxScale;

    float2 minUv;
    float2 maxUv;
    float nearDepth;
    uint mipLevel;
    bool valid = BuildScreenBoundsAndNearDepth(
        worldCenter,
        worldRadius,
        minUv,
        maxUv,
        nearDepth,
        mipLevel
    );
    if (!valid)
        return false;

    float hizDepth = SampleHiZMax(minUv, maxUv, mipLevel);
    float epsilon = 1e-4 + 2.0 * max(Uniforms.HiZInvSize.x, Uniforms.HiZInvSize.y);
    return nearDepth > (hizDepth + epsilon);
}

void AppendVisible(uint pageOffset, uint clusterID, uint instanceID)
{
    uint outIdx;
    DrawArgs.InterlockedAdd(4, 1, outIdx);
    if (outIdx < Uniforms.MaxCandidates)
    {
        VisibleClusters[outIdx] = uint4(pageOffset, clusterID, instanceID, 0);
    }
}

void AppendPhase2Candidate(uint pageOffset, uint clusterID, uint instanceID)
{
    uint outIdx;
    Phase2CandidateCount.InterlockedAdd(0, 1, outIdx);
    if (outIdx < Uniforms.MaxCandidates)
    {
        Phase2CandidateClusters[outIdx] = uint3(pageOffset, clusterID, instanceID);
    }
}

bool LoadAndFilterCandidate(
    uint threadId,
    out uint pageOffset,
    out uint clusterID,
    out uint instanceID,
    out GPUCluster cluster,
    out GpuTransform t
)
{
    pageOffset = 0;
    clusterID = 0;
    instanceID = 0;
    cluster = (GPUCluster)0;
    t = (GpuTransform)0;

    uint count = CandidateCount.Load(0);
    if (threadId >= count)
        return false;

    uint3 candidate = CandidateClusters[threadId];
    pageOffset = candidate.x;
    clusterID = candidate.y;
    instanceID = candidate.z;

    cluster = LoadCluster(pageOffset, clusterID);
    t = Instances[instanceID];

    return PassesLODFilter(cluster, t);
}

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    GPUCluster cluster;
    GpuTransform t;
    if (!LoadAndFilterCandidate(dispatchThreadID.x, pageOffset, clusterID, instanceID, cluster, t))
        return;

    AppendVisible(pageOffset, clusterID, instanceID);
}

[numthreads(64, 1, 1)]
void main_phase1(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    GPUCluster cluster;
    GpuTransform t;
    if (!LoadAndFilterCandidate(dispatchThreadID.x, pageOffset, clusterID, instanceID, cluster, t))
        return;

    if (Uniforms.HasPrevHistory == 0)
    {
        AppendPhase2Candidate(pageOffset, clusterID, instanceID);
        return;
    }

    bool occluded = IsOccludedByHiZ(cluster, t);
    if (occluded)
        AppendPhase2Candidate(pageOffset, clusterID, instanceID);
    else
        AppendVisible(pageOffset, clusterID, instanceID);
}

[numthreads(64, 1, 1)]
void main_phase2(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    GPUCluster cluster;
    GpuTransform t;
    if (!LoadAndFilterCandidate(dispatchThreadID.x, pageOffset, clusterID, instanceID, cluster, t))
        return;

    bool occluded = IsOccludedByHiZ(cluster, t);
    if (!occluded)
        AppendVisible(pageOffset, clusterID, instanceID);
}
