#ifndef CLUSTER_STRUCTURES_SLANG
#define CLUSTER_STRUCTURES_SLANG

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

// ─── MaterialSlotBuffer SOA access ───
// GPU buffer is StructuredBuffer<uint>, SOA layout:
//   field0: [slot0,slot1,...] | field1: [slot0,slot1,...] | ...
// Each pair of ushort slots packs into one uint.
// slotCapacity is always even.
uint GetSlotField(StructuredBuffer<uint> slotBuffer, uint slotOffset, uint fieldIndex, uint slotCapacity)
{
    uint fieldBaseUint = fieldIndex * (slotCapacity / 2);
    uint wordIdx = fieldBaseUint + (slotOffset / 2);
    uint packed = slotBuffer[wordIdx];
    return (slotOffset % 2 == 0) ? (packed & 0xFFFF) : (packed >> 16);
}

struct GpuInstanceHeader
{
    uint BVHRootIndex;
    uint MaterialSlotOffset; // Index into MaterialSlotBuffer
    uint MetadataOffset;
    uint MetadataCount;

    float BoundsExpansion;  // Conservative AABB expansion (world space)
    uint Pad1;
    uint Pad2;
    uint Pad3;
};



struct AABB
{
    float3 Min;
    float3 Max;
    uint Color; // Packed RGBA
    uint Pad;
};



#endif
