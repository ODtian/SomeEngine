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
        ulong hash = ComputeHash(entities);

        int existingIndex = FindCacheEntry(hash, entities);
        if (existingIndex >= 0)
        {
            var entry = _cache[existingIndex];
            entry.RefCount++;
            _cache[existingIndex] = entry;
            return entry.Offset;
        }

        int offset = _buffer.AllocateRange(entities.Length);

        var storedEntities = new Entity[entities.Length];
        entities.CopyTo(storedEntities);

        _cache.Add(new CacheEntry(hash, offset, storedEntities, 1));
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
            _buffer.FreeRange(offset, entry.Entities.Length);
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
            for (int i = 0; i < entry.Entities.Length; i++)
            {
                ushort binKey;
                try
                {
                    binKey = binQueue.GetBinForEntity(entry.Entities[i]);
                }
                catch (KeyNotFoundException)
                {
                    binKey = 0;
                }

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

    private static ulong ComputeHash(ReadOnlySpan<Entity> entities)
    {
        ulong hash = 14695981039346656037UL; // FNV-1a
        foreach (var entity in entities)
        {
            hash ^= (ulong)MaterialEntityUtility.ComputeSlotSignature(entity);
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private int FindCacheEntry(ulong hash, ReadOnlySpan<Entity> entities)
    {
        for (int i = 0; i < _cache.Count; i++)
        {
            if (_cache[i].Hash != hash)
            {
                continue;
            }

            if (Matches(_cache[i].Entities, entities))
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

    private static bool Matches(Entity[] left, ReadOnlySpan<Entity> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i].Id != right[i].Id)
            {
                return false;
            }
        }

        return true;
    }

    private class CacheEntry
    {
        public ulong Hash;
        public int Offset;
        public Entity[] Entities;
        public int RefCount;

        public CacheEntry(ulong hash, int offset, Entity[] entities, int refCount)
        {
            Hash = hash;
            Offset = offset;
            Entities = entities;
            RefCount = refCount;
        }
    }
}
