#ifndef CLUSTER_STRUCTURES_SLANG
#define CLUSTER_STRUCTURES_SLANG

static const uint GPU_CLUSTER_STRIDE_BYTES = 88;
static const uint SLOT_INVALID = 0xFFFFu;
static const uint BIN_INVALID = 0xFFFFFFFFu;

struct GPUPageHeader
{
    uint ClusterCount;        //  0
    uint TotalVertexCount;    //  4
    uint TotalTriangleCount;  //  8
    float QuantOriginX;       // 12
    uint ClustersOffset;      // 16
    uint PositionsOffset;     // 20
    uint AttributesOffset;    // 24
    uint IndicesOffset;       // 28
    float QuantOriginY;       // 32
    float QuantOriginZ;       // 36
    float QuantStep;          // 40
};

struct GPUCluster
{
    int3 IntBase;               //  0 (12 bytes)
    uint PackedCenterXY;        // 12 — CenterOffsetX:16 | CenterOffsetY:16
    float3 LODCenter;           // 16 (12 bytes)
    float LODRadius;            // 28
    uint PackedCenterZRadius;   // 32 — CenterOffsetZ:16 | RadiusQuant:16
    uint PackedLodErrVStart;    // 36 — LODErrorHalf:16 | VertexStart:16
    uint PackedTStartGroup;     // 40 — TriangleStart:16 | GroupId(i16):16
    uint PackedCounts;          // 44 — [VertexCount:8][TriangleCount:8][LODLevel:8][Pad:8]
    //  Total: 48 bytes
    
    uint PackedMaterials;       // 48 — [mat0:8][mat1:8][mat2:8][SlowPathFlag:8] (0xFF = slow path)
    uint PackedRanges;          // 52 — [range0End:8][range1End:8][Pad:16]
    uint MaterialTableOffset;   // 56 — slow path external table byte offset (0xFFFFFFFF = fast path)
    uint VRBBatchInfo;          // 60 — VRB batch encoding (fast: ≤5 batches packed)
    float3 BoundMin;            // 64 — object-space cluster AABB min
    float3 BoundMax;            // 76 — object-space cluster AABB max
    //  Total: 88 bytes
};

// Decode helpers
float3 DecodeClusterCenter(GPUCluster c, float3 quantOrigin, float quantStep)
{
    uint centerOffX = c.PackedCenterXY & 0xFFFF;
    uint centerOffY = (c.PackedCenterXY >> 16) & 0xFFFF;
    uint centerOffZ = c.PackedCenterZRadius & 0xFFFF;
    int3 centerInt = c.IntBase + int3(int(centerOffX), int(centerOffY), int(centerOffZ));
    return float3(centerInt) * quantStep + quantOrigin;
}

float DecodeClusterRadius(GPUCluster c, float quantStep)
{
    uint radiusQuant = (c.PackedCenterZRadius >> 16) & 0xFFFF;
    return float(radiusQuant) * quantStep;
}

float DecodeClusterLODError(GPUCluster c)
{
    uint lodErrBits = c.PackedLodErrVStart & 0xFFFF;
    return f16tof32(lodErrBits);
}

uint DecodeVertexStart(GPUCluster c)
{
    return (c.PackedLodErrVStart >> 16) & 0xFFFF;
}

uint DecodeTriangleStart(GPUCluster c)
{
    return c.PackedTStartGroup & 0xFFFF;
}

int DecodeGroupId(GPUCluster c)
{
    // Sign-extend from 16-bit
    int raw = int(c.PackedTStartGroup >> 16);
    return (raw << 16) >> 16;
}

// ─── VRB Batch Decode ───

struct VRBBatch
{
    uint start; // first triangle index in this batch
    uint count; // number of triangles
};

uint DecodeVRBBatchCount(GPUCluster c)
{
    return ((c.VRBBatchInfo >> 25) & 0x7) + 1;
}

VRBBatch DecodeVRBBatch(GPUCluster c, uint batchIdx)
{
    VRBBatch batch;
    batch.start = 0;
    for (uint i = 0; i < batchIdx; i++)
        batch.start += ((c.VRBBatchInfo >> (i * 5)) & 0x1F) + 1;
    batch.count = ((c.VRBBatchInfo >> (batchIdx * 5)) & 0x1F) + 1;
    return batch;
}

// Total triangles covered by encoded VRB batches (fast path portion)
uint DecodeVRBTotalFastTris(GPUCluster c)
{
    uint batchCount = DecodeVRBBatchCount(c);
    uint total = 0;
    for (uint i = 0; i < batchCount; i++)
        total += ((c.VRBBatchInfo >> (i * 5)) & 0x1F) + 1;
    return total;
}

uint DecodeTriangleCount(GPUCluster c)
{
    return (c.PackedCounts >> 8) & 0xFF;
}

struct ClusterBVHNode
{
    float4 BoundMin;
    float4 BoundMax;
    float4 LODSphere; // xyz: Center, w: Radius
    float LODError;
    uint ChildPointer;
    uint ChildCount;
    uint NodeType; // 0=Internal, 1=Leaf
};

