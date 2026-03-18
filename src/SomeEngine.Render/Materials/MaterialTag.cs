namespace SomeEngine.Render.Materials;

/// <summary>
/// 标记 IMaterialTag struct 的序列化名称。
/// 源生成器扫描此特性生成 string→struct 反序列化映射。
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class MaterialTagAttribute(string name) : Attribute
{
    /// <summary>序列化名称（如 "opaque", "masked"）。</summary>
    public string Name { get; } = name;
}

/// <summary>语义标签接口。所有材质 tag 必须实现此接口。</summary>
public interface IMaterialTag { }

// ─── 基础渲染通道 Tag ───

/// <summary>不透明渲染。</summary>
[MaterialTag("opaque")]
public struct OpaqueTag : IMaterialTag { }

/// <summary>Alpha 裁剪渲染。</summary>
[MaterialTag("masked")]
public struct MaskedTag : IMaterialTag { }

/// <summary>半透明渲染。</summary>
[MaterialTag("translucent")]
public struct TranslucentTag : IMaterialTag { }

/// <summary>双面渲染。</summary>
[MaterialTag("two_sided")]
public struct TwoSidedTag : IMaterialTag { }

/// <summary>模板参考值。</summary>
[MaterialTag("stencil_ref")]
public struct StencilRefTag : IMaterialTag
{
    public byte Value;
}

// ─── 管线兼容性 Tag（不序列化，自动推导） ───

/// <summary>支持 Cluster shader 管线。</summary>
public struct ClusterShaderTag : IMaterialTag { }

/// <summary>支持 Forward 管线。</summary>
public struct ForwardShaderTag : IMaterialTag { }

// ─── 多 Pass Tag ───

/// <summary>投射阴影。</summary>
[MaterialTag("shadow_caster")]
public struct ShadowCasterTag : IMaterialTag { }

/// <summary>
/// 标记 primary pass 拥有 overlay pass。自动推导，不序列化。
/// </summary>
public struct MultiPassTag : IMaterialTag
{
    /// <summary>该材质的 overlay pass 数量。</summary>
    public byte OverlayCount;
}

/// <summary>
/// 标记 overlay pass。包含层序和回指 primary pass 的引用。自动推导，不序列化。
/// </summary>
public struct OverlayTag : IMaterialTag
{
    /// <summary>在 overlay 序列中的索引（0-based）。</summary>
    public byte LayerIndex;

    /// <summary>此 overlay 对应的 primary pass 引用。</summary>
    public MaterialPass? PrimaryPass;
}
