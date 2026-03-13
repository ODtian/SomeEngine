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
    uint DumpHiZData;
    uint Pad5;

    float4x4 PrevViewProj;
    uint HasPrevHistory;
    uint HiZMipCount;
    float2 HiZInvSize;

    float4x4 View;
    float P00;
    float P11;
    float2 Pad7;

    float3 QuantOrigin;
    float QuantStep;

    float4x4 PrevView;
    float PrevP00;
    float PrevP11;
    float2 Pad8;
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
RWByteAddressBuffer Phase2DrawArgs;  // bytes 0-15: DrawInstanced args, byte 16: Phase1 visible count (offset)

// Debug: per-cluster HiZ test data. Layout:
// [0]: atomic counter (uint)
// [4 + i*32 + 0..15]: minUv.x, minUv.y, maxUv.x, maxUv.y (4 floats)
// [4 + i*32 + 16..31]: nearDepth, hizDepth, mipLevel(as float), occluded(0/1) (4 floats)
// Max 4096 entries → buffer size = 4 + 4096*32 = 131076 bytes
RWByteAddressBuffer DebugHiZOutput;

GPUCluster LoadCluster(uint pageOffset, uint clusterIndex)
{
    uint headerOffset = pageOffset;
    uint clustersOffset = PageHeap.Load(headerOffset + 16);
    uint offset = headerOffset + clustersOffset + (clusterIndex * 48);
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
        float lodError = DecodeClusterLODError(cluster);
        if (lodError * Uniforms.LodScale > Uniforms.LodThreshold * clusterDist)
            visible = false;
    }

    return visible;
}

