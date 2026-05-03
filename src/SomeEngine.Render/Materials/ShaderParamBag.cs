using System;
using System.Collections.Generic;
using System.Numerics;
using Diligent;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 动态 shader 参数容器。存储贴图/Buffer 绑定与标量参数，按 string name 索引。
/// 材质参数运行时容器。
/// </summary>
public sealed class ShaderParamBag : IDisposable
{
    private readonly Dictionary<string, Entry> _entries = new();

    /// <summary>参数条目数。</summary>
    public int Count => _entries.Count;

    /// <summary>设置贴图视图绑定。</summary>
    public void Set(string name, ITextureView? view)
    {
        _entries[name] = new Entry(EntryKind.TextureView, view);
    }

    /// <summary>设置 Buffer 视图绑定。</summary>
    public void Set(string name, IBufferView? view)
    {
        _entries[name] = new Entry(EntryKind.BufferView, view);
    }

    /// <summary>设置 Buffer 绑定（非视图）。</summary>
    public void SetBuffer(string name, IBuffer? buffer)
    {
        _entries[name] = new Entry(EntryKind.Buffer, buffer);
    }

    /// <summary>设置 Sampler 绑定。</summary>
    public void Set(string name, ISampler? sampler)
    {
        _entries[name] = new Entry(EntryKind.Sampler, sampler);
    }

    /// <summary>设置标量参数（float）。</summary>
    public void SetScalar(string name, float value)
    {
        _entries[name] = new Entry(EntryKind.Scalar, value);
    }

    /// <summary>设置标量参数（int）。</summary>
    public void SetScalar(string name, int value)
    {
        _entries[name] = new Entry(EntryKind.Scalar, value);
    }

    /// <summary>设置标量参数（Vector4，也用于 Vec2/Vec3）。</summary>
    public void SetScalar(string name, Vector4 value)
    {
        _entries[name] = new Entry(EntryKind.Scalar, value);
    }

    /// <summary>获取标量参数值。</summary>
    public object? GetScalar(string name)
    {
        if (_entries.TryGetValue(name, out var entry) && entry.Kind == EntryKind.Scalar)
            return entry.Value;
        return null;
    }

    public IEnumerable<(string Name, object Value)> EnumerateScalars()
    {
        if (_entries.Count == 0)
            yield break;

        var sortedKeys = new string[_entries.Count];
        int idx = 0;
        foreach (string key in _entries.Keys)
            sortedKeys[idx++] = key;
        Array.Sort(sortedKeys, StringComparer.Ordinal);

        foreach (string name in sortedKeys)
        {
            var entry = _entries[name];
            if (entry.Kind == EntryKind.Scalar && entry.Value != null)
                yield return (name, entry.Value);
        }
    }

    /// <summary>移除绑定。</summary>
    public bool Remove(string name) => _entries.Remove(name);

    /// <summary>是否包含指定绑定。</summary>
    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>
    /// Enumerate resource binding entries (name + kind). Used by signature builders
    /// to discover material resources for PipelineResourceSignature creation.
    /// </summary>
    public IEnumerable<(string Name, ShaderResourceType Type)> EnumerateResources()
    {
        if (_entries.Count == 0)
            yield break;

        var sortedKeys = new string[_entries.Count];
        int idx = 0;
        foreach (var key in _entries.Keys)
            sortedKeys[idx++] = key;
        Array.Sort(sortedKeys, StringComparer.Ordinal);

        foreach (var name in sortedKeys)
        {
            var entry = _entries[name];
            var type = entry.Kind switch
            {
                EntryKind.TextureView => ShaderResourceType.TextureSrv,
                EntryKind.BufferView => ShaderResourceType.BufferSrv,
                EntryKind.Buffer => ShaderResourceType.ConstantBuffer,
                EntryKind.Sampler => ShaderResourceType.Sampler,
                _ => (ShaderResourceType?)null,
            };
            if (type.HasValue)
                yield return (name, type.Value);
        }
    }

    /// <summary>
    /// 计算只反映资源布局（名字 + 类型）的 hash。
    /// 与具体绑定值、标量值、插入顺序无关。
    /// 适用于 PipelineResourceSignature / SRB cache key。
    /// </summary>
    public ulong GetResourceLayoutHash()
    {
        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        foreach (var (name, type) in EnumerateResources())
        {
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            hash ^= (ulong)type;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>
    /// 只对指定资源名集合和全部标量参数计算签名。
    /// 适用于基于 Shader metadata 的按-pass 绑定签名。
    /// </summary>
    public ulong GetFilteredSignatureHash(IEnumerable<string> resourceNames, bool includeScalars = true)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in resourceNames)
        {
            if (seen.Add(name))
                names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);

        ulong hash = 14695981039346656037UL; // FNV-1a offset basis

        foreach (var name in names)
        {
            HashName(ref hash, name);

            if (_entries.TryGetValue(name, out var entry))
            {
                hash ^= (ulong)entry.Kind;
                hash *= 1099511628211UL;
                if (entry.Value != null)
                {
                    hash ^= (ulong)entry.Value.GetHashCode();
                    hash *= 1099511628211UL;
                }
            }
            else
            {
                // Preserve the declared binding slot in the hash even if this material
                // currently leaves it unset.
                hash ^= 255UL;
                hash *= 1099511628211UL;
            }
        }

        if (includeScalars)
        {
            var scalarKeys = new List<string>();
            foreach (var (name, entry) in _entries)
            {
                if (entry.Kind == EntryKind.Scalar)
                    scalarKeys.Add(name);
            }

            scalarKeys.Sort(StringComparer.Ordinal);
            foreach (var name in scalarKeys)
            {
                var entry = _entries[name];
                HashName(ref hash, name);
                hash ^= (ulong)entry.Kind;
                hash *= 1099511628211UL;
                if (entry.Value != null)
                {
                    hash ^= (ulong)entry.Value.GetHashCode();
                    hash *= 1099511628211UL;
                }
            }
        }

        return hash;
    }

