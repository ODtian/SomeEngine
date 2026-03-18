using System.Numerics;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: 像素着色 — 合并 ShadeBin + MaterialShade 为统一入口。
/// 持有底层 pass 实例（PSO 状态），对 Pipeline 暴露单一调用。
/// </summary>
public class ClusterShade : IDisposable
{
    private readonly ClusterShadeBinStage _shadeBinStage;
    private readonly ClusterShadeStage _shadeStage;
    private bool _initialized;

    public ClusterShade(RenderContext context, MaterialRegistry registry)
    {
        _shadeBinStage = new ClusterShadeBinStage(context);
        _shadeStage = new ClusterShadeStage(context, registry);
    }

    public void SetMaterialShadePSO(IPipelineState? pso) => _shadeStage.SetMaterialShadePSO(pso);

    public void Init()
    {
        if (_initialized) return;
        _shadeBinStage.Init();
        _shadeStage.Init();
        _initialized = true;
    }

    /// <summary>
    /// ShadeBin + MaterialShade 合并的统一入口。
    /// </summary>
    public (ClusterShadeBinOutput ShadeBin, ClusterShadeOutput Shade) AddPasses(
        RenderGraph graph,
        in ClusterRasterOutput raster,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        RenderGraphHandle hDrawUniforms,
        RenderGraphHandle hMaterialSlotBuffer,
        RenderGraphHandle colorTarget,
        RenderGraphHandle depthTarget,
        BinSpace binSpace,
        int shadingBinFieldIndex,
        MaterialRegistry registry,
        in Matrix4x4 view,
        in Matrix4x4 proj,
        Vector3 cameraPos,
        uint pageTableSize,
        Vector3 quantOrigin,
        float quantStep,
        ClusterDebugMode debugMode,
        uint screenWidth,
        uint screenHeight
    )
    {
        if (!_initialized) Init();

        uint drawDebugMode = (uint)debugMode;
        bool isResolveOnlyDebug = drawDebugMode == 1 || drawDebugMode == 2;

        // ─── ShadeBin ───
        var shadeBinOut = _shadeBinStage.AddPasses(graph,
            raster, cull, globals,
            ClusterShadeBinConfig.Default(),
            hMaterialSlotBuffer,
            registry.MaterialCount,
            (uint)binSpace.SlotCapacity,
            (uint)shadingBinFieldIndex,
            screenWidth, screenHeight);

        // ─── MaterialShade ───
        var shadeConfig = ClusterShadeConfig.Default(colorTarget) with
        {
            DebugMode = drawDebugMode,
            UseResolveDebug = isResolveOnlyDebug,
            ViewProj = Matrix4x4.Transpose(view * proj),
            View = Matrix4x4.Transpose(view),
            PageTableSize = pageTableSize,
            QuantOrigin = quantOrigin,
            QuantStep = quantStep,
            CameraPos = cameraPos,
        };
        var shadeOut = _shadeStage.AddPasses(graph,
            raster, shadeBinOut, cull, globals, hDrawUniforms,
            shadeConfig, depthTarget, screenWidth, screenHeight);

        return (shadeBinOut, shadeOut);
    }

    public void Dispose()
    {
        _shadeBinStage.Dispose();
        _shadeStage.Dispose();
    }
}
