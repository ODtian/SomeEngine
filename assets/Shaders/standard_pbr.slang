#ifndef STANDARD_PBR_SLANG
#define STANDARD_PBR_SLANG

#include "material_interfaces.slang"
#include "brdf.slang"

// ── Shader-global lighting uniforms ──
// These will move to a LightingProvider interface in future iterations.
// For now, the material reads them directly from the global uniform buffer.

struct DirectionalLightParams
{
    float3 lightDir;
    float  lightIntensity;
    float3 ambientColor;
    float3 cameraPos;
};

/// Standard PBR Material (MVP — fixed parameters, no textures).
/// Implements ISurfaceEvaluate using Cook-Torrance BRDF.
struct StandardPBRMaterial : ISurfaceEvaluate
{
    // Fixed PBR parameters (will be replaced by texture sampling in future)
    float3 baseColor;
    float  metallic;
    float  roughness;
    float  ao;

    // Lighting (injected from uniforms by the pipeline)
    DirectionalLightParams light;

    void evaluateSurface(PixelContext ctx, RWTexture2D<float4> outputColor)
    {
        float3 N = ctx.worldNormal;
        float3 V = normalize(light.cameraPos - ctx.worldPos);

        // Clamp roughness to avoid division issues
        float r = clamp(roughness, 0.04, 1.0);

        // Direct lighting (single directional light)
        float3 directColor = EvaluateDirectionalLight(
            baseColor,
            metallic,
            r,
            N,
            V,
            light.lightDir,
            float3(1.0) * light.lightIntensity,
            1.0     // no shadow
        );

        // Hemisphere ambient (ground + sky)
        float3 skyColor    = light.ambientColor;
        float3 groundColor = light.ambientColor * 0.3;
        float  skyBlend    = N.y * 0.5 + 0.5;
        float3 ambient     = lerp(groundColor, skyColor, skyBlend) * baseColor * ao;

        float3 finalColor = directColor + ambient;

        outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
    }
};

#endif
