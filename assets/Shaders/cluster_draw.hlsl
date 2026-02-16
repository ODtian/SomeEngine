#include "common.hlsl"

cbuffer DrawUniforms
{
    float4x4 ViewProj;
    float4x4 View; // Needed for billboard/impostors if any
    uint PageTableSize;
    uint DebugMode;
};

// Hash Function for Cluster Visualization
uint PCGHash(uint input)
{
    uint state = input * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    uint h = (word >> 22u) ^ word;
    return h;
}

float3 ColorFromHash(uint hash)
{
    float r = float(hash & 255u) / 255.0;
    float g = float((hash >> 8) & 255u) / 255.0;
    float b = float((hash >> 16) & 255u) / 255.0;
    return float3(r, g, b);
}

// Page Table: Maps PageID to ByteOffset in PageHeap
StructuredBuffer<uint> PageTable : register(t0);

// Huge bindless heap
ByteAddressBuffer PageHeap : register(t1);

// Debug/Impostor Instances if needed
StructuredBuffer<GpuTransform> Instances : register(t2);

struct DrawRequest
{
    uint PageID;
    uint ClusterID;
    uint InstanceID;
    uint Pad;
};
StructuredBuffer<DrawRequest> RequestBuffer : register(t3);

struct PSInput
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
    nointerpolation uint ClusterID : CLUSTER_ID;
};

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
    // ... we only need offsets here
    uint VertexStart;
    uint TriangleStart;
    uint PackedCounts; 
};

// Helper to load partial cluster info needed for draw
void LoadClusterInfo(uint pageOffset, uint clusterId, out float3 center, out float radius, out uint vStart, out uint tStart)
{
    uint clusterOffset = pageOffset + 32 + clusterId * 60; // Header is 32 bytes, Stride 60
    // Need Center (0), Radius (12)
    float4 v0 = asfloat(PageHeap.Load4(clusterOffset));
    center = v0.xyz;
    radius = v0.w;
    
    // Need VStart(40), TStart(44)
    // Offset 40
    uint2 v1 = PageHeap.Load2(clusterOffset + 40);
    vStart = v1.x;
    tStart = v1.y;
}

PSInput VSMain(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    PSInput output;
    
    // 1. Decode Page and Cluster from RequestBuffer
    DrawRequest req = RequestBuffer[instanceID];
    uint pageID = req.PageID;
    uint clusterID = req.ClusterID;
    uint realInstanceID = req.InstanceID;
    
    // 2. Get Page Offset
    uint pageOffset = PageTable[pageID];
    
    // 3. Load Header (To get stream offsets)
    // Header is always at pageOffset
    uint4 header1 = PageHeap.Load4(pageOffset + 16); // Offsets are at 16
    uint clustersOffset = header1.x; // Not used, we computed it
    uint posOffset = header1.y;
    uint attrOffset = header1.z;
    uint idxOffset = header1.w;
    
    // 4. Load Cluster Info
    float3 center;
    float radius;
    uint vStart;
    uint tStart;
    LoadClusterInfo(pageOffset, clusterID, center, radius, vStart, tStart);
    
    // 5. Get Triangle Count from Cluster (We need to discard if vertexID out of range)
    // Optimization: Read PackedCounts at correct offset (56)
    uint clusterOffset = pageOffset + 32 + clusterID * 60;
    uint packedCounts = PageHeap.Load(clusterOffset + 56);
    uint triCount = (packedCounts >> 8) & 0xFF;
    
    if (vertexID >= triCount * 3)
    {
        output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000)); // NaN -> Discard
        output.Normal = float3(0,0,0);
        output.UV = float2(0,0);
        output.Color = float3(0,0,0);
        return output;
    }
    
    // 6. Fetch Triangle Index
    uint triIdx = vertexID / 3;
    uint corner = vertexID % 3;
    
    // 6. Fetch Vertex Index (u8)
    // Index Stream is u8 array.
    // Offset = pageOffset + idxOffset + tStart + triIdx * 3 + corner
    uint indexByteAddr = pageOffset + idxOffset + tStart + triIdx * 3 + corner;
    
    // Load aligned word and extract byte
    uint alignedAddr = indexByteAddr & ~3;
    uint shift = (indexByteAddr & 3) * 8;
    uint packedIndex = PageHeap.Load(alignedAddr);
    uint localVIdx = (packedIndex >> shift) & 0xFF;
    
    // 7. Fetch Position (u16[3])
    // Offset = pageOffset + posOffset + (vStart + localVIdx) * 6
    uint posByteAddr = pageOffset + posOffset + (vStart + localVIdx) * 6;
    uint alignedPosAddr = posByteAddr & ~3;
    uint offsetInWord = posByteAddr & 3;
    
    uint2 rawPosWords = PageHeap.Load2(alignedPosAddr); // Load 8 bytes to cover 6
    
    // Extract 3 ushorts
    // Complex extraction due to alignment
    uint p0, p1, p2;
    if (offsetInWord == 0) {
        // [p1H p1L p0H p0L]
        p0 = rawPosWords.x & 0xFFFF;
        p1 = rawPosWords.x >> 16;
        p2 = rawPosWords.y & 0xFFFF;
    } else if (offsetInWord == 2) {
        // [p0H p0L xx xx] [p2H p2L p1H p1L]
        p0 = rawPosWords.x >> 16;
        p1 = rawPosWords.y & 0xFFFF;
        p2 = rawPosWords.y >> 16;
    } else { // offset 1 or 3, shouldn't happen for u16 stream aligned to 2 bytes?
             // Since base offsets in header are uint (4 bytes), and stride is 6.
             // 6 * N can be 2-aligned.
             // It is possible.
             p0 = 0; p1=0; p2=0; // Fail safe
    }
    
    // Dequantize
    float3 localPos;
    localPos.x = (float(p0) / 65535.0) * 2.0 - 1.0;
    localPos.y = (float(p1) / 65535.0) * 2.0 - 1.0;
    localPos.z = (float(p2) / 65535.0) * 2.0 - 1.0;
    
    float3 position = center + localPos * radius;
    
    // 8. Transform
    output.Pos = mul(float4(position, 1.0), ViewProj);
    
    // 9. Fake Normal/Color for Debug
    float3 N = normalize(localPos); // Approx normal from sphere center
    output.Normal = N;
    output.Color = N * 0.5 + 0.5;
    output.UV = float2(0,0);
    output.ClusterID = clusterID;
    
    // Apply Instance Transform
    GpuTransform t = Instances[realInstanceID];
    float3 p = ApplyTransform(position, t); // Reuse common.hlsl logic
    output.Pos = mul(float4(p, 1.0), ViewProj);
    
    // Rotate Normal
    // ... Copy logic from old shader or common.hlsl
    
    // TODO: Load real Attributes
    // Need to know stride and format.
    
    return output;
}

float4 PSMain(PSInput input) : SV_TARGET
{
    if (DebugMode == 1)
    {
        uint h = PCGHash(input.ClusterID);
        return float4(ColorFromHash(h), 1.0);
    }
    // Debug Color based on Normals
    return float4(input.Color, 1.0);
}

float4 PSOverdraw(PSInput input) : SV_TARGET
{
    // Return small additive value
    return float4(0.05, 0.05, 0.05, 1.0);
}
