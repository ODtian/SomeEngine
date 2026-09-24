#ifndef CLUSTER_COMMON_SLANG
#define CLUSTER_COMMON_SLANG

#include "common.slang"
#include "cluster_structures.slang"
#include "generated/instance_header_layout.slang"
#include "visbuffer_encoding.slang"

// ── Shared structures ──

struct DrawRequest
{
    uint PageOffset;
    uint ClusterID;
    uint InstanceID;
    uint Pad;
};

// ── Page Heap helpers ──
// These require a `ByteAddressBuffer PageHeap` to be declared in the calling shader.

struct PageQuant
{
    float3 Origin;
    float Step;
};

PageQuant LoadPageQuant(ByteAddressBuffer pageHeap, uint pageOffset)
{
    PageQuant quant;
    quant.Origin = float3(
        asfloat(pageHeap.Load(pageOffset + 12u)),
        asfloat(pageHeap.Load(pageOffset + 32u)),
        asfloat(pageHeap.Load(pageOffset + 36u)));
    quant.Step = asfloat(pageHeap.Load(pageOffset + 40u));
    return quant;
}

/// Load cluster IntBase, VertexStart, TriangleStart, and PackedCounts from the page heap.
void LoadClusterInfo(ByteAddressBuffer pageHeap, uint pageOffset, uint clusterId,
                     out int3 intBase, out uint vStart, out uint tStart, out uint packedCounts)
{
    uint clustersOffset = pageHeap.Load(pageOffset + 16);
    uint clusterOffset = pageOffset + clustersOffset + clusterId * GPU_CLUSTER_STRIDE_BYTES;

    int3 ib;
    ib.x = asint(pageHeap.Load(clusterOffset + 0));
    ib.y = asint(pageHeap.Load(clusterOffset + 4));
    ib.z = asint(pageHeap.Load(clusterOffset + 8));
    intBase = ib;

    uint packedLodVStart = pageHeap.Load(clusterOffset + 36);
    vStart = (packedLodVStart >> 16) & 0xFFFF;

    uint packedTStartGroup = pageHeap.Load(clusterOffset + 40);
    tStart = packedTStartGroup & 0xFFFF;

    packedCounts = pageHeap.Load(clusterOffset + 44);
}

/// Fetch a quantized vertex position from the page heap and dequantize to object space.
float3 FetchVertexPosition(ByteAddressBuffer pageHeap, uint pageOffset, uint posOffset,
                           uint vStart, uint localVIdx, int3 intBase)
{
    uint posByteAddr = pageOffset + posOffset + (vStart + localVIdx) * 6;
    uint alignedPosAddr = posByteAddr & ~3;
    uint offsetInWord = posByteAddr & 3;

    uint2 rawPosWords = pageHeap.Load2(alignedPosAddr);

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
    PageQuant quant = LoadPageQuant(pageHeap, pageOffset);
    return float3(globalIntPos) * quant.Step + quant.Origin;
}

/// Fetch a single uint8 vertex index from the page heap.
uint FetchVertexIndex(ByteAddressBuffer pageHeap, uint pageOffset, uint idxOffset,
                      uint tStart, uint triIdx, uint corner)
{
    uint indexByteAddr = pageOffset + idxOffset + tStart + triIdx * 3 + corner;
    uint alignedAddr = indexByteAddr & ~3;
    uint shift = (indexByteAddr & 3) * 8;
    uint packedIndex = pageHeap.Load(alignedAddr);
    return (packedIndex >> shift) & 0xFF;
}

// ── Instance Data ──
StructuredBuffer<GpuTransform> Instances;
ByteAddressBuffer InstanceHeaders;
ByteAddressBuffer InstanceDataHeap;

uint GetInstanceBVHRootIndex(uint instanceID)
{
    return LoadInstanceBVHRootIndex(InstanceHeaders, instanceID);
}

uint GetInstanceSlotOffset(uint instanceID)
{
    return LoadInstanceSlotOffset(InstanceHeaders, instanceID);
}

float GetInstanceBoundsExpansion(uint instanceID)
{
    return LoadInstanceBoundsExpansionWorld(InstanceHeaders, instanceID);
}

float4 getInstancePropertyFloat4(uint instanceID, uint relativeOffset, float4 defaultValue)
{
    uint flags = LoadInstanceDataFlags(InstanceHeaders, instanceID);
    if ((flags & INSTANCE_DATA_FLAG_MATERIAL_OVERRIDE) != 0)
    {
        uint baseOffset = LoadInstanceDataOffset(InstanceHeaders, instanceID);
        return asfloat(InstanceDataHeap.Load4(baseOffset + relativeOffset));
    }
    return defaultValue;
}

