using System;
using System.Collections.Generic;
using System.Numerics;
using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 动态 shader 参数容器。存储贴图/Buffer/Sampler 绑定，按 string name 索引。
/// 替代旧的 IShaderParams + 源生成器 ApplyToSRB() 模式（用于材质参数）。
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

    /// <summary>设置采样器绑定。</summary>
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

    /// <summary>移除绑定。</summary>
    public bool Remove(string name) => _entries.Remove(name);

    /// <summary>是否包含指定绑定。</summary>
    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>
    /// 将所有参数绑定到 SRB（Compute stage）。
    /// </summary>
    public void ApplyTo(IShaderResourceBinding srb, ShaderType stage = ShaderType.Compute)
    {
        foreach (var (name, entry) in _entries)
        {
            if (entry.Value == null) continue;

            var variable = srb.GetVariableByName(stage, name);
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

    /// <summary>
    /// 计算绑定签名 hash。相同资源绑定的 MaterialPass 会产生相同 hash，
    /// 用于 BinQueue 去重。
    /// </summary>
    public ulong GetSignatureHash()
    {
        ulong hash = 14695981039346656037UL; // FNV-1a offset basis
        foreach (var (name, entry) in _entries)
        {
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
}
