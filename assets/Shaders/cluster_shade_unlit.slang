#include "cluster_shade_pipeline.slang"

[MaterialScalars]
struct UnlitScalars
{
    float4 BaseColorTint;
};

struct UnlitPixelContext
{
    uint2 pixelCoord;
    uint instanceID;
    float3 worldNormal;
    float2 uv0;
};

UnlitPixelContext BuildUnlitPixelContext(
    uint2 pixelCoord,
    uint instanceID,
    float3 bary,
    StaticVertexEval.DeformedVertex v0,
    StaticVertexEval.DeformedVertex v1,
    StaticVertexEval.DeformedVertex v2)
{
    UnlitPixelContext ctx;
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = instanceID;
    ctx.worldNormal = normalize(bary.x * v0.worldNormal + bary.y * v1.worldNormal + bary.z * v2.worldNormal);
    ctx.uv0 = bary.x * v0.uv0 + bary.y * v1.uv0 + bary.z * v2.uv0;
    return ctx;
}

struct UnlitMaterial : ISurfaceEvaluate<StaticVertexEval>
{
    Texture2D AlbedoMap;
    SamplerState MaterialSampler;

    void evaluateSurface(
        uniform StaticVertexEval eval,
        uint2 pixelCoord,
        uint instanceID,
        float3 bary,
        StaticVertexEval.DeformedVertex v0,
        StaticVertexEval.DeformedVertex v1,
        StaticVertexEval.DeformedVertex v2,
        RWTexture2D<float4> outputColor)
    {
        UnlitPixelContext ctx = BuildUnlitPixelContext(
            pixelCoord,
            instanceID,
            bary,
            v0,
            v1,
            v2);
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        UnlitScalars scalars = LoadMaterialScalars<UnlitScalars>();
        float4 instanceTint = getInstancePropertyFloat4(ctx.instanceID, 0, float4(1.0));
        float4 tint = scalars.BaseColorTint * instanceTint;
        outputColor[ctx.pixelCoord] = float4(albedoSample.rgb * tint.rgb, 1.0);
    }

    void evaluateDebug(
        uniform StaticVertexEval eval,
        uint debugMode,
        uint2 pixelCoord,
        uint instanceID,
        float3 bary,
        StaticVertexEval.DeformedVertex v0,
        StaticVertexEval.DeformedVertex v1,
        StaticVertexEval.DeformedVertex v2,
        RWTexture2D<float4> outputColor)
    {
        if (debugMode == 8)
        {
            outputColor[pixelCoord] = float4(bary, 1.0);
            return;
        }

        UnlitPixelContext ctx = BuildUnlitPixelContext(
            pixelCoord,
            instanceID,
            bary,
            v0,
            v1,
            v2);

        if (debugMode == 9)
        {
            outputColor[pixelCoord] = float4(ctx.worldNormal * 0.5 + 0.5, 1.0);
            return;
        }

        if (debugMode == 10)
        {
            float checker = step(0.5, frac(ctx.uv0.x * 8.0)) != step(0.5, frac(ctx.uv0.y * 8.0)) ? 0.8 : 0.2;
            outputColor[pixelCoord] = float4(ctx.uv0.x, ctx.uv0.y, checker, 1.0);
            return;
        }

        if (debugMode == 11)
        {
            float3 pos = bary.x * v0.position + bary.y * v1.position + bary.z * v2.position;
            float3 basePos = bary.x * v0.basePosition + bary.y * v1.basePosition + bary.z * v2.basePosition;
            outputColor[pixelCoord] = float4(abs(pos - basePos) * 3.0, 1.0);
            return;
        }

        outputColor[pixelCoord] = float4(1.0, 0.0, 1.0, 1.0);
    }
};

[MaterialTarget("cluster.shade")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSUnlitShade(uint3 tid : SV_DispatchThreadID, uniform UnlitMaterial material)
{
    StaticVertexEval vertexEval;
    CSShade(vertexEval, material, tid);
}

[MaterialTarget("cluster.shade.cached")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSUnlitShadeCached(uint3 tid : SV_DispatchThreadID, uniform UnlitMaterial material)
{
    StaticVertexEval vertexEval;
    CSShadeFromCache(vertexEval, material, tid);
}
