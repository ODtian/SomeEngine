#include "cluster_shade_pipeline.slang"

struct UnlitMaterial : ISurfaceEvaluate
{
    Texture2D AlbedoMap;
    SamplerState MaterialSampler;

    void evaluateSurface(PixelContext ctx, RWTexture2D<float4> outputColor)
    {
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        float4 tint = getInstancePropertyFloat4(ctx.instanceID, 0, float4(1.0));
        outputColor[ctx.pixelCoord] = float4(albedoSample.rgb * tint.rgb, 1.0);
    }
};

// ── Material resources (bound per-material via SRB) ──
Texture2D AlbedoMap;
SamplerState MaterialSampler;

// ── Entry Points ──

[shader("compute")]
[numthreads(64, 1, 1)]
void CSUnlitShade(uint3 tid : SV_DispatchThreadID)
{
    WaveVertexEval vertexEval;
    UnlitMaterial mat;
    mat.AlbedoMap = AlbedoMap;
    mat.MaterialSampler = MaterialSampler;
    CSShade(vertexEval, mat, tid);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSUnlitShadeCached(uint3 tid : SV_DispatchThreadID)
{
    WaveVertexEval vertexEval;
    UnlitMaterial mat;
    mat.AlbedoMap = AlbedoMap;
    mat.MaterialSampler = MaterialSampler;
    CSShadeFromCache(vertexEval, mat, tid);
}
