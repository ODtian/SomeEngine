namespace SomeEngine.Core.Collections;

public ref struct InlineListEnumerator<T>
{
    private readonly Span<T> _items;
    private int _index;

    internal InlineListEnumerator(Span<T> items)
    {
        _items = items;
        _index = -1;
    }

    public readonly T Current => _items[_index];

    public bool MoveNext()
    {
        int nextIndex = _index + 1;
        if (nextIndex >= _items.Length)
            return false;

        _index = nextIndex;
        return true;
    }
}
