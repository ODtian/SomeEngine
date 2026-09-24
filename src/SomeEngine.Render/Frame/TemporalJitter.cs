using System.Numerics;

namespace SomeEngine.Render.Frame;

public static class TemporalJitter
{
    public const int DefaultSampleCount = 8;

    // Centered Halton(2,3) sequence for frames 1..8.
    private static readonly Vector2[] SamplePattern =
    [
        new(0.0f, -1.0f / 6.0f),
        new(-0.25f, 1.0f / 6.0f),
        new(0.25f, -7.0f / 18.0f),
        new(-3.0f / 8.0f, -1.0f / 18.0f),
        new(1.0f / 8.0f, 5.0f / 18.0f),
        new(-1.0f / 8.0f, -5.0f / 18.0f),
        new(3.0f / 8.0f, 1.0f / 18.0f),
        new(-7.0f / 16.0f, 7.0f / 18.0f),
    ];

    public static Vector2 SamplePixels(uint frameIndex)
        => SamplePattern[(int)(frameIndex % DefaultSampleCount)];

    private static Vector2 ToNdc(Vector2 pixelOffset, uint width, uint height)
    {
        float safeWidth = Math.Max(width, 1u);
        float safeHeight = Math.Max(height, 1u);
        return new Vector2(
            pixelOffset.X * 2.0f / safeWidth,
            -pixelOffset.Y * 2.0f / safeHeight);
    }

    public static Matrix4x4 ApplyToProjection(Matrix4x4 projection, Vector2 pixelOffset, uint width, uint height)
        => ApplyNdc(projection, ToNdc(pixelOffset, width, height));

    private static Matrix4x4 ApplyNdc(Matrix4x4 projection, Vector2 jitterNdc)
    {
        projection.M31 += jitterNdc.X * projection.M34;
        projection.M32 += jitterNdc.Y * projection.M34;
        return projection;
    }
}
