#include "common.slang"

struct DrawUniforms
{
    float4x4 ViewProj;
    float4x4 View; // Needed for billboard/impostors if any
    uint PageTableSize;
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
    float3 QuantOrigin;  // Global quantization origin
    float QuantStep;     // Global quantization step (power of 2)
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

// Points to original VisibleClusters index
StructuredBuffer<uint> BinnedClusterIndexBuffer;
StructuredBuffer<uint4> VisibleClusters;  // {PageOffset, ClusterID, InstanceID, Pad}

// Phase1 binds a zeroed buffer; Phase2 binds Phase2DrawArgs (offset at byte 16)
ByteAddressBuffer VisibleClusterMeta;

struct PSInput
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
    nointerpolation uint ClusterID : CLUSTER_ID;
};

// NOTE: GPUPageHeader and GPUCluster are defined in cluster_structures.slang

// Helper to load partial cluster info needed for draw
void LoadClusterInfo(uint pageOffset, uint clusterId,
                     out int3 intBase, out uint vStart, out uint tStart, out uint packedCounts)
{
    uint clustersOffset = PageHeap.Load(pageOffset + 16);
    uint clusterOffset = pageOffset + clustersOffset + clusterId * 48;

    // IntBase at offset 0 (int3 = 12 bytes)
    int3 ib;
    ib.x = asint(PageHeap.Load(clusterOffset + 0));
    ib.y = asint(PageHeap.Load(clusterOffset + 4));
    ib.z = asint(PageHeap.Load(clusterOffset + 8));
    intBase = ib;

    // PackedLodErrVStart at offset 36 — LODErrorHalf:16 | VertexStart:16
    uint packedLodVStart = PageHeap.Load(clusterOffset + 36);
    vStart = (packedLodVStart >> 16) & 0xFFFF;

    // PackedTStartGroup at offset 40 — TriangleStart:16 | GroupId:16
    uint packedTStartGroup = PageHeap.Load(clusterOffset + 40);
    tStart = packedTStartGroup & 0xFFFF;

    // PackedCounts at offset 44
    packedCounts = PageHeap.Load(clusterOffset + 44);
}

[shader("vertex")]
PSInput VSMain(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    PSInput output;

    // 1. Decode Page and Cluster from Binned Index
    uint clusterOffset = VisibleClusterMeta.Load(4);  // InstanceCount field of Phase1's DrawArgs
    uint binnedIndex = clusterOffset + instanceID;
    uint originalIndex = BinnedClusterIndexBuffer[binnedIndex];

    uint4 req = VisibleClusters[originalIndex];
    uint pageOffset = req.x;
    uint clusterID = req.y;
    uint realInstanceID = req.z;

    // 3. Load Header (To get stream offsets)
    // Header: clusters(16), pos(20), attr(24), idx(28)
    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint clustersOffset = header1.x;
    uint posOffset = header1.y;
    uint attrOffset = header1.z;
    uint idxOffset = header1.w;

    // 4. Load Cluster Info
    int3 intBase;
    uint vStart;
    uint tStart;
    uint packedCounts;
    LoadClusterInfo(pageOffset, clusterID, intBase, vStart, tStart, packedCounts);

    // 5. Get triangle count from packed cluster counts.
    // [VertexCount:8][TriangleCount:8][LODLevel:8][Pad:8]
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

    // Dequantize: IntBase + local offset → global integer → float position
    // This is bit-exact across clusters sharing the same vertex
    int3 globalIntPos = int3(int(p0), int(p1), int(p2)) + intBase;
    float3 position = float3(globalIntPos) * Uniforms.QuantStep + Uniforms.QuantOrigin;

    // 8. Transform
    GpuTransform t = Instances[realInstanceID];
    float3 worldPos = ApplyTransform(position, t);
    output.Pos = mul(float4(worldPos, 1.0), Uniforms.ViewProj);

    // 9. Fake Normal/Color for Debug
    float3 N = normalize(float3(globalIntPos) - float3(intBase)); // Approx normal from cluster base
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

// ============================================================
// Visibility Buffer output mode
// ============================================================

struct PSVisBufferInput
{
    float4 Pos : SV_POSITION;
    nointerpolation uint VisibleClusterIndex : VIS_CLUSTER_IDX;
    nointerpolation uint TriangleID : TRIANGLE_ID;
};

[shader("vertex")]
PSVisBufferInput VSVisBuffer(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    PSVisBufferInput output;

    uint clusterOffset = VisibleClusterMeta.Load(4);  // InstanceCount field of Phase1's DrawArgs
    uint binnedIndex = clusterOffset + instanceID;
    uint originalIndex = BinnedClusterIndexBuffer[binnedIndex];

    uint4 req = VisibleClusters[originalIndex];
    uint pageOffset = req.x;
    uint clusterID = req.y;
    uint realInstanceID = req.z;

    // Load header
    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint posOffset = header1.y;
    uint idxOffset = header1.w;

    // Load cluster info
    int3 intBase;
    uint vStart;
    uint tStart;
    uint packedCounts;
    LoadClusterInfo(pageOffset, clusterID, intBase, vStart, tStart, packedCounts);

    uint triCount = (packedCounts >> 8) & 0xFF;

    output.VisibleClusterIndex = originalIndex;
    output.TriangleID = vertexID / 3;

    if (vertexID >= triCount * 3)
    {
        output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000));
        return output;
    }

    // Fetch triangle index
    uint triIdx = vertexID / 3;
    uint corner = vertexID % 3;

    uint indexByteAddr = pageOffset + idxOffset + tStart + triIdx * 3 + corner;
    uint alignedAddr = indexByteAddr & ~3;
    uint shift = (indexByteAddr & 3) * 8;
    uint packedIndex = PageHeap.Load(alignedAddr);
    uint localVIdx = (packedIndex >> shift) & 0xFF;

    // Fetch position (u16[3])
    uint posByteAddr = pageOffset + posOffset + (vStart + localVIdx) * 6;
    uint alignedPosAddr = posByteAddr & ~3;
    uint offsetInWord = posByteAddr & 3;

    uint2 rawPosWords = PageHeap.Load2(alignedPosAddr);

    uint p0, p1, p2;
    if (offsetInWord == 0)
    {
        p0 = rawPosWords.x & 0xFFFF;
        p1 = (rawPosWords.x >> 16) & 0xFFFF;
        p2 = rawPosWords.y & 0xFFFF;
    }
    else if (offsetInWord == 2)
    {
        p0 = (rawPosWords.x >> 16) & 0xFFFF;
        p1 = rawPosWords.y & 0xFFFF;
        p2 = (rawPosWords.y >> 16) & 0xFFFF;
    }
    else
    {
        p0 = 0; p1 = 0; p2 = 0;
    }

    int3 globalIntPos = int3(int(p0), int(p1), int(p2)) + intBase;
    float3 position = float3(globalIntPos) * Uniforms.QuantStep + Uniforms.QuantOrigin;

    GpuTransform t = Instances[realInstanceID];
    float3 worldPos = ApplyTransform(position, t);
    output.Pos = mul(float4(worldPos, 1.0), Uniforms.ViewProj);

    return output;
}

[shader("pixel")]
uint PSVisBuffer(PSVisBufferInput input) : SV_TARGET
{
    // Encode: (VisibleClusterIndex + 1) in high 25 bits | TriangleID in low 7 bits
    // +1 offset so that 0 is reserved as "no geometry" sentinel (clear value)
    return ((input.VisibleClusterIndex + 1) << 7) | (input.TriangleID & 0x7F);
}
