using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterDrawPass(
    RenderContext context,
    ClusterResourceManager clusterManager,
    InstanceSyncSystem transformSystem,
    string passName = "ClusterDraw"
) : RenderPass(passName), IDisposable
{
    private ShaderAsset? _drawAsset;
    private IPipelineState? _drawPSO;
    private IPipelineState? _drawWireframePSO;
    private IPipelineState? _drawOverdrawPSO;
    private IShaderResourceBinding? _drawSRB;
    private IShaderResourceBinding? _drawWireframeSRB;
    private IShaderResourceBinding? _drawOverdrawSRB;
    private bool _initialized;

    public RGResourceHandle HVisibleClusters = RGResourceHandle.Invalid,
        HIndirectDrawArgs = RGResourceHandle.Invalid;
    public RGResourceHandle HColorTarget = RGResourceHandle.Invalid,
        HDepthTarget = RGResourceHandle.Invalid;
    public RGResourceHandle HDrawUniforms = RGResourceHandle.Invalid;
    public RGResourceHandle HGlobalTransformBuffer = RGResourceHandle.Invalid;
    public RGResourceHandle HPageHeap = RGResourceHandle.Invalid;

    private ClusterDebugMode _debugMode;
    private bool _wireframe,
        _overdraw;
    private IBuffer? _drawUniformBuffer;

    public void SetFrameData(
        IBuffer drawUB,
        ClusterDebugMode debugMode,
        bool wireframe,
        bool overdraw
    )
    {
        _drawUniformBuffer = drawUB;
        _debugMode = debugMode;
        _wireframe = wireframe;
        _overdraw = overdraw;
    }

    public void Init()
    {
        if (_initialized)
            return;
        var device = context.Device;
        if (device == null)
            return;

        string path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_draw.slang"
            )
        );
        _drawAsset = SlangShaderImporter.Import(path);
        using var vs = _drawAsset.CreateShader(context, "VSMain");
        using var ps = _drawAsset.CreateShader(context, "PSMain");

        var ci = new GraphicsPipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc()
            {
                Name = "Cluster Draw PSO",
                PipelineType = PipelineType.Graphics,
                ResourceLayout = new PipelineResourceLayoutDesc()
                {
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = _drawAsset.GetResourceVariables(
                        context,
                        name =>
                            (name == "RequestBuffer" || name == "Instances")
                                ? ShaderResourceVariableType.Dynamic
                                : null
                    ),
                },
            },
            GraphicsPipeline = new GraphicsPipelineDesc()
            {
                NumRenderTargets = 1,
                RTVFormats = [TextureFormat.RGBA8_UNorm],
                DSVFormat = TextureFormat.D32_Float,
                InputLayout = new InputLayoutDesc() { LayoutElements = [] },
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                RasterizerDesc = new RasterizerStateDesc()
                {
                    CullMode = CullMode.Back,
                    FrontCounterClockwise = true,
                },
                DepthStencilDesc = new DepthStencilStateDesc()
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                },
            },
            Vs = vs,
            Ps = ps,
        };

        _drawPSO = device.CreateGraphicsPipelineState(ci);
        if (_drawPSO != null)
            _drawSRB = _drawPSO.CreateShaderResourceBinding(false);

        ci.PSODesc.Name = "Cluster Draw Wireframe PSO";
        ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
        ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
        _drawWireframePSO = device.CreateGraphicsPipelineState(ci);
        if (_drawWireframePSO != null)
            _drawWireframeSRB = _drawWireframePSO.CreateShaderResourceBinding(false);

        ci.PSODesc.Name = "Cluster Draw Overdraw PSO";
        ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Solid;
        ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.Back;
        ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = false;
        ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.One;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.One;
        using var psOD = _drawAsset.CreateShader(context, "PSOverdraw");
        ci.Ps = psOD;
        _drawOverdrawPSO = device.CreateGraphicsPipelineState(ci);
        if (_drawOverdrawPSO != null)
            _drawOverdrawSRB = _drawOverdrawPSO.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ReadBuffer(HVisibleClusters, ResourceState.ShaderResource);
        builder.ReadBuffer(HIndirectDrawArgs, ResourceState.IndirectArgument);
        builder.ReadBuffer(HDrawUniforms, ResourceState.ConstantBuffer);
        builder.ReadBuffer(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.ReadBuffer(HPageHeap, ResourceState.ShaderResource);
        builder.WriteTexture(HColorTarget, ResourceState.RenderTarget);
        builder.WriteTexture(HDepthTarget, ResourceState.DepthWrite);
    }

    public override void Execute(RenderContext context, RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        if (visible == null || drawArgs == null)
            return;

        IPipelineState? pso;
        IShaderResourceBinding? srb;
        if (_overdraw)
        {
            pso = _drawOverdrawPSO;
            srb = _drawOverdrawSRB;
        }
        else if (_wireframe)
        {
            pso = _drawWireframePSO;
            srb = _drawWireframeSRB;
        }
        else
        {
            pso = _drawPSO;
            srb = _drawSRB;
        }
        if (pso == null || srb == null)
            return;

        srb.GetVariableByName(ShaderType.Vertex, "Uniforms")
            ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Pixel, "Uniforms")
            ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Vertex, "RequestBuffer")
            ?.Set(
                visible.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        srb.GetVariableByName(ShaderType.Vertex, "PageHeap")
            ?.Set(
                clusterManager.PageHeap?.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        var globalTransformView = rgCtx.GetBufferView(HGlobalTransformBuffer, BufferViewType.ShaderResource);
        if (globalTransformView != null)
        {
            srb.GetVariableByName(ShaderType.Vertex, "Instances")
                ?.Set(
                    globalTransformView,
                    SetShaderResourceFlags.None
                );
        }

        ctx.SetPipelineState(pso);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DrawIndirect(
            new DrawIndirectAttribs
            {
                AttribsBuffer = drawArgs,
                DrawArgsOffset = 0,
                Flags = DrawFlags.VerifyAll,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
            }
        );
    }

    public void Dispose()
    {
        _drawSRB?.Dispose();
        _drawPSO?.Dispose();
        _drawWireframeSRB?.Dispose();
        _drawWireframePSO?.Dispose();
        _drawOverdrawSRB?.Dispose();
        _drawOverdrawPSO?.Dispose();
    }
}
