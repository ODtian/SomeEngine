using System.Numerics;
using SomeEngine.Render.Frame;

namespace SomeEngine.Tests;

public sealed class CameraHistoryTests
{
    [Fact]
    public void CommitStores()
    {
        var history = new CameraHistory();
        Matrix4x4 view = Matrix4x4.CreateTranslation(1, 2, 3);
        Matrix4x4 proj = Matrix4x4.CreateScale(2, 3, 4);
        Matrix4x4 motion = Matrix4x4.CreateTranslation(4, 5, 6);

        history.Commit(view, proj, motion);

        Assert.True(history.HasPrevious);
        Assert.Equal(Matrix4x4.Transpose(view * proj), history.PrevViewProjT);
        Assert.Equal(view * proj, history.PrevViewProj);
        Assert.Equal(motion, history.PrevMotionViewProj);
        Assert.Equal(view, history.PrevView);
        Assert.Equal(proj, history.PrevProj);
    }

    [Fact]
    public void ResetClears()
    {
        var history = new CameraHistory();
        history.Commit(
            Matrix4x4.CreateTranslation(1, 2, 3),
            Matrix4x4.CreateScale(2, 3, 4),
            Matrix4x4.CreateTranslation(4, 5, 6));

        history.Reset();

        Assert.False(history.HasPrevious);
        Assert.Equal(Matrix4x4.Identity, history.PrevViewProjT);
        Assert.Equal(Matrix4x4.Identity, history.PrevViewProj);
        Assert.Equal(Matrix4x4.Identity, history.PrevMotionViewProj);
        Assert.Equal(Matrix4x4.Identity, history.PrevView);
        Assert.Equal(Matrix4x4.Identity, history.PrevProj);
    }
}
