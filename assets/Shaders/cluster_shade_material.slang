#include "cluster_shade_pipeline.slang"
#include "standard_pbr.slang"

[MaterialTarget("cluster.shade")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShade(uint3 tid : SV_DispatchThreadID, uniform StandardPBRMaterial material)
{
    StaticVertexEval vertexEval;
    CSShade(vertexEval, material, tid);
}

[MaterialTarget("cluster.shade.cached")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShadeCached(uint3 tid : SV_DispatchThreadID, uniform StandardPBRMaterial material)
{
    StaticVertexEval vertexEval;
    CSShadeFromCache(vertexEval, material, tid);
}
