using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Core.Collections;

public interface IInlineList<T>
{
    int Count { get; }

    [UnscopedRef]
    ref T this[int index] { get; }

    void Add(T item);

    void EnsureCapacity(int capacity);

    void Insert(int index, T item);

    void RemoveAt(int index);

    int IndexOf(T item);

    bool RemoveStable(T item);

    bool RemoveSwapBack(T item);

    void RemoveSwapAt(int index);

    void Clear();

    [UnscopedRef]
    Span<T> AsSpan();

    [UnscopedRef]
    InlineListEnumerator<T> GetEnumerator();
}
