using SomeEngine.Assets;
using SomeEngine.Render.Materials;
using System.Numerics;
using SomeECS.Core.Components;

namespace SomeEngine.Render.Components;

public struct DirectionalLight
{
    /// <summary>Direction the light travels, from the light toward the scene.</summary>
    public Vector3 Direction;
    public Vector3 Color;
    public float Intensity;
    public uint LayerMask;
    public int CookieIndex;
    public float CookieStrength;
    public Vector4 CookieScaleOffset;
    public Matrix4x4 WorldToLightCookie;

    public DirectionalLight(
        Vector3 direction,
        Vector3 color,
        float intensity,
        uint layerMask = SceneLights.DefaultLightLayerMask,
        int cookieIndex = SceneLights.NoCookie,
        float cookieStrength = 1.0f,
        Vector4 cookieScaleOffset = default,
        Matrix4x4 worldToLightCookie = default)
    {
        Direction = direction;
        Color = color;
        Intensity = intensity;
        LayerMask = layerMask;
        CookieIndex = cookieIndex;
        CookieStrength = cookieStrength;
        CookieScaleOffset = cookieScaleOffset;
        WorldToLightCookie = worldToLightCookie;
    }
}

public struct PointLight
{
    public Vector3 Position;
    public float Range;
    public Vector3 Color;
    public float Intensity;
    public uint LayerMask;
    public int CookieIndex;
    public float CookieStrength;
    public Vector4 CookieScaleOffset;
    public Matrix4x4 WorldToLightCookie;

    public PointLight(
        Vector3 position,
        float range,
        Vector3 color,
        float intensity,
        uint layerMask = SceneLights.DefaultLightLayerMask,
        int cookieIndex = SceneLights.NoCookie,
        float cookieStrength = 1.0f,
        Vector4 cookieScaleOffset = default,
        Matrix4x4 worldToLightCookie = default)
    {
        Position = position;
        Range = range;
        Color = color;
        Intensity = intensity;
        LayerMask = layerMask;
        CookieIndex = cookieIndex;
        CookieStrength = cookieStrength;
        CookieScaleOffset = cookieScaleOffset;
        WorldToLightCookie = worldToLightCookie;
    }
}

public struct SpotLight
{
    public Vector3 Position;
    public float Range;
    /// <summary>Direction the light travels, from the light toward the cone center.</summary>
    public Vector3 Direction;
    public float InnerConeCos;
    public Vector3 Color;
    public float Intensity;
    public float OuterConeCos;
    public uint LayerMask;
    public int CookieIndex;
    public float CookieStrength;
    public Vector4 CookieScaleOffset;
    public Matrix4x4 WorldToLightCookie;

    public SpotLight(
        Vector3 position,
        float range,
        Vector3 direction,
        float innerConeCos,
        float outerConeCos,
        Vector3 color,
        float intensity,
        uint layerMask = SceneLights.DefaultLightLayerMask,
        int cookieIndex = SceneLights.NoCookie,
        float cookieStrength = 1.0f,
        Vector4 cookieScaleOffset = default,
        Matrix4x4 worldToLightCookie = default)
    {
        Position = position;
        Range = range;
        Direction = direction;
        InnerConeCos = innerConeCos;
        OuterConeCos = outerConeCos;
        Color = color;
        Intensity = intensity;
        LayerMask = layerMask;
        CookieIndex = cookieIndex;
        CookieStrength = cookieStrength;
        CookieScaleOffset = cookieScaleOffset;
        WorldToLightCookie = worldToLightCookie;
    }
}

public struct SceneLights : IComponent
{
    public const uint DefaultLightLayerMask = 0xFFFFFFFFu;
    public const int NoCookie = -1;

    public ReadOnlyMemory<DirectionalLight> DirectionalLights;
    public ReadOnlyMemory<PointLight> PointLights;
    public ReadOnlyMemory<SpotLight> SpotLights;
    public Handle<Texture> LightCookieAtlas;

    public SceneLights(
        ReadOnlyMemory<DirectionalLight> directionalLights,
        ReadOnlyMemory<PointLight> pointLights,
        ReadOnlyMemory<SpotLight> spotLights,
        Handle<Texture> lightCookieAtlas = default)
    {
        DirectionalLights = directionalLights;
        PointLights = pointLights;
        SpotLights = spotLights;
        LightCookieAtlas = lightCookieAtlas;
    }

    public readonly bool IsEmpty =>
        DirectionalLights.IsEmpty
        && PointLights.IsEmpty
        && SpotLights.IsEmpty;
}
