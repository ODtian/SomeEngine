#include "cluster_structures.slang"
#include "common.slang"
#include "generated/instance_header_layout.slang"

struct CullingUniforms
{
    float4x4 ViewProj;
    float3 CameraPos;
    float LodThreshold; // 64+12=76

    float LodScale;     // 80
    uint MaxQueueNodes; // 84
    uint MaxCandidates; // 88
    uint Pad2;          // 92

    int ForcedLODLevel; // 96
    uint InstanceCount; // 100
    uint DebugMode;     // 104
    uint VisualiseBVH;  // 108
    int DebugBVHDepth;  // 112
    uint CurrentDepth;  // 116
    uint Pad4;
    uint Pad5;

    float4x4 PrevViewProj;
    uint HasPrevHistory;
    uint HiZMipCount;
    float2 HiZInvSize;

    float4x4 View;
    float P00;
    float P11;
    uint ScreenWidth;
    uint ScreenHeight;

    float3 QuantOrigin;
    float QuantStep;

    float4x4 PrevView;
    float PrevP00;
    float PrevP11;
    float2 Pad8;
};

ConstantBuffer<CullingUniforms> Uniforms;

StructuredBuffer<ClusterBVHNode> GlobalBVH;
ByteAddressBuffer PageHeap;
StructuredBuffer<GpuTransform> Instances;
ByteAddressBuffer InstanceHeaders;

cbuffer DepthIndexCB
{
    uint DepthIndex;
};

// ==========================================
// Queues (Ping-Pong)
// ==========================================
StructuredBuffer<uint2> Queue_Current;
ByteAddressBuffer CurrentDispatchArgs;       // IndirectArgs: must stay raw
RWStructuredBuffer<uint2> Queue_Next;
RWByteAddressBuffer NextDispatchArgs;        // IndirectArgs: must stay raw
RWByteAddressBuffer ClearDispatchArgs;       // IndirectArgs: must stay raw
RWStructuredBuffer<uint> CandidateCount;
RWStructuredBuffer<uint3> CandidateClusters;
RWByteAddressBuffer PageFaultBuffer;

static const uint MaxPageFaults = 4096;

float LocalExpansionForWorldRadius(GpuTransform t, float worldExpansion)
{
    if (worldExpansion <= 0.0)
        return 0.0;

    float3 axisScale = abs(t.Stretch) * abs(t.Scale);
    float minAxisScale = max(min(min(axisScale.x, axisScale.y), axisScale.z), 1e-6);
    return worldExpansion / minAxisScale;
}

