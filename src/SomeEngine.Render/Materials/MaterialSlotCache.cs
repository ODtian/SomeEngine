using System;
using System.Collections.Generic;

namespace SomeEngine.Render.Materials;

/// <summary>
/// Slot 区间共享缓存。相同 MaterialPass 组合的 instance 共享同一段 SlotBuffer 区间。
/// 存储 pass 列表以支持 bin rebuild 后 patch。
/// </summary>
public sealed class MaterialSlotCache : IDisposable
{
    private readonly MaterialSlotBuffer _buffer;
    private readonly Dictionary<ulong, CacheEntry> _cache = new();
    private readonly Dictionary<int, ulong> _offsetToHash = new();

    public MaterialSlotCache(MaterialSlotBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>已缓存的唯一 slot 组合数。</summary>
    public int UniqueCount => _cache.Count;

    /// <summary>
    /// 获取或分配 slot 区间。相同 pass 组合共享同一 offset。
    /// </summary>
    public int GetOrAllocate(ReadOnlySpan<MaterialPass> passes)
    {
        ulong hash = ComputeHash(passes);

        if (_cache.TryGetValue(hash, out var entry))
        {
            entry.RefCount++;
            _cache[hash] = entry;
            return entry.Offset;
        }

        int offset = _buffer.AllocateRange(passes.Length);

        // 存储 pass 列表用于后续 RebuildField
        var storedPasses = new MaterialPass[passes.Length];
        passes.CopyTo(storedPasses);

        _cache[hash] = new CacheEntry(offset, storedPasses, 1);
        _offsetToHash[offset] = hash;
        return offset;
    }

    /// <summary>释放引用。refcount 归零时释放 buffer 空间。</summary>
    public void Release(int offset)
    {
        if (!_offsetToHash.TryGetValue(offset, out ulong hash)) return;
        if (!_cache.TryGetValue(hash, out var entry)) return;

        entry.RefCount--;
        if (entry.RefCount <= 0)
        {
            _buffer.FreeRange(offset, entry.Passes.Length);
            _cache.Remove(hash);
            _offsetToHash.Remove(offset);
        }
        else
        {
            _cache[hash] = entry;
        }
    }

    /// <summary>
    /// Bin rebuild 后，为指定 field 重写所有缓存区间的 bin key。
    /// </summary>
    public void RebuildField(int fieldIndex, BinQueue binQueue)
    {
        foreach (var (_, entry) in _cache)
        {
            for (int i = 0; i < entry.Passes.Length; i++)
            {
                ushort binKey;
                try
                {
                    binKey = binQueue.GetBinForPass(entry.Passes[i]);
                }
                catch (KeyNotFoundException)
                {
                    binKey = 0; // pass 不在此 BinQueue 中
                }

                _buffer.SetField(entry.Offset, i, fieldIndex, binKey);
            }
        }
    }

    /// <summary>获取引用计数（测试用）。</summary>
    public int GetRefCount(int offset)
    {
        if (!_offsetToHash.TryGetValue(offset, out ulong hash)) return 0;
        return _cache.TryGetValue(hash, out var entry) ? entry.RefCount : 0;
    }

    public void Dispose()
    {
        _cache.Clear();
        _offsetToHash.Clear();
    }

    private static ulong ComputeHash(ReadOnlySpan<MaterialPass> passes)
    {
        ulong hash = 14695981039346656037UL; // FNV-1a
        foreach (var pass in passes)
        {
            hash ^= pass.MaterialID;
            hash *= 1099511628211UL;
            hash ^= (ulong)pass.Params.GetSignatureHash();
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private class CacheEntry
    {
        public int Offset;
        public MaterialPass[] Passes;
        public int RefCount;

        public CacheEntry(int offset, MaterialPass[] passes, int refCount)
        {
            Offset = offset;
            Passes = passes;
            RefCount = refCount;
        }
    }
}
