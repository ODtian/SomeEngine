#include "cluster_structures.slang"
#include "common.slang"
#include "generated/instance_header_layout.slang"

struct CullingUniforms
{
    float4x4 ViewProj;
    float3 CameraPos;
    float LodThreshold;

    float LodScale;
    uint MaxQueueNodes;
    uint MaxCandidates;
    uint MaxTraversalDepth;

    int ForcedLODLevel;
    uint InstanceCount;
    uint DebugMode;
    uint VisualiseBVH;
    int DebugBVHDepth;
    uint CurrentDepth;
    uint Pad5;
    uint Pad6;

    float4x4 PrevViewProj;
    uint HasPrevHistory;
    uint HiZMipCount;
    float2 HiZInvSize;

    float4x4 View;
    float P00;
    float P11;
    uint ScreenWidth;
    uint ScreenHeight;

    float4x4 PrevView;
    float PrevP00;
    float PrevP11;
    float2 Pad8;
};

ConstantBuffer<CullingUniforms> Uniforms;

StructuredBuffer<GpuTransform> Instances;
ByteAddressBuffer InstanceHeaders;
ByteAddressBuffer PageHeap;
StructuredBuffer<uint3> CandidateClusters;
StructuredBuffer<uint> CandidateCount;
RWByteAddressBuffer DrawArgs;                // IndirectArgs: must stay raw, layout = CullDrawArgs
RWStructuredBuffer<uint4> VisibleClusters;
Texture2D<float> HiZTexture;
RWStructuredBuffer<uint3> Phase2CandidateClusters;
RWStructuredBuffer<uint> Phase2CandidateCount;
RWByteAddressBuffer Phase2DrawArgs;          // IndirectArgs: must stay raw, layout = CullDrawArgs
RWByteAddressBuffer Phase2CandidateArgs;     // IndirectArgs: must stay raw

[shader("compute")]
[numthreads(1, 1, 1)]
void clear_main_single(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x != 0 || tid.y != 0 || tid.z != 0)
        return;

    StoreCullDrawArgs(DrawArgs, 0u, CullDrawArgs(96, 0, 0));
    DrawArgs.Store(12, 0u);
}

[shader("compute")]
[numthreads(1, 1, 1)]
void clear_main_phase1(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x != 0 || tid.y != 0 || tid.z != 0)
        return;

    StoreCullDrawArgs(DrawArgs, 0u, CullDrawArgs(96, 0, 0));
    DrawArgs.Store(12, 0u);
    Phase2CandidateCount[0] = 0;
    StoreDispatchArgs(Phase2CandidateArgs, 0u, DispatchArgs(0, 1, 1));
}

[shader("compute")]
[numthreads(1, 1, 1)]
void clear_main_phase2(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x != 0 || tid.y != 0 || tid.z != 0)
        return;

    StoreCullDrawArgs(Phase2DrawArgs, 0u, CullDrawArgs(96, 0, 0));
    Phase2DrawArgs.Store(12, 0u);
}

float3 LoadFloat3(ByteAddressBuffer buffer, uint offset)
{
    return asfloat(uint3(
        buffer.Load(offset + 0),
        buffer.Load(offset + 4),
        buffer.Load(offset + 8)));
}

