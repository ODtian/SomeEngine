#ifndef STANDARD_PBR_SLANG
#define STANDARD_PBR_SLANG

#include "material_interfaces.slang"
#include "brdf.slang"
#include "cluster_shade_pipeline.slang"

// C# imports this marked struct through Slang reflection and writes the material
// scalar payload using Slang's own field offsets and type size.
[MaterialScalars]
struct StandardPBRScalars
{
    float4 BaseColorTint;
    float MetallicFactor;
    float Roughness;
    float3 EmissiveFactor;
};

struct StandardPBRPixelContext
{
    uint2 pixelCoord;
    uint instanceID;
    float3 surfacePosition;
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

static const uint DEFAULT_LIGHT_LAYER_MASK = 0xFFFFFFFFu;
static const int NO_COOKIE = -1;

float SampleLightCookie(GPULight light, float3 surfacePosition)
{
    if (light.CookieIndex == NO_COOKIE)
        return 1.0;

    float4 cookiePosition = mul(float4(surfacePosition, 1.0), light.WorldToLightCookie);
    if (cookiePosition.w == 0.0)
        return 0.0;

    float2 cookieUv = cookiePosition.xy / cookiePosition.w;
    float2 atlasUv = cookieUv * light.CookieScaleOffset.xy + light.CookieScaleOffset.zw;
    float cookie = LightCookieAtlas.SampleLevel(
        LightCookieSampler,
        float3(atlasUv, (float)light.CookieIndex),
        0.0).r;
    return saturate(cookie * light.CookieStrength);
}

StandardPBRPixelContext BuildStandardPBRPixelContext(
    uint2 pixelCoord,
    uint instanceID,
    float3 bary,
    StaticVertexEval.DeformedVertex v0,
    StaticVertexEval.DeformedVertex v1,
    StaticVertexEval.DeformedVertex v2)
{
    StandardPBRPixelContext ctx;
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = instanceID;
    ctx.surfacePosition = bary.x * v0.position + bary.y * v1.position + bary.z * v2.position;
    ctx.worldNormal = normalize(bary.x * v0.worldNormal + bary.y * v1.worldNormal + bary.z * v2.worldNormal);
    float3 tangent = normalize(bary.x * v0.worldTangent.xyz + bary.y * v1.worldTangent.xyz + bary.z * v2.worldTangent.xyz);
    ctx.worldTangent = float4(tangent, v0.worldTangent.w);
    ctx.uv0 = bary.x * v0.uv0 + bary.y * v1.uv0 + bary.z * v2.uv0;
    return ctx;
}

struct StandardPBRMaterial : ISurfaceEvaluate<StaticVertexEval>
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
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
        StandardPBRPixelContext ctx = BuildStandardPBRPixelContext(
            pixelCoord,
            instanceID,
            bary,
            v0,
            v1,
            v2);

        // Sample material textures
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        float2 armSample    = ARMMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).gb;
        float3 normalTS     = NormalMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb * 2.0 - 1.0;
        StandardPBRScalars scalars = LoadMaterialScalars<StandardPBRScalars>();

        // TBN matrix: transform normal from tangent space to world space
        float3 N_geom = normalize(ctx.worldNormal);
        float3 T = normalize(ctx.worldTangent.xyz - N_geom * dot(N_geom, ctx.worldTangent.xyz)); // Gram-Schmidt
        float3 B = cross(N_geom, T) * ctx.worldTangent.w; // handedness
        float3 mappedNormal = normalize(T * normalTS.x + B * normalTS.y + N_geom * normalTS.z);

        float4 instanceTint = getInstancePropertyFloat4(ctx.instanceID, 0, float4(1.0));
        float4 tint = scalars.BaseColorTint * instanceTint;
        float3 baseColor = albedoSample.rgb * tint.rgb;
        float  roughness = clamp(armSample.x * scalars.Roughness, 0.0, 1.0);
        float  metallic  = clamp(armSample.y * scalars.MetallicFactor, 0.0, 1.0);

        float3 N = mappedNormal;
        float3 V = normalize(Uniforms.CameraPos - ctx.surfacePosition);

        // Clamp roughness to avoid division issues
        float r = clamp(roughness, 0.04, 1.0);

        float3 directColor = float3(0.0);
        uint directionalEnd = LightCounts.DirectionalCount;
        uint pointEnd = directionalEnd + LightCounts.PointCount;
        uint spotEnd = pointEnd + LightCounts.SpotCount;
        uint surfaceLightLayerMask = Uniforms.LightLayerMask != 0u ? Uniforms.LightLayerMask : DEFAULT_LIGHT_LAYER_MASK;

        for (uint lightIndex = 0u; lightIndex < directionalEnd; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            if ((light.LayerMask & surfaceLightLayerMask) == 0u)
                continue;

            float cookieAttenuation = SampleLightCookie(light, ctx.surfacePosition);
            float3 lightColor = light.Color * light.Intensity * cookieAttenuation;
            if (dot(light.Direction, light.Direction) > 0.0)
            {
                directColor += EvaluateDirectionalLight(
                    baseColor,
                    metallic,
                    r,
                    N,
                    V,
                    -normalize(light.Direction),
                    lightColor,
                    1.0);
            }
        }

        uint clusterIndex = GetClusterIndex(ctx.pixelCoord, ctx.surfacePosition);
        if (clusterIndex != LIGHT_GRID_INVALID_CLUSTER)
        {
            uint2 clusterLightSpan = ClusterLightGrid[clusterIndex];
            for (uint clusterLightIndex = 0u; clusterLightIndex < clusterLightSpan.y; clusterLightIndex++)
            {
                uint lightIndex = LightIndexList[clusterLightSpan.x + clusterLightIndex];
                if (lightIndex < directionalEnd || lightIndex >= spotEnd)
                    continue;

                GPULight light = LightBuffer[lightIndex];
                if ((light.LayerMask & surfaceLightLayerMask) == 0u)
                    continue;

                float cookieAttenuation = SampleLightCookie(light, ctx.surfacePosition);
                if (lightIndex < pointEnd)
                {
                    directColor += EvaluatePointLight(
                        baseColor,
                        metallic,
                        r,
                        N,
                        V,
                        ctx.surfacePosition,
                        light.Position,
                        light.Range,
                        light.Color * light.Intensity * cookieAttenuation,
                        1.0);
                }
                else
                {
                    if (dot(light.Direction, light.Direction) > 0.0)
                    {
                        directColor += EvaluateSpotLight(
                            baseColor,
                            metallic,
                            r,
                            N,
                            V,
                            ctx.surfacePosition,
                            light.Position,
                            light.Range,
                            light.Direction,
                            light.InnerConeCos,
                            light.OuterConeCos,
                            light.Color * light.Intensity * cookieAttenuation,
                            1.0);
                    }
                }
            }
        }

        float3 emissive = scalars.EmissiveFactor;
        float3 finalColor = directColor + emissive;

        outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
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

        StandardPBRPixelContext ctx = BuildStandardPBRPixelContext(
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
            float3 delta = abs(pos - basePos) * 3.0;
            outputColor[pixelCoord] = float4(delta, 1.0);
            return;
        }

        outputColor[pixelCoord] = float4(1.0, 0.0, 1.0, 1.0);
    }
};

#endif
