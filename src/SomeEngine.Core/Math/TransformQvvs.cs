using System.Numerics;
using Friflo.Engine.ECS;

namespace SomeEngine.Core.Math;

/// <summary>
/// QVVS Transform: Quaternion, Vector (Position), Vector (Stretch), Scale
/// Based on Latios Framework concepts.
/// </summary>
public struct TransformQvvs(Vector3 position, Quaternion rotation, float scale = 1.0f) : IComponent
{
    public Quaternion Rotation = rotation;
    public Vector3 Position = position;
    public Vector3 Stretch = Vector3.One;
    public float Scale = scale;

    public static readonly TransformQvvs Identity = new()
    {
        Rotation = Quaternion.Identity,
        Position = Vector3.Zero,
        Stretch = Vector3.One,
        Scale = 1.0f,
    };

    public readonly Matrix4x4 ToMatrix()
    {
        return Matrix4x4.CreateScale(Stretch * Scale)
            * Matrix4x4.CreateFromQuaternion(Rotation)
            * Matrix4x4.CreateTranslation(Position);
    }

    public static TransformQvvs Combine(in TransformQvvs parent, in TransformQvvs local)
    {
        var scaledLocalPos = local.Position * (parent.Stretch * parent.Scale);
        var rotatedLocalPos = Vector3.Transform(scaledLocalPos, parent.Rotation);

        return new TransformQvvs
        {
            Rotation = parent.Rotation * local.Rotation,
            Scale = parent.Scale * local.Scale,
            Stretch = parent.Stretch * local.Stretch,
            Position = parent.Position + rotatedLocalPos,
        };
    }

    /// <summary>
    /// Transforms a point from local space to world space.
    /// </summary>
    public readonly Vector3 TransformPoint(Vector3 point)
    {
        // p' = p + q * (s * v * x)
        var scaled = point * (Stretch * Scale);
        var rotated = Vector3.Transform(scaled, Rotation);
        return Position + rotated;
    }

    /// <summary>
    /// Transforms a direction vector (ignores translation).
    /// </summary>
    public readonly Vector3 TransformDirection(Vector3 direction)
    {
        var scaled = direction * (Stretch * Scale);
        return Vector3.Transform(scaled, Rotation);
    }

    /// <summary>
    /// Returns the inverse of this transform.
    /// </summary>
    public TransformQvvs Inverse()
    {
        // Inverse operation needs to reverse the order:
        // World = Parent * Local
        // Local = Parent^-1 * World

        // Scale/Stretch inversion
        // Safety: Avoid divide by zero
        float invScale = Scale != 0.0f ? 1.0f / Scale : 1.0f;
        Vector3 invStretch = new(
            Stretch.X != 0.0f ? 1.0f / Stretch.X : 1.0f,
            Stretch.Y != 0.0f ? 1.0f / Stretch.Y : 1.0f,
            Stretch.Z != 0.0f ? 1.0f / Stretch.Z : 1.0f
        );

        Quaternion invRotation = Quaternion.Inverse(Rotation);

        // Position:
        // P_world = P_parent + R_parent * (S_parent * V_parent * P_local)
        // P_local = (S_parent * V_parent)^-1 * R_parent^-1 * (P_world - P_parent)

        Vector3 relPos = -Position; // (0 - P_parent) if we are inverting relative to origin
        // Actually, Inverse() is the transform that maps World -> Local.
        // T(x) = P + R(S*V*x)
        // y = P + R(S*V*x)
        // y - P = R(S*V*x)
        // R^-1(y - P) = S*V*x
        // (S*V)^-1 * R^-1(y - P) = x

        // So new Translation T' = (S*V)^-1 * R^-1 * (-P)
        // New Rotation R' = R^-1
        // New Scale S' = 1/S
        // New Stretch V' = 1/V

        Vector3 unrotated = Vector3.Transform(relPos, invRotation);
        Vector3 invPos = unrotated * (invStretch * invScale);

        return new TransformQvvs
        {
            Position = invPos,
            Rotation = invRotation,
            Scale = invScale,
            Stretch = invStretch,
        };
    }
}
