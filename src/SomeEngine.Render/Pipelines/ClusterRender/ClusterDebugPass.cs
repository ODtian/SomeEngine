using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterDebugPass(RenderContext context) : IDisposable
{
    private readonly RenderContext _context = context;
    private ShaderAsset? _copyAsset;
    private IPipelineState? _debugCopyPSO;
    private IShaderResourceBinding? _debugCopySRB;

    private ShaderAsset? _sphereAsset;
    private IPipelineState? _debugSpherePSO;
    private IShaderResourceBinding? _debugSphereSRB;

    private bool _initialized;

    public void Init()
    {
        if (_initialized)
            return;
        var device = _context.Device;
        if (device == null)
            return;

        InitCopyPSO(device);
        InitSpherePSO(device);
        _initialized = true;
    }

    private void InitCopyPSO(IRenderDevice device)
    {
        string path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/debug_args_copy.cs.hlsl"
            )
        );
        _copyAsset = SlangShaderImporter.Import(path);
        using var cs = _copyAsset.CreateShader(_context, "main");
        var ci = new ComputePipelineStateCreateInfo();
        ci.PSODesc.Name = "Debug Copy PSO";
        ci.PSODesc.PipelineType = PipelineType.Compute;
        ci.Cs = cs;
        ci.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Dynamic;
        _debugCopyPSO = device.CreateComputePipelineState(ci);
        if (_debugCopyPSO != null)
            _debugCopySRB = _debugCopyPSO.CreateShaderResourceBinding(false);
    }

    private void InitSpherePSO(IRenderDevice device)
    {
        string path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/debug_sphere.hlsl"
            )
        );
        _sphereAsset = SlangShaderImporter.Import(path);
        using var vs = _sphereAsset.CreateShader(_context, "VSMain");
        using var ps = _sphereAsset.CreateShader(_context, "PSMain");

        var ci = new GraphicsPipelineStateCreateInfo();
        ci.PSODesc.Name = "Debug Sphere PSO";
        ci.PSODesc.PipelineType = PipelineType.Graphics;
        ci.GraphicsPipeline.NumRenderTargets = 1;
        ci.GraphicsPipeline.RTVFormats = new[] { TextureFormat.RGBA8_UNorm };
        ci.GraphicsPipeline.DSVFormat = TextureFormat.D32_Float;
        ci.GraphicsPipeline.InputLayout.LayoutElements = Array.Empty<LayoutElement>();
        ci.GraphicsPipeline.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
        ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
        ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
        ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.SrcAlpha;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.InvSrcAlpha;
        ci.Vs = vs;
        ci.Ps = ps;
        ci.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Dynamic;
        _debugSpherePSO = device.CreateGraphicsPipelineState(ci);
        if (_debugSpherePSO != null)
            _debugSphereSRB = _debugSpherePSO.CreateShaderResourceBinding(false);
    }

    public void SetupSphereCopy(RenderGraphBuilder builder, RenderGraphHandle hIndirectDrawArgs, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hCopyUB)
    {
        builder.Read(hIndirectDrawArgs, ResourceState.UnorderedAccess); // Still read/write? Actually it's written in this pass logic but setup says Read. Let's use Write if we modify it.
        builder.Write(hDebugIndirectArgs, ResourceState.UnorderedAccess);
        builder.Read(hCopyUB, ResourceState.ConstantBuffer);
    }

    public void ExecuteSphereCopy(RenderContext context, RenderGraphContext rgCtx, RenderGraphHandle hIndirectDrawArgs, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hCopyUB)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || _debugCopyPSO == null || _debugCopySRB == null)
            return;

        var drawArgs = rgCtx.GetBuffer(hIndirectDrawArgs);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        var copyUniformBuffer = rgCtx.GetBuffer(hCopyUB);
        if (drawArgs == null || debugIndirectArgs == null || copyUniformBuffer == null)
            return;

        _debugCopySRB
            .GetVariableByName(ShaderType.Compute, "CopyUniforms")
            ?.Set(copyUniformBuffer, SetShaderResourceFlags.None);
        _debugCopySRB
            .GetVariableByName(ShaderType.Compute, "IndirectArgs")
            ?.Set(
                drawArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );
        _debugCopySRB
            .GetVariableByName(ShaderType.Compute, "DebugArgs")
            ?.Set(
                debugIndirectArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                SetShaderResourceFlags.None
            );

        ctx.SetPipelineState(_debugCopyPSO);
        ctx.CommitShaderResources(_debugCopySRB, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = 1,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void SetupSphereDraw(RenderGraphBuilder builder, RenderGraphHandle hVisibleClusters, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hDrawUB, RenderGraphHandle hPageHeap, RenderGraphHandle hColor, RenderGraphHandle hDepth)
    {
        builder.Read(hVisibleClusters, ResourceState.ShaderResource);
        builder.Read(hDebugIndirectArgs, ResourceState.IndirectArgument);
        builder.Read(hDrawUB, ResourceState.ConstantBuffer);
        builder.Read(hPageHeap, ResourceState.ShaderResource);
        builder.Write(hColor, ResourceState.RenderTarget);
        builder.Write(hDepth, ResourceState.DepthWrite);
    }

    public void ExecuteSphereDraw(RenderContext context, RenderGraphContext rgCtx, RenderGraphHandle hVisibleClusters, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hPageHeap, RenderGraphHandle hDrawUB)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || _debugSpherePSO == null || _debugSphereSRB == null)
            return;

        var visible = rgCtx.GetBuffer(hVisibleClusters);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        var pageHeap = rgCtx.GetBuffer(hPageHeap);
        var drawUniformBuffer = rgCtx.GetBuffer(hDrawUB);

        if (visible == null || debugIndirectArgs == null || pageHeap == null || drawUniformBuffer == null)
            return;

        _debugSphereSRB
            .GetVariableByName(ShaderType.Vertex, "DrawUniforms")
            ?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        _debugSphereSRB
            .GetVariableByName(ShaderType.Vertex, "RequestBuffer")
            ?.Set(
                visible.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _debugSphereSRB
            .GetVariableByName(ShaderType.Vertex, "PageHeap")
            ?.Set(
                pageHeap.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        ctx.SetPipelineState(_debugSpherePSO);
        ctx.CommitShaderResources(_debugSphereSRB, ResourceStateTransitionMode.Verify);
        ctx.DrawIndirect(
            new DrawIndirectAttribs
            {
                AttribsBuffer = debugIndirectArgs,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                DrawArgsOffset = 0,
                DrawCount = 1,
                Flags = DrawFlags.None,
            }
        );
    }

    public void Dispose()
    {
        _debugCopySRB?.Dispose();
        _debugCopyPSO?.Dispose();
        _debugSphereSRB?.Dispose();
        _debugSpherePSO?.Dispose();
    }
}
