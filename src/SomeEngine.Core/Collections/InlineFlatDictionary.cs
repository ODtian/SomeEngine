using System.Collections;
using System.Runtime.CompilerServices;

namespace SomeEngine.Core.Collections;

public sealed class InlineFlatDictionary<TKey, TValue> :
    IDictionary<TKey, TValue>,
    IReadOnlyDictionary<TKey, TValue>,
    IDisposable
    where TKey : notnull
{
    private InlineFlatCore<TKey, TValue> _core;
    private int _version;

    public InlineFlatDictionary(int capacity = 0, IEqualityComparer<TKey>? comparer = null)
    {
        _core = new InlineFlatCore<TKey, TValue>(capacity, comparer);
    }

    public InlineFlatDictionary(IEqualityComparer<TKey> comparer)
    {
        _core = new InlineFlatCore<TKey, TValue>(0, comparer);
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
        private readonly InlineFlatDictionary<TKey, TValue> _dictionary;
        private readonly int _version;
        private int _index;
        private KeyValuePair<TKey, TValue> _current;

        internal Enumerator(InlineFlatDictionary<TKey, TValue> dictionary)
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

internal struct InlineFlatCore<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private const int InlineCapacity = 8;

    private InlineArray8<TKey> _inlineKeys;
    private InlineArray8<TValue> _inlineValues;
    private FlatDictionaryCore<TKey, TValue> _spill;
    private IEqualityComparer<TKey>? _comparer;
    private int _inlineCount;

    internal InlineFlatCore(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        this = default;
        _comparer = comparer;
        EnsureCapacity(capacity);
    }

    internal readonly int Count => HasSpill ? _spill.Count : _inlineCount;

    internal readonly int SlotCount => HasSpill ? _spill.SlotCount : _inlineCount;

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
        if (HasSpill)
            return _spill.TryGetValue(key, out value);

        CheckKey(key);
        for (int index = 0; index < _inlineCount; index++)
        {
            if (Comparer.Equals(_inlineKeys[index], key))
            {
                value = _inlineValues[index];
                return true;
            }
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
        if (HasSpill)
            return _spill.TryAdd(key, value);

        CheckKey(key);
        for (int index = 0; index < _inlineCount; index++)
        {
            if (Comparer.Equals(_inlineKeys[index], key))
                return false;
        }

        if (_inlineCount < InlineCapacity)
        {
            _inlineKeys[_inlineCount] = key;
            _inlineValues[_inlineCount] = value;
            _inlineCount++;
            return true;
        }

        EnsureSpill(_inlineCount + 1);
        return _spill.TryAdd(key, value);
    }

    internal void Set(TKey key, TValue value)
    {
        if (HasSpill)
        {
            _spill.Set(key, value);
            return;
        }

        CheckKey(key);
        for (int index = 0; index < _inlineCount; index++)
        {
            if (!Comparer.Equals(_inlineKeys[index], key))
                continue;

            _inlineValues[index] = value;
            return;
        }

        if (_inlineCount < InlineCapacity)
        {
            _inlineKeys[_inlineCount] = key;
            _inlineValues[_inlineCount] = value;
            _inlineCount++;
            return;
        }

        EnsureSpill(_inlineCount + 1);
        _spill.Set(key, value);
    }

    internal int GetOrAddSlot(TKey key, out bool exists)
    {
        if (HasSpill)
            return _spill.GetOrAddSlot(key, out exists);

        CheckKey(key);
        for (int index = 0; index < _inlineCount; index++)
        {
            if (!Comparer.Equals(_inlineKeys[index], key))
                continue;

            exists = true;
            return index;
        }

        exists = false;
        if (_inlineCount < InlineCapacity)
        {
            int index = _inlineCount++;
            _inlineKeys[index] = key;
            _inlineValues[index] = default!;
            return index;
        }

        EnsureSpill(_inlineCount + 1);
        return _spill.GetOrAddSlot(key, out exists);
    }

    internal readonly TValue GetValueAt(int slot)
        => HasSpill ? _spill.GetValueAt(slot) : _inlineValues[slot];

    internal void SetValueAt(int slot, TValue value)
    {
        if (HasSpill)
        {
            _spill.SetValueAt(slot, value);
            return;
        }

        _inlineValues[slot] = value;
    }

    internal bool Remove(TKey key)
    {
        if (HasSpill)
            return _spill.Remove(key);

        CheckKey(key);
        for (int index = 0; index < _inlineCount; index++)
        {
            if (!Comparer.Equals(_inlineKeys[index], key))
                continue;

            int last = _inlineCount - 1;
            if (index != last)
            {
                _inlineKeys[index] = _inlineKeys[last];
                _inlineValues[index] = _inlineValues[last];
            }

            _inlineKeys[last] = default!;
            _inlineValues[last] = default!;
            _inlineCount--;
            return true;
        }

        return false;
    }

    internal void Clear()
    {
        var comparer = _comparer;
        for (int index = 0; index < _inlineCount; index++)
        {
            _inlineKeys[index] = default!;
            _inlineValues[index] = default!;
        }

        _spill.Clear();
        this = default;
        _comparer = comparer;
    }

    internal void ClearNoResize()
    {
        if (HasSpill)
        {
            _spill.ClearNoResize();
            return;
        }

        for (int index = 0; index < _inlineCount; index++)
        {
            _inlineKeys[index] = default!;
            _inlineValues[index] = default!;
        }

        _inlineCount = 0;
    }

    internal void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (capacity <= InlineCapacity && !HasSpill)
            return;
        EnsureSpill(capacity);
    }

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
        if (array.Length - arrayIndex < Count)
            throw new ArgumentException("Destination array does not have enough space.", nameof(array));

        for (int slot = 0; slot < SlotCount; slot++)
        {
            if (TryPairSlot(slot, out var pair))
                array[arrayIndex++] = pair;
        }
    }

    internal readonly TKey[] CopyKeys()
    {
        if (HasSpill)
            return _spill.CopyKeys();

        var keys = new TKey[_inlineCount];
        for (int index = 0; index < _inlineCount; index++)
            keys[index] = _inlineKeys[index];
        return keys;
    }

    internal readonly TValue[] CopyValues()
    {
        if (HasSpill)
            return _spill.CopyValues();

        var values = new TValue[_inlineCount];
        for (int index = 0; index < _inlineCount; index++)
            values[index] = _inlineValues[index];
        return values;
    }

    internal readonly bool TryPairSlot(int slot, out KeyValuePair<TKey, TValue> pair)
    {
        if (HasSpill)
            return _spill.TryPairSlot(slot, out pair);

        if ((uint)slot >= (uint)_inlineCount)
        {
            pair = default;
            return false;
        }

        pair = new KeyValuePair<TKey, TValue>(_inlineKeys[slot], _inlineValues[slot]);
        return true;
    }

    private readonly bool HasSpill => _spill.HasStorage;

    private readonly IEqualityComparer<TKey> Comparer
        => _comparer ?? EqualityComparer<TKey>.Default;

    private void EnsureSpill(int capacity)
    {
        if (HasSpill)
        {
            _spill.EnsureCapacity(capacity);
            return;
        }

        _spill = new FlatDictionaryCore<TKey, TValue>(capacity, _comparer);
        for (int index = 0; index < _inlineCount; index++)
        {
            _spill.Add(_inlineKeys[index], _inlineValues[index]);
            _inlineKeys[index] = default!;
            _inlineValues[index] = default!;
        }

        _inlineCount = 0;
    }

    private static void CheckKey(TKey key)
    {
        if (!typeof(TKey).IsValueType && key is null)
            throw new ArgumentNullException(nameof(key));
    }

    [InlineArray(InlineCapacity)]
    private struct InlineArray8<T>
    {
        private T _element0;
    }
}
