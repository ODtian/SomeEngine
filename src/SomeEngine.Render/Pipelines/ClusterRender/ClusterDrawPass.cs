using System;
using System.Collections.Concurrent;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterDrawPass(
    RenderContext context,
    string passName = "ClusterDraw"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;

    // ═══════════════════════════════════════════════════════════════
    //  Static: PSO（编译一次） + SRB Pool（per-PSO，支持未来多线程）
    // ═══════════════════════════════════════════════════════════════
    private static IPipelineState? s_drawPSO;
    private static IPipelineState? s_drawWireframePSO;
    private static IPipelineState? s_drawOverdrawPSO;
    private static IPipelineState? s_drawDepthOnlyPSO;
    private static IPipelineState? s_drawVisBufferPSO;
    private static IPipelineState? s_drawVisBufferTransparentPSO;

    private static readonly ConcurrentBag<IShaderResourceBinding> s_drawSRBPool = [];
    private static readonly ConcurrentBag<IShaderResourceBinding> s_wireframeSRBPool = [];
    private static readonly ConcurrentBag<IShaderResourceBinding> s_overdrawSRBPool = [];
    private static readonly ConcurrentBag<IShaderResourceBinding> s_depthOnlySRBPool = [];
    private static readonly ConcurrentBag<IShaderResourceBinding> s_visBufferSRBPool = [];
    private static readonly ConcurrentBag<IShaderResourceBinding> s_visBufferTransparentSRBPool = [];

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    private static IShaderResourceBinding RentSRB(IPipelineState pso, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(false);

    private static void ReturnSRB(IShaderResourceBinding srb, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.Add(srb);

    private static void EnsureInitialized(RenderContext context)
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
                    "../../../../../../assets/Shaders/cluster_draw.slang"
                )
            );
            var drawAsset = SlangShaderImporter.Import(path);
            using var vs = drawAsset.CreateShader(context, "VSMain");
            using var ps = drawAsset.CreateShader(context, "PSMain");

            var ci = new GraphicsPipelineStateCreateInfo()
            {
                PSODesc = new PipelineStateDesc()
                {
                    Name = "Cluster Draw PSO",
                    PipelineType = PipelineType.Graphics,
                    ResourceLayout = new PipelineResourceLayoutDesc()
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
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

            s_drawPSO = device.CreateGraphicsPipelineState(ci);

            ci.PSODesc.Name = "Cluster Draw Wireframe PSO";
            ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
            ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
            s_drawWireframePSO = device.CreateGraphicsPipelineState(ci);

            ci.PSODesc.Name = "Cluster Draw Depth Only PSO";
            ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Solid;
            ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.Back;
            ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
            ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = true;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].RenderTargetWriteMask = ColorMask.None;
            s_drawDepthOnlyPSO = device.CreateGraphicsPipelineState(ci);

            ci.PSODesc.Name = "Cluster Draw Overdraw PSO";
            ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = false;
            ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].RenderTargetWriteMask = ColorMask.All;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.One;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.One;
            using var psOD = drawAsset.CreateShader(context, "PSOverdraw");
            ci.Ps = psOD;
            s_drawOverdrawPSO = device.CreateGraphicsPipelineState(ci);

            // VisBuffer PSO: R32_UINT render target, depth write, no blending
            using var vsVB = drawAsset.CreateShader(context, "VSVisBuffer");
            using var psVB = drawAsset.CreateShader(context, "PSVisBuffer");
            var ciVB = new GraphicsPipelineStateCreateInfo()
            {
                PSODesc = new PipelineStateDesc()
                {
                    Name = "Cluster Draw VisBuffer PSO",
                    PipelineType = PipelineType.Graphics,
                    ResourceLayout = new PipelineResourceLayoutDesc()
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                GraphicsPipeline = new GraphicsPipelineDesc()
                {
                    NumRenderTargets = 1,
                    RTVFormats = [TextureFormat.R32_UInt],
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
                Vs = vsVB,
                Ps = psVB,
            };
            s_drawVisBufferPSO = device.CreateGraphicsPipelineState(ciVB);

            // Transparent VisBuffer PSO: depth test YES, depth write NO
            ciVB.PSODesc.Name = "Cluster Draw VisBuffer Transparent PSO";
            ciVB.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
            s_drawVisBufferTransparentPSO = device.CreateGraphicsPipelineState(ciVB);

            s_initialized = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Instance: 只有 handle + config（轻量，每帧 new）
    // ═══════════════════════════════════════════════════════════════
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid,
        HVisibleClustersData = RenderGraphHandle.Invalid,
        HIndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid,
        HDepthTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisBufferTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusterMeta = RenderGraphHandle.Invalid;

    private ClusterDebugMode _debugMode;
    private bool _wireframe,
        _overdraw,
        _useVisBuffer,
        _depthWrite = true;

    /// <summary>
    /// Raster bin index to draw. -1 = use non-binned DrawArgs (offset 0).
    /// >= 0 = use BinnedDrawArgs at offset BinIndex * 16.
    /// </summary>
    public int BinIndex { get; set; } = -1;

    public void SetFrameData(ClusterDebugMode debugMode, bool wireframe, bool overdraw, bool useVisBuffer = false, bool depthWrite = true)
    {
        _debugMode = debugMode;
        _wireframe = wireframe;
        _overdraw = overdraw;
        _useVisBuffer = useVisBuffer;
        _depthWrite = depthWrite;
    }

    public void Init()
    {
        EnsureInitialized(context);
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        if (HVisibleClustersData.IsValid)
            builder.Read(HVisibleClustersData, ResourceState.ShaderResource);
        builder.Read(HIndirectDrawArgs, ResourceState.IndirectArgument);
        builder.Read(HDrawUniforms, ResourceState.ConstantBuffer);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        if (HVisibleClusterMeta.IsValid)
            builder.Read(HVisibleClusterMeta, ResourceState.ShaderResource);
        if (_useVisBuffer && HVisBufferTarget.IsValid)
        {
            builder.Write(HVisBufferTarget, ResourceState.RenderTarget);
        }
        else
        {
            builder.Write(HColorTarget, ResourceState.RenderTarget);
        }
        builder.Write(HDepthTarget, ResourceState.DepthWrite);
    }

    private static void BindSRB(IShaderResourceBinding srb, IBuffer drawUniformBuffer, IBuffer visible, IBuffer? visibleData, IBuffer? pageHeapBuffer, IBufferView? globalTransformView, IBuffer? metaBuffer)
    {
        srb.GetVariableByName(ShaderType.Vertex, "Uniforms")
            ?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Pixel, "Uniforms")
            ?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Vertex, "BinnedClusterIndexBuffer")
            ?.Set(
                visible.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        if (visibleData != null)
        {
            srb.GetVariableByName(ShaderType.Vertex, "VisibleClusters")
                ?.Set(
                    visibleData.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
        }
        srb.GetVariableByName(ShaderType.Vertex, "PageHeap")
            ?.Set(
                pageHeapBuffer?.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        if (metaBuffer != null)
        {
            srb.GetVariableByName(ShaderType.Vertex, "VisibleClusterMeta")
                ?.Set(
                    metaBuffer.GetDefaultView(BufferViewType.ShaderResource),
                    SetShaderResourceFlags.None
                );
        }

        if (globalTransformView != null)
        {
            srb.GetVariableByName(ShaderType.Vertex, "Instances")
                ?.Set(globalTransformView, SetShaderResourceFlags.None);
        }
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        EnsureInitialized(context);

        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        if (visible == null || drawArgs == null)
            return;

        var drawUniformBuffer = rgCtx.GetBuffer(HDrawUniforms);
        if (drawUniformBuffer == null)
            return;

        var pageHeapBuffer = rgCtx.GetBuffer(HPageHeap);
        var metaBuffer = HVisibleClusterMeta.IsValid ? rgCtx.GetBuffer(HVisibleClusterMeta) : null;
        var visibleDataBuffer = HVisibleClustersData.IsValid ? rgCtx.GetBuffer(HVisibleClustersData) : null;
        var globalTransformView = rgCtx.GetBufferView(
            HGlobalTransformBuffer,
            BufferViewType.ShaderResource
        );

        // Compute draw args offset from BinIndex
        ulong drawArgsOffset = BinIndex >= 0 ? (ulong)(BinIndex * 16) : 0;

        // VisBuffer path
        if (_useVisBuffer && HVisBufferTarget.IsValid)
        {
            var vbRtv = rgCtx.GetTextureView(HVisBufferTarget, TextureViewType.RenderTarget);
            var dsv = rgCtx.GetTextureView(HDepthTarget, TextureViewType.DepthStencil);
            var vbPso = _depthWrite ? s_drawVisBufferPSO : s_drawVisBufferTransparentPSO;
            var vbPool = _depthWrite ? s_visBufferSRBPool : s_visBufferTransparentSRBPool;
            if (vbRtv != null && dsv != null && vbPso != null)
            {
                var srb = RentSRB(vbPso, vbPool);
                ctx.SetRenderTargets([vbRtv], dsv, ResourceStateTransitionMode.Verify);
                BindSRB(srb, drawUniformBuffer, visible, visibleDataBuffer, pageHeapBuffer, globalTransformView, metaBuffer);
                ctx.SetPipelineState(vbPso);
                ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
                ctx.DrawIndirect(
                    new DrawIndirectAttribs
                    {
                        AttribsBuffer = drawArgs,
                        DrawArgsOffset = drawArgsOffset,
                        Flags = DrawFlags.VerifyAll,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    }
                );
                ReturnSRB(srb, vbPool);
            }
            return;
        }

        // Original forward shading path
        var rtv = rgCtx.GetTextureView(HColorTarget, TextureViewType.RenderTarget);
        var fwdDsv = rgCtx.GetTextureView(HDepthTarget, TextureViewType.DepthStencil);
        if (rtv != null && fwdDsv != null)
        {
            ctx.SetRenderTargets([rtv], fwdDsv, ResourceStateTransitionMode.Verify);
        }

        if (_overdraw)
        {
            // 1. Depth Only Pre-pass
            if (s_drawDepthOnlyPSO != null)
            {
                var srb = RentSRB(s_drawDepthOnlyPSO, s_depthOnlySRBPool);
                BindSRB(srb, drawUniformBuffer, visible, visibleDataBuffer, pageHeapBuffer, globalTransformView, metaBuffer);
                ctx.SetPipelineState(s_drawDepthOnlyPSO);
                ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
                ctx.DrawIndirect(
                    new DrawIndirectAttribs
                    {
                        AttribsBuffer = drawArgs,
                        DrawArgsOffset = drawArgsOffset,
                        Flags = DrawFlags.VerifyAll,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    }
                );
                ReturnSRB(srb, s_depthOnlySRBPool);
            }

            // 2. Overdraw Additive Blending without depth testing
            if (s_drawOverdrawPSO != null)
            {
                var srb = RentSRB(s_drawOverdrawPSO, s_overdrawSRBPool);
                BindSRB(srb, drawUniformBuffer, visible, visibleDataBuffer, pageHeapBuffer, globalTransformView, metaBuffer);
                ctx.SetPipelineState(s_drawOverdrawPSO);
                ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
                ctx.DrawIndirect(
                    new DrawIndirectAttribs
                    {
                        AttribsBuffer = drawArgs,
                        DrawArgsOffset = drawArgsOffset,
                        Flags = DrawFlags.VerifyAll,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    }
                );
                ReturnSRB(srb, s_overdrawSRBPool);
            }
        }
        else
        {
            IPipelineState? pso = _wireframe ? s_drawWireframePSO : s_drawPSO;
            var pool = _wireframe ? s_wireframeSRBPool : s_drawSRBPool;

            if (pso != null)
            {
                var srb = RentSRB(pso, pool);
                BindSRB(srb, drawUniformBuffer, visible, visibleDataBuffer, pageHeapBuffer, globalTransformView, metaBuffer);
                ctx.SetPipelineState(pso);
                ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
                ctx.DrawIndirect(
                    new DrawIndirectAttribs
                    {
                        AttribsBuffer = drawArgs,
                        DrawArgsOffset = drawArgsOffset,
                        Flags = DrawFlags.VerifyAll,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    }
                );
                ReturnSRB(srb, pool);
            }
        }
    }
    /// <summary>No-op: PSO/SRB are static-cached, instance holds no GPU resources.</summary>
    public void Dispose() { }
}
