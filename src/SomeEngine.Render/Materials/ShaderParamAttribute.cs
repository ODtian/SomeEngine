using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 标记字段对应的 shader 资源绑定参数。
/// 源生成器扫描此标注生成 ApplyToSRB() 中的绑定代码。
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class ShaderParamAttribute : Attribute
{
    /// <summary>Shader 中的资源名。null 时使用字段名。</summary>
    public string? Name { get; }

    /// <summary>目标 shader 阶段。默认 Compute。</summary>
    public ShaderType Stage { get; set; } = ShaderType.Compute;

    /// <summary>
    /// false = Mutable 变量（默认，需 AllowOverwrite 标记才能重绑）。
    /// true = Dynamic 变量（每帧可自由重绑，无需 AllowOverwrite）。
    /// </summary>
    public bool Dynamic { get; set; } = false;

    public ShaderParamAttribute(string? name = null) => Name = name;
}
