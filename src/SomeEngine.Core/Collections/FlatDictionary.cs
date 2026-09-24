using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;

namespace SomeEngine.Core.Collections;

public sealed class FlatDictionary<TKey, TValue> :
    IDictionary<TKey, TValue>,
    IReadOnlyDictionary<TKey, TValue>,
    IDisposable
    where TKey : notnull
{
    private FlatDictionaryCore<TKey, TValue> _core;
    private int _version;

    public FlatDictionary(int capacity = 0, IEqualityComparer<TKey>? comparer = null)
    {
        _core = new FlatDictionaryCore<TKey, TValue>(capacity, comparer);
    }

    public FlatDictionary(IEqualityComparer<TKey> comparer)
    {
        _core = new FlatDictionaryCore<TKey, TValue>(0, comparer);
    }

    public int Count => _core.Count;

    public bool IsReadOnly => false;

    public TValue this[TKey key]
    {
        get => _core.GetValue(key);
        set
        {
            _core.Set(key, value);
            _version++;
        }
    }

    public bool ContainsKey(TKey key)
        => _core.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value)
        => _core.TryGetValue(key, out value);

    public void Add(TKey key, TValue value)
    {
        _core.Add(key, value);
        _version++;
    }

    public bool TryAdd(TKey key, TValue value)
    {
        if (!_core.TryAdd(key, value))
            return false;

        _version++;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        _core.Set(key, value);
        _version++;
    }

    public bool Remove(TKey key)
    {
        if (!_core.Remove(key))
            return false;

        _version++;
        return true;
    }

    public void Clear()
    {
        _core.Clear();
        _version++;
    }

    public void ClearNoResize()
    {
        _core.ClearNoResize();
        _version++;
    }

    public void EnsureCapacity(int capacity)
    {
        _core.EnsureCapacity(capacity);
        _version++;
    }

    public Enumerator GetEnumerator()
        => new(this);

    public void Dispose()
        => Clear();

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item)
        => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
        => _core.Contains(item);

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        => _core.CopyTo(array, arrayIndex);

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
    {
        if (!_core.Contains(item))
            return false;
        return Remove(item.Key);
    }

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    ICollection<TKey> IDictionary<TKey, TValue>.Keys
        => _core.CopyKeys();

    ICollection<TValue> IDictionary<TKey, TValue>.Values
        => _core.CopyValues();

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys
        => _core.CopyKeys();

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values
        => _core.CopyValues();

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly FlatDictionary<TKey, TValue> _dictionary;
        private readonly int _version;
        private int _index;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(FlatDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _version = dictionary._version;
            _index = -1;
            _current = default;
        }

        public readonly KeyValuePair<TKey, TValue> Current => _current;

        readonly object IEnumerator.Current => _current;

        public bool MoveNext()
        {
            ThrowIfModified();
            for (int next = _index + 1; next < _dictionary._core.SlotCount; next++)
            {
                if (!_dictionary._core.TryPairSlot(next, out var pair))
                    continue;

                _index = next;
                _current = pair;
                return true;
            }

            return false;
        }

        public void Reset()
        {
            ThrowIfModified();
            _index = -1;
            _current = default;
        }

        public readonly void Dispose()
        {
        }

        private readonly void ThrowIfModified()
        {
            if (_version != _dictionary._version)
                throw new InvalidOperationException("Collection was modified during enumeration.");
        }
    }
}

