using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 无状态 Draw/Rasterize 工具函数。
/// 每次调用创建轻量 ClusterDrawPass 实例（PSO 在 Pass 内 static 缓存）。
/// </summary>
public static class ClusterDraw
{
    /// <summary>
    /// 添加 Draw pass，返回 VisBuffer 和 Depth。
    /// </summary>
    public static ClusterRasterOutput AddPasses(
        RenderGraph graph,
        RenderContext context,
        in ClusterRasterBinOutput rasterBin,
        in ClusterCullOutput cull,
        in ClusterGlobalResources globals,
        in ClusterDrawConfig config,
        RenderGraphHandle depthTarget,
        uint screenWidth,
        uint screenHeight
    )
    {
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

        // ─── Fallback: create zero-offset buffer if caller didn't provide ───
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

        // ─── Create lightweight pass instance (PSO is static-cached inside) ───
        var drawPass = new ClusterDrawPass(context, $"{tag}ClusterDraw");
        drawPass.HVisibleClusters = rasterBin.BinnedClusterIndex;
        drawPass.HVisibleClustersData = cull.VisibleClusters;
        drawPass.HIndirectDrawArgs = rasterBin.BinnedDrawArgs;
        drawPass.HVisBufferTarget = hVisBuffer;
        drawPass.HDepthTarget = hDepth;
        drawPass.HDrawUniforms = globals.DrawUniforms;
        drawPass.HGlobalTransformBuffer = globals.GlobalTransform;
        drawPass.HPageHeap = globals.PageHeap;
        drawPass.HVisibleClusterMeta = hVisibleClusterMeta;
        drawPass.BinIndex = config.BinIndex;
        drawPass.SetFrameData(
            config.DebugMode,
            config.Wireframe,
            config.Overdraw,
            config.UseVisBuffer,
            config.DepthWrite
        );
        graph.AddPass(drawPass);

        return new ClusterRasterOutput(hVisBuffer, hDepth);
    }
}
