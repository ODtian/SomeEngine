using System;
using System.Collections.Generic;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// CPU 端 slot buffer，SOA 布局。
/// 内部为 ushort[] flat array，按字段分段存储：
/// <code>
/// [ field0: s0,s1,...,s(cap-1) | field1: s0,s1,... | ... ]
/// </code>
/// 访问: _data[fieldIndex * _capacity + slotOffset + localIdx]
/// <para>
/// GPU 上传时直接传整个 _data（capacity * stride 个 ushort），
/// 因为 capacity 保证偶数，GPU 按 uint 对齐读取。
/// </para>
/// </summary>
internal struct DirtyRange
{
    public int MinSlot;
    public int MaxSlot;
    public bool IsDirty => MinSlot <= MaxSlot;

    public void Reset() { MinSlot = int.MaxValue; MaxSlot = -1; }
    public void Add(int slotIdx)
    {
        if (slotIdx < MinSlot) MinSlot = slotIdx;
        if (slotIdx > MaxSlot) MaxSlot = slotIdx;
    }
}

internal sealed class SlotDirty
{
    private readonly DirtyRange[] _ranges;

    public SlotDirty(int stride)
    {
        if (stride <= 0)
            throw new ArgumentOutOfRangeException(nameof(stride));

        _ranges = new DirtyRange[stride];
        ClearRanges();
        Full = true;
    }

    public bool Full { get; private set; }

    public void RequireFull()
        => Full = true;

    public void Clear()
    {
        Full = false;
        ClearRanges();
    }

    public void Mark(int field, int slot)
        => _ranges[field].Add(slot);

    public void Mark(int field, int start, int count)
    {
        if (count <= 0)
            return;

        _ranges[field].Add(start);
        _ranges[field].Add(start + count - 1);
    }

    public bool TryRange(int field, out int minSlot, out int maxSlot)
    {
        DirtyRange range = _ranges[field];
        minSlot = range.MinSlot;
        maxSlot = range.MaxSlot;
        return range.IsDirty;
    }

    private void ClearRanges()
    {
        for (int i = 0; i < _ranges.Length; i++)
            _ranges[i].Reset();
    }
}

internal sealed class ClusterSlotBuffer : IDisposable
{
    private ushort[] _data;
    private int _slotCount;   // 逻辑 slot 数
    private ClusterSlotLayout _layout;
    private readonly List<(int Offset, int Count)> _freeList = new();
    private readonly SlotDirty _dirty;

    public ClusterSlotBuffer(int fields, int initialCapacity = 256)
    {
        _layout = new ClusterSlotLayout(fields, initialCapacity);
        _data = new ushort[_layout.ElementCount];
        _dirty = new SlotDirty(fields);
    }

    public ClusterSlotLayout Layout => _layout;
    public int Fields => _layout.Fields;

    /// <summary>已分配 slot 总数。</summary>
    public int SlotCount => _slotCount;

    /// <summary>当前容量（偶数，用于 GPU uniform）。</summary>
    public int Capacity => _layout.Capacity;

    public bool NeedsFull => _dirty.Full;

    public void ForceFullUpload()
        => _dirty.RequireFull();

    public void ClearDirty()
        => _dirty.Clear();

    public bool TryDirty(int fieldIndex, out int minSlot, out int maxSlot)
        => _dirty.TryRange(fieldIndex, out minSlot, out maxSlot);

    /// <summary>分配连续 slot 区间，返回起始 slot offset。</summary>
    public int AllocateRange(int slotCount)
    {
        // Try free list
        for (int i = 0; i < _freeList.Count; i++)
        {
            var (offset, size) = _freeList[i];
            if (size >= slotCount)
            {
                _freeList.RemoveAt(i);
                if (size > slotCount)
                    _freeList.Add((offset + slotCount, size - slotCount));
                return offset;
            }
        }

        // Append
        int start = _slotCount;
        _slotCount += slotCount;
        EnsureCapacity(_slotCount);
        return start;
    }

    /// <summary>释放 slot 区间。</summary>
    public void FreeRange(int offset, int count)
    {
        // Clear each field's segment for this slot range
        for (int f = 0; f < _layout.Fields; f++)
        {
            _data.AsSpan(_layout.Index(f, offset), count).Fill(ushort.MaxValue);
            _dirty.Mark(f, offset, count);
        }
        _freeList.Add((offset, count));
    }

    /// <summary>设置单个 slot 的单个字段（SOA 寻址）。</summary>
    public void SetField(int slotOffset, int localIdx, int fieldIndex, ushort value)
    {
        _data[_layout.Index(fieldIndex, slotOffset + localIdx)] = value;
        _dirty.Mark(fieldIndex, slotOffset + localIdx);
    }

    /// <summary>读取单个 slot 的单个字段（SOA 寻址）。</summary>
    public ushort GetField(int slotOffset, int localIdx, int fieldIndex)
    {
        return _data[_layout.Index(fieldIndex, slotOffset + localIdx)];
    }

    /// <summary>
    /// 获取底层数据用于上传到 GPU。
    /// 长度 = capacity * stride 个 ushort。
    /// GPU 将其视为 StructuredBuffer&lt;uint&gt;，每 2 个 ushort = 1 个 uint。
    /// </summary>
    public ReadOnlySpan<ushort> GetData() => _data.AsSpan(0, _layout.ElementCount);

    private void EnsureCapacity(int requiredSlots)
    {
        if (requiredSlots <= _layout.Capacity) return;

        ClusterSlotLayout oldLayout = _layout;
        ClusterSlotLayout newLayout = _layout.Grow(requiredSlots);

        var newData = new ushort[newLayout.ElementCount];

        // SOA 搬运：逐 field 拷贝旧数据到新段位置
        for (int f = 0; f < oldLayout.Fields; f++)
        {
            Array.Copy(
                _data, oldLayout.Field(f),
                newData, newLayout.Field(f),
                oldLayout.Capacity
            );
        }

        _data = newData;
        _layout = newLayout;
        _dirty.RequireFull();
    }

    public void Dispose()
    {
        _data = [];
        _slotCount = 0;
        _layout = default;
        _freeList.Clear();
    }
}
