using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Merges SW raster depth (R32_UINT DepthUAV) into the HW depth target (D32_Float DSV).
/// Renders a fullscreen triangle that reads SW depth and outputs SV_Depth.
/// </summary>
public static partial class ClusterHiZ
{
internal sealed class DepthMergeResources : IDisposable
{
    internal const string ShaderFile = "depth_merge.slang";
    internal const string VertexEntryPoint = "VSFullscreen";
    internal const string PixelEntryPoint = "PSDepthMerge";

    internal IPipelineState? MergePSO;
    internal ShaderAsset? ShaderAsset;
    internal readonly SRBPool Pool = new();

    private bool _initialized;
    private readonly Lock _initLock = new();
internal void EnsureInitialized(RenderContext context)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            var device = context.Device;
            if (device == null) return;

            string path = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(path);
            ShaderAsset = shaderAsset;
            var vs = shaderAsset.CreateShader(context, VertexEntryPoint);
            var ps = shaderAsset.CreateShader(context, PixelEntryPoint);

            MergePSO = device.CreateGraphicsPipelineState(new GraphicsPipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "DepthMerge PSO",
                    PipelineType = PipelineType.Graphics,
                    ResourceLayout = new PipelineResourceLayoutDesc
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                GraphicsPipeline = new GraphicsPipelineDesc
                {
                    NumRenderTargets = 0,
                    DSVFormat = TextureFormat.D32_Float,
                    InputLayout = new InputLayoutDesc { LayoutElements = [] },
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    RasterizerDesc = new RasterizerStateDesc
                    {
                        CullMode = CullMode.None,
                    },
                    DepthStencilDesc = new DepthStencilStateDesc
                    {
                        DepthEnable = true,
                        DepthWriteEnable = true,
                        // Merge SW depth only when it is closer than the depth already
                        // produced by HW draws. Phase2 reuses the Phase1 SW depth UAV,
                        // so Always would replay stale SW depths over nearer HW occluders.
                        DepthFunc = ComparisonFunction.Less,
                    },
                },
                Vs = vs,
                Ps = ps,
            });
            _initialized = true;
        }
    }

    public void Dispose()
    {
        Pool.Dispose();
        MergePSO?.Dispose();
    }
}
}

/// <summary>
/// RenderGraph pass: fullscreen depth merge (SW → HW depth).
/// Draws a fullscreen triangle that reads SW DepthUAV and writes closer SW
/// depths into the HW depth target.
/// </summary>
internal class DepthMergePass(
    RenderContext context,
    ClusterHiZ.DepthMergeResources resources,
    string passName = "DepthMerge"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HSWDepthUAV = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDepthTarget = RenderGraphHandle.Invalid;

    public void Init() => resources.EnsureInitialized(context);

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HSWDepthUAV, ResourceState.ShaderResource);
        builder.Write(HDepthTarget, ResourceState.DepthWrite);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);
        var pso = resources.MergePSO;
        if (pso == null) return;

        var ctx = context.ImmediateContext;
        if (ctx == null) return;

        var swDepthSRV = rgCtx.GetTextureView(HSWDepthUAV, TextureViewType.ShaderResource);
        var dsv = rgCtx.GetTextureView(HDepthTarget, TextureViewType.DepthStencil);
        if (swDepthSRV == null || dsv == null) return;

        // 1. (Removed erroneous clear) The depth target is already cleared by the main Clear pass in Program.cs.
        // We just need to ensure the render targets are set for the fullscreen triangle.
        ctx.SetRenderTargets([], dsv, ResourceStateTransitionMode.None);

        // 2. Draw fullscreen triangle to inject SW depths
        var srb = resources.Pool.Rent(resources.MergePSO!);

        srb.GetVariableByReflectedBinding(context, resources.ShaderAsset, ShaderType.Pixel, "SWDepthUAV")
            ?.Set(swDepthSRV, SetShaderResourceFlags.None);

        ctx.SetPipelineState(pso);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);

        // Draw fullscreen triangle (3 vertices, no vertex buffer)
        ctx.Draw(new DrawAttribs
        {
            NumVertices = 3,
            Flags = DrawFlags.VerifyAll,
        });

        resources.Pool.Return(srb);
    }
}
