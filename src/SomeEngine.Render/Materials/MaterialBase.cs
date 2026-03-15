using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 材质基类。实现 IShaderParams — 材质本身就是一个参数容器。
/// <para>
/// 参数绑定流程（源生成器自动生成 ApplyToSRB）：
/// 1. base.ApplyToSRB(srb) — 继承链
/// 2. 自身 [ShaderParam] 资源/标量字段 — 直接绑定
/// 3. IShaderParams 类型字段 — field.ApplyToSRB(srb)（params 组合）
/// </para>
/// </summary>
public abstract partial class MaterialBase : IShaderParams, IDisposable
{
    /// <summary>全局唯一 ID，由 MaterialRegistry 分配。</summary>
    public uint MaterialID { get; internal set; }

    /// <summary>对应的 Slang struct 名称（用于泛型特化编译）。</summary>
    public abstract string SlangStructName { get; }

    /// <summary>关联的 ShaderType（PSO 缓存）。</summary>
    public MaterialShaderType ShaderType { get; internal set; } = null!;

    /// <summary>独立的 Shader Resource Binding。</summary>
    public IShaderResourceBinding SRB { get; internal set; } = null!;

    /// <summary>语义标签集合。</summary>
    public MaterialTagSet Tags { get; } = new();

    /// <summary>名称（调试/序列化用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>资产路径（序列化引用）。</summary>
    public string? AssetPath { get; set; }

    /// <summary>
    /// 将所有参数绑定到指定 SRB。
    /// 源生成器自动实现，处理：继承链 + 自身字段 + 组合的 IShaderParams 字段。
    /// </summary>
    public virtual void ApplyToSRB(IShaderResourceBinding srb) { }

    /// <summary>提交绑定到自身 SRB（语法糖）。</summary>
    public void CommitBindings() => ApplyToSRB(SRB);

    public void Dispose()
    {
        SRB?.Dispose();
    }
}
