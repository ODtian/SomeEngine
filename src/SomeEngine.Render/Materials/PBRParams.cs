using System.Numerics;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 标准 PBR 参数块。可复用的 shader 参数容器。
/// 对应 Slang 端 PBRBase struct 的字段。
/// </summary>
public partial class PBRParams : IShaderParams
{
    [ShaderParam]
    public TextureSlot AlbedoMap;

    [ShaderParam]
    public TextureSlot NormalMap;

    [ShaderParam]
    public TextureSlot ARMMap;

    [ShaderParam("MaterialSampler")]
    public SamplerSlot Sampler;

    [ShaderParam("tint")]
    public Vector4 BaseColorTint = Vector4.One;
}
