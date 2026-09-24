namespace SomeEngine.Render.Frame;

public sealed class TemporalState
{
    public bool Ready { get; private set; }
    public bool ResetRequested { get; private set; }

    public void RequestReset()
        => ResetRequested = true;

    public bool ConsumeReset()
    {
        if (!ResetRequested)
            return false;

        ResetRequested = false;
        Ready = false;
        return true;
    }

    public void SetReady(bool ready)
        => Ready = ready;

    public void Reset()
    {
        Ready = false;
        ResetRequested = false;
    }
}