bool BuildScreenBoundsAndNearDepth(
    float3 worldCenter,
    float worldRadius,
    bool usePrev,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel)
{
    minUv = 0.0.xx;
    maxUv = 1.0.xx;
    nearDepth = 0.0;
    mipLevel = 0;

    if (Uniforms.HiZMipCount == 0)
        return false;

    // Select matrices based on phase
    float4x4 viewMat = usePrev ? Uniforms.PrevView : Uniforms.View;
    float4x4 viewProjMat = usePrev ? Uniforms.PrevViewProj : Uniforms.ViewProj;
    float projP00 = usePrev ? Uniforms.PrevP00 : Uniforms.P00;
    float projP11 = usePrev ? Uniforms.PrevP11 : Uniforms.P11;

    // Transform bounds to view space for Exact Sphere Projection
    float3 viewPos = mul(float4(worldCenter, 1.0), viewMat).xyz;
    // View matrix uses right-hand convention (forward = -Z), negate to match
    // the sphere projection math which assumes positive Z = forward
    viewPos.z = -viewPos.z;

    float zNear = 0.1; // Safe distance for near plane
    if (viewPos.z < worldRadius + zNear)
    {
        // Intersects near plane: bounds cover screen conservatively
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
    }
    else
    {
        float r = worldRadius;
        float cx2 = viewPos.x * viewPos.x;
        float cy2 = viewPos.y * viewPos.y;
        float cz2 = viewPos.z * viewPos.z;
        float r2 = r * r;

        // X bounds — tangent lines from origin to circle (cx, cz) radius r
        // When denominator ≤ 0, the silhouette extends past FOV edge in that
        // direction → set the bound to ±large so it clamps to screen edge.
        float dx = max(0.0, cx2 + cz2 - r2);
        float tx = sqrt(dx);

        float denom_minx = viewPos.z * tx + viewPos.x * r;
        float denom_maxx = viewPos.z * tx - viewPos.x * r;
        float minx = (denom_minx > 1e-6) ? (viewPos.x * tx - r * viewPos.z) / denom_minx : -1e6;
        float maxx = (denom_maxx > 1e-6) ? (viewPos.x * tx + r * viewPos.z) / denom_maxx :  1e6;

        // Y bounds — tangent lines from origin to circle (cy, cz) radius r
        float dy = max(0.0, cy2 + cz2 - r2);
        float ty = sqrt(dy);

        float denom_miny = viewPos.z * ty + viewPos.y * r;
        float denom_maxy = viewPos.z * ty - viewPos.y * r;
        float miny = (denom_miny > 1e-6) ? (viewPos.y * ty - r * viewPos.z) / denom_miny : -1e6;
        float maxy = (denom_maxy > 1e-6) ? (viewPos.y * ty + r * viewPos.z) / denom_maxy :  1e6;

        // Convert to NDC and then UV
        float2 minNdc = float2(minx * projP00, miny * projP11);
        float2 maxNdc = float2(maxx * projP00, maxy * projP11);

        float2 ndcMin = min(minNdc, maxNdc);
        float2 ndcMax = max(minNdc, maxNdc);

        ndcMin = clamp(ndcMin, -1.0.xx, 1.0.xx);
        ndcMax = clamp(ndcMax, -1.0.xx, 1.0.xx);



        // Convert NDC to UV (Y is flipped)
        float2 uv1 = ndcMin * float2(0.5, -0.5) + 0.5.xx;
        float2 uv2 = ndcMax * float2(0.5, -0.5) + 0.5.xx;

        minUv = saturate(min(uv1, uv2));
        maxUv = saturate(max(uv1, uv2));

        if (any(isnan(minUv)) || any(isnan(maxUv)))
        {
            minUv = 0.0.xx;
            maxUv = 1.0.xx;
        }
    }

    float2 onePixel = max(Uniforms.HiZInvSize, 1e-6.xx);
    maxUv = min(1.0.xx, max(maxUv, minUv + onePixel));

    float pixelDiameterX = (maxUv.x - minUv.x) / onePixel.x;
    float pixelDiameterY = (maxUv.y - minUv.y) / onePixel.y;
    float pixelDiameter = max(1.0, max(pixelDiameterX, pixelDiameterY));

    float mipF = max(0.0, ceil(log2(pixelDiameter)));
    uint maxMip = Uniforms.HiZMipCount - 1;
    mipLevel = (uint)clamp((int)mipF, 0, (int)maxMip);

    // Compute nearDepth by projecting the sphere point closest along the
    // view Z axis.  The depth buffer is a function of view-space Z, so the
    // point with minimum depth is at (center - camForward * radius), NOT
    // at (center - normalize(center-eye) * radius).
    float3 camForward = -float3(viewMat[0][2], viewMat[1][2], viewMat[2][2]);
    float3 nearPoint = worldCenter - camForward * worldRadius;
    float4 clipNear = mul(float4(nearPoint, 1.0), viewProjMat);
    nearDepth = (clipNear.w > 1e-5)
                    ? saturate(clipNear.z / clipNear.w)
                    : 0.0;

    return true;
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

bool IsOccludedByHiZ(GPUCluster cluster, GpuTransform t, bool usePrev)
{
    // Decode Center/Radius from packed quantized values
    float3 worldCenter = ApplyTransform(
        DecodeClusterCenter(cluster, Uniforms.QuantOrigin, Uniforms.QuantStep), t);
    float maxScale = t.Scale * max(max(t.Stretch.x, t.Stretch.y), t.Stretch.z);
    float worldRadius = DecodeClusterRadius(cluster, Uniforms.QuantStep) * maxScale;

    float2 minUv;
    float2 maxUv;
    float nearDepth;
    uint mipLevel;
    bool valid = BuildScreenBoundsAndNearDepth(
        worldCenter,
        worldRadius,
        usePrev,
        minUv,
        maxUv,
        nearDepth,
        mipLevel);
    if (!valid)
        return false;

    float hizDepth = SampleHiZMax(minUv, maxUv, mipLevel);
    float epsilon = 5e-5 + 0.5 * max(Uniforms.HiZInvSize.x, Uniforms.HiZInvSize.y);
    bool result = nearDepth > (hizDepth + epsilon);

    // Write debug data (DumpHiZData is set when dumping is requested)
    if (Uniforms.DumpHiZData != 0)
    {
        uint debugIdx;
        DebugHiZOutput.InterlockedAdd(0, 1, debugIdx);
        if (debugIdx < 4096)
        {
            uint base = 4 + debugIdx * 48;
            DebugHiZOutput.Store(base + 0, asuint(minUv.x));
            DebugHiZOutput.Store(base + 4, asuint(minUv.y));
            DebugHiZOutput.Store(base + 8, asuint(maxUv.x));
            DebugHiZOutput.Store(base + 12, asuint(maxUv.y));
            DebugHiZOutput.Store(base + 16, asuint(nearDepth));
            DebugHiZOutput.Store(base + 20, asuint(hizDepth));
            DebugHiZOutput.Store(base + 24, mipLevel);
            DebugHiZOutput.Store(base + 28, (result ? 1u : 0u) | (usePrev ? 2u : 0u));
            DebugHiZOutput.Store(base + 32, asuint(worldCenter.x));
            DebugHiZOutput.Store(base + 36, asuint(worldCenter.y));
            DebugHiZOutput.Store(base + 40, asuint(worldCenter.z));
            DebugHiZOutput.Store(base + 44, asuint(worldRadius));
        }
    }

    return result;
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
    out GpuTransform t)
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

    bool occluded = IsOccludedByHiZ(cluster, t, true);
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

    bool occluded = IsOccludedByHiZ(cluster, t, false);
    if (!occluded)
        AppendVisiblePhase2(pageOffset, clusterID, instanceID);
}

// Phase 2: use Phase2DrawArgs for own InstanceCount, read Phase1's DrawArgs[4] for global offset
void AppendVisiblePhase2(uint pageOffset, uint clusterID, uint instanceID)
{
    uint localIdx;
    Phase2DrawArgs.InterlockedAdd(4, 1, localIdx);
    uint phase1Count = DrawArgs.Load(4);  // Phase 1's finalized InstanceCount
    uint globalIdx = phase1Count + localIdx;
    if (globalIdx < Uniforms.MaxCandidates)
    {
        VisibleClusters[globalIdx] = uint4(pageOffset, clusterID, instanceID, 0);
    }
}

