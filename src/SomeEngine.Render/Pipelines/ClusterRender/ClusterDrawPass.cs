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
    string passName = "ClusterDraw"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;
    private ShaderAsset? _drawAsset;
    private IPipelineState? _drawPSO;
    private IPipelineState? _drawWireframePSO;
    private IPipelineState? _drawOverdrawPSO;
    private IPipelineState? _drawDepthOnlyPSO;
    private IPipelineState? _drawVisBufferPSO;
    private IPipelineState? _drawVisBufferTransparentPSO;
    private IShaderResourceBinding? _drawSRB;
    private IShaderResourceBinding? _drawWireframeSRB;
    private IShaderResourceBinding? _drawOverdrawSRB;
    private IShaderResourceBinding? _drawDepthOnlySRB;
    private IShaderResourceBinding? _drawVisBufferSRB;
    private IShaderResourceBinding? _drawVisBufferTransparentSRB;
    private bool _initialized;

    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid,
        HVisibleClustersData = RenderGraphHandle.Invalid, // The original data buffer
        HIndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid,
        HDepthTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisBufferTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusterMeta = RenderGraphHandle.Invalid; // Phase2DrawArgs buffer (offset at byte 16)

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

        _drawPSO = device.CreateGraphicsPipelineState(ci);
        if (_drawPSO != null)
            _drawSRB = _drawPSO.CreateShaderResourceBinding(false);

        ci.PSODesc.Name = "Cluster Draw Wireframe PSO";
        ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
        ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
        _drawWireframePSO = device.CreateGraphicsPipelineState(ci);
        if (_drawWireframePSO != null)
            _drawWireframeSRB = _drawWireframePSO.CreateShaderResourceBinding(false);

        ci.PSODesc.Name = "Cluster Draw Depth Only PSO";
        ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Solid;
        ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.Back;
        ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
        ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = true;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].RenderTargetWriteMask = ColorMask.None;
        _drawDepthOnlyPSO = device.CreateGraphicsPipelineState(ci);
        if (_drawDepthOnlyPSO != null)
            _drawDepthOnlySRB = _drawDepthOnlyPSO.CreateShaderResourceBinding(false);

        ci.PSODesc.Name = "Cluster Draw Overdraw PSO";
        ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = false;
        ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].RenderTargetWriteMask = ColorMask.All;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.One;
        ci.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.One;
        using var psOD = _drawAsset.CreateShader(context, "PSOverdraw");
        ci.Ps = psOD;
        _drawOverdrawPSO = device.CreateGraphicsPipelineState(ci);
        if (_drawOverdrawPSO != null)
            _drawOverdrawSRB = _drawOverdrawPSO.CreateShaderResourceBinding(false);

        // VisBuffer PSO: R32_UINT render target, depth write, no blending
        using var vsVB = _drawAsset.CreateShader(context, "VSVisBuffer");
        using var psVB = _drawAsset.CreateShader(context, "PSVisBuffer");
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
        _drawVisBufferPSO = device.CreateGraphicsPipelineState(ciVB);
        if (_drawVisBufferPSO != null)
            _drawVisBufferSRB = _drawVisBufferPSO.CreateShaderResourceBinding(false);

        // Transparent VisBuffer PSO: depth test YES, depth write NO
        ciVB.PSODesc.Name = "Cluster Draw VisBuffer Transparent PSO";
        ciVB.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
        _drawVisBufferTransparentPSO = device.CreateGraphicsPipelineState(ciVB);
        if (_drawVisBufferTransparentPSO != null)
            _drawVisBufferTransparentSRB = _drawVisBufferTransparentPSO.CreateShaderResourceBinding(false);

        _initialized = true;
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

    private void BindSRB(IShaderResourceBinding srb, IBuffer drawUniformBuffer, IBuffer visible, IBuffer? visibleData, IBuffer? pageHeapBuffer, IBufferView? globalTransformView, IBuffer? metaBuffer)
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
            // Select PSO based on depth write mode
            var vbPso = _depthWrite ? _drawVisBufferPSO : _drawVisBufferTransparentPSO;
            var vbSrb = _depthWrite ? _drawVisBufferSRB : _drawVisBufferTransparentSRB;
            if (vbRtv != null && dsv != null && vbPso != null && vbSrb != null)
            {
                ctx.SetRenderTargets([vbRtv], dsv, ResourceStateTransitionMode.Verify);
                BindSRB(vbSrb, drawUniformBuffer, visible, visibleDataBuffer, pageHeapBuffer, globalTransformView, metaBuffer);
                ctx.SetPipelineState(vbPso);
                ctx.CommitShaderResources(vbSrb, ResourceStateTransitionMode.Verify);
                ctx.DrawIndirect(
                    new DrawIndirectAttribs
                    {
                        AttribsBuffer = drawArgs,
                        DrawArgsOffset = drawArgsOffset,
                        Flags = DrawFlags.VerifyAll,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    }
                );
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
            if (_drawDepthOnlyPSO != null && _drawDepthOnlySRB != null)
            {
                BindSRB(_drawDepthOnlySRB, drawUniformBuffer, visible, visibleDataBuffer, pageHeapBuffer, globalTransformView, metaBuffer);
                ctx.SetPipelineState(_drawDepthOnlyPSO);
                ctx.CommitShaderResources(_drawDepthOnlySRB, ResourceStateTransitionMode.Verify);
                ctx.DrawIndirect(
                    new DrawIndirectAttribs
                    {
                        AttribsBuffer = drawArgs,
                        DrawArgsOffset = drawArgsOffset,
                        Flags = DrawFlags.VerifyAll,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    }
                );
            }

            // 2. Overdraw Additive Blending without depth testing
            if (_drawOverdrawPSO != null && _drawOverdrawSRB != null)
            {
                BindSRB(_drawOverdrawSRB, drawUniformBuffer, visible, visibleDataBuffer, pageHeapBuffer, globalTransformView, metaBuffer);
                ctx.SetPipelineState(_drawOverdrawPSO);
                ctx.CommitShaderResources(_drawOverdrawSRB, ResourceStateTransitionMode.Verify);
                ctx.DrawIndirect(
                    new DrawIndirectAttribs
                    {
                        AttribsBuffer = drawArgs,
                        DrawArgsOffset = drawArgsOffset,
                        Flags = DrawFlags.VerifyAll,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    }
                );
            }
        }
        else
        {
            IPipelineState? pso = _wireframe ? _drawWireframePSO : _drawPSO;
            IShaderResourceBinding? srb = _wireframe ? _drawWireframeSRB : _drawSRB;

            if (pso != null && srb != null)
            {
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
            }
        }
    }

    public void Dispose()
    {
        _drawSRB?.Dispose();
        _drawPSO?.Dispose();
        _drawWireframeSRB?.Dispose();
        _drawWireframePSO?.Dispose();
        _drawDepthOnlySRB?.Dispose();
        _drawDepthOnlyPSO?.Dispose();
        _drawOverdrawSRB?.Dispose();
        _drawOverdrawPSO?.Dispose();
        _drawVisBufferSRB?.Dispose();
        _drawVisBufferPSO?.Dispose();
        _drawVisBufferTransparentSRB?.Dispose();
        _drawVisBufferTransparentPSO?.Dispose();
    }
}
