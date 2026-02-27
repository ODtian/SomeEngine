using System.IO;
using System.Numerics;
using Diligent;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public unsafe class HelloTrianglePass : IDisposable
{
    private readonly RenderContext _context;
    private IPipelineState? _pso;

    public HelloTrianglePass(RenderContext context)
    {
        _context = context;
        Initialize();
    }

    private void Initialize()
    {
        var device = _context.Device;
        if (device == null)
            return;

        // 1. Create Shaders
        ShaderCreateInfo shaderCI = new ShaderCreateInfo();
        shaderCI.SourceLanguage = ShaderSourceLanguage.Hlsl;
        shaderCI.ShaderCompiler = ShaderCompiler.Dxc;
        // Assuming we have shader file
        string shaderPath = Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../assets/Shaders/triangle.hlsl"
        );
        if (!File.Exists(shaderPath))
            shaderPath = "triangle.hlsl"; // Fallback

        // VS
        using var shaderSourceFactory = _context.Factory?.CreateDefaultShaderSourceStreamFactory(
            "assets/Shaders"
        );
        shaderCI.Desc.Name = "Triangle VS";
        shaderCI.Desc.ShaderType = ShaderType.Vertex;
        shaderCI.EntryPoint = "VSMain";
        shaderCI.FilePath = "triangle.hlsl";
        shaderCI.ShaderSourceStreamFactory = shaderSourceFactory;
        using var vs = device.CreateShader(shaderCI, out _);

        // PS
        shaderCI.Desc.Name = "Triangle PS";
        shaderCI.Desc.ShaderType = ShaderType.Pixel;
        shaderCI.EntryPoint = "PSMain";

        using var ps = device.CreateShader(shaderCI, out _);

        // 2. Create PSO
        var psoCI = new GraphicsPipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Hello Triangle PSO",
                PipelineType = PipelineType.Graphics,
            },
            GraphicsPipeline = new GraphicsPipelineDesc
            {
                NumRenderTargets = 1,
                RTVFormats = [_context.SwapChain!.GetDesc().ColorBufferFormat],
                DSVFormat = _context.SwapChain!.GetDesc().DepthBufferFormat,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                RasterizerDesc = new RasterizerStateDesc { CullMode = CullMode.None },
                DepthStencilDesc = new DepthStencilStateDesc { DepthEnable = false },
            },
            Vs = vs,
            Ps = ps,
        };

        _pso = device.CreateGraphicsPipelineState(psoCI);
    }

    public void Draw()
    {
        var ctx = _context.ImmediateContext;
        var swapChain = _context.SwapChain;
        if (ctx == null || swapChain == null || _pso == null)
            return;

        var rtv = swapChain.GetCurrentBackBufferRTV();
        var dsv = swapChain.GetDepthBufferDSV();

        ctx.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Verify);
        ctx.ClearRenderTarget(
            rtv,
            new System.Numerics.Vector4(0.2f, 0.2f, 0.2f, 1.0f),
            ResourceStateTransitionMode.Verify
        );
        ctx.ClearDepthStencil(
            dsv,
            ClearDepthStencilFlags.Depth,
            1.0f,
            0,
            ResourceStateTransitionMode.Verify
        );

        ctx.SetPipelineState(_pso);

        var drawAttrs = new DrawAttribs { NumVertices = 3, Flags = DrawFlags.VerifyAll };
        ctx.Draw(drawAttrs);
    }

    public void Dispose()
    {
        _pso?.Dispose();
    }
}