// ─── SlotBuffer SOA access ───
// GPU buffer is StructuredBuffer<uint>, SOA layout:
//   field0: [slot0,slot1,...] | field1: [slot0,slot1,...] | ...
// CPU slot address: slot offset + local material index.
// Each pair of ushort slots packs into one uint.
// slotCapacity is always even.
uint GetSlotField(
    StructuredBuffer<uint> slotBuffer,
    uint slotOffset,
    uint localMaterialIndex,
    uint fieldIndex,
    uint slotCapacity)
{
    uint slotIndex = slotOffset + localMaterialIndex;
    if (slotIndex >= slotCapacity)
        return SLOT_INVALID;

    uint fieldBaseUint = fieldIndex * (slotCapacity / 2);
    uint wordIdx = fieldBaseUint + (slotIndex / 2);
    uint packed = slotBuffer[wordIdx];
    return (slotIndex % 2 == 0) ? (packed & 0xFFFF) : (packed >> 16);
}

uint LocalMaterial(uint packedMaterials, uint packedRanges, uint triStart)
{
    uint range0End = packedRanges & 0xFFu;
    uint range1End = (packedRanges >> 8) & 0xFFu;
    uint lane = triStart < range0End ? 0u : (triStart < range1End ? 1u : 2u);
    return (packedMaterials >> (lane * 8u)) & 0xFFu;
}

uint MaterialEnd(uint packedRanges, uint triStart, uint triangleCount)
{
    uint range0End = min(packedRanges & 0xFFu, triangleCount);
    uint range1End = min((packedRanges >> 8) & 0xFFu, triangleCount);
    return triStart < range0End ? range0End : (triStart < range1End ? range1End : triangleCount);
}

struct AABB
{
    float3 Min;
    float3 Max;
    uint Color; // Packed RGBA
    uint Pad;
};

// ─── GPU-side typed struct definitions for eliminating manual byte offsets ───

struct DispatchArgs
{
    uint GroupX;
    uint GroupY;
    uint GroupZ;
};

struct DrawInstancedArgs
{
    uint VertexCount;
    uint InstanceCount;
    uint StartVertex;
    uint StartInstance;
};

// Cull output draw args: [0]=vertex count per cluster, [1]=swCount, [2]=hwCount.
// The raw buffer slot at byte 12 is used as an internal total reservation counter.
struct CullDrawArgs
{
    uint VertexCountPerCluster;
    uint SWCount;
    uint HWCount;
};

// BVH traverse dispatch: {GroupX, GroupY, GroupZ, NodeCount}
struct BVHDispatchArgs
{
    uint GroupX;
    uint GroupY;
    uint GroupZ;
    uint NodeCount;
};

DispatchArgs LoadDispatchArgs(ByteAddressBuffer buffer, uint byteOffset)
{
    return DispatchArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

DispatchArgs LoadDispatchArgs(RWByteAddressBuffer buffer, uint byteOffset)
{
    return DispatchArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

void StoreDispatchArgs(RWByteAddressBuffer buffer, uint byteOffset, DispatchArgs value)
{
    buffer.Store(byteOffset + 0u, value.GroupX);
    buffer.Store(byteOffset + 4u, value.GroupY);
    buffer.Store(byteOffset + 8u, value.GroupZ);
}

DrawInstancedArgs LoadDrawInstancedArgs(ByteAddressBuffer buffer, uint byteOffset)
{
    return DrawInstancedArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u),
        buffer.Load(byteOffset + 12u));
}

DrawInstancedArgs LoadDrawInstancedArgs(RWByteAddressBuffer buffer, uint byteOffset)
{
    return DrawInstancedArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u),
        buffer.Load(byteOffset + 12u));
}

void StoreDrawInstancedArgs(RWByteAddressBuffer buffer, uint byteOffset, DrawInstancedArgs value)
{
    buffer.Store(byteOffset + 0u, value.VertexCount);
    buffer.Store(byteOffset + 4u, value.InstanceCount);
    buffer.Store(byteOffset + 8u, value.StartVertex);
    buffer.Store(byteOffset + 12u, value.StartInstance);
}

CullDrawArgs LoadCullDrawArgs(ByteAddressBuffer buffer, uint byteOffset)
{
    return CullDrawArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

CullDrawArgs LoadCullDrawArgs(RWByteAddressBuffer buffer, uint byteOffset)
{
    return CullDrawArgs(
        buffer.Load(byteOffset + 0u),
        buffer.Load(byteOffset + 4u),
        buffer.Load(byteOffset + 8u));
}

void StoreCullDrawArgs(RWByteAddressBuffer buffer, uint byteOffset, CullDrawArgs value)
{
    buffer.Store(byteOffset + 0u, value.VertexCountPerCluster);
    buffer.Store(byteOffset + 4u, value.SWCount);
    buffer.Store(byteOffset + 8u, value.HWCount);
}

// Raster Bin metadata (32B = 8×uint)
struct RasterBinEntry
{
    uint ClusterOffset;
    uint BinCapacity;
    uint BinSWCount;
    uint BinHWCount;
    uint SWCursor;
    uint HWCursor;
    uint SWOffset;     // Exclusive prefix sum of SW clusters only across bins
    uint Pad;
};

// Deform Bin metadata (16B = 4xuint). Deform cache dispatch is keyed by VertexEval,
// not RasterBin, so keep this layout independent of RasterBinEntry.
struct DeformBinEntry
{
    uint ClusterOffset;
    uint BinCount;
    uint Cursor;
    uint Pad;
};

#endif
