using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: Draw/Rasterize — 管理 DrawPass + VisBuffer/Depth 创建。
/// </summary>
public class ClusterDrawStage : IDisposable
{
    private readonly RenderContext _context;
    private ClusterDrawPass? _drawPass;
    private bool _initialized;

    public ClusterDrawStage(RenderContext context, string name = "ClusterDraw")
    {
        _context = context;
        _name = name;
    }

    private readonly string _name;

    public void Init()
    {
        if (_initialized) return;
        _drawPass = new ClusterDrawPass(_context, _name);
        _drawPass.Init();
        _initialized = true;
    }

    /// <summary>
    /// 添加 Draw pass，返回 VisBuffer 和 Depth。
    /// </summary>
    public ClusterRasterOutput AddPasses(
        RenderGraph graph,
        in ClusterRasterBinOutput rasterBin,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        in ClusterDrawConfig config,
        RenderGraphHandle depthTarget,
        uint screenWidth,
        uint screenHeight
    )
    {
        if (!_initialized) Init();

        string tag = config.Tag ?? "";

        // ─── Create or reuse output targets ───
        var hVisBuffer = config.OutputVisBuffer.IsValid
            ? config.OutputVisBuffer
            : config.UseVisBuffer
                ? graph.CreateTexture($"{tag}VisBuffer", new TextureDesc
                {
                    Type = ResourceDimension.Tex2d,
                    Width = screenWidth,
                    Height = screenHeight,
                    MipLevels = 1,
                    Format = TextureFormat.R32_UInt,
                    Usage = Usage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                    ClearValue = new OptimizedClearValue
                    {
                        Format = TextureFormat.R32_UInt,
                        Color = new System.Numerics.Vector4(0, 0, 0, 0),
                    },
                })
                : RenderGraphHandle.Invalid;

        var hDepth = config.OutputDepth.IsValid
            ? config.OutputDepth
            : depthTarget;

        var hVisibleClusterMeta = config.VisibleClusterMeta;

        // ─── Clear VisBuffer if needed ───
        if (config.ClearTargets && config.UseVisBuffer && hVisBuffer.IsValid)
        {
            graph.AddPass<object>(
                $"{tag}ClearVisBuffer",
                (builder, _) => { builder.Write(hVisBuffer, ResourceState.RenderTarget); },
                (rgCtx, _) =>
                {
                    var ctx2 = rgCtx.RenderContext.ImmediateContext;
                    var rtv = rgCtx.GetTextureView(hVisBuffer, TextureViewType.RenderTarget);
                    if (ctx2 != null && rtv != null)
                    {
                        ctx2.SetRenderTargets([rtv], null, ResourceStateTransitionMode.Verify);
                        ctx2.ClearRenderTarget(rtv, new System.Numerics.Vector4(0, 0, 0, 0), ResourceStateTransitionMode.Verify);
                    }
                }
            );
        }

        // ─── Wire DrawPass ───
        // Fallback: if caller does not provide visible-cluster metadata, use a local zero-offset buffer.
        if (!hVisibleClusterMeta.IsValid)
        {
            hVisibleClusterMeta = graph.CreateBuffer($"{tag}ZeroOffset", new BufferDesc
            {
                Size = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            });

            graph.AddPass<object>(
                $"{tag}ClearZeroOffset",
                (builder, _) => { builder.Write(hVisibleClusterMeta, ResourceState.CopyDest); },
                (rgCtx, _) =>
                {
                    Span<uint> zeroData = [0, 0, 0, 0];
                    var buf = rgCtx.GetBuffer(hVisibleClusterMeta);
                    if (buf != null)
                    {
                        rgCtx.RenderContext.ImmediateContext?.UpdateBuffer(
                            buf, 0, zeroData, ResourceStateTransitionMode.Verify
                        );
                    }
                }
            );
        }

        _drawPass!.HVisibleClusters = rasterBin.BinnedClusterIndex;
        _drawPass.HVisibleClustersData = cull.VisibleClusters;
        _drawPass.HIndirectDrawArgs = rasterBin.BinnedDrawArgs;
        _drawPass.HVisBufferTarget = hVisBuffer;
        _drawPass.HDepthTarget = hDepth;
        _drawPass.HDrawUniforms = globals.DrawUniforms;
        _drawPass.HGlobalTransformBuffer = globals.GlobalTransform;
        _drawPass.HPageHeap = globals.PageHeap;
        _drawPass.HVisibleClusterMeta = hVisibleClusterMeta;
        _drawPass.BinIndex = config.BinIndex;
        _drawPass.SetFrameData(
            config.DebugMode,
            config.Wireframe,
            config.Overdraw,
            config.UseVisBuffer,
            config.DepthWrite
        );
        graph.AddPass(_drawPass);

        return new ClusterRasterOutput(hVisBuffer, hDepth);
    }

    public void Dispose()
    {
        _drawPass?.Dispose();
    }
}
