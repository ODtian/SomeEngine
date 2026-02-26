#include "common.slang"
#include "cluster_structures.slang"

struct PatchUniforms
{
    uint NodeCount;
    uint NewPagePointer; // New PageOffset or 0xFFFFFFFF for PageFault
};

ConstantBuffer<PatchUniforms> Uniforms;

StructuredBuffer<uint> NodeIndices;
RWStructuredBuffer<ClusterBVHNode> GlobalBVH;

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint threadId = dispatchThreadID.x;
    if (threadId >= Uniforms.NodeCount) return;
    
    uint nodeIndex = NodeIndices[threadId];
    ClusterBVHNode node = GlobalBVH[nodeIndex];
    if (node.NodeType == 1) // Ensure it's a leaf node just in case
    {
        node.ChildPointer = Uniforms.NewPagePointer;
        GlobalBVH[nodeIndex] = node;
    }
}
