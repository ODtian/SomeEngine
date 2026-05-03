using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace SomeEngine.Render.Materials;

/// <summary>
/// Slot 区间共享缓存。相同 Entity 组合共享同一段 SlotBuffer 区间。
/// </summary>
public sealed class MaterialSlotCache : IDisposable
{
    private readonly MaterialSlotBuffer _buffer;
    private readonly List<CacheEntry> _cache = [];

    public MaterialSlotCache(MaterialSlotBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>已缓存的唯一 slot 组合数。</summary>
    public int UniqueCount => _cache.Count;

    /// <summary>
    /// 获取或分配 slot 区间。相同 Entity 组合共享同一 offset。
    /// </summary>
    public int GetOrAllocate(ReadOnlySpan<Entity> entities)
    {
        MaterialSlotBinding[] bindings = new MaterialSlotBinding[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            bindings[i] = new MaterialSlotBinding(entities[i], [entities[i]]);
        }

        return GetOrAllocate(bindings);
    }

    /// <summary>
    /// 获取或分配 slot 区间。每个 local slot 可为不同 field 指向不同实体。
    /// </summary>
    public int GetOrAllocate(ReadOnlySpan<MaterialSlotBinding> bindings)
    {
        ulong hash = ComputeHash(bindings);

        int existingIndex = FindCacheEntry(hash, bindings);
        if (existingIndex >= 0)
        {
            var entry = _cache[existingIndex];
            entry.RefCount++;
            _cache[existingIndex] = entry;
            return entry.Offset;
        }

        int offset = _buffer.AllocateRange(bindings.Length);

        var storedBindings = new MaterialSlotBinding[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            Entity[] fieldEntities = new Entity[bindings[i].FieldEntities.Length];
            bindings[i].FieldEntities.CopyTo(fieldEntities, 0);
            storedBindings[i] = new MaterialSlotBinding(bindings[i].RepresentativeEntity, fieldEntities);
        }

        _cache.Add(new CacheEntry(hash, offset, storedBindings, 1));
        return offset;
    }

    /// <summary>释放引用。refcount 归零时释放 buffer 空间。</summary>
    public void Release(int offset)
    {
        int index = FindCacheEntryByOffset(offset);
        if (index < 0)
        {
            return;
        }

        var entry = _cache[index];
        entry.RefCount--;
        if (entry.RefCount <= 0)
        {
            _buffer.FreeRange(offset, entry.Bindings.Length);
            _cache.RemoveAt(index);
        }
        else
        {
            _cache[index] = entry;
        }
    }

    /// <summary>
    /// Bin rebuild 后，为指定 field 重写所有缓存区间的 bin key。
    /// </summary>
    public void RebuildField(int fieldIndex, BinQueue binQueue)
    {
        for (int cacheIndex = 0; cacheIndex < _cache.Count; cacheIndex++)
        {
            var entry = _cache[cacheIndex];
            for (int i = 0; i < entry.Bindings.Length; i++)
            {
                Entity entity = fieldIndex < entry.Bindings[i].FieldEntities.Length
                    ? entry.Bindings[i].FieldEntities[fieldIndex]
                    : entry.Bindings[i].RepresentativeEntity;
                ushort binKey = binQueue.TryGetBinForEntity(entity, out ushort foundBin)
                    ? foundBin
                    : (ushort)0;

                if (_buffer.GetField(entry.Offset, i, fieldIndex) != binKey)
                {
                    _buffer.SetField(entry.Offset, i, fieldIndex, binKey);
                }
            }
        }
    }

    /// <summary>获取引用计数（测试用）。</summary>
    public int GetRefCount(int offset)
    {
        int index = FindCacheEntryByOffset(offset);
        return index >= 0 ? _cache[index].RefCount : 0;
    }

    public void Dispose()
    {
        _cache.Clear();
    }

    internal static ulong ComputeHash(ReadOnlySpan<MaterialSlotBinding> bindings)
    {
        ulong hash = 14695981039346656037UL; // FNV-1a
        foreach (MaterialSlotBinding binding in bindings)
        {
            hash ^= (ulong)binding.RepresentativeEntity.Id;
            hash *= 1099511628211UL;
            foreach (Entity entity in binding.FieldEntities)
            {
                hash ^= entity.IsNull ? 0UL : (ulong)entity.Id;
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }

    private int FindCacheEntry(ulong hash, ReadOnlySpan<MaterialSlotBinding> bindings)
    {
        for (int i = 0; i < _cache.Count; i++)
        {
            if (_cache[i].Hash != hash)
            {
                continue;
            }

            if (Matches(_cache[i].Bindings, bindings))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindCacheEntryByOffset(int offset)
    {
        for (int i = 0; i < _cache.Count; i++)
        {
            if (_cache[i].Offset == offset)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Matches(MaterialSlotBinding[] left, ReadOnlySpan<MaterialSlotBinding> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i].RepresentativeEntity.Id != right[i].RepresentativeEntity.Id)
            {
                return false;
            }

            if (left[i].FieldEntities.Length != right[i].FieldEntities.Length)
            {
                return false;
            }

            for (int fieldIndex = 0; fieldIndex < left[i].FieldEntities.Length; fieldIndex++)
            {
                if (left[i].FieldEntities[fieldIndex].Id != right[i].FieldEntities[fieldIndex].Id)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private class CacheEntry
    {
        public ulong Hash;
        public int Offset;
        public MaterialSlotBinding[] Bindings;
        public int RefCount;

        public CacheEntry(ulong hash, int offset, MaterialSlotBinding[] bindings, int refCount)
        {
            Hash = hash;
            Offset = offset;
            Bindings = bindings;
            RefCount = refCount;
        }
    }
}