// ── Screen-space utilities ──

/// Project world position to screen space, returning (screenX, screenY, ndcDepth).
float3 ProjectToScreen(float3 worldPos, float4x4 viewProj, uint screenWidth, uint screenHeight)
{
    float4 clip = mul(float4(worldPos, 1.0), viewProj);
    float3 ndc = clip.xyz / clip.w;
    float2 screen;
    screen.x = (ndc.x * 0.5 + 0.5) * float(screenWidth);
    screen.y = (1.0 - (ndc.y * 0.5 + 0.5)) * float(screenHeight);
    return float3(screen, ndc.z);
}

struct ScreenProjection
{
    float2 Pixel;
    float InvW;
    float Depth;
};

ScreenProjection ProjectToScreenProjection(float3 worldPos, float4x4 viewProj, uint screenWidth, uint screenHeight)
{
    float4 clip = mul(float4(worldPos, 1.0), viewProj);
    float invW = 1.0 / clip.w;
    float2 ndc = clip.xy * invW;

    ScreenProjection projection;
    projection.Pixel.x = (ndc.x * 0.5 + 0.5) * float(screenWidth);
    projection.Pixel.y = (1.0 - (ndc.y * 0.5 + 0.5)) * float(screenHeight);
    projection.InvW = invW;
    projection.Depth = clip.z * invW;
    return projection;
}

/// Compute barycentric coordinates from 2D screen positions.
float3 ComputeBarycentric(float2 p, float2 v0, float2 v1, float2 v2)
{
    float2 e0 = v1 - v0;
    float2 e1 = v2 - v0;
    float2 ep = p - v0;

    float d00 = dot(e0, e0);
    float d01 = dot(e0, e1);
    float d11 = dot(e1, e1);
    float dp0 = dot(ep, e0);
    float dp1 = dot(ep, e1);

    float invDenom = 1.0 / (d00 * d11 - d01 * d01);
    float u = (d11 * dp0 - d01 * dp1) * invDenom;
    float v = (d00 * dp1 - d01 * dp0) * invDenom;

    return float3(1.0 - u - v, u, v);
}

float3 ComputePerspectiveBarycentric(float2 p, ScreenProjection v0, ScreenProjection v1, ScreenProjection v2)
{
    float3 bary = ComputeBarycentric(p, v0.Pixel, v1.Pixel, v2.Pixel);
    float3 weighted = bary * float3(v0.InvW, v1.InvW, v2.InvW);
    float denom = weighted.x + weighted.y + weighted.z;
    if (abs(denom) < 1e-8)
        return float3(0.0, 0.0, 0.0);
    return weighted / denom;
}

// ── Debug / visualization ──

