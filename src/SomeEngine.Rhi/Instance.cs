using SomeEngine.Rhi.Backends.Null;

namespace SomeEngine.Rhi;

public static class Instance
{
    public static IInstance Create(params IBackendFactory[] backendFactories)
    {
        var factories = new IBackendFactory[backendFactories.Length + 1];
        factories[0] = NullBackendFactory.Instance;
        backendFactories.CopyTo(factories.AsSpan(1));
        return new AggregateInstance(factories);
    }
}

internal sealed class AggregateInstance(IReadOnlyList<IBackendFactory> factories) : IInstance
{
    private bool _disposed;

    public IReadOnlyList<AdapterInfo> EnumerateAdapters()
    {
        ThrowIfDisposed();
        var adapters = new List<AdapterInfo>();
        foreach (var factory in factories)
            adapters.AddRange(factory.EnumerateAdapters());
        return adapters;
    }

    public IDevice CreateDevice(DeviceDesc desc)
    {
        ThrowIfDisposed();
        foreach (var factory in factories)
        {
            if (factory.Backend == desc.Backend)
                return factory.CreateDevice(desc);
        }

        throw new RhiException(ErrorCode.UnsupportedFeature, $"Backend '{desc.Backend}' has no registered factory.");
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
