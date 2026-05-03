using System;
using System.IO;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterDebugAABBResources : IDisposable
{
    internal const string ShaderFile = "debug_aabb.slang";
    internal const string VertexEntryPoint = "VSMain";
    internal const string PixelEntryPoint = "PSMain";

    internal IPipelineState? PSO;
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

            string shaderPath = ClusterStageUtils.ShaderPath(ShaderFile);
            var shaderAsset = SlangShaderImporter.Import(shaderPath);
            ShaderAsset = shaderAsset;

            var vs = shaderAsset.CreateShader(context, VertexEntryPoint);
            var ps = shaderAsset.CreateShader(context, PixelEntryPoint);

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

            _initialized = true;
        }
    }

    public void Dispose()
    {
        Pool.Dispose();
        PSO?.Dispose();
    }
}

internal sealed class ClusterDebugAABBPass : IRenderGraphPass, IDisposable
{
    private readonly RenderContext _context;
    private readonly ClusterDebugAABBResources _resources;

    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid;

    public string Name => "Debug 2D Projections";

    public ClusterDebugAABBPass(RenderContext context, ClusterDebugAABBResources resources)
    {
        _context = context;
        _resources = resources;
        _resources.EnsureInitialized(context);
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDebugHiZOutput, ResourceState.ShaderResource);
        builder.Write(HColorTarget, ResourceState.RenderTarget);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        _resources.EnsureInitialized(_context);
        if (_resources.PSO == null) return;

        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null) return;

        var debugBuf = graphContext.GetBuffer(HDebugHiZOutput);
        if (debugBuf == null) return;

        var colorTex = graphContext.GetTexture(HColorTarget);
        if (colorTex == null) return;

        var colorRtv = colorTex.GetDefaultView(TextureViewType.RenderTarget);
        if (colorRtv == null) return;

        var srv = debugBuf.GetDefaultView(BufferViewType.ShaderResource);
        var srb = _resources.Pool.Rent(_resources.PSO);
        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Vertex, "DebugHiZInput")
            ?.Set(srv, SetShaderResourceFlags.None);

        ctx.SetRenderTargets([colorRtv], null, ResourceStateTransitionMode.None);
        ctx.SetPipelineState(_resources.PSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.Draw(new DrawAttribs { NumVertices = 4096 * 8, Flags = DrawFlags.VerifyAll });

        _resources.Pool.Return(srb);
    }

    public void Dispose() { }
}
