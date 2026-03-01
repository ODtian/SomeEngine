using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterDebugPass(RenderContext context, ClusterResourceManager clusterManager) : IDisposable
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

    private bool _initialized;

    public void Init()
    {
        if (_initialized)
            return;
        var device = _context.Device;
        if (device == null)
            return;

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
                        name =>
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
            name =>
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
            name =>
                (name == "RequestBuffer")
                    ? ShaderResourceVariableType.Dynamic
                    : null
        );
        _debugSpherePSO = device.CreateGraphicsPipelineState(ci);
        if (_debugSpherePSO != null)
            _debugSphereSRB = _debugSpherePSO.CreateShaderResourceBinding(false);
    }

    public void SetupBVH(RenderGraphBuilder builder, RGResourceHandle hBvhDebug, RGResourceHandle hBvhDebugCount, RGResourceHandle hDrawUB, RGResourceHandle hColor, RGResourceHandle hDepth)
    {
        builder.ReadBuffer(hBvhDebug, ResourceState.ShaderResource);
        builder.ReadBuffer(hBvhDebugCount, ResourceState.IndirectArgument);
        builder.ReadBuffer(hDrawUB, ResourceState.ConstantBuffer);
        builder.WriteTexture(hColor, ResourceState.RenderTarget);
        builder.WriteTexture(hDepth, ResourceState.DepthWrite);
    }

    public void ExecuteBVH(RenderContext context, RenderGraphContext rgCtx, RGResourceHandle hBvhDebug, RGResourceHandle hBvhDebugCount, IBuffer drawUniformBuffer)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || _bvhDebugPSO == null || _bvhDebugSRB == null)
            return;

        var bvhDebug = rgCtx.GetBuffer(hBvhDebug);
        var bvhDebugCount = rgCtx.GetBuffer(hBvhDebugCount);

        if (bvhDebug == null || bvhDebugCount == null)
            return;

        _bvhDebugSRB
            .GetVariableByName(ShaderType.Vertex, "Uniforms")
            ?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        _bvhDebugSRB
            .GetVariableByName(ShaderType.Vertex, "DebugAABBs")
            ?.Set(
                bvhDebug.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );

        ctx.SetPipelineState(_bvhDebugPSO);
        ctx.CommitShaderResources(_bvhDebugSRB, ResourceStateTransitionMode.Verify);
        ctx.DrawIndirect(
            new DrawIndirectAttribs
            {
                AttribsBuffer = bvhDebugCount,
                DrawArgsOffset = 0,
                Flags = DrawFlags.VerifyAll,
                AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
            }
        );
    }

    public void SetupSphereCopy(RenderGraphBuilder builder, RGResourceHandle hIndirectDrawArgs, RGResourceHandle hDebugIndirectArgs, RGResourceHandle hCopyUB)
    {
        builder.ReadBuffer(hIndirectDrawArgs, ResourceState.UnorderedAccess); // Still read/write? Actually it's written in this pass logic but setup says Read. Let's use Write if we modify it.
        builder.WriteBuffer(hDebugIndirectArgs, ResourceState.UnorderedAccess);
        builder.ReadBuffer(hCopyUB, ResourceState.ConstantBuffer);
    }

    public void ExecuteSphereCopy(RenderContext context, RenderGraphContext rgCtx, RGResourceHandle hIndirectDrawArgs, RGResourceHandle hDebugIndirectArgs, IBuffer copyUniformBuffer)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || _debugCopyPSO == null || _debugCopySRB == null)
            return;

        var drawArgs = rgCtx.GetBuffer(hIndirectDrawArgs);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        if (drawArgs == null || debugIndirectArgs == null)
            return;

        var copyMap = ctx.MapBuffer<CopyUniforms>(
            copyUniformBuffer,
            MapType.Write,
            MapFlags.Discard
        );
        copyMap[0] = new CopyUniforms { SphereVertexCount = 1536 };
        ctx.UnmapBuffer(copyUniformBuffer, MapType.Write);

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

    public void SetupSphereDraw(RenderGraphBuilder builder, RGResourceHandle hVisibleClusters, RGResourceHandle hDebugIndirectArgs, RGResourceHandle hDrawUB, RGResourceHandle hPageHeap, RGResourceHandle hColor, RGResourceHandle hDepth)
    {
        builder.ReadBuffer(hVisibleClusters, ResourceState.ShaderResource);
        builder.ReadBuffer(hDebugIndirectArgs, ResourceState.IndirectArgument);
        builder.ReadBuffer(hDrawUB, ResourceState.ConstantBuffer);
        builder.ReadBuffer(hPageHeap, ResourceState.ShaderResource);
        builder.WriteTexture(hColor, ResourceState.RenderTarget);
        builder.WriteTexture(hDepth, ResourceState.DepthWrite);
    }

    public void ExecuteSphereDraw(RenderContext context, RenderGraphContext rgCtx, RGResourceHandle hVisibleClusters, RGResourceHandle hDebugIndirectArgs, RGResourceHandle hPageHeap, IBuffer drawUniformBuffer)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || _debugSpherePSO == null || _debugSphereSRB == null)
            return;

        var visible = rgCtx.GetBuffer(hVisibleClusters);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        var pageHeap = rgCtx.GetBuffer(hPageHeap);

        if (visible == null || debugIndirectArgs == null || pageHeap == null)
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
        _bvhDebugSRB?.Dispose();
        _bvhDebugPSO?.Dispose();
        _debugCopySRB?.Dispose();
        _debugCopyPSO?.Dispose();
        _debugSphereSRB?.Dispose();
        _debugSpherePSO?.Dispose();
    }
}
