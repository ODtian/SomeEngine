#include "common.slang"
#include "cluster_structures.slang"

struct CullingUniforms
{
    float4x4 ViewProj;
    float3 CameraPos;
    float LodThreshold; // 64+12=76
    
    float LodScale; // 80
    uint PageOffset; // 84
    uint PageID; // 88
    uint BaseInstanceID; // 92
    
    int ForcedLODLevel; // 96
    uint InstanceCount; // 100
    uint DebugMode; // 104
    uint VisualiseBVH; // 108
    int DebugBVHDepth; // 112
    uint CurrentDepth; // 116
};

ConstantBuffer<CullingUniforms> Uniforms;

StructuredBuffer<ClusterBVHNode> GlobalBVH;
StructuredBuffer<uint> PageTable;
ByteAddressBuffer PageHeap;

StructuredBuffer<uint> Queue_Current;
ByteAddressBuffer CurrentDispatchArgs;
RWStructuredBuffer<uint> Queue_Next;
RWByteAddressBuffer NextDispatchArgs;
RWByteAddressBuffer CandidateCount;
RWStructuredBuffer<uint2> CandidateClusters;

RWStructuredBuffer<AABB> DebugAABBs;
RWByteAddressBuffer DebugAABBCount;

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint count = CurrentDispatchArgs.Load(12);
    uint threadId = dispatchThreadID.x;
    if (threadId >= count) return;
    
    uint nodeIndex = Queue_Current[threadId];
    ClusterBVHNode node = GlobalBVH[nodeIndex];
    
    bool culled = false;
    uint color = 0x8000FF00; // Default: Green (Accepted)

    float dist = distance(node.LODSphere.xyz, Uniforms.CameraPos);
    float distToSurface = max(0.0, dist - node.LODSphere.w);
    
    // LOD Culling
    if (Uniforms.ForcedLODLevel < 0 && Uniforms.DebugMode == 0) {
        if (node.LODError * Uniforms.LodScale <= Uniforms.LodThreshold * distToSurface) {
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
            // Blue:  0x80FF0000 (A=128, B=255)
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

    if (culled) return;
    
    if (node.NodeType == 0) {
        uint start = node.ChildPointer;
        uint count = node.ChildCount;
        uint outIndex;
        NextDispatchArgs.InterlockedAdd(12, count, outIndex);
        for (uint i = 0; i < count; ++i) Queue_Next[outIndex + i] = start + i;
    } else {
        uint packed = node.ChildPointer;
        uint pageID = packed >> 12;
        uint start = packed & 0xFFF;
        uint count = node.ChildCount;
        
        uint outIndex;
        // Offset 0 used for count storage
        CandidateCount.InterlockedAdd(0, count, outIndex);
        
        for (uint i = 0; i < count; ++i) {
             uint clusterID = start + i;
             CandidateClusters[outIndex + i] = uint2(pageID, clusterID);
        }
    }
}


[numthreads(1, 1, 1)]
void UpdateArgs(uint3 id : SV_DispatchThreadID)
{
    uint count = NextDispatchArgs.Load(12);
    uint groups = (count + 63) / 64;
    NextDispatchArgs.Store(0, groups);
    NextDispatchArgs.Store(4, 1);
    NextDispatchArgs.Store(8, 1);
}
