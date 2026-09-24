using System.Numerics;
using SomeEngine.Render.Frame;

namespace SomeEngine.Tests;

public class TemporalJitterTests
{
    [Fact]
    public void SamplePixelsUsesDeterministicCenteredHaltonPattern()
    {
        Vector2 first = TemporalJitter.SamplePixels(0);
        Vector2 second = TemporalJitter.SamplePixels(1);
        Vector2 wrapped = TemporalJitter.SamplePixels((uint)TemporalJitter.DefaultSampleCount);

        Assert.InRange(first.X, -1e-6f, 1e-6f);
        Assert.InRange(first.Y, -1.0f / 6.0f - 1e-6f, -1.0f / 6.0f + 1e-6f);
        Assert.InRange(second.X, -0.25f - 1e-6f, -0.25f + 1e-6f);
        Assert.InRange(second.Y, 1.0f / 6.0f - 1e-6f, 1.0f / 6.0f + 1e-6f);
        Assert.Equal(first, wrapped);
    }

    [Fact]
    public void SamplePixelsStaysInsideHalfPixelBounds()
    {
        for (uint i = 0; i < 32; i++)
        {
            Vector2 sample = TemporalJitter.SamplePixels(i);
            Assert.InRange(sample.X, -0.5f, 0.5f);
            Assert.InRange(sample.Y, -0.5f, 0.5f);
        }
    }

    [Fact]
    public void ApplyToProjectionConvertsPixelOffsetToClipOffset()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4.0f,
            16.0f / 9.0f,
            0.1f,
            1000.0f);
        Vector2 pixelOffset = new(0.5f, -0.25f);
        Vector2 expectedNdc = new(0.01f, 0.01f);
        Matrix4x4 jittered = TemporalJitter.ApplyToProjection(projection, pixelOffset, 100, 50);

        Vector4 viewPosition = new(0.25f, -0.5f, -5.0f, 1.0f);
        Vector4 clip = Vector4.Transform(viewPosition, projection);
        Vector4 jitteredClip = Vector4.Transform(viewPosition, jittered);

        Assert.InRange((jitteredClip.X - clip.X) / clip.W, expectedNdc.X - 1e-6f, expectedNdc.X + 1e-6f);
        Assert.InRange((jitteredClip.Y - clip.Y) / clip.W, expectedNdc.Y - 1e-6f, expectedNdc.Y + 1e-6f);
    }
}
