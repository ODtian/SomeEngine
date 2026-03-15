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

#endif
