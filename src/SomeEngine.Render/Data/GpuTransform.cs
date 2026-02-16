using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Core.Math;

namespace SomeEngine.Render.Data;

[StructLayout(LayoutKind.Sequential)]
public struct GpuTransform
{
    public Vector4 Rotation; // 16 bytes
    public Vector3 Position; // 12 bytes
    public float Scale;      // 4 bytes
    public Vector3 Stretch;  // 12 bytes
    public float Padding;    // 4 bytes

    public const int SizeInBytes = 48;

    public static GpuTransform FromQvvs(in TransformQvvs qvvs)
    {
        return new GpuTransform
        {
            Rotation = new Vector4(qvvs.Rotation.X, qvvs.Rotation.Y, qvvs.Rotation.Z, qvvs.Rotation.W),
            Position = qvvs.Position,
            Scale = qvvs.Scale,
            Stretch = qvvs.Stretch,
            Padding = 0.0f
        };
    }
}
