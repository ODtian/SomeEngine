#include "cluster_shade_pipeline.slang"
#include "standard_pbr.slang"

// ── Material resources (bound per-material via SRB) ──
Texture2D AlbedoMap;
Texture2D NormalMap;
Texture2D ARMMap;
SamplerState MaterialSampler;

// ── Entry Points ──

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShade(uint3 tid : SV_DispatchThreadID)
{
    WaveVertexEval vertexEval;
    StandardPBRMaterial mat;
    mat.AlbedoMap = AlbedoMap;
    mat.NormalMap = NormalMap;
    mat.ARMMap = ARMMap;
    mat.MaterialSampler = MaterialSampler;
    CSShade(vertexEval, mat, tid);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShadeCached(uint3 tid : SV_DispatchThreadID)
{
    WaveVertexEval vertexEval;
    StandardPBRMaterial mat;
    mat.AlbedoMap = AlbedoMap;
    mat.NormalMap = NormalMap;
    mat.ARMMap = ARMMap;
    mat.MaterialSampler = MaterialSampler;
    CSShadeFromCache(vertexEval, mat, tid);
}