GPUCluster LoadCluster(uint pageOffset, uint clusterIndex)
{
    uint headerOffset = pageOffset;
    uint clustersOffset = PageHeap.Load(headerOffset + 16);
    uint offset = headerOffset + clustersOffset + (clusterIndex * GPU_CLUSTER_STRIDE_BYTES);
    GPUCluster c;

    // IntBase (0..11)
    c.IntBase.x = asint(PageHeap.Load(offset + 0));
    c.IntBase.y = asint(PageHeap.Load(offset + 4));
    c.IntBase.z = asint(PageHeap.Load(offset + 8));

    // PackedCenterXY (12)
    c.PackedCenterXY = PageHeap.Load(offset + 12);

    // LODCenter (16..27), LODRadius (28)
    uint4 uLOD = PageHeap.Load<uint4>(offset + 16);
    c.LODCenter = asfloat(uLOD.xyz);
    c.LODRadius = asfloat(uLOD.w);

    // PackedCenterZRadius (32)
    c.PackedCenterZRadius = PageHeap.Load(offset + 32);

    // PackedLodErrVStart (36)
    c.PackedLodErrVStart = PageHeap.Load(offset + 36);

    // PackedTStartGroup (40)
    c.PackedTStartGroup = PageHeap.Load(offset + 40);

    // PackedCounts (44)
    c.PackedCounts = PageHeap.Load(offset + 44);

    // PackedMaterials (48)
    c.PackedMaterials = PageHeap.Load(offset + 48);

    // PackedRanges (52)
    c.PackedRanges = PageHeap.Load(offset + 52);

    // MaterialTableOffset (56), VRBBatchInfo (60)
    c.MaterialTableOffset = PageHeap.Load(offset + 56);
    c.VRBBatchInfo = PageHeap.Load(offset + 60);

    // BoundMin (64..75), BoundMax (76..87)
    c.BoundMin = LoadFloat3(PageHeap, offset + 64);
    c.BoundMax = LoadFloat3(PageHeap, offset + 76);

    return c;
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
        float lodError = DecodeClusterLODError(cluster);
        if (lodError * Uniforms.LodScale > Uniforms.LodThreshold * clusterDist)
            visible = false;
    }

    return visible;
}

void BuildWorldAabbScreenBoundsAndNearDepth(
    float3 worldMin,
    float3 worldMax,
    bool usePrev,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel)
{
    float4x4 viewProjMat = usePrev ? Uniforms.PrevViewProj : Uniforms.ViewProj;

    float3 corners[8];
    corners[0] = float3(worldMin.x, worldMin.y, worldMin.z);
    corners[1] = float3(worldMax.x, worldMin.y, worldMin.z);
    corners[2] = float3(worldMin.x, worldMax.y, worldMin.z);
    corners[3] = float3(worldMax.x, worldMax.y, worldMin.z);
    corners[4] = float3(worldMin.x, worldMin.y, worldMax.z);
    corners[5] = float3(worldMax.x, worldMin.y, worldMax.z);
    corners[6] = float3(worldMin.x, worldMax.y, worldMax.z);
    corners[7] = float3(worldMax.x, worldMax.y, worldMax.z);

    float2 ndcMin = 1e20.xx;
    float2 ndcMax = -1e20.xx;
    uint validCornerCount = 0;
    bool touchesNearPlane = false;
    nearDepth = 1.0;

    [unroll]
    for (uint i = 0; i < 8; i++)
    {
        float4 clip = mul(float4(corners[i], 1.0), viewProjMat);
        if (clip.w <= 1e-5 || clip.z < 0.0)
        {
            touchesNearPlane = true;
            continue;
        }

        float3 ndc = clip.xyz / clip.w;
        ndcMin = min(ndcMin, ndc.xy);
        ndcMax = max(ndcMax, ndc.xy);
        nearDepth = min(nearDepth, saturate(ndc.z));
        validCornerCount++;
    }

    if (touchesNearPlane || validCornerCount == 0)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
    }
    else
    {
        ndcMin = clamp(ndcMin, -1.0.xx, 1.0.xx);
        ndcMax = clamp(ndcMax, -1.0.xx, 1.0.xx);
        float2 uv1 = ndcMin * float2(0.5, -0.5) + 0.5.xx;
        float2 uv2 = ndcMax * float2(0.5, -0.5) + 0.5.xx;
        minUv = saturate(min(uv1, uv2));
        maxUv = saturate(max(uv1, uv2));
    }

    float2 onePixel = float2(1.0 / Uniforms.ScreenWidth, 1.0 / Uniforms.ScreenHeight);
    if (Uniforms.HiZMipCount > 0)
        onePixel = max(Uniforms.HiZInvSize, 1e-6.xx);
    maxUv = min(1.0.xx, max(maxUv, minUv + onePixel));

    float pixelDiameterX = (maxUv.x - minUv.x) / onePixel.x;
    float pixelDiameterY = (maxUv.y - minUv.y) / onePixel.y;
    float pixelDiameter = max(1.0, max(pixelDiameterX, pixelDiameterY));

    float mipF = max(0.0, ceil(log2(pixelDiameter)));
    uint maxMip = Uniforms.HiZMipCount > 0 ? Uniforms.HiZMipCount - 1 : 0;
    mipLevel = (uint)clamp((int)mipF, 0, (int)maxMip);

    if (any(isnan(minUv)) || any(isnan(maxUv)) || isnan(nearDepth))
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
        mipLevel = 0;
    }
}

