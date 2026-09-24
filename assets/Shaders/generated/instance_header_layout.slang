#ifndef INSTANCE_HEADER_LAYOUT_SLANG
#define INSTANCE_HEADER_LAYOUT_SLANG

static const uint INSTANCE_HEADER_STRIDE_BYTES = 32;
static const uint2 INSTANCE_HEADER_LAYOUT_HASH = uint2(0x55DADE12u, 0xAAE5DCF2u);

static const uint IH_BVH_ROOT_INDEX = 0;
static const uint IH_SLOT_OFFSET = 4;
static const uint IH_INSTANCE_DATA_OFFSET = 8;
static const uint IH_INSTANCE_DATA_FLAGS = 12;
static const uint IH_BOUNDS_EXPANSION_WORLD = 16;

static const uint INSTANCE_DATA_FLAG_MATERIAL_OVERRIDE = 1u << 0;

uint InstanceHeaderLoadU32(ByteAddressBuffer headers, uint instanceID, uint byteOffset)
{
    return headers.Load(instanceID * INSTANCE_HEADER_STRIDE_BYTES + byteOffset);
}

float InstanceHeaderLoadF32(ByteAddressBuffer headers, uint instanceID, uint byteOffset)
{
    return asfloat(InstanceHeaderLoadU32(headers, instanceID, byteOffset));
}

uint LoadInstanceBVHRootIndex(ByteAddressBuffer headers, uint instanceID)
{
    return InstanceHeaderLoadU32(headers, instanceID, IH_BVH_ROOT_INDEX);
}

uint LoadInstanceSlotOffset(ByteAddressBuffer headers, uint instanceID)
{
    return InstanceHeaderLoadU32(headers, instanceID, IH_SLOT_OFFSET);
}

uint LoadInstanceDataOffset(ByteAddressBuffer headers, uint instanceID)
{
    return InstanceHeaderLoadU32(headers, instanceID, IH_INSTANCE_DATA_OFFSET);
}

uint LoadInstanceDataFlags(ByteAddressBuffer headers, uint instanceID)
{
    return InstanceHeaderLoadU32(headers, instanceID, IH_INSTANCE_DATA_FLAGS);
}

float LoadInstanceBoundsExpansionWorld(ByteAddressBuffer headers, uint instanceID)
{
    return InstanceHeaderLoadF32(headers, instanceID, IH_BOUNDS_EXPANSION_WORLD);
}

#endif
