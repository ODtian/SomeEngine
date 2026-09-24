using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Core.Collections;

public sealed class KeyBucketMap<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, SmallList<TValue>> _buckets;

    public KeyBucketMap()
        : this(null)
    {
    }

    public KeyBucketMap(IEqualityComparer<TKey>? comparer)
    {
        _buckets = new Dictionary<TKey, SmallList<TValue>>(comparer);
    }

    public int Count => _buckets.Count;

    public void Add(TKey key, TValue value)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(_buckets, key, out bool exists);
        if (!exists)
            bucket = default;

        bucket.Add(value);
    }

    public bool AddUnique(TKey key, TValue value)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(_buckets, key, out bool exists);
        if (!exists)
            bucket = default;

        if (bucket.IndexOf(value) >= 0)
            return false;

        bucket.Add(value);
        return true;
    }

    public bool RemoveSwapBack(TKey key, TValue value)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(_buckets, key);
        if (Unsafe.IsNullRef(ref bucket))
            return false;

        if (!bucket.RemoveSwapBack(value))
            return false;

        if (bucket.Count == 0)
            _buckets.Remove(key);

        return true;
    }

    /// <summary>
    /// Gets the values currently stored for <paramref name="key" />.
    /// </summary>
    /// <remarks>
    /// The returned span is borrowed from this map and is valid only until the next mutation of the map.
    /// </remarks>
    public ReadOnlySpan<TValue> Get(TKey key)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(_buckets, key);
        return Unsafe.IsNullRef(ref bucket)
            ? ReadOnlySpan<TValue>.Empty
            : bucket.AsSpan();
    }

    public void Clear() => _buckets.Clear();
}
