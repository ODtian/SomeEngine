using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SomeEngine.Assets;

public sealed class AssetStore : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Type, IDisposable> _stores = new();
    private bool _disposed;

    public Handle<T> Add<T>(AssetGuid guid, T asset)
        where T : class
    {
        ThrowIfDisposed();
        return Set<T>().Add(guid, asset);
    }

    public bool TryFind<T>(AssetGuid guid, out Handle<T> handle)
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stores.TryGetValue(typeof(T), out IDisposable? store)
                && ((AssetStore<T>)store).TryFind(guid, out handle))
            {
                return true;
            }
        }

        handle = default;
        return false;
    }

    public bool TryGet<T>(Handle<T> handle, out T? asset)
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stores.TryGetValue(typeof(T), out IDisposable? store))
                return ((AssetStore<T>)store).TryGet(handle, out asset);
        }

        asset = null;
        return false;
    }

    public ulong GetVersion<T>()
        where T : class
    {
        ThrowIfDisposed();
        return _stores.TryGetValue(typeof(T), out IDisposable? store)
            ? ((AssetStore<T>)store).Version
            : 0;
    }

    public T Get<T>(Handle<T> handle)
        where T : class
        => TryGet(handle, out T? asset)
            ? asset!
            : throw new InvalidOperationException($"Asset handle '{handle}' is not valid in this store.");

    public Task<Handle<T>> Request<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, T?> load,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        return Request<T>(
            guid,
            (assetGuid, token) => Task.FromResult(load(assetGuid, token)),
            cancellationToken);
    }

    public Task<Handle<T>> Request<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<T?>> load,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        return Set<T>().Request(guid, load, cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (IDisposable store in _stores.Values)
                store.Dispose();
            _stores.Clear();
            _disposed = true;
        }
    }

    private AssetStore<T> Set<T>()
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Type type = typeof(T);
            if (!_stores.TryGetValue(type, out IDisposable? store))
            {
                store = new AssetStore<T>();
                _stores.TryAdd(type, store);
            }

            return (AssetStore<T>)store;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed))
            throw new ObjectDisposedException(nameof(AssetStore));
    }
}

public sealed class AssetStore<T> : IDisposable
    where T : class
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [default];
    private readonly Dictionary<AssetGuid, Handle<T>> _byGuid = new();
    private readonly Dictionary<AssetGuid, Task<Handle<T>>> _requests = new();
    private ulong _version;
    private bool _disposed;

    public ulong Version => Volatile.Read(ref _version);

    public Handle<T> Add(AssetGuid guid, T asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!guid.IsEmpty && _byGuid.TryGetValue(guid, out Handle<T> existing))
            {
                int existingIndex = existing.Id;
                if (existingIndex > 0 && existingIndex < _entries.Count)
                {
                    Entry entry = _entries[existingIndex];
                    if (entry.Asset is IDisposable disposable && !ReferenceEquals(entry.Asset, asset))
                        disposable.Dispose();

                    int generation = Next(entry.Generation);
                    var replacement = new Handle<T>(existingIndex, generation);
                    _entries[existingIndex] = new Entry(generation, asset);
                    _byGuid[guid] = replacement;
                    _requests.Remove(guid);
                    Touch();
                    return replacement;
                }
            }

            int id = _entries.Count;
            var created = new Handle<T>(id, generation: 1);
            _entries.Add(new Entry(1, asset));
            if (!guid.IsEmpty)
            {
                _byGuid[guid] = created;
                _requests.Remove(guid);
            }

            Touch();
            return created;
        }
    }

    public bool TryFind(AssetGuid guid, out Handle<T> handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return TryFindCore(guid, out handle);
        }
    }

    public bool TryGet(Handle<T> handle, out T? asset)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!handle.IsValid
                || handle.Id >= _entries.Count
                || handle.Generation != _entries[handle.Id].Generation)
            {
                asset = null;
                return false;
            }

            asset = _entries[handle.Id].Asset;
            return asset != null;
        }
    }

    public T Get(Handle<T> handle)
        => TryGet(handle, out T? asset)
            ? asset!
            : throw new InvalidOperationException($"Asset handle '{handle}' is not valid in this store.");

    public Task<Handle<T>> Request(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<T?>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (guid.IsEmpty)
            return Task.FromResult(default(Handle<T>));

        lock (_gate)
        {
            ThrowIfDisposed();
            if (TryFindCore(guid, out Handle<T> ready))
                return Task.FromResult(ready);

            if (_requests.TryGetValue(guid, out Task<Handle<T>>? existing))
                return existing;

            Task<Handle<T>> request = Run(guid, load, cancellationToken);
            _requests.Add(guid, request);
            return request;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            for (int i = 1; i < _entries.Count; i++)
            {
                if (_entries[i].Asset is IDisposable disposable)
                    disposable.Dispose();
            }

            _entries.Clear();
            _byGuid.Clear();
            _requests.Clear();
            Touch();
            _disposed = true;
        }
    }

    private async Task<Handle<T>> Run(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<T?>> load,
        CancellationToken cancellationToken)
    {
        try
        {
            T? asset = await Task
                .Run(() => load(guid, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            return asset == null ? default : Add(guid, asset);
        }
        finally
        {
            lock (_gate)
            {
                _requests.Remove(guid);
            }
        }
    }

    private bool TryFindCore(AssetGuid guid, out Handle<T> handle)
    {
        return _byGuid.TryGetValue(guid, out handle);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AssetStore));
    }

    private void Touch()
    {
        ulong version = _version + 1;
        if (version == 0)
            version = 1;
        Volatile.Write(ref _version, version);
    }

    private readonly record struct Entry(int Generation, T? Asset);

    private static int Next(int generation)
        => generation == int.MaxValue
            ? throw new InvalidOperationException("Asset handle generation overflow.")
            : generation + 1;
}
