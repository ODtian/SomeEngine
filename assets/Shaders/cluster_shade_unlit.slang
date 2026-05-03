#include "cluster_shade_pipeline.slang"

[MaterialScalars]
struct UnlitScalars
{
    float4 BaseColorTint;
};

UnlitScalars GetDefaultUnlitScalars()
{
    UnlitScalars scalars;
    scalars.BaseColorTint = float4(1.0);
    return scalars;
}

struct UnlitMaterial : ISurfaceEvaluate
{
    Texture2D AlbedoMap;
    SamplerState MaterialSampler;

    void evaluateSurface(PixelContext ctx, RWTexture2D<float4> outputColor)
    {
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        UnlitScalars scalars = LoadMaterialScalars<UnlitScalars>(
            GetDefaultUnlitScalars());
        float4 instanceTint = getInstancePropertyFloat4(ctx.instanceID, 0, float4(1.0));
        float4 tint = scalars.BaseColorTint * instanceTint;
        outputColor[ctx.pixelCoord] = float4(albedoSample.rgb * tint.rgb, 1.0);
    }
};

[ClusterShade]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSUnlitShade(uint3 tid : SV_DispatchThreadID, uniform UnlitMaterial material)
{
    StaticVertexEval vertexEval;
    CSShade(vertexEval, material, tid);
}

[ClusterShade]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSUnlitShadeCached(uint3 tid : SV_DispatchThreadID, uniform UnlitMaterial material)
{
    StaticVertexEval vertexEval;
    CSShadeFromCache(vertexEval, material, tid);
}
