#include "fullscreen_triangle.slang"

Texture2D<float4> SceneColor;
Texture2D<float4> PreviousSceneColor;
Texture2D<float2> MotionVectors;
Texture2D<float2> PreviousMotionVectors;
Texture2D<float> SceneDepth;
Texture2D<float> PreviousSceneDepth;

struct TemporalResolveUniformData
{
    float HistoryWeight;
    float NeighborhoodClampScale;
    float NeighborhoodClampMin;
    float MotionRejectionScale;
};

ConstantBuffer<TemporalResolveUniformData> TemporalResolveUniforms;

float3 LoadCurrentClamped(int2 pixel, int2 maxPixel)
{
    return SceneColor.Load(int3(clamp(pixel, int2(0, 0), maxPixel), 0)).rgb;
}

float3 RgbToYCoCg(float3 color)
{
    return float3(
        dot(color, float3(0.25, 0.5, 0.25)),
        color.r - color.b,
        color.g - 0.5 * (color.r + color.b));
}

float3 YCoCgToRgb(float3 color)
{
    float rbAverage = color.x - color.z * 0.5;
    return float3(
        rbAverage + color.y * 0.5,
        color.x + color.z * 0.5,
        rbAverage - color.y * 0.5);
}

float LoadDepthClamped(Texture2D<float> depthTexture, int2 pixel, int2 maxPixel)
{
    return depthTexture.Load(int3(clamp(pixel, int2(0, 0), maxPixel), 0));
}

float Max3(float3 value)
{
    return max(value.x, max(value.y, value.z));
}

float3 ClipAabb(float3 aabbMin, float3 aabbMax, float3 value)
{
    float3 center = 0.5 * (aabbMin + aabbMax);
    float3 extents = max(0.5 * (aabbMax - aabbMin), float3(1e-5, 1e-5, 1e-5));
    float3 offset = value - center;
    float3 unit = abs(offset / extents);
    float maxUnit = Max3(unit);
    return maxUnit > 1.0 ? center + offset / maxUnit : value;
}

void ComputeCurrentNeighborhoodYCoCg(
    int2 pixel,
    int2 maxPixel,
    out float3 minColor,
    out float3 maxColor,
    out float3 meanColor,
    out float3 sigmaColor)
{
    minColor = float3(1e20, 1e20, 1e20);
    maxColor = float3(-1e20, -1e20, -1e20);
    float3 m1 = float3(0.0, 0.0, 0.0);
    float3 m2 = float3(0.0, 0.0, 0.0);

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float3 color = RgbToYCoCg(LoadCurrentClamped(pixel + int2(x, y), maxPixel));
            minColor = min(minColor, color);
            maxColor = max(maxColor, color);
            m1 += color;
            m2 += color * color;
        }
    }

    meanColor = m1 / 9.0;
    sigmaColor = sqrt(max(m2 / 9.0 - meanColor * meanColor, float3(0.0, 0.0, 0.0)));
}

float3 ClipHistoryToCurrentNeighborhood(int2 pixel, int2 maxPixel, float3 currentColor, float3 previousColor)
{
    float3 minColor;
    float3 maxColor;
    float3 meanColor;
    float3 sigmaColor;
    ComputeCurrentNeighborhoodYCoCg(pixel, maxPixel, minColor, maxColor, meanColor, sigmaColor);

    float3 currentYCoCg = RgbToYCoCg(currentColor);
    float3 previousYCoCg = RgbToYCoCg(previousColor);
    float3 radius = max(abs(currentYCoCg) * TemporalResolveUniforms.NeighborhoodClampScale, TemporalResolveUniforms.NeighborhoodClampMin.xxx);
    float3 varianceMin = meanColor - sigmaColor * 1.25;
    float3 varianceMax = meanColor + sigmaColor * 1.25;
    float3 strictMin = max(minColor, varianceMin);
    float3 strictMax = min(maxColor, varianceMax);
    float3 aabbMin = min(strictMin, strictMax) - radius;
    float3 aabbMax = max(strictMin, strictMax) + radius;
    float3 clippedYCoCg = ClipAabb(aabbMin, aabbMax, previousYCoCg);
    return YCoCgToRgb(clippedYCoCg);
}

