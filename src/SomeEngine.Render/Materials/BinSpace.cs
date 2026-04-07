using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 统一 bin 基础设施入口。管理动态字段 SlotBuffer + 内部 BinQueue + SlotCache。
/// <para>
/// 纯 CPU 数据 + GPU buffer 数据，不负责 GPU dispatch。
/// Feature 通过 RegisterField / RegisterGroup 注册，通过 GetRanges / GetEntity 查询。
/// </para>
/// </summary>
public sealed class BinSpace : IDisposable
{
    private readonly List<FieldInfo> _fields = new();
    private bool _frozen;
    private MaterialSlotBuffer? _buffer;
    private MaterialSlotCache? _cache;
    private readonly List<EntitySlotEntry> _slotOffsets = [];
    private uint _version;

    public uint Version => _version;

    public MaterialSlotBuffer? SlotBuffer => _buffer;

    public bool IsFrozen => _frozen;

    /// <summary>Stride（= 注册字段数）。FreezeLayout 后不可变。</summary>
    public int Stride => _fields.Count;

    /// <summary>SlotBuffer 已分配的 slot 数。</summary>
    public int SlotCount => _buffer?.SlotCount ?? 0;

    /// <summary>SlotBuffer 当前容量（偶数，GPU uniform 用）。</summary>
    public int SlotCapacity => _buffer?.Capacity ?? 0;

    // ── 字段注册 ──

    /// <summary>
    /// 注册一个 bin key 字段。返回 fieldIndex，用于后续 RegisterGroup / GetRanges 调用。
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

    // ── Group 注册 ──

    public void RegisterGroup(int fieldIndex, BinQueue.BinGroup group)
    {
        ValidateFieldIndex(fieldIndex);
        _fields[fieldIndex].BinQueue.RegisterGroup(group);
    }

    // ── Slot 管理 ──

    public int AllocateSlots(ReadOnlySpan<Entity> entities)
    {
        EnsureFrozen();
        int offset = _cache!.GetOrAllocate(entities);

        for (int i = 0; i < entities.Length; i++)
        {
            AddOrReplaceSlotOffset(entities[i].Id, offset + i);
        }

        return offset;
    }

    public int GetSlotOffset(Entity entity)
    {
        for (int i = 0; i < _slotOffsets.Count; i++)
        {
            if (_slotOffsets[i].EntityId == entity.Id)
            {
                return _slotOffsets[i].Offset;
            }
        }

        throw new KeyNotFoundException($"Entity {entity.Id} not allocated in this BinSpace.");
    }

    /// <summary>释放 slot 区间。</summary>
    public void ReleaseSlots(int offset)
    {
        _cache?.Release(offset);
    }

    // ── Rebuild ──

    public void RebuildIfDirty()
    {
        EnsureFrozen();
        ForceRebuild();
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
        _version++;
    }

    // ── 查询 ──

    public BinQueue.BinRange[] GetRanges(int fieldIndex)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.GetRanges();
    }

    public Entity GetEntity(int fieldIndex, int binIndex)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.GetEntity(binIndex);
    }

    /// <summary>获取指定 field 的总 bin 数。</summary>
    public int GetTotalBinCount(int fieldIndex)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.TotalBinCount;
    }

    public ushort GetBinForEntity(int fieldIndex, Entity entity)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.GetBinForEntity(entity);
    }

    public int GetArgsBin(int fieldIndex, int binIndex)
    {
        ValidateFieldIndex(fieldIndex);
        return _fields[fieldIndex].BinQueue.GetArgsBin(binIndex);
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
        _slotOffsets.Clear();
    }

    private void AddOrReplaceSlotOffset(int entityId, int offset)
    {
        for (int i = 0; i < _slotOffsets.Count; i++)
        {
            if (_slotOffsets[i].EntityId == entityId)
            {
                _slotOffsets[i] = new EntitySlotEntry(entityId, offset);
                return;
            }
        }

        _slotOffsets.Add(new EntitySlotEntry(entityId, offset));
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

    private readonly record struct EntitySlotEntry(int EntityId, int Offset);
}