void BuildClusterAabbScreenBoundsAndNearDepth(
    GPUCluster cluster,
    GpuTransform transform,
    bool usePrev,
    float boundsExpansion,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel)
{
    uint vertexCount = cluster.PackedCounts & 0xFF;
    if (vertexCount == 0)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
        mipLevel = 0;
        return;
    }

    float3 localMin = cluster.BoundMin;
    float3 localMax = cluster.BoundMax;

    float3 localCorners[8];
    localCorners[0] = float3(localMin.x, localMin.y, localMin.z);
    localCorners[1] = float3(localMax.x, localMin.y, localMin.z);
    localCorners[2] = float3(localMin.x, localMax.y, localMin.z);
    localCorners[3] = float3(localMax.x, localMax.y, localMin.z);
    localCorners[4] = float3(localMin.x, localMin.y, localMax.z);
    localCorners[5] = float3(localMax.x, localMin.y, localMax.z);
    localCorners[6] = float3(localMin.x, localMax.y, localMax.z);
    localCorners[7] = float3(localMax.x, localMax.y, localMax.z);

    float3 worldMin = 1e20.xxx;
    float3 worldMax = -1e20.xxx;
    [unroll]
    for (uint cornerIdx = 0; cornerIdx < 8; cornerIdx++)
    {
        float3 worldPos = ApplyTransform(localCorners[cornerIdx], transform);
        worldMin = min(worldMin, worldPos);
        worldMax = max(worldMax, worldPos);
    }

    float expansion = max(0.0, boundsExpansion);
    worldMin -= expansion.xxx;
    worldMax += expansion.xxx;

    BuildWorldAabbScreenBoundsAndNearDepth(
        worldMin, worldMax, usePrev,
        minUv, maxUv, nearDepth, mipLevel);
}

float SampleHiZMax(float2 minUv, float2 maxUv, uint mipLevel)
{
    float2 fullSize = 1.0.xx / max(Uniforms.HiZInvSize, 1e-6.xx);
    
    // Scale UVs to exact pixel coordinates
    float2 pxMin = minUv * fullSize;
    float2 pxMax = maxUv * fullSize;

    // The divisor maps pixels to texels exactly via integer truncation
    float divisor = exp2((float)mipLevel);
    
    // Calculate the mip texture boundary using exact truncation matching GPU
    float2 mipSize = max(1.0.xx, floor(fullSize / divisor));
    float2 maxCoord = mipSize - 1.0.xx;

    // Divide by 2^L and clamp to ensure we safely load within bounds.
    // Since mipLevel = ceil(log2(pixelDiameter)), pxMax/D - pxMin/D is GUARANTEED <= 1.
    // The 4 loaded texels are guaranteed to perfectly cover the whole cluster UV.
    int2 p0 = int2(clamp(pxMin / divisor, 0.0.xx, maxCoord));
    int2 p1 = int2(clamp(float2(pxMax.x, pxMin.y) / divisor, 0.0.xx, maxCoord));
    int2 p2 = int2(clamp(float2(pxMin.x, pxMax.y) / divisor, 0.0.xx, maxCoord));
    int2 p3 = int2(clamp(pxMax / divisor, 0.0.xx, maxCoord));

    float d0 = HiZTexture.Load(int3(p0, mipLevel));
    float d1 = HiZTexture.Load(int3(p1, mipLevel));
    float d2 = HiZTexture.Load(int3(p2, mipLevel));
    float d3 = HiZTexture.Load(int3(p3, mipLevel));

    return max(max(d0, d1), max(d2, d3));
}

