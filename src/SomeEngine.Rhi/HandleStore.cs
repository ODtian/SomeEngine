using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi;

internal sealed class HandleStore<THandle, TValue>
    where THandle : struct
{
    private readonly Func<uint, uint, THandle> _makeHandle;
    private readonly Func<THandle, uint> _getId;
    private readonly Func<THandle, uint> _getGeneration;
    private readonly FlatDictionary<uint, Entry> _entries = new();
    private readonly System.Threading.Lock _gate = new();
    private static readonly System.Threading.Lock s_idGate = new();
    private static uint s_nextId;

    public HandleStore(
        Func<uint, uint, THandle> makeHandle,
        Func<THandle, uint> getId,
        Func<THandle, uint> getGeneration)
    {
        _makeHandle = makeHandle;
        _getId = getId;
        _getGeneration = getGeneration;
    }

    public THandle Add(TValue value)
    {
        uint id = NextId();
        const uint generation = 1;
        lock (_gate)
        {
            _entries.Add(id, new Entry(generation, value));
        }

        return _makeHandle(id, generation);
    }

    public TValue Get(THandle handle, string kind)
    {
        uint id = _getId(handle);
        uint generation = _getGeneration(handle);
        if (id == 0 || generation == 0)
            throw new RhiException(ErrorCode.InvalidHandle, $"{kind} handle is invalid.");
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Generation != generation)
                throw new RhiException(ErrorCode.InvalidHandle, $"{kind} handle id={id}, generation={generation} is stale or unknown.");
            return entry.Value;
        }
    }

    public void Destroy(THandle handle, string kind)
    {
        uint id = _getId(handle);
        uint generation = _getGeneration(handle);
        if (id == 0 || generation == 0)
            throw new RhiException(ErrorCode.InvalidHandle, $"{kind} handle is invalid.");
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Generation != generation)
                throw new RhiException(ErrorCode.InvalidHandle, $"{kind} handle id={id}, generation={generation} is stale or unknown.");
            _entries.Remove(id);
        }
    }

    public void Set(THandle handle, TValue value, string kind)
    {
        uint id = _getId(handle);
        uint generation = _getGeneration(handle);
        if (id == 0 || generation == 0)
            throw new RhiException(ErrorCode.InvalidHandle, $"{kind} handle is invalid.");
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Generation != generation)
                throw new RhiException(ErrorCode.InvalidHandle, $"{kind} handle id={id}, generation={generation} is stale or unknown.");
            _entries[id] = entry with { Value = value };
        }
    }

    public bool IsAlive(THandle handle)
    {
        uint id = _getId(handle);
        uint generation = _getGeneration(handle);
        if (id == 0 || generation == 0)
            return false;

        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry)
                && entry.Generation == generation;
        }
    }

    public bool Any(Func<TValue, bool> predicate)
    {
        List<TValue> values = [];
        lock (_gate)
        {
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                values.Add(entry.Value);
            }
        }

        for (int i = 0; i < values.Count; i++)
        {
            if (predicate(values[i]))
                return true;
        }

        return false;
    }

    public IEnumerable<TValue> Values
    {
        get
        {
            List<TValue> values = [];
            lock (_gate)
            {
                foreach (var pair in _entries)
                {
                    var entry = pair.Value;
                    values.Add(entry.Value);
                }
            }

            return values;
        }
    }

    public void Clear(Action<TValue>? release = null)
    {
        List<TValue>? values = null;
        lock (_gate)
        {
            if (release != null)
            {
                values = [];
                foreach (var pair in _entries)
                {
                    var entry = pair.Value;
                    values.Add(entry.Value);
                }
            }

            _entries.Clear();
        }

        if (values == null)
            return;

        for (int i = 0; i < values.Count; i++)
            release!(values[i]);
    }

    private static uint NextId()
    {
        lock (s_idGate)
        {
            if (s_nextId == uint.MaxValue)
                throw new RhiException(ErrorCode.BackendFailure, "RHI handle id space is exhausted.");
            return ++s_nextId;
        }
    }

    private readonly record struct Entry(uint Generation, TValue Value);
}
