using System;
using System.Collections.Concurrent;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Merges SW raster depth (R32_UINT DepthUAV) into the HW depth target (D32_Float DSV).
/// Renders a fullscreen triangle that reads SW depth and outputs SV_Depth.
/// </summary>
public static class DepthMergePSOs
{
    internal static IPipelineState? MergePSO;
    internal static readonly ConcurrentBag<IShaderResourceBinding> SRBPool = [];

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    internal static IShaderResourceBinding RentSRB()
        => SRBPool.TryTake(out var srb) ? srb : MergePSO!.CreateShaderResourceBinding(false);

    internal static void ReturnSRB(IShaderResourceBinding srb) => SRBPool.Add(srb);

    internal static void EnsureInitialized(RenderContext context)
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;
            var device = context.Device;
            if (device == null) return;

            string path = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../../assets/Shaders/depth_merge.slang"
                )
            );
            var shaderAsset = SlangShaderImporter.Import(path);
            using var vs = shaderAsset.CreateShader(context, "VSFullscreen");
            using var ps = shaderAsset.CreateShader(context, "PSDepthMerge");

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
                        // ALWAYS: unconditionally write SW depth into the HW buffer.
                        // The PS outputs SV_Depth with the converted SW depth value,
                        // and discards pixels with no SW depth (swDepthBits == 0).
                        DepthFunc = ComparisonFunction.Always,
                    },
                },
                Vs = vs,
                Ps = ps,
            });

            s_initialized = true;
        }
    }
}

/// <summary>
/// RenderGraph pass: fullscreen depth merge (SW → HW depth).
/// Clears depthTarget to 1.0 (far plane), then draws a fullscreen triangle
/// that reads SW DepthUAV and outputs SV_Depth for SW-drawn pixels.
/// </summary>
public class DepthMergePass(
    RenderContext context,
    string passName = "DepthMerge"
) : IRenderGraphPass
{
    public string Name { get; } = passName;

    public RenderGraphHandle HSWDepthUAV = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDepthTarget = RenderGraphHandle.Invalid;

    public void Init() => DepthMergePSOs.EnsureInitialized(context);

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HSWDepthUAV, ResourceState.ShaderResource);
        builder.Write(HDepthTarget, ResourceState.DepthWrite);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        DepthMergePSOs.EnsureInitialized(context);
        var pso = DepthMergePSOs.MergePSO;
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
        var srb = DepthMergePSOs.RentSRB();

        srb.GetVariableByName(ShaderType.Pixel, "SWDepthUAV")
            ?.Set(swDepthSRV, SetShaderResourceFlags.None);

        ctx.SetPipelineState(pso);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);

        // Draw fullscreen triangle (3 vertices, no vertex buffer)
        ctx.Draw(new DrawAttribs
        {
            NumVertices = 3,
            Flags = DrawFlags.VerifyAll,
        });

        DepthMergePSOs.ReturnSRB(srb);
    }
}