float MotionRejection(float2 motion, float2 size)
{
    float motionPixels = length(motion * size);
    return saturate((motionPixels - 0.25) * TemporalResolveUniforms.MotionRejectionScale);
}

float HistoryColorRejection(float3 currentColor, float3 previousColor, float3 boundedPreviousColor)
{
    float3 currentYCoCg = RgbToYCoCg(currentColor);
    float3 previousYCoCg = RgbToYCoCg(previousColor);
    float3 boundedYCoCg = RgbToYCoCg(boundedPreviousColor);
    float currentMagnitude = max(abs(currentYCoCg.x), 1e-3);
    float tolerance = max(
        currentMagnitude * TemporalResolveUniforms.NeighborhoodClampScale,
        TemporalResolveUniforms.NeighborhoodClampMin) * 2.0;

    float clampDelta = Max3(abs(previousYCoCg - boundedYCoCg));
    float currentDelta = abs(currentYCoCg.x - boundedYCoCg.x);
    float clampRejection = saturate((clampDelta - tolerance) / max(tolerance, 1e-4));
    float colorRejection = saturate((currentDelta - tolerance * 2.0) / max(tolerance * 4.0, 1e-4));

    return max(clampRejection, colorRejection * 0.75);
}

float ResolveHistoryWeight(
    float baseWeight,
    float2 motion,
    float2 size,
    float3 currentColor,
    float3 previousColor,
    float3 boundedPreviousColor)
{
    float rejection = max(
        MotionRejection(motion, size),
        HistoryColorRejection(currentColor, previousColor, boundedPreviousColor) * 0.20);
    return saturate(baseWeight) * (1.0 - rejection);
}

float4 LoadPreviousSceneColorBilinear(float2 uv, float2 size, int2 maxPixel)
{
    float2 pixel = uv * size - 0.5;
    int2 p0 = int2(floor(pixel));
    int2 p1 = p0 + int2(1, 1);
    float2 f = frac(pixel);

    float4 c00 = PreviousSceneColor.Load(int3(clamp(p0, int2(0, 0), maxPixel), 0));
    float4 c10 = PreviousSceneColor.Load(int3(clamp(int2(p1.x, p0.y), int2(0, 0), maxPixel), 0));
    float4 c01 = PreviousSceneColor.Load(int3(clamp(int2(p0.x, p1.y), int2(0, 0), maxPixel), 0));
    float4 c11 = PreviousSceneColor.Load(int3(clamp(p1, int2(0, 0), maxPixel), 0));

    return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
}

float LoadPreviousDepthBilinear(float2 uv, float2 size, int2 maxPixel)
{
    float2 pixel = uv * size - 0.5;
    int2 p0 = int2(floor(pixel));
    int2 p1 = p0 + int2(1, 1);
    float2 f = frac(pixel);

    float d00 = LoadDepthClamped(PreviousSceneDepth, p0, maxPixel);
    float d10 = LoadDepthClamped(PreviousSceneDepth, int2(p1.x, p0.y), maxPixel);
    float d01 = LoadDepthClamped(PreviousSceneDepth, int2(p0.x, p1.y), maxPixel);
    float d11 = LoadDepthClamped(PreviousSceneDepth, p1, maxPixel);

    return lerp(lerp(d00, d10, f.x), lerp(d01, d11, f.x), f.y);
}

