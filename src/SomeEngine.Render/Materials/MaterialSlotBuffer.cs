using System;
using System.Collections.Generic;

namespace SomeEngine.Render.Materials;

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
public struct DirtyRange
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

public sealed class MaterialSlotBuffer : IDisposable
{
    private ushort[] _data;
    private int _slotCount;   // 逻辑 slot 数
    private int _capacity;    // 当前容量（保证偶数）
    private readonly int _stride;
    private readonly List<(int Offset, int Count)> _freeList = new();
    
    private readonly DirtyRange[] _dirtyRanges;
    private bool _fullUploadRequired = true;

    public MaterialSlotBuffer(int stride, int initialCapacity = 256)
    {
        if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));
        _stride = stride;
        // 保证容量为偶数（GPU uint 对齐）
        _capacity = (initialCapacity + 1) & ~1;
        _data = new ushort[_capacity * stride];

        _dirtyRanges = new DirtyRange[stride];
        for (int i = 0; i < stride; i++) _dirtyRanges[i].Reset();
    }

    /// <summary>Stride（字段数）。</summary>
    public int Stride => _stride;

    /// <summary>已分配 slot 总数。</summary>
    public int SlotCount => _slotCount;

    /// <summary>当前容量（偶数，用于 GPU uniform）。</summary>
    public int Capacity => _capacity;

    public bool RequiresFullUpload 
    {
        get => _fullUploadRequired;
        set => _fullUploadRequired = value;
    }

    public void ClearDirty()
    {
        _fullUploadRequired = false;
        for (int i = 0; i < _stride; i++) _dirtyRanges[i].Reset();
    }

    public bool TryGetDirtyRange(int fieldIndex, out int minSlot, out int maxSlot)
    {
        var r = _dirtyRanges[fieldIndex];
        minSlot = r.MinSlot;
        maxSlot = r.MaxSlot;
        return r.IsDirty;
    }

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
        for (int f = 0; f < _stride; f++)
        {
            Array.Clear(_data, f * _capacity + offset, count);
            _dirtyRanges[f].Add(offset);
            _dirtyRanges[f].Add(offset + count - 1);
        }
        _freeList.Add((offset, count));
    }

    /// <summary>设置单个 slot 的单个字段（SOA 寻址）。</summary>
    public void SetField(int slotOffset, int localIdx, int fieldIndex, ushort value)
    {
        _data[fieldIndex * _capacity + slotOffset + localIdx] = value;
        _dirtyRanges[fieldIndex].Add(slotOffset + localIdx);
    }

    /// <summary>读取单个 slot 的单个字段（SOA 寻址）。</summary>
    public ushort GetField(int slotOffset, int localIdx, int fieldIndex)
    {
        return _data[fieldIndex * _capacity + slotOffset + localIdx];
    }

    /// <summary>
    /// 获取底层数据用于上传到 GPU。
    /// 长度 = capacity * stride 个 ushort。
    /// GPU 将其视为 StructuredBuffer&lt;uint&gt;，每 2 个 ushort = 1 个 uint。
    /// </summary>
    public ReadOnlySpan<ushort> GetData() => _data.AsSpan(0, _capacity * _stride);

    private void EnsureCapacity(int requiredSlots)
    {
        if (requiredSlots <= _capacity) return;

        int oldCapacity = _capacity;
        // 新容量：至少翻倍，保证偶数
        int newCapacity = Math.Max(oldCapacity * 2, requiredSlots);
        newCapacity = (newCapacity + 1) & ~1;

        var newData = new ushort[newCapacity * _stride];

        // SOA 搬运：逐 field 拷贝旧数据到新段位置
        for (int f = 0; f < _stride; f++)
        {
            Array.Copy(
                _data, f * oldCapacity,
                newData, f * newCapacity,
                oldCapacity
            );
        }

        _data = newData;
        _capacity = newCapacity;
        _fullUploadRequired = true;
    }

    public void Dispose()
    {
        _data = [];
        _slotCount = 0;
        _capacity = 0;
        _freeList.Clear();
    }
}