internal struct FlatDictionaryCore<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private const int EmptyHash = 0;
    private const int DeletedHash = -1;
    private const int MinimumCapacity = 16;

    private TKey[]? _keys;
    private TValue[]? _values;
    private int[]? _hashes;
    private IEqualityComparer<TKey>? _comparer;
    private int _count;
    private int _usedSlots;

    internal FlatDictionaryCore(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        this = default;
        _comparer = comparer;
        EnsureCapacity(capacity);
    }

    internal readonly int Count => _count;

    internal readonly bool HasStorage => _keys != null;

    internal readonly int SlotCount => _hashes?.Length ?? 0;

    internal readonly TValue GetValue(TKey key)
    {
        if (TryGetValue(key, out var value))
            return value;
        throw new KeyNotFoundException($"The key '{key}' was not found.");
    }

    internal readonly bool ContainsKey(TKey key)
        => TryGetValue(key, out _);

    internal readonly bool TryGetValue(TKey key, out TValue value)
    {
        if (_keys == null)
        {
            CheckKey(key);
            value = default!;
            return false;
        }

        int hash = GetStoredHash(key);
        int slot = FindSlot(key, hash, out bool found);
        if (found)
        {
            value = _values![slot];
            return true;
        }

        value = default!;
        return false;
    }

    internal void Add(TKey key, TValue value)
    {
        if (!TryAdd(key, value))
            throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
    }

    internal bool TryAdd(TKey key, TValue value)
    {
        int hash = GetStoredHash(key);
        if (_keys != null)
        {
            _ = FindSlot(key, hash, out bool exists);
            if (exists)
                return false;
        }

        GrowForAdd(_count + 1);
        int slot = FindSlot(key, hash, out bool found);
        if (found)
            return false;

        InsertAt(slot, hash, key, value);
        return true;
    }

    internal void Set(TKey key, TValue value)
    {
        int hash = GetStoredHash(key);
        if (_keys != null)
        {
            int existingSlot = FindSlot(key, hash, out bool exists);
            if (exists)
            {
                _values![existingSlot] = value;
                return;
            }
        }

        GrowForAdd(_count + 1);
        int slot = FindSlot(key, hash, out bool found);
        if (found)
        {
            _values![slot] = value;
            return;
        }

        InsertAt(slot, hash, key, value);
    }

    internal int GetOrAddSlot(TKey key, out bool exists)
    {
        int hash = GetStoredHash(key);
        if (_keys != null)
        {
            int existingSlot = FindSlot(key, hash, out exists);
            if (exists)
                return existingSlot;
        }

        GrowForAdd(_count + 1);
        int slot = FindSlot(key, hash, out exists);
        if (!exists)
            InsertAt(slot, hash, key, default!);

        return slot;
    }

    internal readonly TValue GetValueAt(int slot)
        => _values![slot];

    internal void SetValueAt(int slot, TValue value)
        => _values![slot] = value;

    internal bool Remove(TKey key)
    {
        if (_keys == null)
        {
            CheckKey(key);
            return false;
        }

        int hash = GetStoredHash(key);
        int slot = FindSlot(key, hash, out bool found);
        if (!found)
            return false;

        _hashes![slot] = DeletedHash;
        _keys[slot] = default!;
        _values![slot] = default!;
        _count--;
        return true;
    }

    internal void Clear()
    {
        var keys = _keys;
        var values = _values;
        var hashes = _hashes;
        var comparer = _comparer;
        this = default;
        _comparer = comparer;

        if (keys != null)
            ArrayPool<TKey>.Shared.Return(keys, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TKey>());
        if (values != null)
            ArrayPool<TValue>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
        if (hashes != null)
            ArrayPool<int>.Shared.Return(hashes);
    }

    internal void ClearNoResize()
    {
        if (_keys == null)
            return;

        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
            Array.Clear(_keys);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            Array.Clear(_values!);
        Array.Clear(_hashes!);
        _count = 0;
        _usedSlots = 0;
    }

    internal void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (capacity == 0)
            return;
        if (_keys != null && FitsLoadFactor(capacity, _keys.Length))
            return;

        int nextSize = _keys == null ? MinimumCapacity : _keys.Length;
        while (!FitsLoadFactor(capacity, nextSize))
            nextSize = checked(nextSize * 2);
        Resize(nextSize);
    }

    public readonly Enumerator GetEnumerator()
        => new(this);

    public void Dispose()
        => Clear();

    internal readonly bool Contains(KeyValuePair<TKey, TValue> item)
        => TryGetValue(item.Key, out var value)
            && EqualityComparer<TValue>.Default.Equals(value, item.Value);

    internal readonly void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if ((uint)arrayIndex > (uint)array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        if (array.Length - arrayIndex < _count)
            throw new ArgumentException("Destination array does not have enough space.", nameof(array));

        foreach (var pair in this)
            array[arrayIndex++] = pair;
    }

    internal readonly TKey[] CopyKeys()
    {
        var keys = new TKey[_count];
        int index = 0;
        foreach (var pair in this)
            keys[index++] = pair.Key;
        return keys;
    }

    internal readonly TValue[] CopyValues()
    {
        var values = new TValue[_count];
        int index = 0;
        foreach (var pair in this)
            values[index++] = pair.Value;
        return values;
    }

    internal readonly bool TryPairSlot(int slot, out KeyValuePair<TKey, TValue> pair)
    {
        if (_hashes == null || (uint)slot >= (uint)_hashes.Length || _hashes[slot] <= EmptyHash)
        {
            pair = default;
            return false;
        }

        pair = new KeyValuePair<TKey, TValue>(_keys![slot], _values![slot]);
        return true;
    }

    private void GrowForAdd(int capacity)
    {
        if (_keys != null)
        {
            if (FitsLoadFactor(capacity, _keys.Length) && FitsLoadFactor(_usedSlots + 1, _keys.Length))
                return;

            if (FitsLoadFactor(capacity, _keys.Length))
            {
                Resize(_keys.Length);
                return;
            }
        }

        int nextSize = _keys == null ? MinimumCapacity : checked(_keys.Length * 2);
        while (!FitsLoadFactor(capacity, nextSize))
            nextSize = checked(nextSize * 2);
        Resize(nextSize);
    }

    private void Resize(int capacity)
    {
        var oldKeys = _keys;
        var oldValues = _values;
        var oldHashes = _hashes;

        var nextKeys = ArrayPool<TKey>.Shared.Rent(capacity);
        var nextValues = ArrayPool<TValue>.Shared.Rent(capacity);
        var nextHashes = ArrayPool<int>.Shared.Rent(capacity);
        Array.Clear(nextHashes, 0, nextHashes.Length);

        _keys = nextKeys;
        _values = nextValues;
        _hashes = nextHashes;
        _count = 0;
        _usedSlots = 0;

        if (oldKeys != null)
        {
            for (int index = 0; index < oldKeys.Length; index++)
            {
                int hash = oldHashes![index];
                if (hash <= EmptyHash)
                    continue;
                InsertNoGrow(oldKeys[index], oldValues![index], hash);
            }

            ArrayPool<TKey>.Shared.Return(oldKeys, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TKey>());
            ArrayPool<TValue>.Shared.Return(oldValues!, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
            ArrayPool<int>.Shared.Return(oldHashes!);
        }
    }

    private void InsertNoGrow(TKey key, TValue value, int hash)
    {
        int slot = FindSlot(key, hash, out bool found);
        if (found)
        {
            _values![slot] = value;
            return;
        }

        InsertAt(slot, hash, key, value);
    }

    private void InsertAt(int slot, int hash, TKey key, TValue value)
    {
        if (_hashes![slot] == EmptyHash)
            _usedSlots++;

        _hashes[slot] = hash;
        _keys![slot] = key;
        _values![slot] = value;
        _count++;
    }

    private readonly int FindSlot(TKey key, int hash, out bool found)
    {
        var keys = _keys!;
        var hashes = _hashes!;
        int slot = hash % hashes.Length;
        int firstDeleted = -1;

        for (int probe = 0; probe < hashes.Length; probe++)
        {
            int currentHash = hashes[slot];
            if (currentHash == EmptyHash)
            {
                found = false;
                return firstDeleted >= 0 ? firstDeleted : slot;
            }

            if (currentHash == DeletedHash)
            {
                if (firstDeleted < 0)
                    firstDeleted = slot;
            }
            else if (currentHash == hash && Comparer.Equals(keys[slot], key))
            {
                found = true;
                return slot;
            }

            slot++;
            if (slot == hashes.Length)
                slot = 0;
        }

        found = false;
        if (firstDeleted >= 0)
            return firstDeleted;
        throw new InvalidOperationException("Flat dictionary is full.");
    }

    private readonly int GetStoredHash(TKey key)
    {
        CheckKey(key);
        int hash = Comparer.GetHashCode(key) & 0x7fffffff;
        return hash == EmptyHash ? 1 : hash;
    }

    private readonly IEqualityComparer<TKey> Comparer
        => _comparer ?? EqualityComparer<TKey>.Default;

    private static bool FitsLoadFactor(int count, int capacity)
        => count <= capacity - (capacity / 4);

    private static void CheckKey(TKey key)
    {
        if (!typeof(TKey).IsValueType && key is null)
            throw new ArgumentNullException(nameof(key));
    }

    internal struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
        private readonly FlatDictionaryCore<TKey, TValue> _dictionary;
        private int _index;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(FlatDictionaryCore<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _index = -1;
            _current = default;
        }

        public readonly KeyValuePair<TKey, TValue> Current => _current;

        readonly object IEnumerator.Current => _current;

        public bool MoveNext()
        {
            for (int next = _index + 1; next < _dictionary.SlotCount; next++)
            {
                if (!_dictionary.TryPairSlot(next, out var pair))
                    continue;

                _index = next;
                _current = pair;
                return true;
            }

            return false;
        }

        public void Reset()
        {
            _index = -1;
            _current = default;
        }

        public readonly void Dispose()
        {
        }
    }
}
