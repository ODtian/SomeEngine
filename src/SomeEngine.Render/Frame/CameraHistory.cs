using System.Numerics;

namespace SomeEngine.Render.Frame;

public sealed class CameraHistory
{
    private Matrix4x4 _prevViewProjT = Matrix4x4.Identity;
    private Matrix4x4 _prevMotionViewProj = Matrix4x4.Identity;
    private Matrix4x4 _prevView = Matrix4x4.Identity;
    private Matrix4x4 _prevProj = Matrix4x4.Identity;

    public Matrix4x4 PrevViewProjT => _prevViewProjT;
    public Matrix4x4 PrevViewProj => Matrix4x4.Transpose(_prevViewProjT);
    public Matrix4x4 PrevMotionViewProj => _prevMotionViewProj;
    public Matrix4x4 PrevView => _prevView;
    public Matrix4x4 PrevProj => _prevProj;
    public bool HasPrevious { get; private set; }

    public void Commit(
        in Matrix4x4 view,
        in Matrix4x4 proj,
        in Matrix4x4 motionViewProj)
    {
        _prevViewProjT = Matrix4x4.Transpose(view * proj);
        _prevMotionViewProj = motionViewProj;
        _prevView = view;
        _prevProj = proj;
        HasPrevious = true;
    }

    public void Reset()
    {
        _prevViewProjT = Matrix4x4.Identity;
        _prevMotionViewProj = Matrix4x4.Identity;
        _prevView = Matrix4x4.Identity;
        _prevProj = Matrix4x4.Identity;
        HasPrevious = false;
    }
}
