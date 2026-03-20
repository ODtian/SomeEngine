using Diligent;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 一组共享同一 PSO 的 shade bins。Feature 持有，rebuild 时构建。
/// BinQueue 保证同 ShaderAsset 的 bin 在 region 内连续，所以用 BinStart + BinCount。
/// </summary>
public struct ShadePSOGroup
{
    public IPipelineState PSO;
    public IShaderResourceBinding[] SRBs;  // per-bin within this group
    public MaterialPass[] Passes;           // per-bin, rebuild 时绑定材质参数
    public int BinStart;
    public int BinCount;
}
