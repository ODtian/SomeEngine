using SomeEngine.Render.Frame;

namespace SomeEngine.Tests;

public sealed class TemporalStateTests
{
    [Fact]
    public void ConsumeReset()
    {
        var state = new TemporalState();
        state.SetReady(true);
        state.RequestReset();

        Assert.True(state.ConsumeReset());
        Assert.False(state.Ready);
        Assert.False(state.ResetRequested);
        Assert.False(state.ConsumeReset());
    }

    [Fact]
    public void ResetClears()
    {
        var state = new TemporalState();
        state.SetReady(true);
        state.RequestReset();

        state.Reset();

        Assert.False(state.Ready);
        Assert.False(state.ResetRequested);
    }
}
