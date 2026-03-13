#include "common.slang"
#include "cluster_structures.slang"

struct BVHPatchData
{
    uint NodeIndex;
    uint NewPagePointer;
};

struct PatchUniforms
{
    uint PatchCount;
    uint Pad0, Pad1, Pad2;
};
ConstantBuffer<PatchUniforms> Uniforms;

StructuredBuffer<BVHPatchData> Patches;
RWStructuredBuffer<ClusterBVHNode> GlobalBVH;

[numthreads(64, 1, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint threadId = dispatchThreadID.x;
    if (threadId >= Uniforms.PatchCount) return;
    
    BVHPatchData patch = Patches[threadId];
    uint nodeIndex = patch.NodeIndex;
    
    ClusterBVHNode node = GlobalBVH[nodeIndex];
    if (node.NodeType == 1) // Ensure it's a leaf node just in case
    {
        node.ChildPointer = patch.NewPagePointer;
        GlobalBVH[nodeIndex] = node;
    }
}
