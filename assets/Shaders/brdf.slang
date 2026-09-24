#ifndef BRDF_SLANG
#define BRDF_SLANG

// ── Cook-Torrance Microfacet BRDF ──
// Standard PBR direct lighting model:
//   f(l,v) = D(h) * G(l,v) * F(v,h) / (4 * NdotL * NdotV) + (1-F) * diffuse/PI

static const float PI = 3.14159265358979323846;
static const float MIN_N_DOT_V = 0.05;
static const float MIN_DISTANCE_SQ = 0.0001;

/// GGX / Trowbridge-Reitz Normal Distribution Function
float D_GGX(float NdotH, float roughness)
{
    float a  = roughness * roughness;
    float a2 = a * a;
    float d  = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

/// Smith-Schlick Geometry Function (single direction)
float G_SchlickGGX(float NdotX, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;    // direct lighting remapping
    return NdotX / (NdotX * (1.0 - k) + k);
}

/// Smith Geometry Function (combined view + light)
float G_Smith(float NdotV, float NdotL, float roughness)
{
    return G_SchlickGGX(NdotV, roughness) * G_SchlickGGX(NdotL, roughness);
}

/// Schlick Fresnel Approximation
float3 F_Schlick(float cosTheta, float3 F0)
{
    float t = 1.0 - cosTheta;
    float t2 = t * t;
    return F0 + (1.0 - F0) * (t2 * t2 * t);   // pow(1-cos, 5)
}

/// Evaluate Cook-Torrance BRDF for a single directional light.
/// Returns final lit color (diffuse + specular).
float3 EvaluateDirectionalLight(
    float3 baseColor,
    float  metallic,
    float  roughness,
    float3 N,           // world normal (normalized)
    float3 V,           // view direction (normalized, toward camera)
    float3 L,           // light direction (normalized, toward light)
    float3 lightColor,  // light radiance (color * intensity)
    float  shadow       // 1.0 = fully lit, 0.0 = fully shadowed
)
{
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0)
        return float3(0.0);

    float rawNdotV = dot(N, V);
    if (rawNdotV <= 0.0)
        return float3(0.0);

    float NdotV = max(rawNdotV, MIN_N_DOT_V);
    float3 H    = normalize(V + L);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);

    // Fresnel reflectance at normal incidence
    // Dielectric: F0 = 0.04, Metal: F0 = baseColor
    float3 F0 = lerp(float3(0.04), baseColor, metallic);

    // Cook-Torrance specular BRDF
    float  D = D_GGX(NdotH, roughness);
    float  G = G_Smith(NdotV, NdotL, roughness);
    float3 F = F_Schlick(VdotH, F0);

    float3 numerator   = D * G * F;
    float  denominator = 4.0 * NdotV * NdotL + 0.0001;
    float3 specular    = numerator / denominator;

    // Energy conservation: diffuse uses remaining energy
    float3 kD = (1.0 - F) * (1.0 - metallic);
    float3 diffuse = kD * baseColor / PI;

    return (diffuse + specular) * lightColor * NdotL * shadow;
}

float EvaluateDistanceAttenuation(float distanceSq, float range)
{
    if (range <= 0.0)
        return 0.0;

    float rangeSq = range * range;
    if (distanceSq >= rangeSq)
        return 0.0;

    return 1.0 / max(distanceSq, MIN_DISTANCE_SQ);
}

/// Evaluate Cook-Torrance BRDF for a single point light.
/// lightColor is unattenuated radiance color * intensity.
float3 EvaluatePointLight(
    float3 baseColor,
    float  metallic,
    float  roughness,
    float3 N,
    float3 V,
    float3 surfacePosition,
    float3 lightPosition,
    float  range,
    float3 lightColor,
    float  shadow
)
{
    float3 toLight = lightPosition - surfacePosition;
    float distanceSq = dot(toLight, toLight);
    float attenuation = EvaluateDistanceAttenuation(distanceSq, range);
    if (attenuation <= 0.0)
        return float3(0.0);

    float3 L = toLight * rsqrt(max(distanceSq, MIN_DISTANCE_SQ));
    return EvaluateDirectionalLight(
        baseColor,
        metallic,
        roughness,
        N,
        V,
        L,
        lightColor * attenuation,
        shadow);
}

float EvaluateSpotCone(float3 L, float3 spotDirection, float innerConeCos, float outerConeCos)
{
    float3 lightToSurface = -L;
    float cosAngle = dot(normalize(spotDirection), lightToSurface);
    if (cosAngle <= outerConeCos)
        return 0.0;
    if (cosAngle >= innerConeCos || innerConeCos <= outerConeCos)
        return 1.0;

    return saturate((cosAngle - outerConeCos) / (innerConeCos - outerConeCos));
}

/// Evaluate Cook-Torrance BRDF for a single spot light.
/// spotDirection points from the light toward the center of the cone.
float3 EvaluateSpotLight(
    float3 baseColor,
    float  metallic,
    float  roughness,
    float3 N,
    float3 V,
    float3 surfacePosition,
    float3 lightPosition,
    float  range,
    float3 spotDirection,
    float  innerConeCos,
    float  outerConeCos,
    float3 lightColor,
    float  shadow
)
{
    float3 toLight = lightPosition - surfacePosition;
    float distanceSq = dot(toLight, toLight);
    float attenuation = EvaluateDistanceAttenuation(distanceSq, range);
    if (attenuation <= 0.0)
        return float3(0.0);

    float3 L = toLight * rsqrt(max(distanceSq, MIN_DISTANCE_SQ));
    float cone = EvaluateSpotCone(L, spotDirection, innerConeCos, outerConeCos);
    if (cone <= 0.0)
        return float3(0.0);

    return EvaluateDirectionalLight(
        baseColor,
        metallic,
        roughness,
        N,
        V,
        L,
        lightColor * attenuation * cone,
        shadow);
}

#endif
