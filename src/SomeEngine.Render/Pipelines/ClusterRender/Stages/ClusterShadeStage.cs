using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: Material Shade — 基于 ShadeBin 结果执行按材质着色。
/// </summary>
public class ClusterShadeStage : IDisposable
{
    private readonly RenderContext _context;
    private readonly MaterialRegistry _registry;
    private ClusterMaterialShadePass? _materialShadePass;
    private ClusterResolvePass? _resolvePass;
    private bool _initialized;

    public ClusterShadeStage(RenderContext context, MaterialRegistry registry)
    {
        _context = context;
        _registry = registry;
    }

    public void Init()
    {
        if (_initialized) return;
        _materialShadePass = new ClusterMaterialShadePass(_context, _registry);
        _resolvePass = new ClusterResolvePass(_context);
        _resolvePass.Init();
        _initialized = true;
    }

    /// <summary>
    /// 添加 Shade pass，返回着色结果。
    /// </summary>
    public ClusterShadeOutput AddPasses(
        RenderGraph graph,
        in ClusterRasterOutput raster,
        in ClusterShadeBinOutput shadeBin,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        in ClusterShadeConfig config,
        RenderGraphHandle depthTarget,
        uint screenWidth,
        uint screenHeight
    )
    {
        if (!_initialized) Init();

        // ─── Shade uniforms ───
        var hShadeUniforms = graph.CreateBuffer("ShadeUniforms", new BufferDesc
        {
            Size = 256,
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // ─── Resolve target ───
        var hResolveTarget = config.OutputColor.IsValid
            ? config.OutputColor
            : graph.CreateTexture("ResolveTarget", new TextureDesc
            {
                Type = ResourceDimension.Tex2d,
                Width = screenWidth,
                Height = screenHeight,
                MipLevels = 1,
                Format = TextureFormat.RGBA8_UNorm,
                Usage = Usage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.RenderTarget,
            });

        // ─── Check for resolve-only debug ───
        bool isResolveOnlyDebug = config.DebugMode == 1 || config.DebugMode == 2;
        if (config.UseResolveDebug || isResolveOnlyDebug)
        {
            if (_resolvePass != null)
            {
                _resolvePass.HVisBuffer = raster.VisBuffer;
                _resolvePass.HDepthTarget = depthTarget;
                _resolvePass.HVisibleClusters = cull.VisibleClusters;
                _resolvePass.HPageHeap = globals.PageHeap;
                _resolvePass.HGlobalTransformBuffer = globals.GlobalTransform;
                _resolvePass.HDrawUniforms = globals.DrawUniforms;
                _resolvePass.HColorTarget = hResolveTarget;
                graph.AddPass(_resolvePass);
            }

            AddCopyToBackBuffer(graph, hResolveTarget, config);
            return new ClusterShadeOutput(hResolveTarget);
        }

        // ─── Upload shade uniforms ───
        var shadeUniformData = new ShadeUniforms
        {
            ViewProj = config.ViewProj,
            View = config.View,
            PageTableSize = config.PageTableSize,
            DebugMode = config.DebugMode,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
            QuantOrigin = config.QuantOrigin,
            QuantStep = config.QuantStep,
            MaterialID = 0,
            MaterialCount = Math.Max(_registry.MaterialCount, 1u),
            LightDir = config.LightDir,
            LightIntensity = config.LightIntensity,
            AmbientColor = config.AmbientColor,
            CameraPos = config.CameraPos,
        };
        graph.AddPass<object>(
            "UploadShadeUniforms",
            (builder, _) => { builder.Write(hShadeUniforms, ResourceState.ConstantBuffer); },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hShadeUniforms);
                if (ctx2 != null && buf != null)
                {
                    var mapped = ctx2.MapBuffer<ShadeUniforms>(buf, MapType.Write, MapFlags.Discard);
                    mapped[0] = shadeUniformData;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );

        // ─── Clear resolve target ───
        graph.AddPass<object>(
            "ClearResolveTarget",
            (builder, _) => { builder.Write(hResolveTarget, ResourceState.RenderTarget); },
            (rgCtx, _) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var tex = rgCtx.GetTexture(hResolveTarget);
                if (ctx2 != null && tex != null)
                {
                    var rtv = tex.GetDefaultView(TextureViewType.RenderTarget);
                    if (rtv != null)
                    {
                        ctx2.SetRenderTargets([rtv], null, ResourceStateTransitionMode.Verify);
                        ctx2.ClearRenderTarget(rtv, new Vector4(0, 0, 0, 0), ResourceStateTransitionMode.Verify);
                    }
                }
            }
        );

        // ─── Material Shade pass ───
        _materialShadePass!.HVisBuffer = raster.VisBuffer;
        _materialShadePass.HVisibleClusters = cull.VisibleClusters;
        _materialShadePass.HPageHeap = globals.PageHeap;
        _materialShadePass.HInstances = globals.GlobalTransform;
        _materialShadePass.HInstanceHeaders = globals.GlobalInstanceHeader;
        _materialShadePass.HInstanceDataHeap = globals.InstanceDataHeap;
        _materialShadePass.HShadeUniforms = hShadeUniforms;
        _materialShadePass.HPixelCoordBuffer = shadeBin.PixelCoordBuffer;
        _materialShadePass.HBinOffsets = shadeBin.BinOffsets;
        _materialShadePass.HBinIndirectArgs = shadeBin.BinIndirectArgs;
        _materialShadePass.HOutputColor = hResolveTarget;
        _materialShadePass.ShadeUniformData = shadeUniformData;
        graph.AddPass(_materialShadePass);

        // ─── Copy to back buffer ───
        AddCopyToBackBuffer(graph, hResolveTarget, config);

        return new ClusterShadeOutput(hResolveTarget);
    }

    private static void AddCopyToBackBuffer(
        RenderGraph graph, RenderGraphHandle hResolveTarget, in ClusterShadeConfig config)
    {
        if (!config.CopyToBackBuffer || !config.BackBuffer.IsValid) return;

        var backBuffer = config.BackBuffer;
        graph.AddPass<object>(
            "CopyShadedToBackBuffer",
            (builder, _) =>
            {
                builder.Read(hResolveTarget, ResourceState.CopySource);
                builder.Write(backBuffer, ResourceState.CopyDest);
            },
            (rgCtx, _) =>
            {
                var src = rgCtx.GetTexture(hResolveTarget);
                var dst = rgCtx.GetTexture(backBuffer);
                if (src != null && dst != null)
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    ctx2?.CopyTexture(new CopyTextureAttribs
                    {
                        SrcTexture = src,
                        DstTexture = dst,
                        SrcTextureTransitionMode = ResourceStateTransitionMode.Verify,
                        DstTextureTransitionMode = ResourceStateTransitionMode.Verify,
                    });
                }
            }
        );
    }

    public void Dispose()
    {
        _resolvePass?.Dispose();
    }
}
