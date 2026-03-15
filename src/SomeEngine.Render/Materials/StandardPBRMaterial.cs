namespace SomeEngine.Render.Materials;

/// <summary>
/// 标准 PBR 材质。对应 Slang 端 StandardPBRMaterial : ISurfaceEvaluate。
/// 通过组合 PBRParams 块承载 PBR 参数。
/// </summary>
public partial class StandardPBRMaterial : MaterialBase
{
    public override string SlangStructName => "StandardPBRMaterial";

    /// <summary>PBR 参数块（组合）。</summary>
    public PBRParams PBR { get; } = new();
}