bool IsOccludedByHiZ(
    GPUCluster cluster,
    GpuTransform transform,
    float boundsExpansion,
    bool usePrev,
    out float2 minUv,
    out float2 maxUv)
{
    float nearDepth;
    uint mipLevel;
    BuildClusterAabbScreenBoundsAndNearDepth(
        cluster, transform, usePrev, boundsExpansion,
        minUv, maxUv, nearDepth, mipLevel);

    float hizDepth = 0.0;
    float epsilon = 5e-5 + 0.5 * max(Uniforms.HiZInvSize.x, Uniforms.HiZInvSize.y);

    if (Uniforms.HiZMipCount == 0)
        return false;

    hizDepth = SampleHiZMax(minUv, maxUv, mipLevel);
    return nearDepth > (hizDepth + epsilon);
}

// SW/HW threshold: clusters with screen area smaller than this (in pixels^2) are SW rasterized.
// roughly 45x45 pixels
static const float SW_AREA_THRESHOLD = 2000.0; 
static const uint ENABLE_SW_HW_SPLIT = 1;

bool ShouldUseSWRaster(float2 minUv, float2 maxUv)
{
    if (ENABLE_SW_HW_SPLIT == 0)
        return true;

    float width = (maxUv.x - minUv.x) * Uniforms.ScreenWidth;
    float height = (maxUv.y - minUv.y) * Uniforms.ScreenHeight;
    float area = width * height;
    return area < SW_AREA_THRESHOLD;
}

void AppendVisible(uint pageOffset, uint clusterID, uint instanceID, bool isSW)
{
    uint totalIdx;
    DrawArgs.InterlockedAdd(12, 1, totalIdx);
    if (totalIdx >= Uniforms.MaxCandidates)
        return;

    uint outIdx;
    if (isSW)
    {
        // SW clusters: write from front (CullDrawArgs.SWCount at byte 4)
        DrawArgs.InterlockedAdd(4, 1, outIdx);
        if (outIdx < Uniforms.MaxCandidates)
            VisibleClusters[outIdx] = uint4(pageOffset, clusterID, instanceID, 0);
    }
    else
    {
        // HW clusters: write from back (CullDrawArgs.HWCount at byte 8)
        DrawArgs.InterlockedAdd(8, 1, outIdx);
        uint hwIdx = Uniforms.MaxCandidates - 1 - outIdx;
        if (outIdx < Uniforms.MaxCandidates)
            VisibleClusters[hwIdx] = uint4(pageOffset, clusterID, instanceID, 0);
    }
}

void AppendPhase2Candidate(uint pageOffset, uint clusterID, uint instanceID)
{
    uint outIdx;
    InterlockedAdd(Phase2CandidateCount[0], 1, outIdx);
    if (outIdx >= Uniforms.MaxCandidates)
        return;

    uint groups = (outIdx + 64) / 64;
    uint unused;
    Phase2CandidateArgs.InterlockedMax(0, groups, unused);
    Phase2CandidateClusters[outIdx] = uint3(pageOffset, clusterID, instanceID);
}

bool LoadAndFilterCandidate(
    uint threadId,
    out uint pageOffset,
    out uint clusterID,
    out uint instanceID,
    out GPUCluster cluster,
    out GpuTransform t,
    out float boundsExpansion)
{
    pageOffset = 0;
    clusterID = 0;
    instanceID = 0;
    cluster = (GPUCluster)0;
    t = (GpuTransform)0;
    boundsExpansion = 0.0;

    uint count = min(CandidateCount[0], Uniforms.MaxCandidates);
    if (threadId >= count)
        return false;

    uint3 candidate = CandidateClusters[threadId];
    pageOffset = candidate.x;
    clusterID = candidate.y;
    instanceID = candidate.z;

    cluster = LoadCluster(pageOffset, clusterID);
    t = Instances[instanceID];
    boundsExpansion = max(0.0, LoadInstanceBoundsExpansionWorld(InstanceHeaders, instanceID));

    bool passesLod = PassesLODFilter(cluster, t);
    return passesLod;
}

