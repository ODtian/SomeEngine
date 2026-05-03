#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

// ── Material System Interfaces (MVP) ──
// Minimal PixelContext + ISurfaceEvaluate for the Visibility Buffer shading pipeline.
// Light/Decal/IBL providers will be added as separate interfaces in future iterations.

/// Pixel context for surface evaluation.
/// Constructed by the pipeline from VisBuffer data, consumed by materials.
struct PixelContext
{
    // ── Geometry ──
    uint2  pixelCoord;
    uint   instanceID;
    float3 worldPos;
    float3 worldNormal;     // interpolated + transformed to world space
    float4 worldTangent;    // xyz = tangent dir (world), w = handedness (±1)
    float2 uv0;

    // ── Output target ── (set by pipeline before calling evaluateSurface)
    uint2  _outputSize;     // internal: output texture dimensions
};

/// Surface evaluation interface.
/// Materials implement this to perform shading in the Shade CS pass.
interface ISurfaceEvaluate
{
    void evaluateSurface(PixelContext ctx, RWTexture2D<float4> outputColor);
};

static const uint MATERIAL_SCALAR_HEADER_BYTES = 16;

ByteAddressBuffer MaterialScalarRegion;

uint GetMaterialScalarPayloadByteSize()
{
    return MaterialScalarRegion.Load(0);
}

uint LoadMaterialScalarLayoutHash()
{
    return MaterialScalarRegion.Load(4);
}

uint4 LoadMaterialScalarBytesRaw(uint byteOffset, uint4 defaultValue)
{
    if (byteOffset + 16 > GetMaterialScalarPayloadByteSize())
        return defaultValue;

    return MaterialScalarRegion.Load4(MATERIAL_SCALAR_HEADER_BYTES + byteOffset);
}

T LoadMaterialScalarBytes<T>(uint byteOffset, T defaultValue)
{
    if (byteOffset + sizeof(T) > GetMaterialScalarPayloadByteSize())
        return defaultValue;

    return MaterialScalarRegion.Load<T>(MATERIAL_SCALAR_HEADER_BYTES + byteOffset);
}

T LoadMaterialScalars<T>(T defaultValue)
{
    if (GetMaterialScalarPayloadByteSize() < sizeof(T))
        return defaultValue;

    return MaterialScalarRegion.Load<T>(MATERIAL_SCALAR_HEADER_BYTES);
}

#endif