bool IsNodeOutsideFrustum(ClusterBVHNode node, GpuTransform t, float boundsExpansion)
{
    float3 localMin = node.BoundMin.xyz;
    float3 localMax = node.BoundMax.xyz;
    float localExpansion = LocalExpansionForWorldRadius(t, boundsExpansion);
    float3 localExpansion3 = float3(localExpansion, localExpansion, localExpansion);
    localMin -= localExpansion3;
    localMax += localExpansion3;

    float3 corners[8];
    corners[0] = float3(localMin.x, localMin.y, localMin.z);
    corners[1] = float3(localMax.x, localMin.y, localMin.z);
    corners[2] = float3(localMin.x, localMax.y, localMin.z);
    corners[3] = float3(localMax.x, localMax.y, localMin.z);
    corners[4] = float3(localMin.x, localMin.y, localMax.z);
    corners[5] = float3(localMax.x, localMin.y, localMax.z);
    corners[6] = float3(localMin.x, localMax.y, localMax.z);
    corners[7] = float3(localMax.x, localMax.y, localMax.z);

    uint outsideLeft = 0;
    uint outsideRight = 0;
    uint outsideBottom = 0;
    uint outsideTop = 0;
    uint outsideNear = 0;
    uint outsideFar = 0;

    [unroll]
    for (uint i = 0; i < 8; ++i)
    {
        float3 world = ApplyTransform(corners[i], t);
        float4 clip = mul(float4(world, 1.0), Uniforms.ViewProj);

        outsideLeft += (clip.x < -clip.w) ? 1 : 0;
        outsideRight += (clip.x > clip.w) ? 1 : 0;
        outsideBottom += (clip.y < -clip.w) ? 1 : 0;
        outsideTop += (clip.y > clip.w) ? 1 : 0;
        outsideNear += (clip.z < 0.0) ? 1 : 0;
        outsideFar += (clip.z > clip.w) ? 1 : 0;
    }

    return outsideLeft == 8 || outsideRight == 8 || outsideBottom == 8 || outsideTop == 8 || outsideNear == 8 || outsideFar == 8;
}

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint count = CurrentDispatchArgs.Load<BVHDispatchArgs>(0).NodeCount;
    uint threadId = dispatchThreadID.x;
    if (threadId >= count)
        return;

    uint2 queueItem = Queue_Current[threadId];
    uint nodeIndex = queueItem.x;
    uint instanceID = queueItem.y;

    ClusterBVHNode node = GlobalBVH[nodeIndex];

    if (node.NodeType == 1 && node.ChildPointer == 0xFFFFFFFF)
    {
        uint faultIndex;
        PageFaultBuffer.InterlockedAdd(0, 1, faultIndex);
        if (faultIndex < MaxPageFaults)
        {
            PageFaultBuffer.Store(4 + faultIndex * 4, nodeIndex);
        }
        return;
    }

    GpuTransform t = Instances[instanceID];
    float boundsExpansion = max(0.0, LoadInstanceBoundsExpansionWorld(InstanceHeaders, instanceID));

    bool culled = false;
    if (Uniforms.DebugMode == 0)
    {
        if (IsNodeOutsideFrustum(node, t, boundsExpansion))
        {
            culled = true;
        }
    }

    float3 worldCenter = ApplyTransform(node.LODSphere.xyz, t);
    float maxScale = t.Scale * max(max(t.Stretch.x, t.Stretch.y), t.Stretch.z);
    float worldRadius = node.LODSphere.w * maxScale;

    float dist = distance(worldCenter, Uniforms.CameraPos);
    float distToSurface = max(0.0, dist - worldRadius);

    // LOD Culling
    if (Uniforms.ForcedLODLevel < 0 && Uniforms.DebugMode == 0)
    {
        if (node.LODError * Uniforms.LodScale <= Uniforms.LodThreshold * distToSurface)
        {
            culled = true;
        }
    }

    if (culled)
        return;

    if (node.NodeType == 0)
    {
        uint start = node.ChildPointer;
        uint count = node.ChildCount;
        uint outIndex;
        BVHDispatchArgs curArgs = NextDispatchArgs.Load<BVHDispatchArgs>(0);
        NextDispatchArgs.InterlockedAdd(12, count, outIndex);
        for (uint i = 0; i < count; ++i)
        {
            if (outIndex + i < Uniforms.MaxQueueNodes)
            {
                Queue_Next[outIndex + i] = uint2(start + i, instanceID);
            }
        }
    }
    else
    {
        uint pageOffset = node.ChildPointer;
        uint start = node.ChildCount & 0xFFF;
        uint count = node.ChildCount >> 12;

        uint outIndex;
        InterlockedAdd(CandidateCount[0], count, outIndex);

        for (uint i = 0; i < count; ++i)
        {
            if (outIndex + i < Uniforms.MaxCandidates)
            {
                uint clusterID = start + i;
                CandidateClusters[outIndex + i] = uint3(pageOffset, clusterID, instanceID);
            }
        }
    }
}

[numthreads(1, 1, 1)]
void UpdateArgs(uint3 id: SV_DispatchThreadID)
{
    uint count = min(NextDispatchArgs.Load<BVHDispatchArgs>(0).NodeCount, Uniforms.MaxQueueNodes);
    uint groups = (count + 63) / 64;
    NextDispatchArgs.Store<BVHDispatchArgs>(0, BVHDispatchArgs(groups, 1, 1, count));
    ClearDispatchArgs.Store<BVHDispatchArgs>(0, BVHDispatchArgs(0, 1, 1, 0));
}

[numthreads(1, 1, 1)]
void InitArgs(uint3 id: SV_DispatchThreadID)
{
    NextDispatchArgs.Store<BVHDispatchArgs>(0, BVHDispatchArgs(0, 1, 1, 0));
    ClearDispatchArgs.Store<BVHDispatchArgs>(0, BVHDispatchArgs(0, 1, 1, 0));
}

[numthreads(64, 1, 1)]
void InitQueue(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint instanceID = dispatchThreadID.x;

    if (instanceID >= Uniforms.InstanceCount)
        return;

    uint bvhRootIndex = LoadInstanceBVHRootIndex(InstanceHeaders, instanceID);
    if (bvhRootIndex == 0xFFFFFFFF)
        return;

    uint outIndex;
    NextDispatchArgs.InterlockedAdd(12, 1, outIndex);
    if (outIndex < Uniforms.MaxQueueNodes)
    {
        Queue_Next[outIndex] = uint2(bvhRootIndex, instanceID);
    }
}
