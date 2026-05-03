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

StandardPBRScalars GetDefaultStandardPBRScalars()
{
    StandardPBRScalars scalars;
    scalars.BaseColorTint = float4(1.0);
    scalars.MetallicFactor = 1.0;
    scalars.Roughness = 1.0;
    scalars.EmissiveFactor = float3(0.0);
    return scalars;
}

/// Standard PBR Material (MVP).
/// Implements ISurfaceEvaluate using Cook-Torrance BRDF.
/// Lighting is read from the global ShadeUniforms constant buffer.
struct StandardPBRMaterial : ISurfaceEvaluate
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    void evaluateSurface(PixelContext ctx, RWTexture2D<float4> outputColor)
    {
        // Sample material textures
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        float3 armSample    = ARMMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb;
        float3 normalTS     = NormalMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb * 2.0 - 1.0;
        StandardPBRScalars scalars = LoadMaterialScalars<StandardPBRScalars>(
            GetDefaultStandardPBRScalars());

        // TBN matrix: transform normal from tangent space to world space
        float3 N_geom = normalize(ctx.worldNormal);
        float3 T = normalize(ctx.worldTangent.xyz - N_geom * dot(N_geom, ctx.worldTangent.xyz)); // Gram-Schmidt
        float3 B = cross(N_geom, T) * ctx.worldTangent.w; // handedness
        float3 mappedNormal = normalize(T * normalTS.x + B * normalTS.y + N_geom * normalTS.z);

        float4 instanceTint = getInstancePropertyFloat4(ctx.instanceID, 0, float4(1.0));
        float4 tint = scalars.BaseColorTint * instanceTint;
        float3 baseColor = albedoSample.rgb * tint.rgb;
        float  ao        = armSample.r;
        float  roughness = clamp(armSample.g * scalars.Roughness, 0.0, 1.0);
        float  metallic  = clamp(armSample.b * scalars.MetallicFactor, 0.0, 1.0);

        float3 N = mappedNormal;
        float3 V = normalize(Uniforms.CameraPos - ctx.worldPos);

        // Clamp roughness to avoid division issues
        float r = clamp(roughness, 0.04, 1.0);

        // Direct lighting (single directional light)
        float3 directColor = EvaluateDirectionalLight(
            baseColor,
            metallic,
            r,
            N,
            V,
            Uniforms.LightDir,
            float3(1.0) * Uniforms.LightIntensity,
            1.0     // no shadow
        );

        // Hemisphere ambient (ground + sky)
        float3 skyColor    = Uniforms.AmbientColor;
        float3 groundColor = Uniforms.AmbientColor * 0.3;
        float  skyBlend    = N.y * 0.5 + 0.5;
        float3 ambient     = lerp(groundColor, skyColor, skyBlend) * baseColor * ao;

        float3 emissive = scalars.EmissiveFactor;
        float3 finalColor = directColor + ambient + emissive;

        outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
    }
};

#endif
