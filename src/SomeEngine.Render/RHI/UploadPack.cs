namespace SomeEngine.Render.RHI;

public readonly record struct UploadItem(ulong Offset, ReadOnlyMemory<byte> Data);

public sealed class UploadPack
{
    private readonly List<UploadPart> _items = [];
    private byte[] _copyData = [];
    private int _copyBytes;

    public UploadPack(int copyCapacity = 0)
    {
        if (copyCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(copyCapacity));
        if (copyCapacity != 0)
            _copyData = new byte[copyCapacity];
    }

    public int Count => _items.Count;
    public long ByteCount { get; private set; }
    public long CopyBytes => _copyBytes;

    public void Add(ulong offset, ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return;

        _items.Add(new UploadPart(offset, 0, data.Length, data, false));
        ByteCount += data.Length;
    }

    public void Copy(ulong offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        int copyOffset = _copyBytes;
        EnsureCopy(data.Length);
        data.CopyTo(_copyData.AsSpan(copyOffset));
        _copyBytes += data.Length;
        _items.Add(new UploadPart(offset, copyOffset, data.Length, default, true));
        ByteCount += data.Length;
    }

    public UploadItem[] Take()
    {
        if (_items.Count == 0)
            return [];

        var items = new UploadItem[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            UploadPart part = _items[i];
            ReadOnlyMemory<byte> data = part.IsCopy
                ? _copyData.AsMemory(part.CopyOffset, part.Length)
                : part.Memory;
            items[i] = new UploadItem(part.Offset, data);
        }

        Clear();
        return items;
    }

    public bool TryPacked(out ReadOnlyMemory<byte> data)
    {
        if (_items.Count == 0 || _copyBytes != ByteCount)
        {
            data = default;
            return false;
        }

        data = _copyData.AsMemory(0, _copyBytes);
        Clear();
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _copyData = [];
        _copyBytes = 0;
        ByteCount = 0;
    }

    private void EnsureCopy(int count)
    {
        int required = checked(_copyBytes + count);
        if (_copyData.Length >= required)
            return;

        Array.Resize(ref _copyData, required);
    }

    private readonly record struct UploadPart(
        ulong Offset,
        int CopyOffset,
        int Length,
        ReadOnlyMemory<byte> Memory,
        bool IsCopy);
}
