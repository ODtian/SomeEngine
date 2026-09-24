namespace SomeEngine.Rhi.Backends.Null;

internal sealed class NullInstance : IInstance
{
    private bool _disposed;

    public IReadOnlyList<AdapterInfo> EnumerateAdapters()
    {
        ThrowIfDisposed();
        return
        [
            new AdapterInfo
            {
                Name = "Null Adapter",
                Backend = Backend.Null,
            },
        ];
    }

    public IDevice CreateDevice(DeviceDesc desc)
    {
        ThrowIfDisposed();
        if (desc.Backend != Backend.Null)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Backend '{desc.Backend}' is not implemented in this batch.");
        return new NullDevice();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new RhiException(ErrorCode.InvalidHandle, "Instance is disposed.");
    }
}

internal sealed class NullBackendFactory : IBackendFactory
{
    public static readonly NullBackendFactory Instance = new();

    private NullBackendFactory()
    {
    }

    public Backend Backend => Backend.Null;

    public IReadOnlyList<AdapterInfo> EnumerateAdapters()
        =>
        [
            new AdapterInfo
            {
                Name = "Null Adapter",
                Backend = Backend.Null,
            },
        ];

    public IDevice CreateDevice(DeviceDesc desc)
    {
        if (desc.Backend != Backend.Null)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Backend '{desc.Backend}' cannot be created by Null backend factory.");
        return new NullDevice();
    }
}
