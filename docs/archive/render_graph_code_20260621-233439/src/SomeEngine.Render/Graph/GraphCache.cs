using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Render.Graph;

internal enum GraphHit
{
    None,
    Recent,
    Local,
    Shared,
}

internal sealed class GraphCache<T>
    where T : class
{
    private const int Capacity = 64;
    // LocalSize is used as a bitmask slot count; keep it a power of two.
    private const int LocalSize = 256;

    private readonly Dictionary<int, List<Entry>> _buckets = [];
    private readonly List<Entry> _entries = new(Capacity);
    private readonly Entry?[] _local = new Entry?[LocalSize];
    private Entry? _recent;
    private int _next;

    public bool TryGet(GraphSchema schema, [NotNullWhen(true)] out T? value, out GraphHit hit)
    {
        int hash = schema.GetHashCode();
        if (_recent != null && _recent.Schema.Equals(schema))
        {
            value = _recent.Value;
            hit = GraphHit.Recent;
            return true;
        }

        Entry? local = _local[Slot(hash)];
        if (local != null && local.Schema.Equals(schema))
        {
            _recent = local;
            value = local.Value;
            hit = GraphHit.Local;
            return true;
        }

        if (TryFind(hash, schema, out Entry? entry))
        {
            _recent = entry;
            _local[Slot(hash)] = entry;
            value = entry.Value;
            hit = GraphHit.Shared;
            return true;
        }

        value = null;
        hit = GraphHit.None;
        return false;
    }

    public void Store(GraphSchema schema, T value)
    {
        int hash = schema.GetHashCode();
        if (TryFind(hash, schema, out Entry? entry))
        {
            entry.Value = value;
            _recent = entry;
            _local[Slot(hash)] = entry;
            return;
        }

        var added = new Entry(schema, value);
        if (_entries.Count < Capacity)
        {
            _entries.Add(added);
        }
        else
        {
            Entry old = _entries[_next];
            RemoveBucket(old);
            if (ReferenceEquals(_recent, old))
                _recent = null;
            ClearLocal(old);
            _entries[_next] = added;
            _next++;
            if (_next == Capacity)
                _next = 0;
        }

        AddBucket(hash, added);
        _local[Slot(hash)] = added;
        _recent = added;
    }

    public void Clear()
    {
        _entries.Clear();
        _buckets.Clear();
        Array.Clear(_local);
        _recent = null;
        _next = 0;
    }

    private bool TryFind(int hash, GraphSchema schema, [NotNullWhen(true)] out Entry? entry)
    {
        if (!_buckets.TryGetValue(hash, out List<Entry>? bucket))
        {
            entry = null;
            return false;
        }

        for (int index = 0; index < bucket.Count; index++)
        {
            if (bucket[index].Schema.Equals(schema))
            {
                entry = bucket[index];
                return true;
            }
        }

        entry = null;
        return false;
    }

    private void AddBucket(int hash, Entry entry)
    {
        if (!_buckets.TryGetValue(hash, out List<Entry>? bucket))
        {
            bucket = [];
            _buckets.Add(hash, bucket);
        }

        bucket.Add(entry);
    }

    private void RemoveBucket(Entry entry)
    {
        int hash = entry.Schema.GetHashCode();
        if (!_buckets.TryGetValue(hash, out List<Entry>? bucket))
            return;

        bucket.Remove(entry);
        if (bucket.Count == 0)
            _buckets.Remove(hash);
    }

    private void ClearLocal(Entry entry)
    {
        int slot = Slot(entry.Schema.GetHashCode());
        if (ReferenceEquals(_local[slot], entry))
            _local[slot] = null;
    }

    private static int Slot(int hash)
        => hash & (LocalSize - 1);

    private sealed class Entry(GraphSchema schema, T value)
    {
        public GraphSchema Schema { get; } = schema;
        public T Value { get; set; } = value;
    }
}

internal sealed class GraphSchema : IEquatable<GraphSchema>
{
    private readonly int[] _values;
    private readonly int _hash;

    public GraphSchema(ReadOnlySpan<int> values)
    {
        _values = values.ToArray();
        _hash = Hash(values);
    }

    public bool Equals(GraphSchema? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other == null
            || _hash != other._hash
            || _values.Length != other._values.Length)
        {
            return false;
        }

        for (int i = 0; i < _values.Length; i++)
        {
            if (_values[i] != other._values[i])
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj)
        => obj is GraphSchema other && Equals(other);

    public override int GetHashCode()
        => _hash;

    internal static int Hash(ReadOnlySpan<int> values)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            for (int i = 0; i < values.Length; i++)
            {
                hash ^= (uint)values[i];
                hash *= prime;
            }

            return (int)hash;
        }
    }
}
