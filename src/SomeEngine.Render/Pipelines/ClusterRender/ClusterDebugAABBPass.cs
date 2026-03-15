using System.IO;
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
internal sealed class ClusterDebugAABBPass : IRenderGraphPass, IDisposable
{
    private readonly RenderContext _context;
    private IPipelineState? _pso;
    private IShaderResourceBinding? _srb;
    private bool _initialized;

    public RenderGraphHandle HDebugHiZOutput = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid;

    public string Name => "Debug 2D Projections";

    public ClusterDebugAABBPass(RenderContext context)
    {
        _context = context;
    }

    private void Init()
    {
        if (_initialized) return;
        var device = _context.Device;
        if (device == null) return;

        string shaderPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/debug_aabb.slang")
        );
        var shaderAsset = SlangShaderImporter.Import(shaderPath);

        using var vs = shaderAsset.CreateShader(_context, "VSMain");
        using var ps = shaderAsset.CreateShader(_context, "PSMain");

        var ci = new GraphicsPipelineStateCreateInfo()
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
                RasterizerDesc = new RasterizerStateDesc()
                {
                    CullMode = CullMode.None,
                },
                DepthStencilDesc = new DepthStencilStateDesc()
                {
                    DepthEnable = false,
                },
            },
            Vs = vs,
            Ps = ps,
        };

        _pso = device.CreateGraphicsPipelineState(ci);
        if (_pso != null)
            _srb = _pso.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HDebugHiZOutput, ResourceState.ShaderResource);
        builder.Write(HColorTarget, ResourceState.RenderTarget);
    }

    public void Execute(RenderGraphContext graphContext)
    {
        Init();
        if (_pso == null || _srb == null) return;

        var ctx = graphContext.RenderContext.ImmediateContext;
        if (ctx == null) return;

        var debugBuf = graphContext.GetBuffer(HDebugHiZOutput);
        if (debugBuf == null) return;

        var colorTex = graphContext.GetTexture(HColorTarget);
        if (colorTex == null) return;

        var colorRtv = colorTex.GetDefaultView(TextureViewType.RenderTarget);
        if (colorRtv == null) return;

        var dVar = _srb.GetVariableByName(ShaderType.Vertex, "DebugHiZInput");
        var srv = debugBuf.GetDefaultView(BufferViewType.ShaderResource);
        dVar?.Set(srv, SetShaderResourceFlags.None);

        ctx.SetRenderTargets(
            [colorRtv], null, ResourceStateTransitionMode.Verify
        );
        ctx.SetPipelineState(_pso);
        ctx.CommitShaderResources(_srb, ResourceStateTransitionMode.Verify);

        // 4096 max entries × 8 vertices per rect (4 edges × 2 endpoints)
        ctx.Draw(new DrawAttribs
        {
            NumVertices = 4096 * 8,
            Flags = DrawFlags.VerifyAll,
        });
    }

    public void Dispose()
    {
        _srb?.Dispose();
        _pso?.Dispose();
    }
}
