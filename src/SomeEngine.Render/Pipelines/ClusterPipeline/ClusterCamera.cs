using System.Numerics;
using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Pipelines;

internal readonly record struct ClusterCameraData
{
    public Matrix4x4 View { get; init; }
    public Matrix4x4 Proj { get; init; }
    public Matrix4x4 MotionViewProj { get; init; }
    public Vector3 CameraPos { get; init; }
    public float LodThreshold { get; init; }
    public float LodScale { get; init; }
    public int ForcedLODLevel { get; init; }
    public uint ScreenWidth { get; init; }
    public uint ScreenHeight { get; init; }
    public Matrix4x4 PrevViewProj { get; init; }
    public Matrix4x4 PrevMotionViewProj { get; init; }
    public Matrix4x4 PrevView { get; init; }
    public Matrix4x4 PrevProj { get; init; }

    public static ClusterCameraData Default(
        Matrix4x4 view,
        Matrix4x4 proj,
        Vector3 cameraPos,
        uint screenWidth,
        uint screenHeight)
        => new()
        {
            View = view,
            Proj = proj,
            MotionViewProj = view * proj,
            CameraPos = cameraPos,
            LodThreshold = 1.0f,
            LodScale = 500.0f,
            ForcedLODLevel = -1,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            PrevViewProj = Matrix4x4.Identity,
            PrevMotionViewProj = Matrix4x4.Identity,
            PrevView = Matrix4x4.Identity,
            PrevProj = Matrix4x4.Identity,
        };
}

internal sealed class ClusterCamera
{
    private ClusterCameraData _current;
    private ClusterCameraData _frozen;
    private bool _frozenSet;

    public bool Frozen => _frozenSet;
    public ClusterCameraData Active => _frozenSet ? _frozen : _current;

    public void Set(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        in Matrix4x4 motionViewProj,
        Vector3 cameraPos,
        float lodThreshold,
        float lodScale,
        int forcedLODLevel,
        uint screenWidth,
        uint screenHeight,
        CameraHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        _current = ClusterCameraData.Default(view, proj, cameraPos, screenWidth, screenHeight) with
        {
            LodThreshold = lodThreshold,
            LodScale = lodScale,
            ForcedLODLevel = forcedLODLevel,
            MotionViewProj = motionViewProj,
            PrevViewProj = history.PrevViewProj,
            PrevMotionViewProj = history.PrevMotionViewProj,
            PrevView = history.PrevView,
            PrevProj = history.PrevProj,
        };
    }

    public void Freeze(bool value)
    {
        if (value && !_frozenSet)
            _frozen = _current;

        _frozenSet = value;
    }
}
