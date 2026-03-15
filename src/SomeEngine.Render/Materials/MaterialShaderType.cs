using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>Pass key marker 接口。各管线独立扩展自己的 pass key。</summary>
public interface IPassKey { }

/// <summary>默认/主着色 pass。</summary>
public struct MainPassKey : IPassKey { }

/// <summary>
/// 材质着色器类型 = 一组 PSO（按 Pass 分）。
/// 与 Slang struct 1:1 对应，由 MaterialRegistry 在首次注册时创建。
/// 多个 Material 实例共享同一个 MaterialShaderType。
/// </summary>
public sealed class MaterialShaderType : IDisposable
{
    /// <summary>名称（调试用）。</summary>
    public string Name { get; }

    /// <summary>对应的 Slang struct 名称。</summary>
    public string SlangStructName { get; }

    private readonly Dictionary<Type, IPipelineState> _passes = new();

    public MaterialShaderType(string name, string slangStructName, IPipelineState mainPSO)
    {
        Name = name;
        SlangStructName = slangStructName;
        _passes[typeof(MainPassKey)] = mainPSO;
    }

    /// <summary>注册额外的 shader pass PSO（如 ShadowCaster、MotionVector）。</summary>
    public void RegisterPass<TPass>(IPipelineState pso) where TPass : struct, IPassKey
        => _passes[typeof(TPass)] = pso;

    /// <summary>获取指定 pass 的 PSO。</summary>
    public IPipelineState? GetPSO<TPass>() where TPass : struct, IPassKey
        => _passes.GetValueOrDefault(typeof(TPass));

    /// <summary>主 PSO 便捷访问。</summary>
    public IPipelineState PSO => _passes[typeof(MainPassKey)];

    /// <summary>所有已注册的 pass key 类型。</summary>
    public IReadOnlyCollection<Type> PassKeys => _passes.Keys;

    /// <summary>为一个新 Material 实例创建独立 SRB（基于主 PSO）。</summary>
    public IShaderResourceBinding CreateSRB()
        => PSO.CreateShaderResourceBinding(false);

    /// <summary>为指定 Pass 创建 SRB。</summary>
    public IShaderResourceBinding CreateSRB<TPass>() where TPass : struct, IPassKey
        => _passes[typeof(TPass)].CreateShaderResourceBinding(false);

    public void Dispose()
    {
        foreach (var pso in _passes.Values)
            pso.Dispose();
    }
}
