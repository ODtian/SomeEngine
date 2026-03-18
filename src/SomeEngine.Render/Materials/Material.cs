using System;
using Diligent;

namespace SomeEngine.Render.Materials;

/// <summary>
/// 材质资产层 + 运行时 source。替代旧 MaterialBase。
/// <para>
/// Material 持有 shader 参数（贴图/采样器），resolve 后生成 MaterialPass 列表。
/// Phase 2 每个 Material 只产生 1 个 MaterialPass。
/// </para>
/// </summary>
public class Material : IDisposable
{
    /// <summary>名称（调试/序列化用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>对应的 Slang struct 名称。</summary>
    public string ShaderAssetName { get; set; } = "";

    /// <summary>资产路径（序列化引用）。</summary>
    public string? AssetPath { get; set; }

    /// <summary>所有贴图/采样器参数。</summary>
    public ShaderParamBag Params { get; private set; } = new();

    /// <summary>resolve 后的 pass 列表。Phase 2 只有 1 个。</summary>
    private MaterialPass[]? _passes;

    /// <summary>获取 resolved passes。首次调用时自动 resolve。</summary>
    public ReadOnlySpan<MaterialPass> Passes
    {
        get
        {
            _passes ??= Resolve();
            return _passes;
        }
    }

    /// <summary>
    /// 克隆此材质。新实例 Params 独立副本，共享底层 GPU 资源引用。
    /// </summary>
    public Material Instantiate()
    {
        return new Material
        {
            Name = Name + " (Instance)",
            ShaderAssetName = ShaderAssetName,
            AssetPath = AssetPath,
            Params = Params.Clone(),
        };
    }

    /// <summary>设置贴图参数。</summary>
    public void SetTexture(string name, ITextureView? view)
    {
        Params.Set(name, view);
        InvalidateResolvedPasses();
    }

    /// <summary>设置采样器参数。</summary>
    public void SetSampler(string name, ISampler? sampler)
    {
        Params.Set(name, sampler);
        InvalidateResolvedPasses();
    }

    /// <summary>设置 Buffer 参数。</summary>
    public void SetBuffer(string name, IBufferView? view)
    {
        Params.Set(name, view);
        InvalidateResolvedPasses();
    }

    /// <summary>强制重新 resolve passes。</summary>
    public void InvalidateResolvedPasses()
    {
        _passes = null;
    }

    /// <summary>
    /// 追加一个 pass（用于多 pass 材质，如 overlay）。
    /// 必须在首次访问 Passes 之后调用（以确保 primary pass 已 resolve）。
    /// </summary>
    public MaterialPass AddPass(ShaderParamBag? @params = null)
    {
        // 确保 primary pass 已 resolve
        _ = Passes;

        var pass = new MaterialPass(this, @params ?? new ShaderParamBag());
        var list = new MaterialPass[_passes!.Length + 1];
        _passes.CopyTo(list, 0);
        list[^1] = pass;
        _passes = list;
        return pass;
    }

    private MaterialPass[] Resolve()
    {
        // 默认生成 1 个 primary MaterialPass，共享 Params
        return [new MaterialPass(this, Params)];
    }

    public void Dispose()
    {
        Params.Dispose();
    }
}
