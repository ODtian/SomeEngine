using System;
using System.Collections.Generic;
using System.Linq;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 全局 Tag 索引。存储对象的标签，支持快速交集查询。
/// <para>
/// 设计要点：
/// - 每个 tag type 维护一个索引（HashSet）
/// - 交集查询遍历较小集合做过滤
/// - Version 属性在增删/tag 变更时递增
/// </para>
/// </summary>
public sealed class TagStore<T> where T : class
{
    private readonly HashSet<T> _items = new();
    private readonly Dictionary<Type, HashSet<T>> _tagIndex = new();
    private readonly Dictionary<(T, Type), object> _tagValues = new();

    /// <summary>增删/tag 变更时递增的版本号。</summary>
    public uint Version { get; private set; }

    /// <summary>已注册对象数。</summary>
    public int Count => _items.Count;

    /// <summary>注册一个对象。</summary>
    public void Register(T item)
    {
        if (_items.Add(item))
            Version++;
    }

    /// <summary>注销一个对象，同时移除其所有 tag。</summary>
    public void Unregister(T item)
    {
        if (!_items.Remove(item)) return;

        // Remove from all tag indexes
        foreach (var (tagType, set) in _tagIndex)
        {
            set.Remove(item);
        }

        // Remove tag values
        var keysToRemove = new List<(T, Type)>();
        foreach (var key in _tagValues.Keys)
        {
            if (ReferenceEquals(key.Item1, item))
                keysToRemove.Add(key);
        }
        foreach (var key in keysToRemove)
            _tagValues.Remove(key);

        Version++;
    }

    /// <summary>为对象设置标签。</summary>
    public void SetTag<TTag>(T item, TTag value = default!) where TTag : struct, IMaterialTag
    {
        if (!_items.Contains(item))
            throw new InvalidOperationException("Item not registered.");

        var type = typeof(TTag);
        if (!_tagIndex.TryGetValue(type, out var set))
        {
            set = new HashSet<T>();
            _tagIndex[type] = set;
        }
        set.Add(item);
        _tagValues[(item, type)] = value;
        Version++;
    }

    /// <summary>移除对象的标签。</summary>
    public bool RemoveTag<TTag>(T item) where TTag : struct, IMaterialTag
    {
        var type = typeof(TTag);
        bool removed = false;
        if (_tagIndex.TryGetValue(type, out var set))
            removed = set.Remove(item);
        removed |= _tagValues.Remove((item, type));
        if (removed) Version++;
        return removed;
    }

    /// <summary>获取对象的标签值。</summary>
    public TTag? GetTag<TTag>(T item) where TTag : struct, IMaterialTag
    {
        if (_tagValues.TryGetValue((item, typeof(TTag)), out var value))
            return (TTag)value;
        return null;
    }

    /// <summary>对象是否有指定标签。</summary>
    public bool HasTag<TTag>(T item) where TTag : struct, IMaterialTag
    {
        return _tagIndex.TryGetValue(typeof(TTag), out var set) && set.Contains(item);
    }

    /// <summary>查询具有指定标签的所有对象。</summary>
    public T[] Query<T1>() where T1 : struct, IMaterialTag
    {
        if (!_tagIndex.TryGetValue(typeof(T1), out var set))
            return [];
        return set.ToArray();
    }

    /// <summary>查询同时具有两个标签的所有对象（交集）。</summary>
    public T[] Query<T1, T2>() where T1 : struct, IMaterialTag where T2 : struct, IMaterialTag
    {
        if (!_tagIndex.TryGetValue(typeof(T1), out var set1))
            return [];
        if (!_tagIndex.TryGetValue(typeof(T2), out var set2))
            return [];

        // 遍历较小集合做过滤
        var (smaller, larger) = set1.Count <= set2.Count ? (set1, set2) : (set2, set1);
        var result = new List<T>();
        foreach (var item in smaller)
        {
            if (larger.Contains(item))
                result.Add(item);
        }
        return result.ToArray();
    }

    /// <summary>获取所有已注册对象。</summary>
    public T[] GetAll() => _items.ToArray();
}