uint PCGHash(uint input)
{
    uint state = input * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float3 ColorFromHash(uint hash)
{
    float r = float(hash & 255u) / 255.0;
    float g = float((hash >> 8) & 255u) / 255.0;
    float b = float((hash >> 16) & 255u) / 255.0;
    return float3(r, g, b);
}

// ── SoA Stream Cursor ──
// Tracks cumulative byte offset across streams in a page's attribute region.
// Usage: create with { attrBase, totalVertexCount }, then call advance(bytesPerVertex)
// for each stream in the same order as ClusterBuilder writes them.
//
// Stream sizes vary by attribute:
//   NORMAL  (VEC3 × Int8)   = 3 bytes/vertex
//   TANGENT (VEC4 × Int8)   = 4 bytes/vertex
//   UV      (VEC2 × Float16)= 4 bytes/vertex
//   COLOR   (VEC3 × UInt8)  = 3 bytes/vertex  (or VEC4 = 4)

struct StreamCursor
{
    uint base;
    uint totalVerts;

    /// Advance past one stream, returning the base address of that stream.
    /// bytesPerVertex = actual bytes per vertex in this stream.
    [mutating] uint advance(uint bytesPerVertex)
    {
        uint current = base;
        base += totalVerts * bytesPerVertex;
        return current;
    }
};

// ── SoA Attribute Decode Helpers ──
// Each function decodes a single DWORD (4 bytes) loaded from a stream.
// All standard streams use stride = 4 bytes per vertex.
/// Decode Int8x4 SNORM → float4 (used for Normal, Tangent)
float4 DecodeSnorm8x4(uint raw)
{
    int4 s;
    s.x = int(raw & 0xFF);
    s.y = int((raw >> 8) & 0xFF);
    s.z = int((raw >> 16) & 0xFF);
    s.w = int((raw >> 24) & 0xFF);
    // Sign-extend from 8-bit
    s.x = (s.x << 24) >> 24;
    s.y = (s.y << 24) >> 24;
    s.z = (s.z << 24) >> 24;
    s.w = (s.w << 24) >> 24;
    return max(float4(s) / 127.0, float4(-1.0));
}

/// Decode Float16x2 → float2 (used for UV)
float2 DecodeFloat16x2(uint raw)
{
    return float2(f16tof32(raw & 0xFFFF), f16tof32(raw >> 16));
}

/// Decode UInt8x4 UNORM → float4 (used for Vertex Color)
float4 DecodeUnorm8x4(uint raw)
{
    float4 c;
    c.x = float(raw & 0xFF) / 255.0;
    c.y = float((raw >> 8) & 0xFF) / 255.0;
    c.z = float((raw >> 16) & 0xFF) / 255.0;
    c.w = float((raw >> 24) & 0xFF) / 255.0;
    return c;
}

/// Load N bytes (1-4) from ByteAddressBuffer at an arbitrary byte offset.
/// Handles unaligned access by loading the surrounding DWORD(s) and extracting.
/// Returns the bytes packed into the low bits of a uint.
uint LoadStreamBytes(ByteAddressBuffer pageHeap, uint byteAddr, uint numBytes)
{
    uint aligned = byteAddr & ~3u;
    uint shift = (byteAddr & 3u) * 8u;
    uint lo = pageHeap.Load(aligned);
    // If the bytes span a DWORD boundary, load the next DWORD too
    uint hi = (shift + numBytes * 8u > 32u) ? pageHeap.Load(aligned + 4u) : 0u;
    // Combine and extract
    uint combined = (shift > 0u) ? ((lo >> shift) | (hi << (32u - shift))) : lo;
    uint mask = (numBytes >= 4u) ? 0xFFFFFFFFu : ((1u << (numBytes * 8u)) - 1u);
    return combined & mask;
}

/// Generic: load one DWORD from a 4-byte-stride attribute stream.
uint LoadStreamDword(ByteAddressBuffer pageHeap, uint streamBase, uint vStart, uint localVi)
{
    return pageHeap.Load(streamBase + (vStart + localVi) * 4);
}

/// Decode an Int8x3 (3 bytes) SNORM → float3.
float3 DecodeSnorm8x3(uint raw)
{
    int3 s;
    s.x = int(raw & 0xFF);
    s.y = int((raw >> 8) & 0xFF);
    s.z = int((raw >> 16) & 0xFF);
    s.x = (s.x << 24) >> 24;
    s.y = (s.y << 24) >> 24;
    s.z = (s.z << 24) >> 24;
    return max(float3(s) / 127.0, float3(-1.0));
}

/// Fetch normal from a 3-byte-per-vertex stream (VEC3 × Int8 SNORM).
float3 FetchNormal3(ByteAddressBuffer pageHeap, uint streamBase, uint vStart, uint localVi)
{
    uint byteAddr = streamBase + (vStart + localVi) * 3;
    uint raw = LoadStreamBytes(pageHeap, byteAddr, 3);
    return DecodeSnorm8x3(raw);
}

/// Fetch normal from a 4-byte-per-vertex stream (Int8x4, padded, xyz valid).
float3 FetchNormal(ByteAddressBuffer pageHeap, uint streamBase, uint vStart, uint localVi)
{
    return DecodeSnorm8x4(LoadStreamDword(pageHeap, streamBase, vStart, localVi)).xyz;
}

/// Fetch tangent (Int8x4 SNORM, 4 bytes/vertex, xyzw valid, w = handedness).
float4 FetchTangent(ByteAddressBuffer pageHeap, uint streamBase, uint vStart, uint localVi)
{
    return DecodeSnorm8x4(LoadStreamDword(pageHeap, streamBase, vStart, localVi));
}

/// Fetch UV (Float16x2, 4 bytes/vertex). Uses unaligned load because
/// preceding streams may cause non-4-byte-aligned streamBase.
float2 FetchUV(ByteAddressBuffer pageHeap, uint streamBase, uint vStart, uint localVi)
{
    uint byteAddr = streamBase + (vStart + localVi) * 4;
    uint raw = LoadStreamBytes(pageHeap, byteAddr, 4);
    return DecodeFloat16x2(raw);
}

/// Fetch vertex color (UInt8x4 UNORM, 4 bytes/vertex).
float4 FetchVertexColor(ByteAddressBuffer pageHeap, uint streamBase, uint vStart, uint localVi)
{
    return DecodeUnorm8x4(LoadStreamDword(pageHeap, streamBase, vStart, localVi));
}

#endif
