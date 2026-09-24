namespace SomeEngine.Rhi;

public sealed class RhiException : InvalidOperationException
{
    public RhiException(ErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}

