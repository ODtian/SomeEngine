using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterDebugPass(RenderContext context, ClusterResourceManager clusterManager) : RenderPass("ClusterDebug"), IDisposable
{
    private readonly RenderContext _context = context;
    private ShaderAsset? _bvhDebugAsset;
    private IPipelineState? _bvhDebugPSO;
    private IShaderResourceBinding? _bvhDebugSRB;

    private ShaderAsset? _copyAsset;
    private IPipelineState? _debugCopyPSO;
    private IShaderResourceBinding? _debugCopySRB;

    private ShaderAsset? _sphereAsset;
    private IPipelineState? _debugSpherePSO;
    private IShaderResourceBinding? _debugSphereSRB;

    private IBuffer? _debugIndirectArgsBuffer;
    private bool _initialized;

    public RGResourceHandle HBvhDebugBuffer,
        HBvhDebugCountBuffer;
    public RGResourceHandle HVisibleClusters,
        HIndirectDrawArgs;
    public RGResourceHandle HColorTarget,
        HDepthTarget;
    public RGResourceHandle HDrawUniforms,
        HCopyUniforms;

    private bool _visualiseBVH,
        _debugSpheres;
    private IBuffer? _drawUniformBuffer,
        _copyUniformBuffer;

    public void SetFrameData(IBuffer drawUB, IBuffer copyUB, bool visBVH, bool debugSpheres)
    {
        _drawUniformBuffer = drawUB;
        _copyUniformBuffer = copyUB;
        _visualiseBVH = visBVH;
        _debugSpheres = debugSpheres;
    }

    public void Init()
    {
        if (_initialized)
            return;
        var device = _context.Device;
        if (device == null)
            return;

        _debugIndirectArgsBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "Debug Indirect Args",
                Size = 256,
                Usage = Usage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
                Mode = BufferMode.Raw,
            }
        );

        InitBVHDebugPSO(device);
        InitCopyPSO(device);
        InitSpherePSO(device);
        _initialized = true;
    }

    private void InitBVHDebugPSO(IRenderDevice device)
    {
        string path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/debug_aabb.slang"
            )
        );
        _bvhDebugAsset = SlangShaderImporter.Import(path);
        using var vs = _bvhDebugAsset.CreateShader(_context, "VSMain");
        using var ps = _bvhDebugAsset.CreateShader(_context, "PSMain");

        var ci = new GraphicsPipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "BVH Debug AABB PSO",
                PipelineType = PipelineType.Graphics,
                ResourceLayout = new PipelineResourceLayoutDesc
                {
                    DefaultVariableType = ShaderResourceVariableType.Mutable,
                    Variables = _bvhDebugAsset.GetResourceVariables(
                        _context,
                        (name, cat) =>
                            (name == "DebugAABBs")
                                ? ShaderResourceVariableType.Dynamic
                                : null
                    ),
                },
            },
            GraphicsPipeline = new GraphicsPipelineDesc
            {
                NumRenderTargets = 1,
                RTVFormats = [TextureFormat.RGBA8_UNorm],
                DSVFormat = TextureFormat.D32_Float,
                PrimitiveTopology = PrimitiveTopology.LineList,
                InputLayout = new InputLayoutDesc { LayoutElements = [] },
                RasterizerDesc = new RasterizerStateDesc { CullMode = CullMode.None },
                DepthStencilDesc = new DepthStencilStateDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = false,
                },
                BlendDesc = new BlendStateDesc
                {
                    RenderTargets =
                    [
                        new RenderTargetBlendDesc
                        {
                            BlendEnable = true,
                            SrcBlend = BlendFactor.SrcAlpha,
                            DestBlend = BlendFactor.InvSrcAlpha,
                        },
                    ],
                },
            },
            Vs = vs,
            Ps = ps,
        };

        _bvhDebugPSO = device.CreateGraphicsPipelineState(ci);
        if (_bvhDebugPSO != null)
            _bvhDebugSRB = _bvhDebugPSO.CreateShaderResourceBinding(false);
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
        ci.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Mutable;
        ci.PSODesc.ResourceLayout.Variables = _copyAsset.GetResourceVariables(
            _context,
            (name, cat) =>
                (name == "IndirectArgs")
                    ? ShaderResourceVariableType.Dynamic
                    : null
        );
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
        ci.PSODesc.ResourceLayout.DefaultVariableType = ShaderResourceVariableType.Mutable;
        ci.PSODesc.ResourceLayout.Variables = _sphereAsset.GetResourceVariables(
            _context,
            (name, cat) =>
                (name == "RequestBuffer")
                    ? ShaderResourceVariableType.Dynamic
                    : null
        );
        _debugSpherePSO = device.CreateGraphicsPipelineState(ci);
        if (_debugSpherePSO != null)
            _debugSphereSRB = _debugSpherePSO.CreateShaderResourceBinding(false);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ReadBuffer(HBvhDebugBuffer, ResourceState.ShaderResource);
        builder.ReadBuffer(HBvhDebugCountBuffer, ResourceState.IndirectArgument);
        builder.ReadBuffer(HVisibleClusters, ResourceState.ShaderResource);
        builder.ReadBuffer(HIndirectDrawArgs, ResourceState.UnorderedAccess);
        builder.ReadBuffer(HDrawUniforms, ResourceState.ConstantBuffer);
        builder.ReadBuffer(HCopyUniforms, ResourceState.ConstantBuffer);
        builder.WriteTexture(HColorTarget, ResourceState.RenderTarget);
        builder.WriteTexture(HDepthTarget, ResourceState.DepthWrite);
    }

    public override void Execute(RenderContext context, RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var bvhDebug = rgCtx.GetBuffer(HBvhDebugBuffer);
        var bvhDebugCount = rgCtx.GetBuffer(HBvhDebugCountBuffer);
        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);

        if (
            _visualiseBVH
            && _bvhDebugPSO != null
            && _bvhDebugSRB != null
            && bvhDebug != null
            && bvhDebugCount != null
        )
        {
            _bvhDebugSRB
                .GetVariable(_context, _bvhDebugAsset, ShaderType.Vertex, "Uniforms")
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _bvhDebugSRB
                .GetVariable(_context, _bvhDebugAsset, ShaderType.Vertex, "DebugAABBs")
                ?.Set(
                    bvhDebug.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );

            ctx.TransitionResourceStates([
                new StateTransitionDesc
                {
                    Resource = bvhDebugCount,
                    NewState = ResourceState.IndirectArgument,
                    Flags = StateTransitionFlags.UpdateState,
                },
                new StateTransitionDesc
                {
                    Resource = bvhDebug,
                    NewState = ResourceState.ShaderResource,
                    Flags = StateTransitionFlags.UpdateState,
                },
            ]);

            ctx.SetPipelineState(_bvhDebugPSO);
            ctx.CommitShaderResources(_bvhDebugSRB, ResourceStateTransitionMode.Transition);
            ctx.DrawIndirect(
                new DrawIndirectAttribs
                {
                    AttribsBuffer = bvhDebugCount,
                    DrawArgsOffset = 0,
                    Flags = DrawFlags.VerifyAll,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
                }
            );
        }

        if (
            _debugSpheres
            && _debugCopyPSO != null
            && _debugCopySRB != null
            && _debugSpherePSO != null
            && _debugSphereSRB != null
            && visible != null
            && drawArgs != null
        )
        {
            var copyMap = ctx.MapBuffer<CopyUniforms>(
                _copyUniformBuffer,
                MapType.Write,
                MapFlags.Discard
            );
            copyMap[0] = new CopyUniforms { SphereVertexCount = 1536 };
            ctx.UnmapBuffer(_copyUniformBuffer, MapType.Write);

            ctx.TransitionResourceStates([
                new StateTransitionDesc
                {
                    Resource = drawArgs,
                    NewState = ResourceState.UnorderedAccess,
                    Flags = StateTransitionFlags.UpdateState,
                },
            ]);

            _debugCopySRB
                .GetVariable(_context, _copyAsset, ShaderType.Compute, "CopyUniforms")
                ?.Set(_copyUniformBuffer, SetShaderResourceFlags.None);
            _debugCopySRB
                .GetVariable(_context, _copyAsset, ShaderType.Compute, "IndirectArgs")
                ?.Set(
                    drawArgs.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );
            _debugCopySRB
                .GetVariable(_context, _copyAsset, ShaderType.Compute, "DebugArgs")
                ?.Set(
                    _debugIndirectArgsBuffer?.GetDefaultView(BufferViewType.UnorderedAccess),
                    SetShaderResourceFlags.None
                );

            ctx.SetPipelineState(_debugCopyPSO);
            ctx.CommitShaderResources(_debugCopySRB, ResourceStateTransitionMode.Transition);
            ctx.DispatchCompute(
                new DispatchComputeAttribs
                {
                    ThreadGroupCountX = 1,
                    ThreadGroupCountY = 1,
                    ThreadGroupCountZ = 1,
                }
            );

            _debugSphereSRB
                .GetVariable(_context, _sphereAsset, ShaderType.Vertex, "DrawUniforms")
                ?.Set(_drawUniformBuffer, SetShaderResourceFlags.None);
            _debugSphereSRB
                .GetVariable(_context, _sphereAsset, ShaderType.Vertex, "RequestBuffer")
                ?.Set(
                    visible.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
            _debugSphereSRB
                .GetVariable(_context, _sphereAsset, ShaderType.Vertex, "PageHeap")
                ?.Set(
                    clusterManager.PageHeap?.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );

            ctx.SetPipelineState(_debugSpherePSO);
            ctx.CommitShaderResources(_debugSphereSRB, ResourceStateTransitionMode.Transition);
            ctx.DrawIndirect(
                new DrawIndirectAttribs
                {
                    AttribsBuffer = _debugIndirectArgsBuffer,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Transition,
                    DrawArgsOffset = 0,
                    DrawCount = 1,
                    Flags = DrawFlags.None,
                }
            );
        }
    }

    public void Dispose()
    {
        _bvhDebugSRB?.Dispose();
        _bvhDebugPSO?.Dispose();
        _debugCopySRB?.Dispose();
        _debugCopyPSO?.Dispose();
        _debugSphereSRB?.Dispose();
        _debugSpherePSO?.Dispose();
        _debugIndirectArgsBuffer?.Dispose();
    }
}
