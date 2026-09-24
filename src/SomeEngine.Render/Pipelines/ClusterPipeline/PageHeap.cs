namespace SomeEngine.Render.Pipelines;

internal sealed class PageHeap
{
    public const uint CapacityBytes = 64 * 1024 * 1024;
    private readonly List<FreeBlock> _freeBlocks = [];

    public PageHeap()
    {
        _freeBlocks.Add(new FreeBlock(0, CapacityBytes));
    }

    public uint FreeBytes
    {
        get
        {
            uint total = 0;
            for (int i = 0; i < _freeBlocks.Count; i++)
                total += _freeBlocks[i].Size;

            return total;
        }
    }

    public uint UsedBytes => CapacityBytes - FreeBytes;
    public int FreeBlockCount => _freeBlocks.Count;

    public bool TryAlloc(uint size, out uint offset)
    {
        uint alignedSize = Align(size);
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            FreeBlock block = _freeBlocks[i];
            if (block.Size < alignedSize)
                continue;

            offset = block.Offset;
            if (block.Size == alignedSize)
            {
                _freeBlocks.RemoveAt(i);
            }
            else
            {
                _freeBlocks[i] = new FreeBlock(
                    block.Offset + alignedSize,
                    block.Size - alignedSize);
            }

            return true;
        }

        offset = 0;
        return false;
    }

    public void Free(uint offset, uint size)
    {
        uint alignedSize = Align(size);
        if (alignedSize == 0)
            return;

        var block = new FreeBlock(offset, alignedSize);
        int index = 0;
        while (index < _freeBlocks.Count && _freeBlocks[index].Offset < block.Offset)
            index++;

        _freeBlocks.Insert(index, block);
        MergeAt(index);
    }

    public bool Has(uint size)
    {
        uint alignedSize = Align(size);
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            if (_freeBlocks[i].Size >= alignedSize)
                return true;
        }

        return false;
    }

    public uint Largest()
    {
        uint largest = 0;
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            if (_freeBlocks[i].Size > largest)
                largest = _freeBlocks[i].Size;
        }

        return largest;
    }

    private static uint Align(uint size)
        => (size + 15) & ~15u;

    private void MergeAt(int index)
    {
        if (index > 0)
        {
            FreeBlock prev = _freeBlocks[index - 1];
            FreeBlock cur = _freeBlocks[index];
            if (prev.Offset + prev.Size == cur.Offset)
            {
                _freeBlocks[index - 1] = new FreeBlock(prev.Offset, prev.Size + cur.Size);
                _freeBlocks.RemoveAt(index);
                index--;
            }
        }

        if (index + 1 < _freeBlocks.Count)
        {
            FreeBlock cur = _freeBlocks[index];
            FreeBlock next = _freeBlocks[index + 1];
            if (cur.Offset + cur.Size == next.Offset)
            {
                _freeBlocks[index] = new FreeBlock(cur.Offset, cur.Size + next.Size);
                _freeBlocks.RemoveAt(index + 1);
            }
        }
    }

    private readonly record struct FreeBlock(uint Offset, uint Size);
}