    /// <summary>
    /// 将所有参数绑定到 SRB（Compute stage）。
    /// </summary>
    public void ApplyTo(IShaderResourceBinding srb, RenderContext context, ShaderAsset shaderAsset, ShaderType stage = ShaderType.Compute)
    {
        ApplyTo(srb, context, shaderAsset, stage, fallbacks: null, onFallback: null);
    }

    public void ApplyTo(
        IShaderResourceBinding srb,
        RenderContext context,
        ShaderAsset shaderAsset,
        ShaderType stage,
        MaterialResourceFallbacks? fallbacks,
        Action<string, ShaderResourceType>? onFallback = null)
    {
        if (fallbacks != null)
            ApplyFallbacks(shaderAsset, fallbacks, onFallback);

        foreach (var (name, entry) in _entries)
        {
            if (entry.Value == null) continue;

            var variable = srb.GetVariableByReflectedBinding(context, shaderAsset, stage, name);

            if (variable == null) continue;

            switch (entry.Kind)
            {
                case EntryKind.TextureView:
                    variable.Set((ITextureView)entry.Value, SetShaderResourceFlags.AllowOverwrite);
                    break;
                case EntryKind.BufferView:
                    variable.Set((IBufferView)entry.Value, SetShaderResourceFlags.AllowOverwrite);
                    break;
                case EntryKind.Buffer:
                    variable.Set((IBuffer)entry.Value, SetShaderResourceFlags.AllowOverwrite);
                    break;
                case EntryKind.Sampler:
                    variable.Set((ISampler)entry.Value, SetShaderResourceFlags.AllowOverwrite);
                    break;
            }
        }
    }

    public int ApplyFallbacks(
        ShaderAsset? shaderAsset,
        MaterialResourceFallbacks fallbacks,
        Action<string, ShaderResourceType>? onFallback = null)
    {
        if (fallbacks == null)
            throw new ArgumentNullException(nameof(fallbacks));

        int applied = 0;
        if (shaderAsset?.Metadata?.MaterialBindings == null)
            return applied;

        foreach (var binding in shaderAsset.Metadata.MaterialBindings)
        {
            string? name = binding.Name;
            if (string.IsNullOrWhiteSpace(name) || Contains(name))
                continue;

            switch (binding.ResourceType)
            {
                case MaterialBindingTexture:
                    ITextureView? texture = fallbacks.ResolveTexture(name);
                    if (texture == null)
                        continue;

                    Set(name, texture);
                    onFallback?.Invoke(name, ShaderResourceType.TextureSrv);
                    applied++;
                    break;
                case MaterialBindingSampler:
                    if (fallbacks.DefaultSampler == null)
                        continue;

                    Set(name, fallbacks.DefaultSampler);
                    onFallback?.Invoke(name, ShaderResourceType.Sampler);
                    applied++;
                    break;
                case MaterialBindingBuffer:
                    if (fallbacks.DefaultBufferView != null)
                    {
                        Set(name, fallbacks.DefaultBufferView);
                        onFallback?.Invoke(name, ShaderResourceType.BufferSrv);
                        applied++;
                    }
                    else if (fallbacks.DefaultConstantBuffer != null)
                    {
                        SetBuffer(name, fallbacks.DefaultConstantBuffer);
                        onFallback?.Invoke(name, ShaderResourceType.ConstantBuffer);
                        applied++;
                    }

                    break;
            }
        }

        return applied;
    }

    /// <summary>
    /// 计算绑定签名 hash。相同资源绑定会产生相同 hash，用于 BinQueue 去重。
    /// </summary>
    public ulong GetSignatureHash()
    {
        // Sort keys to ensure deterministic hash regardless of Dictionary enumeration order.
        int count = _entries.Count;
        if (count == 0) return 14695981039346656037UL;

        var sortedKeys = new string[count];
        int idx = 0;
        foreach (var key in _entries.Keys)
            sortedKeys[idx++] = key;
        Array.Sort(sortedKeys, StringComparer.Ordinal);

        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        for (int i = 0; i < count; i++)
        {
            string name = sortedKeys[i];
            var entry = _entries[name];

            foreach (char c in name)
            {
                hash ^= c;
                hash *= 1099511628211UL; // FNV-1a prime
            }
            hash ^= (ulong)entry.Kind;
            hash *= 1099511628211UL;
            if (entry.Value != null)
            {
                hash ^= (ulong)entry.Value.GetHashCode();
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }

    /// <summary>深拷贝。新容器与原容器共享底层 GPU 资源引用。</summary>
    public ShaderParamBag Clone()
    {
        var clone = new ShaderParamBag();
        foreach (var (name, entry) in _entries)
        {
            clone._entries[name] = entry;
        }
        return clone;
    }

    public void Dispose()
    {
        _entries.Clear();
    }

    private static void HashName(ref ulong hash, string name)
    {
        foreach (char c in name)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
    }

    // ── Internal ──

    private enum EntryKind : byte
    {
        TextureView,
        BufferView,
        Buffer,
        Sampler,
        Scalar,
    }

    private readonly record struct Entry(EntryKind Kind, object? Value);

    private const byte MaterialBindingTexture = 0;
    private const byte MaterialBindingSampler = 1;
    private const byte MaterialBindingBuffer = 2;
}
