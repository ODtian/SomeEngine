#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

#include "vertex_evaluate.slang"

interface ISurfaceEvaluate<TVE : IVertexEvaluate>
{
    void evaluateSurface(
        uniform TVE eval,
        uint2 pixelCoord,
        uint instanceID,
        float3 bary,
        TVE.DeformedVertex v0,
        TVE.DeformedVertex v1,
        TVE.DeformedVertex v2,
        RWTexture2D<float4> outputColor);

    void evaluateDebug(
        uniform TVE eval,
        uint debugMode,
        uint2 pixelCoord,
        uint instanceID,
        float3 bary,
        TVE.DeformedVertex v0,
        TVE.DeformedVertex v1,
        TVE.DeformedVertex v2,
        RWTexture2D<float4> outputColor);
};

static const uint MATERIAL_SCALAR_HEADER_BYTES = 16;

ByteAddressBuffer MaterialScalarRegion;

T LoadMaterialScalars<T>()
{
    return MaterialScalarRegion.Load<T>(MATERIAL_SCALAR_HEADER_BYTES);
}

#endif
