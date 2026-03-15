namespace SomeEngine.Render.Materials;

/// <summary>
/// 材质语义标签 marker 接口。
/// 每个标签定义为实现此接口的 struct，以 Type 为 identity。
/// 支持纯 marker（无值）和带值标签（如 StencilRefTag）。
/// 各管线/系统可独立扩展自己的标签类型。
/// </summary>
public interface IMaterialTag { }

/// <summary>
/// 标签集合。内部用 Dictionary&lt;Type, IMaterialTag&gt; 存储。
/// struct 标签存入时会有一次装箱，但标签是低频操作可接受。
/// </summary>
public sealed class MaterialTagSet
{
    private readonly Dictionary<Type, IMaterialTag> _tags = new();

    /// <summary>设置带值标签。</summary>
    public void Set<T>(T tag) where T : struct, IMaterialTag => _tags[typeof(T)] = tag;

    /// <summary>添加无值 marker 标签。</summary>
    public void Add<T>() where T : struct, IMaterialTag => _tags[typeof(T)] = new T();

    /// <summary>获取标签值（不存在返回 default）。</summary>
    public T Get<T>() where T : struct, IMaterialTag
        => _tags.TryGetValue(typeof(T), out var t) ? (T)t : default;

    /// <summary>是否有此标签。</summary>
    public bool Has<T>() where T : struct, IMaterialTag => _tags.ContainsKey(typeof(T));

    /// <summary>按 Type 查询是否有此标签。</summary>
    public bool Has(Type tagType) => _tags.ContainsKey(tagType);

    /// <summary>移除标签。</summary>
    public void Remove<T>() where T : struct, IMaterialTag => _tags.Remove(typeof(T));

    /// <summary>标签数量。</summary>
    public int Count => _tags.Count;
}

// ── 核心标签（渲染层通用）──

/// <summary>不透明。</summary>
public struct OpaqueTag : IMaterialTag { }

/// <summary>Alpha Test / Masked。</summary>
public struct MaskedTag : IMaterialTag { }

/// <summary>半透明。</summary>
public struct TranslucentTag : IMaterialTag { }

/// <summary>双面渲染。</summary>
public struct TwoSidedTag : IMaterialTag { }

/// <summary>Stencil 参考值。</summary>
public struct StencilRefTag : IMaterialTag
{
    public byte Value;
    public StencilRefTag(byte value) => Value = value;
}
