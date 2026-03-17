using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Draws 2D screen-space rectangles for each cluster's HiZ projection.
/// Red = visible (not occluded), Green = occluded/culled.
/// Reads UV bounds directly from DebugHiZOutput buffer.
/// </summary>
internal static class ClusterDebugAABBPSOs
{
    internal static IPipelineState? PSO;
    internal static readonly ConcurrentBag<IShaderResourceBinding> SRBPool = [];
    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    internal static void EnsureInitialized(RenderContext context)
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;
            var device = context.Device;
            if (device == null) return;

            string shaderPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/debug_aabb.slang")
            );
            var shaderAsset = SlangShaderImporter.Import(shaderPath);

            using var vs = shaderAsset.CreateShader(context, "VSMain");
            using var ps = shaderAsset.CreateShader(context, "PSMain");

            PSO = device.CreateGraphicsPipelineState(new GraphicsPipelineStateCreateInfo()
            {
                PSODesc = new PipelineStateDesc()
                {
                    Name = "Debug 2D Projection PSO",
                    PipelineType = PipelineType.Graphics,
                    ResourceLayout = new PipelineResourceLayoutDesc()
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                GraphicsPipeline = new GraphicsPipelineDesc()
                {
                    PrimitiveTopology = PrimitiveTopology.LineList,
                    NumRenderTargets = 1,
                    RTVFormats = [TextureFormat.RGBA8_UNorm],
                    DSVFormat = TextureFormat.Unknown,
                    InputLayout = new InputLayoutDesc() { LayoutElements = [] },
                    RasterizerDesc = new RasterizerStateDesc() { CullMode = CullMode.None },
                    DepthStencilDesc = new DepthStencilStateDesc() { DepthEnable = false },
                },
                Vs = vs,
                Ps = ps,
            });

            s_initialized = true;
        }
    }

    internal static IShaderResourceBinding RentSRB()
    {
        if (PSO == null) throw new InvalidOperationException("PSO is not initialized.");
        return SRBPool.TryTake(out var srb) ? srb : PSO.CreateShaderResourceBinding(false);
    }

    internal static void ReturnSRB(IShaderResourceBinding srb) => SRBPool.Add(srb);
}

internal sealed class ClusterDebugAABBPass : IRenderGraphPass, IDisposable
{
    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid;

    public string Name => "Debug 2D Projections";

    public ClusterDebugAABBPass(RenderContext context)
    {
        ClusterDebugAABBPSOs.EnsureInitialized(context);
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDebugHiZOutput, ResourceState.ShaderResource);
        builder.Write(HColorTarget, ResourceState.RenderTarget);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        if (ClusterDebugAABBPSOs.PSO == null) return;

        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null) return;

        var debugBuf = graphContext.GetBuffer(HDebugHiZOutput);
        if (debugBuf == null) return;

        var colorTex = graphContext.GetTexture(HColorTarget);
        if (colorTex == null) return;

        var colorRtv = colorTex.GetDefaultView(TextureViewType.RenderTarget);
        if (colorRtv == null) return;

        var srv = debugBuf.GetDefaultView(BufferViewType.ShaderResource);
        var srb = ClusterDebugAABBPSOs.RentSRB();
        srb.GetVariableByName(ShaderType.Vertex, "DebugHiZInput")?.Set(srv, SetShaderResourceFlags.None);

        ctx.SetRenderTargets([colorRtv], null, ResourceStateTransitionMode.Verify);
        ctx.SetPipelineState(ClusterDebugAABBPSOs.PSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);

        // 4096 max entries × 8 vertices per rect (4 edges × 2 endpoints)
        ctx.Draw(new DrawAttribs { NumVertices = 4096 * 8, Flags = DrawFlags.VerifyAll });

        ClusterDebugAABBPSOs.ReturnSRB(srb);
    }

    public void Dispose() { }
}
