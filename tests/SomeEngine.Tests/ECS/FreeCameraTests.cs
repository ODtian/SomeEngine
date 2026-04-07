using SomeEngine.Core.Math;
using System.Numerics;

namespace SomeEngine.Tests.ECS;

public class FreeCameraTests
{
    [Fact]
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

        Assert.InRange(camera.Position.X, 0 - 1e-5f, 0 + 1e-5f);
        Assert.InRange(camera.Position.Z, 2 - 1e-5f, 2 + 1e-5f);
    }

    [Fact]
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

        Assert.True(camera.Pitch < MathF.PI * 0.5f);
    }

    [Fact]
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

        Assert.InRange(lodScale, expected - 1e-5f, expected + 1e-5f);
    }
}
