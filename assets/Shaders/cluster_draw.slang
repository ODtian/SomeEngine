#include "common.slang"

struct DrawUniforms
{
    float4x4 ViewProj;
    float4x4 View; // Needed for billboard/impostors if any
    uint PageTableSize;
    uint DebugMode;
};

ConstantBuffer<DrawUniforms> Uniforms;

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

// Huge bindless heap
ByteAddressBuffer PageHeap;

// Debug/Impostor Instances if needed
StructuredBuffer<GpuTransform> Instances;

struct DrawRequest
{
    uint PageOffset;
    uint ClusterID;
    uint InstanceID;
    uint Pad;
};

StructuredBuffer<DrawRequest> RequestBuffer;

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
    float3 LODCenter;
    float LODRadius;
    // ... we only need offsets here
    uint VertexStart;
    uint TriangleStart;
    uint PackedCounts;
};

// Helper to load partial cluster info needed for draw
void LoadClusterInfo(uint pageOffset, uint clusterId, out float3 center, out float radius, out uint vStart, out uint tStart, out uint packedCounts)
{
    uint clusterOffset = pageOffset + 32 + clusterId * 52; // Header is 32 bytes, Stride 52

    // 1. Center (0), Radius (12)
    float4 v0 = asfloat(PageHeap.Load4(clusterOffset));
    center = v0.xyz;
    radius = v0.w;

    // 2. LODCenter (16), LODRadius (28) - Skipped for draw

    // 3. LODError (32), VertexStart (36), TriangleStart (40), GroupId (44)
    uint4 v1 = PageHeap.Load4(clusterOffset + 32);
    // v1.x = LODError
    vStart = v1.y;
    tStart = v1.z;
    // v1.w = GroupId

    // 4. PackedCounts is at 48
    packedCounts = PageHeap.Load(clusterOffset + 48);
}

[shader("vertex")]
PSInput VSMain(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    PSInput output;

    // 1. Decode Page and Cluster from RequestBuffer
    DrawRequest req = RequestBuffer[instanceID];
    uint pageOffset = req.PageOffset;
    uint clusterID = req.ClusterID;
    uint realInstanceID = req.InstanceID;

    // 3. Load Header (To get stream offsets)
    // Header is always at pageOffset
    uint4 header1 = PageHeap.Load4(pageOffset + 16); // Offsets are at 16
    uint clustersOffset = header1.x;                 // Not used, we computed it
    uint posOffset = header1.y;
    uint attrOffset = header1.z;
    uint idxOffset = header1.w;

    // 4. Load Cluster Info
    float3 center;
    float radius;
    uint vStart;
    uint tStart;
    uint packedCounts;
    LoadClusterInfo(pageOffset, clusterID, center, radius, vStart, tStart, packedCounts);

    // 5. Get Triangle Count from Cluster (We need to discard if vertexID out of range)
    // Optimization: Read PackedCounts at correct offset (56)
    // uint clusterOffset = pageOffset + 32 + clusterID * 60;
    // uint packedCounts = PageHeap.Load(clusterOffset + 56);
    // [VertexCount:8][TriangleCount:8][LODLevel:8][Pad:8]
    // packedCounts is uint at offset 56.
    // Wait, struct says offset 48 + 8 = 56.
    // In LoadClusterInfo above, we loaded offset 48+8 -> offset 56. Correct.

    uint triCount = (packedCounts >> 8) & 0xFF;

    output.ClusterID = clusterID; // Pass to PS for debug

    if (vertexID >= triCount * 3)
    {
        output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000)); // NaN -> Discard
        output.Normal = float3(0, 0, 0);
        output.UV = float2(0, 0);
        output.Color = float3(0, 0, 0);
        return output;
    }

    uint lodLevel = (packedCounts >> 16) & 0xFF;

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

    // We need to load enough bytes. u16[3] = 6 bytes.
    // If alignedPosAddr is 4-byte aligned.
    // If offsetInWord=0, need bytes 0-5. Load 8 bytes (2 uints).
    // If offsetInWord=2, need bytes 2-7. Load 8 bytes (2 uints).

    uint2 rawPosWords = PageHeap.Load2(alignedPosAddr); // Load 8 bytes to cover 6

    // Extract 3 ushorts
    uint p0, p1, p2;
    // Assuming Little Endian
    // rawPosWords.x: [3][2][1][0]
    // rawPosWords.y: [7][6][5][4]

    if (offsetInWord == 0)
    {
        // [1 0] -> p0
        // [3 2] -> p1
        // [5 4] -> p2 (from .y [1 0])
        p0 = rawPosWords.x & 0xFFFF;
        p1 = (rawPosWords.x >> 16) & 0xFFFF;
        p2 = rawPosWords.y & 0xFFFF;
    }
    else if (offsetInWord == 2)
    {
        // [3 2] -> p0 (from .x)
        // [5 4] -> p1 (from .y [1 0])
        // [7 6] -> p2 (from .y [3 2])
        p0 = (rawPosWords.x >> 16) & 0xFFFF;
        p1 = rawPosWords.y & 0xFFFF;
        p2 = (rawPosWords.y >> 16) & 0xFFFF;
    }
    else
    {
        // Misaligned?
        p0 = 0;
        p1 = 0;
        p2 = 0;
    }

    // Dequantize (assuming normalized u16 to -1..1)
    float3 localPos;
    localPos.x = (float(p0) / 65535.0) * 2.0 - 1.0;
    localPos.y = (float(p1) / 65535.0) * 2.0 - 1.0;
    localPos.z = (float(p2) / 65535.0) * 2.0 - 1.0;

    float3 position = center + localPos * radius;

    // 8. Transform
    GpuTransform t = Instances[realInstanceID];
    float3 worldPos = ApplyTransform(position, t);
    output.Pos = mul(float4(worldPos, 1.0), Uniforms.ViewProj);

    // 9. Fake Normal/Color for Debug
    float3 N = normalize(localPos); // Approx normal from sphere center
    output.Normal = N;

    // Color by LOD Level if DebugMode is set in specific way, or just pass LOD
    if (Uniforms.DebugMode == 2) // LOD Visualization
    {
        // Heatmap: Blue (0) -> Green -> Red (High)
        float l = float(lodLevel) / 10.0;
        output.Color = float3(l, 1.0 - l, 0.0);
    }
    else
    {
        output.Color = N * 0.5 + 0.5;
    }

    output.UV = float2(0, 0);

    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    if (Uniforms.DebugMode == 1)
    {
        uint h = PCGHash(input.ClusterID);
        return float4(ColorFromHash(h), 1.0);
    }
    // Debug Color based on Normals
    return float4(input.Color, 1.0);
}

[shader("pixel")]
float4 PSOverdraw(PSInput input) : SV_TARGET
{
    // Return small additive value
    return float4(0.05, 0.05, 0.05, 1.0);
}
