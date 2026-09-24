namespace SomeEngine.Render.Data;

internal sealed class InstanceHeaderData
{
    private int[] _heads = [];
    private Entry[] _entries = [];
    private int _count;

    public uint Version { get; private set; }

    public void Clear()
    {
        Array.Fill(_heads, -1);
        _count = 0;
        Version++;
    }

    public void SetU32(int index, int offset, uint value)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        Ensure(index + 1);
        for (int entry = _heads[index]; entry >= 0; entry = _entries[entry].Next)
        {
            if (_entries[entry].Offset == offset)
            {
                if (_entries[entry].Value == value)
                    return;

                _entries[entry].Value = value;
                Version++;
                return;
            }
        }

        EnsureEntry(_count + 1);
        _entries[_count] = new Entry(_heads[index], offset, value);
        _heads[index] = _count++;
        Version++;
    }

    public void SetFloat32(int index, int offset, float value)
        => SetU32(index, offset, BitConverter.SingleToUInt32Bits(value));

    public void Write(int index, Span<byte> header)
    {
        if ((uint)index >= (uint)_heads.Length)
            return;

        for (int entry = _heads[index]; entry >= 0; entry = _entries[entry].Next)
            InstanceHeaderLayout.WriteU32(header, _entries[entry].Offset, _entries[entry].Value);
    }

    private void Ensure(int count)
    {
        if (_heads.Length >= count)
            return;

        int old = _heads.Length;
        int capacity = Math.Max(count, old == 0 ? 16 : old * 2);
        Array.Resize(ref _heads, capacity);
        Array.Fill(_heads, -1, old, capacity - old);
    }

    private void EnsureEntry(int count)
    {
        if (_entries.Length >= count)
            return;

        Array.Resize(ref _entries, Math.Max(count, _entries.Length == 0 ? 32 : _entries.Length * 2));
    }

    private struct Entry
    {
        public int Next;
        public int Offset;
        public uint Value;

        public Entry(int next, int offset, uint value)
        {
            Next = next;
            Offset = offset;
            Value = value;
        }
    }
}
