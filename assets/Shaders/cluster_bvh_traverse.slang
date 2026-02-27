#include "cluster_structures.slang"
#include "common.slang"

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
};

ConstantBuffer<CullingUniforms> Uniforms;

StructuredBuffer<ClusterBVHNode> GlobalBVH;
ByteAddressBuffer PageHeap;
StructuredBuffer<GpuTransform> Instances;
StructuredBuffer<GpuInstanceHeader> InstanceHeaders;

cbuffer DepthIndexCB
{
    uint DepthIndex;
};

// ==========================================
// Queues (Ping-Pong)
// ==========================================
StructuredBuffer<uint2> Queue_Current;
ByteAddressBuffer CurrentDispatchArgs;
RWStructuredBuffer<uint2> Queue_Next;
RWByteAddressBuffer NextDispatchArgs;
RWByteAddressBuffer CandidateCount;
RWStructuredBuffer<uint3> CandidateClusters;
RWByteAddressBuffer PageFaultBuffer;

RWStructuredBuffer<AABB> DebugAABBs;
RWByteAddressBuffer DebugAABBCount;

static const uint MaxPageFaults = 4096;

bool IsNodeOutsideFrustum(ClusterBVHNode node, GpuTransform t)
{
    float3 localMin = node.BoundMin.xyz;
    float3 localMax = node.BoundMax.xyz;

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
    uint count = CurrentDispatchArgs.Load(12);
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

    bool culled = false;
    uint color = 0x8000FF00; // Default: Green (Accepted)

    if (Uniforms.DebugMode == 0)
    {
        if (IsNodeOutsideFrustum(node, t))
        {
            culled = true;
            color = 0x800000FF; // Red: culled by frustum
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
            color = 0x80FF0000; // Blue: Culled by LOD (RGBA: R=0, G=0, B=255, A=128 -> wait, 0x80FF0000 is B=255 in RGBA? No. 0xRRGGBBAA? Or 0xAABBGGRR?)
            // debug_aabb.slang uses:
            // float r = (color >> 0) & 0xFF;
            // float g = (color >> 8) & 0xFF;
            // float b = (color >> 16) & 0xFF;
            // float a = (color >> 24) & 0xFF;
            // So it's RGBA.
            // Red:   0x800000FF (A=128, B=0, G=0, R=255)
            // Green: 0x8000FF00 (A=128, G=255)
            color = 0x80FF0000;
        }
    }

    if (Uniforms.VisualiseBVH != 0)
    {
        bool showThisDepth = (Uniforms.DebugBVHDepth < 0) || (Uniforms.DebugBVHDepth == (int)Uniforms.CurrentDepth);
        if (showThisDepth)
        {
            uint debugIndex;
            // Offset 4 used for InstanceCount in Draw Args
            DebugAABBCount.InterlockedAdd(4, 1, debugIndex);

            AABB box;
            box.Min = node.BoundMin.xyz;
            box.Max = node.BoundMax.xyz;
            box.Color = color;
            box.Pad = 0;
            DebugAABBs[debugIndex] = box;
        }
    }

    if (culled)
        return;

    if (node.NodeType == 0)
    {
        uint start = node.ChildPointer;
        uint count = node.ChildCount;
        uint outIndex;
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
        // Offset 0 used for count storage
        CandidateCount.InterlockedAdd(0, count, outIndex);

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
    uint count = NextDispatchArgs.Load(12);
    uint groups = (count + 63) / 64;
    NextDispatchArgs.Store(0, groups);
    NextDispatchArgs.Store(4, 1);
    NextDispatchArgs.Store(8, 1);
}

[numthreads(1, 1, 1)]
void ClearArgs(uint3 id: SV_DispatchThreadID)
{
    NextDispatchArgs.Store(0, 0);
    NextDispatchArgs.Store(4, 1);
    NextDispatchArgs.Store(8, 1);
    NextDispatchArgs.Store(12, 0);
}

[numthreads(64, 1, 1)]
void InitQueue(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint instanceID = dispatchThreadID.x;
    if (instanceID >= Uniforms.InstanceCount)
        return;

    GpuInstanceHeader header = InstanceHeaders[instanceID];
    if (header.BVHRootIndex == 0xFFFFFFFF)
        return;

    uint outIndex;
    NextDispatchArgs.InterlockedAdd(12, 1, outIndex);
    if (outIndex < Uniforms.MaxQueueNodes)
    {
        Queue_Next[outIndex] = uint2(header.BVHRootIndex, instanceID);
    }
}
