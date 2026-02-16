using NUnit.Framework;
using SomeEngine.Core.Math;
using System.Numerics;

namespace SomeEngine.Tests.ECS;

[TestFixture]
public class FreeCameraTests
{
    [Test]
    public void MoveLocal_Forward_UpdatesPositionAlongForward()
    {
        var camera = new FreeCamera(
            position: Vector3.Zero,
            yaw: MathF.PI * 0.5f,
            pitch: 0.0f,
            fovY: MathF.PI / 3.0f,
            nearPlane: 0.1f,
            farPlane: 1000.0f
        );

        camera.MoveLocal(new Vector3(0, 0, 2));

        Assert.That(camera.Position.X, Is.EqualTo(0).Within(1e-5f));
        Assert.That(camera.Position.Z, Is.EqualTo(2).Within(1e-5f));
    }

    [Test]
    public void AddYawPitch_ClampsPitch()
    {
        var camera = new FreeCamera(
            position: Vector3.Zero,
            yaw: 0.0f,
            pitch: 0.0f,
            fovY: MathF.PI / 4.0f,
            nearPlane: 0.1f,
            farPlane: 1000.0f
        );

        camera.AddYawPitch(0.0f, 10.0f);

        Assert.That(camera.Pitch, Is.LessThan(MathF.PI * 0.5f));
    }

    [Test]
    public void GetLodScale_MatchesProjectionFormula()
    {
        var camera = new FreeCamera(
            position: Vector3.Zero,
            yaw: 0.0f,
            pitch: 0.0f,
            fovY: MathF.PI / 4.0f,
            nearPlane: 0.1f,
            farPlane: 1000.0f
        );

        float lodScale = camera.GetLodScale(720.0f);
        float expected = 720.0f / (2.0f * MathF.Tan((MathF.PI / 4.0f) * 0.5f));

        Assert.That(lodScale, Is.EqualTo(expected).Within(1e-5f));
    }
}
