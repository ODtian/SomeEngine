using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace SomeEngine.Core.Collections;

[InlineArray(3)]
internal struct Inline3<T>
{
    private T _element0;
}

[InlineArray(8)]
internal struct Inline8<T>
{
    private T _element0;
}

/// <summary>
/// Small collection container: the first 3 elements stay inline, then storage moves to an overflow array.
/// </summary>
public struct SmallList<T>
{
    private InlineList<T, Inline3<T>> _items;

    public readonly int Count => _items.Count;

    [UnscopedRef]
    public ref T this[int index]
    {
        get => ref _items[index];
    }

    public void Add(T item) => _items.Add(item);

    public void EnsureCapacity(int capacity) => _items.EnsureCapacity(capacity);

    public void Insert(int index, T item) => _items.Insert(index, item);

    public void RemoveAt(int index) => _items.RemoveAt(index);

    public int IndexOf(T item) => _items.IndexOf(item);

    public bool RemoveStable(T item) => _items.RemoveStable(item);

    public bool RemoveSwapBack(T item) => _items.RemoveSwapBack(item);

    public void RemoveSwapAt(int index) => _items.RemoveSwapAt(index);

    public void Clear() => _items.Clear();

    [UnscopedRef]
    public Span<T> AsSpan() => _items.AsSpan();

    public readonly Enumerator GetEnumerator() => new(this);

    public struct Enumerator
    {
        private readonly SmallList<T> _list;
        private int _index;

        internal Enumerator(SmallList<T> list)
        {
            _list = list;
            _index = -1;
        }

        public readonly T Current => _list[_index];

        public bool MoveNext()
        {
            int nextIndex = _index + 1;
            if (nextIndex >= _list.Count)
                return false;

            _index = nextIndex;
            return true;
        }
    }
}