[numthreads(64, 1, 1)]
void main_phase1(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    GPUCluster cluster;
    GpuTransform t;
    float boundsExpansion;
    if (!LoadAndFilterCandidate(dispatchThreadID.x, pageOffset, clusterID, instanceID, cluster, t, boundsExpansion))
        return;

    if (Uniforms.HasPrevHistory == 0)
    {
        AppendPhase2Candidate(pageOffset, clusterID, instanceID);
        return;
    }

    float2 minUv, maxUv;
    bool occluded = IsOccludedByHiZ(cluster, t, boundsExpansion, true, minUv, maxUv);
    if (occluded)
    {
        AppendPhase2Candidate(pageOffset, clusterID, instanceID);
    }
    else
    {
        bool isSW = ShouldUseSWRaster(minUv, maxUv);
        AppendVisible(pageOffset, clusterID, instanceID, isSW);
    }
}

[numthreads(64, 1, 1)]
void main_single(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    GPUCluster cluster;
    GpuTransform t;
    float boundsExpansion;
    if (!LoadAndFilterCandidate(dispatchThreadID.x, pageOffset, clusterID, instanceID, cluster, t, boundsExpansion))
        return;

    float2 minUv;
    float2 maxUv;
    float nearDepth;
    uint mipLevel;
    BuildClusterAabbScreenBoundsAndNearDepth(
        cluster,
        t,
        false,
        boundsExpansion,
        minUv,
        maxUv,
        nearDepth,
        mipLevel);

    bool isSW = ShouldUseSWRaster(minUv, maxUv);
    AppendVisible(pageOffset, clusterID, instanceID, isSW);
}

// HiZ follow-up cull: use Phase2DrawArgs for own counts, read Phase1's counts for global offset
void AppendVisiblePhase2(uint pageOffset, uint clusterID, uint instanceID, bool isSW)
{
    uint localIdx;
    CullDrawArgs phase1 = LoadCullDrawArgs(DrawArgs, 0u);
    uint phase1SWCount = phase1.SWCount;
    uint phase1HWCount = phase1.HWCount;
    uint phase1Count = phase1SWCount + phase1HWCount;

    uint totalIdx;
    Phase2DrawArgs.InterlockedAdd(12, 1, totalIdx);
    if (phase1Count + totalIdx >= Uniforms.MaxCandidates)
        return;

    if (isSW)
    {
        // Phase2 SW: write after Phase1 SW (CullDrawArgs.SWCount at byte 4)
        Phase2DrawArgs.InterlockedAdd(4, 1, localIdx);
        uint globalIdx = phase1SWCount + localIdx;
        if (globalIdx < Uniforms.MaxCandidates)
            VisibleClusters[globalIdx] = uint4(pageOffset, clusterID, instanceID, 0);
    }
    else
    {
        // Phase2 HW: write before Phase1 HW (CullDrawArgs.HWCount at byte 8)
        Phase2DrawArgs.InterlockedAdd(8, 1, localIdx);
        uint hwIdx = Uniforms.MaxCandidates - 1 - phase1HWCount - localIdx;
        if (localIdx < Uniforms.MaxCandidates)
            VisibleClusters[hwIdx] = uint4(pageOffset, clusterID, instanceID, 0);
    }
}

[numthreads(64, 1, 1)]
void main_phase2(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    GPUCluster cluster;
    GpuTransform t;
    float boundsExpansion;
    if (!LoadAndFilterCandidate(dispatchThreadID.x, pageOffset, clusterID, instanceID, cluster, t, boundsExpansion))
        return;

    float2 minUv, maxUv;
    bool occluded = IsOccludedByHiZ(cluster, t, boundsExpansion, false, minUv, maxUv);
    if (!occluded)
    {
        bool isSW = ShouldUseSWRaster(minUv, maxUv);
        AppendVisiblePhase2(pageOffset, clusterID, instanceID, isSW);
    }
}