float2 LoadPreviousMotionBilinear(float2 uv, float2 size, int2 maxPixel)
{
    float2 pixel = uv * size - 0.5;
    int2 p0 = int2(floor(pixel));
    int2 p1 = p0 + int2(1, 1);
    float2 f = frac(pixel);

    float2 m00 = PreviousMotionVectors.Load(int3(clamp(p0, int2(0, 0), maxPixel), 0));
    float2 m10 = PreviousMotionVectors.Load(int3(clamp(int2(p1.x, p0.y), int2(0, 0), maxPixel), 0));
    float2 m01 = PreviousMotionVectors.Load(int3(clamp(int2(p0.x, p1.y), int2(0, 0), maxPixel), 0));
    float2 m11 = PreviousMotionVectors.Load(int3(clamp(p1, int2(0, 0), maxPixel), 0));

    return lerp(lerp(m00, m10, f.x), lerp(m01, m11, f.x), f.y);
}

float2 LoadDilatedMotionByClosestDepth(int2 pixel, int2 maxPixel)
{
    float closestDepth = LoadDepthClamped(SceneDepth, pixel, maxPixel);
    float2 closestMotion = MotionVectors.Load(int3(clamp(pixel, int2(0, 0), maxPixel), 0));

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 samplePixel = pixel + int2(x, y);
            float sampleDepth = LoadDepthClamped(SceneDepth, samplePixel, maxPixel);
            if (sampleDepth < closestDepth)
            {
                closestDepth = sampleDepth;
                closestMotion = MotionVectors.Load(int3(clamp(samplePixel, int2(0, 0), maxPixel), 0));
            }
        }
    }

    return closestMotion;
}

float VelocityDisocclusionRejection(float2 previousUv, float2 motion, float2 size, int2 maxPixel)
{
    float2 previousMotion = LoadPreviousMotionBilinear(previousUv, size, maxPixel);
    float velocityDeltaPixels = length((previousMotion - motion) * size);
    return saturate((velocityDeltaPixels - 0.25) * 0.08);
}

float DepthDisocclusionRejection(float currentDepth, float previousDepth)
{
    float depthThreshold = max(0.0005, previousDepth * 0.002);
    float disocclusion = currentDepth - previousDepth;
    return saturate((disocclusion - depthThreshold) * 96.0);
}

[shader("pixel")]
float4 PSMain(FullscreenVSOut input) : SV_Target
{
    int2 pixel = int2(input.Position.xy);
    float4 current = SceneColor.Load(int3(pixel, 0));

    uint width;
    uint height;
    SceneColor.GetDimensions(width, height);
    width = max(width, 1u);
    height = max(height, 1u);
    int2 maxPixel = int2((int)width - 1, (int)height - 1);
    float2 size = float2((float)width, (float)height);
    float2 uv = (float2(pixel) + 0.5) / size;

    // MotionVectors store current output UV minus previous history UV.
    // Jitter is applied by the projection that produced SceneColor; history reprojection
    // is owned by motion vectors and must not apply current jitter again.
    // No-history frames bypass this shader and copy current SceneColor.
    float2 motion = LoadDilatedMotionByClosestDepth(pixel, maxPixel);
    float2 previousUv = uv - motion;
    if (any(previousUv < float2(0.0, 0.0)) || any(previousUv > float2(1.0, 1.0)))
        return current;

    float4 previous = LoadPreviousSceneColorBilinear(previousUv, size, maxPixel);
    float3 boundedPrevious = ClipHistoryToCurrentNeighborhood(pixel, maxPixel, current.rgb, previous.rgb);
    float currentDepth = LoadDepthClamped(SceneDepth, pixel, maxPixel);
    float previousDepth = LoadPreviousDepthBilinear(previousUv, size, maxPixel);
    float depthRejection = DepthDisocclusionRejection(currentDepth, previousDepth);
    float velocityRejection = VelocityDisocclusionRejection(previousUv, motion, size, maxPixel);
    float weight = ResolveHistoryWeight(
        TemporalResolveUniforms.HistoryWeight,
        motion,
        size,
        current.rgb,
        previous.rgb,
        boundedPrevious);
    weight *= 1.0 - max(depthRejection, velocityRejection);
    float3 resolved = lerp(current.rgb, boundedPrevious, weight);

    return float4(resolved, current.a);
}
