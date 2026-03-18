using System;
using System.Collections.Generic;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 统一 bin 基础设施入口。管理动态字段 SlotBuffer + 内部 BinQueue + SlotCache。
/// <para>
/// 纯 CPU 数据 + GPU buffer 数据，不负责 GPU dispatch。
/// Feature 通过 RegisterField / RegisterRegion 注册，通过 GetRange / GetPass 查询。
/// </para>
/// </summary>
public sealed class BinSpace : IDisposable
{
    private readonly List<FieldInfo> _fields = new();
    private bool _frozen;
    private MaterialSlotBuffer? _buffer;
    private MaterialSlotCache? _cache;
    private int[] _slotOffsetByMaterialId = [];
    private uint _lastRegistryVersion = uint.MaxValue;

    public bool IsFrozen => _frozen;

    /// <summary>Stride（= 注册字段数）。FreezeLayout 后不可变。</summary>
    public int Stride => _fields.Count;

    /// <summary>SlotBuffer 已分配的 slot 数。</summary>
    public int SlotCount => _buffer?.SlotCount ?? 0;

    /// <summary>SlotBuffer 当前容量（偶数，GPU uniform 用）。</summary>
    public int SlotCapacity => _buffer?.Capacity ?? 0;

    // ── 字段注册 ──

    /// <summary>
    /// 注册一个 bin key 字段。返回 fieldIndex，用于后续 RegisterRegion / GetRange 调用。
    /// 必须在 FreezeLayout 之前调用。
    /// </summary>
    public int RegisterField(string name)
    {
        if (_frozen) throw new InvalidOperationException("Layout already frozen.");
        int index = _fields.Count;
        _fields.Add(new FieldInfo(name, new BinQueue()));
        return index;
    }

    /// <summary>冻结字段布局，创建 SlotBuffer 和 SlotCache。</summary>
    public void FreezeLayout()
    {
        if (_frozen) return;
        if (_fields.Count == 0) throw new InvalidOperationException("No fields registered.");
        _frozen = true;
        _buffer = new MaterialSlotBuffer(_fields.Count);
        _cache = new MaterialSlotCache(_buffer);
    }

    /// <summary>按名称查找 fieldIndex。</summary>
    public int GetFieldIndex(string name)
    {
        for (int i = 0; i < _fields.Count; i++)
            if (_fields[i].Name == name) return i;
        throw new KeyNotFoundException($"Field '{name}' not registered.");
    }

    // ── Region 注册 ──

    /// <summary>
    /// 在指定 field 的内部 BinQueue 上注册一个 region。
    /// </summary>
    public void RegisterRegion(int fieldIndex, string regionName,
        Func<MaterialPass[]> queryFunc,
        Func<MaterialPass, ulong> signatureFunc)
    {
        ValidateFieldIndex(fieldIndex);
        _fields[fieldIndex].BinQueue.RegisterRegion(regionName, queryFunc, signatureFunc);
    }

    // ── Slot 管理 ──

    /// <summary>分配 slot 区间（cache-aware）。返回 slotOffset。自动维护 MaterialID → slotOffset 映射。</summary>
    public int AllocateSlots(ReadOnlySpan<MaterialPass> passes)
    {
        EnsureFrozen();
        int offset = _cache!.GetOrAllocate(passes);

        // 维护 flat array 映射
        for (int i = 0; i < passes.Length; i++)
        {
            uint id = passes[i].MaterialID;
            if (id >= (uint)_slotOffsetByMaterialId.Length)
            {
                int newLen = Math.Max((int)(id + 1) * 2, 16);
                Array.Resize(ref _slotOffsetByMaterialId, newLen);
            }
            _slotOffsetByMaterialId[id] = offset + i;
        }
        return offset;
    }

    /// <summary>O(1) 查询 MaterialPass 对应的 slot offset。</summary>
    public int GetSlotOffset(uint materialId)
    {
        if (materialId >= (uint)_slotOffsetByMaterialId.Length)
            throw new ArgumentOutOfRangeException(nameof(materialId),
                $"MaterialID {materialId} not allocated in this BinSpace.");
        return _slotOffsetByMaterialId[materialId];
    }

    /// <summary>释放 slot 区间。</summary>
    public void ReleaseSlots(int offset)
    {
        _cache?.Release(offset);
    }

    // ── Rebuild ──

    /// <summary>
    /// 如果 MaterialRegistry 版本变化，重建所有 field 的 BinQueue 并 patch SlotBuffer。
    /// </summary>
    public void RebuildIfDirty(MaterialRegistry registry)
    {
        EnsureFrozen();
        uint currentVersion = registry.Version;
        if (currentVersion == _lastRegistryVersion) return;
        _lastRegistryVersion = currentVersion;

        for (int i = 0; i < _fields.Count; i++)
        {
            _fields[i].BinQueue.Rebuild();
            _cache!.RebuildField(i, _fields[i].BinQueue);
        }
    }

    /// <summary>强制重建所有 field（不检查版本）。</summary>
    public void ForceRebuild()
    {
        EnsureFrozen();
        for (int i = 0; i < _fields.Count; i++)
        {
            _fields[i].BinQueue.Rebuild();
            _cache!.RebuildField(i, _fields[i].BinQueue);
        }
    }

    // ── 查询 ──

    /// <summary>获取指定 field + region 的 bin 范围。</summary>
    public BinQueue.BinRange GetRange(int fieldIndex, string regionName)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.GetRange(regionName);
    }

    /// <summary>获取指定 field + bin index 的 MaterialPass。</summary>
    public MaterialPass GetPass(int fieldIndex, int binIndex)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.GetPass(binIndex);
    }

    /// <summary>获取指定 field 的总 bin 数。</summary>
    public int GetTotalBinCount(int fieldIndex)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.TotalBinCount;
    }

    /// <summary>反向查找 pass 在指定 field 中的 bin index。</summary>
    public ushort GetBinForPass(int fieldIndex, MaterialPass pass)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.GetBinForPass(pass);
    }

    // ── GPU 数据 ──

    /// <summary>获取底层 ushort 数据（用于上传到 GPU buffer）。</summary>
    public ReadOnlySpan<ushort> GetData()
    {
        EnsureFrozen();
        return _buffer!.GetData();
    }

    // ── 辅助 ──

    private void EnsureFrozen()
    {
        if (!_frozen) throw new InvalidOperationException("Must call FreezeLayout() first.");
    }

    private void ValidateFieldIndex(int fieldIndex)
    {
        if (fieldIndex < 0 || fieldIndex >= _fields.Count)
            throw new ArgumentOutOfRangeException(nameof(fieldIndex));
    }

    public void Dispose()
    {
        _cache?.Dispose();
        _buffer?.Dispose();
    }

    private class FieldInfo
    {
        public string Name;
        public BinQueue BinQueue;

        public FieldInfo(string name, BinQueue binQueue)
        {
            Name = name;
            BinQueue = binQueue;
        }
    }
}
