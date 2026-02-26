using System.Numerics;

namespace SomeEngine.Core.Math;

public sealed class FreeCamera(
    Vector3 position,
    float yaw,
    float pitch,
    float fovY,
    float nearPlane,
    float farPlane
)
{
    private const float MaxPitch = 1.553343f;

    public Vector3 Position { get; set; } = position;
    public float Yaw { get; private set; } = yaw;
    public float Pitch { get; private set; } = ClampPitch(pitch);
    public float FovY { get; set; } = fovY;
    public float NearPlane { get; set; } = nearPlane;
    public float FarPlane { get; set; } = farPlane;
    public Vector3 WorldUp { get; set; } = Vector3.UnitY;

    public Vector3 Forward
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            Vector3 forward = new Vector3(
                cosPitch * MathF.Cos(Yaw),
                MathF.Sin(Pitch),
                cosPitch * MathF.Sin(Yaw)
            );
            return Vector3.Normalize(forward);
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, WorldUp));

    public void AddYawPitch(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = ClampPitch(Pitch + deltaPitch);
    }

    public void MoveLocal(Vector3 localDelta)
    {
        Position += Right * localDelta.X;
        Position += WorldUp * localDelta.Y;
        Position += Forward * localDelta.Z;
    }

    public Matrix4x4 GetViewMatrix()
    {
        return Matrix4x4.CreateLookAt(Position, Position + Forward, WorldUp);
    }

    public Matrix4x4 GetProjectionMatrix(float aspect)
    {
        return Matrix4x4.CreatePerspectiveFieldOfView(FovY, aspect, NearPlane, FarPlane);
    }

    public float GetLodScale(float viewportHeight)
    {
        return viewportHeight / (2.0f * MathF.Tan(FovY * 0.5f));
    }

    private static float ClampPitch(float pitch)
    {
        if (pitch > MaxPitch)
            return MaxPitch;
        if (pitch < -MaxPitch)
            return -MaxPitch;
        return pitch;
    }
}
