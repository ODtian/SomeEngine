using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 运行时 dispatch 单元。每个 MaterialPass 对应一个 bin + 一次 dispatch。
/// 由 Material resolve 生成，在 MaterialRegistry 中注册后分配 MaterialID。
/// </summary>
public class MaterialPass
{
    /// <summary>全局唯一 ID，由 MaterialRegistry 分配。</summary>
    public uint MaterialID { get; internal set; }

    /// <summary>回指所属 Material。</summary>
    public Material Owner { get; }

    /// <summary>
    /// 绑定参数。Phase 2 中直接共享 Material.Params；
    /// 后续 Phase 可以存储 resolve 后的子集。
    /// </summary>
    public ShaderParamBag Params { get; }

    /// <summary>关联的 ShaderAsset</summary>
    public SomeEngine.Assets.Schema.ShaderAsset? Shader { get; internal set; }

    internal MaterialPass(Material owner, ShaderParamBag? @params = null)
    {
        Owner = owner;
        Params = @params ?? owner.Params;
    }

    /// <summary>将材质参数绑定到指定 SRB。</summary>
    public void ApplyToSRB(IShaderResourceBinding srb)
    {
        Params.ApplyTo(srb);
    }

    /// <summary>计算包含 Shader 身份和材质参数的唯一签名</summary>
    public ulong ComputeSignature()
    {
        ulong shaderHash = Shader != null
            ? (ulong)Shader.Name.GetHashCode() : 0UL;
        return shaderHash ^ Params.GetSignatureHash();
    }
}
