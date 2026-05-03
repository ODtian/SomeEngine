using System;
using Diligent;
using Friflo.Engine.ECS;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal static partial class ClusterDraw
{
internal sealed class Resources : IDisposable
{
    internal const string ShaderFile = "cluster_draw.slang";
    internal const string VSMain = "VSMain";
    internal const string PSMain = "PSMain";
    internal const string PSOverdraw = "PSOverdraw";
    internal const string VSVisBuffer = "VSVisBuffer";
    internal const string PSVisBuffer = "PSVisBuffer";

    internal IPipelineState? DrawPSO;
    internal IPipelineState? DrawWireframePSO;
    internal IPipelineState? DrawOverdrawPSO;
    internal IPipelineState? DrawDepthOnlyPSO;
    internal IPipelineState? DrawVisBufferPSO;
    internal IPipelineState? DrawVisBufferTransparentPSO;
    internal ShaderAsset? DrawShaderAsset;

    internal readonly SRBPool DrawPool = new();
    internal readonly SRBPool WireframePool = new();
    internal readonly SRBPool OverdrawPool = new();
    internal readonly SRBPool DepthOnlyPool = new();
    internal readonly SRBPool VisBufferPool = new();
    internal readonly SRBPool VisBufferTransparentPool = new();

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

            string path = ClusterStageUtils.ShaderPath(ShaderFile);
            var drawAsset = SlangShaderImporter.Import(path);
            DrawShaderAsset = drawAsset;
            var vs = drawAsset.CreateShader(context, VSMain);
            var ps = drawAsset.CreateShader(context, PSMain);

            var ci = new GraphicsPipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "Cluster Draw PSO",
                    PipelineType = PipelineType.Graphics,
                    ResourceLayout = new PipelineResourceLayoutDesc
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                GraphicsPipeline = new GraphicsPipelineDesc
                {
                    NumRenderTargets = 1,
                    RTVFormats = [TextureFormat.RGBA8_UNorm],
                    DSVFormat = TextureFormat.D32_Float,
                    InputLayout = new InputLayoutDesc { LayoutElements = [] },
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    RasterizerDesc = new RasterizerStateDesc
                    {
                        CullMode = CullMode.Back,
                        FrontCounterClockwise = true,
                    },
                    DepthStencilDesc = new DepthStencilStateDesc
                    {
                        DepthEnable = true,
                        DepthWriteEnable = true,
                    },
                },
                Vs = vs,
                Ps = ps,
            };

            DrawPSO = device.CreateGraphicsPipelineState(ci);

            ci.PSODesc.Name = "Cluster Draw Wireframe PSO";
            ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Wireframe;
            ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.None;
            DrawWireframePSO = device.CreateGraphicsPipelineState(ci);

            ci.PSODesc.Name = "Cluster Draw Depth Only PSO";
            ci.GraphicsPipeline.RasterizerDesc.FillMode = FillMode.Solid;
            ci.GraphicsPipeline.RasterizerDesc.CullMode = CullMode.Back;
            ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = true;
            ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = true;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].RenderTargetWriteMask = ColorMask.None;
            DrawDepthOnlyPSO = device.CreateGraphicsPipelineState(ci);

            ci.PSODesc.Name = "Cluster Draw Overdraw PSO";
            ci.GraphicsPipeline.DepthStencilDesc.DepthEnable = false;
            ci.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].RenderTargetWriteMask = ColorMask.All;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.One;
            ci.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.One;
            ci.Ps = drawAsset.CreateShader(context, PSOverdraw);
            DrawOverdrawPSO = device.CreateGraphicsPipelineState(ci);

            var vsVB = drawAsset.CreateShader(context, VSVisBuffer);
            var psVB = drawAsset.CreateShader(context, PSVisBuffer);
            var ciVB = new GraphicsPipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "Cluster Draw VisBuffer PSO",
                    PipelineType = PipelineType.Graphics,
                    ResourceLayout = new PipelineResourceLayoutDesc
                    {
                        DefaultVariableType = ShaderResourceVariableType.Dynamic,
                    },
                },
                GraphicsPipeline = new GraphicsPipelineDesc
                {
                    NumRenderTargets = 1,
                    RTVFormats = [TextureFormat.R32_UInt],
                    DSVFormat = TextureFormat.D32_Float,
                    InputLayout = new InputLayoutDesc { LayoutElements = [] },
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    RasterizerDesc = new RasterizerStateDesc
                    {
                        CullMode = CullMode.Back,
                        FrontCounterClockwise = true,
                    },
                    DepthStencilDesc = new DepthStencilStateDesc
                    {
                        DepthEnable = true,
                        DepthWriteEnable = true,
                    },
                },
                Vs = vsVB,
                Ps = psVB,
            };
            DrawVisBufferPSO = device.CreateGraphicsPipelineState(ciVB);

            ciVB.PSODesc.Name = "Cluster Draw VisBuffer Transparent PSO";
            ciVB.GraphicsPipeline.DepthStencilDesc.DepthWriteEnable = false;
            DrawVisBufferTransparentPSO = device.CreateGraphicsPipelineState(ciVB);
            _initialized = true;
        }
    }

    public void Dispose()
    {
        DrawPool.Dispose();
        WireframePool.Dispose();
        OverdrawPool.Dispose();
        DepthOnlyPool.Dispose();
        VisBufferPool.Dispose();
        VisBufferTransparentPool.Dispose();
        DrawPSO?.Dispose();
        DrawWireframePSO?.Dispose();
        DrawOverdrawPSO?.Dispose();
        DrawDepthOnlyPSO?.Dispose();
        DrawVisBufferPSO?.Dispose();
        DrawVisBufferTransparentPSO?.Dispose();
    }
}
}

internal class ClusterDrawPass(
    RenderContext context,
    ClusterDraw.Resources resources,
    string passName = "ClusterDraw"
) : IRenderGraphPass, IDisposable
{
    public string Name { get; } = passName;

    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid,
        HVisibleClustersData = RenderGraphHandle.Invalid,
        HIndirectDrawArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid,
        HDepthTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisBufferTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawDispatchUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusterMeta = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformCache = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;
    public MaterialPSOGroup[]? PSOGroups;
    public bool PSOGroupsAreGraphics;

    private ClusterDebugMode _debugMode;
    private bool _wireframe,
        _overdraw,
        _useVisBuffer,
        _depthWrite = true;

    public int BinIndex { get; set; } = -1;

    public void SetFrameData(
        ClusterDebugMode debugMode,
        bool wireframe,
        bool overdraw,
        bool useVisBuffer = false,
        bool depthWrite = true)
    {
        _debugMode = debugMode;
        _wireframe = wireframe;
        _overdraw = overdraw;
        _useVisBuffer = useVisBuffer;
        _depthWrite = depthWrite;
    }

    public void Init() => resources.EnsureInitialized(context);

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        if (HVisibleClustersData.IsValid)
            builder.Read(HVisibleClustersData, ResourceState.ShaderResource);
        builder.Read(HIndirectDrawArgs, ResourceState.IndirectArgument);
        builder.Read(HDrawUniforms, ResourceState.ConstantBuffer);
        builder.Read(HDrawDispatchUniforms, ResourceState.ConstantBuffer);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        if (HVisibleClusterMeta.IsValid)
            builder.Read(HVisibleClusterMeta, ResourceState.ShaderResource);
        if (HDeformCache.IsValid)
            builder.Read(HDeformCache, ResourceState.ShaderResource);
        if (HCacheOffsets.IsValid)
            builder.Read(HCacheOffsets, ResourceState.ShaderResource);
        if (_useVisBuffer && HVisBufferTarget.IsValid)
            builder.Write(HVisBufferTarget, ResourceState.RenderTarget);
        else
            builder.Write(HColorTarget, ResourceState.RenderTarget);
        builder.Write(HDepthTarget, ResourceState.DepthWrite);
    }

    private static void BindSRB(
        IShaderResourceBinding srb,
        RenderContext context,
        ShaderAsset? vertexAsset,
        ShaderAsset? pixelAsset,
        IBuffer drawUniformBuffer,
        IBuffer drawDispatchUniformBuffer,
        IBuffer visible,
        IBuffer? visibleData,
        IBuffer? pageHeapBuffer,
        IBufferView? globalTransformView,
        IBuffer? metaBuffer,
        IBuffer? deformCacheBuffer,
        IBuffer? cacheOffsetsBuffer)
    {
        srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "Uniforms")
            ?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, pixelAsset, ShaderType.Pixel, "Uniforms")
            ?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "DispatchUniforms")
            ?.Set(drawDispatchUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, pixelAsset, ShaderType.Pixel, "DispatchUniforms")
            ?.Set(drawDispatchUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "BinnedClusterIndexBuffer")
            ?.Set(visible.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (visibleData != null)
        {
            srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "VisibleClusters")
                ?.Set(visibleData.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        }
        srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "PageHeap")
            ?.Set(pageHeapBuffer?.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (metaBuffer != null)
        {
            srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "VisibleClusterMeta")
                ?.Set(metaBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        }
        if (globalTransformView != null)
        {
            srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "Instances")
                ?.Set(globalTransformView, SetShaderResourceFlags.None);
        }
        if (deformCacheBuffer != null)
        {
            srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "DeformCache")
                ?.Set(deformCacheBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        }
        if (cacheOffsetsBuffer != null)
        {
            srb.GetVariableByReflectedBinding(context, vertexAsset, ShaderType.Vertex, "CacheOffsets")
                ?.Set(cacheOffsetsBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        }
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        resources.EnsureInitialized(context);

        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var visible = rgCtx.GetBuffer(HVisibleClusters);
        var drawArgs = rgCtx.GetBuffer(HIndirectDrawArgs);
        var drawUniformBuffer = rgCtx.GetBuffer(HDrawUniforms);
        var drawDispatchUniformBuffer = rgCtx.GetBuffer(HDrawDispatchUniforms);
        if (visible == null || drawArgs == null || drawUniformBuffer == null || drawDispatchUniformBuffer == null)
            return;

        var pageHeapBuffer = rgCtx.GetBuffer(HPageHeap);
        var metaBuffer = HVisibleClusterMeta.IsValid ? rgCtx.GetBuffer(HVisibleClusterMeta) : null;
        var deformCacheBuffer = HDeformCache.IsValid ? rgCtx.GetBuffer(HDeformCache) : null;
        var cacheOffsetsBuffer = HCacheOffsets.IsValid ? rgCtx.GetBuffer(HCacheOffsets) : null;
        var visibleDataBuffer = HVisibleClustersData.IsValid ? rgCtx.GetBuffer(HVisibleClustersData) : null;
        var globalTransformView = rgCtx.GetBufferView(
            HGlobalTransformBuffer,
            BufferViewType.ShaderResource
        );

        void WriteDispatchUniforms(ulong drawArgsOffset)
        {
            var mapped = ctx.MapBuffer<DrawDispatchUniforms>(
                drawDispatchUniformBuffer,
                MapType.Write,
                MapFlags.Discard);
            mapped[0] = new DrawDispatchUniforms
            {
                DrawArgsByteOffset = (uint)drawArgsOffset,
            };
            ctx.UnmapBuffer(drawDispatchUniformBuffer, MapType.Write);
        }

        void BindMaterialResources(
            IShaderResourceBinding srb,
            Entity entity,
            ShaderAsset? vertexAsset,
            ShaderAsset? pixelAsset)
        {
            if (!entity.IsNull
                && entity.TryGetComponent<MaterialRef>(out MaterialRef materialRef)
                && materialRef.Owner != null)
            {
                if (vertexAsset != null)
                    materialRef.Owner.Params.ApplyTo(srb, context, vertexAsset, ShaderType.Vertex);
                if (pixelAsset != null)
                    materialRef.Owner.Params.ApplyTo(srb, context, pixelAsset, ShaderType.Pixel);
            }
        }

        void DrawIndirectWithState(
            IPipelineState pso,
            SRBPool pool,
            ulong drawArgsOffset,
            Entity materialEntity)
        {
            WriteDispatchUniforms(drawArgsOffset);
            var srb = pool.Rent(pso);
            var shaderAsset = resources.DrawShaderAsset;
            BindSRB(
                srb,
                context,
                shaderAsset,
                shaderAsset,
                drawUniformBuffer,
                drawDispatchUniformBuffer,
                visible,
                visibleDataBuffer,
                pageHeapBuffer,
                globalTransformView,
                metaBuffer,
                deformCacheBuffer,
                cacheOffsetsBuffer);
            BindMaterialResources(srb, materialEntity, shaderAsset, shaderAsset);
            ctx.SetPipelineState(pso);
            ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
            ctx.DrawIndirect(
                new DrawIndirectAttribs
                {
                    AttribsBuffer = drawArgs,
                    DrawArgsOffset = drawArgsOffset,
                    Flags = DrawFlags.VerifyAll,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
                }
            );
            pool.Return(srb);
        }

        void DrawIndirectWithGroup(MaterialPSOGroup group, int groupBinIndex)
        {
            if (group.PSO == null || group.SRB == null)
                return;

            int argsBin = group.ArgsBins != null && groupBinIndex < group.ArgsBins.Length
                ? group.ArgsBins[groupBinIndex]
                : group.BinStart + groupBinIndex;
            Entity materialEntity = groupBinIndex < group.Entities.Length
                ? group.Entities[groupBinIndex]
                : default;
            ulong drawArgsOffset = (ulong)(argsBin * 16);
            ShaderAsset? vertexShader = group.VertexVariant.Shader;
            ShaderAsset? pixelShader = group.PixelVariant.Shader;
            if (vertexShader == null || pixelShader == null)
                return;

            WriteDispatchUniforms(drawArgsOffset);
            BindSRB(
                group.SRB,
                context,
                vertexShader,
                pixelShader,
                drawUniformBuffer,
                drawDispatchUniformBuffer,
                visible,
                visibleDataBuffer,
                pageHeapBuffer,
                globalTransformView,
                metaBuffer,
                deformCacheBuffer,
                cacheOffsetsBuffer);
            BindMaterialResources(group.SRB, materialEntity, vertexShader, pixelShader);

            ctx.SetPipelineState(group.PSO);
            ctx.CommitShaderResources(group.SRB, ResourceStateTransitionMode.None);
            ctx.DrawIndirect(
                new DrawIndirectAttribs
                {
                    AttribsBuffer = drawArgs,
                    DrawArgsOffset = drawArgsOffset,
                    Flags = DrawFlags.VerifyAll,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
                }
            );
        }

        bool TryDrawMaterialGraphicsGroups()
        {
            if (!PSOGroupsAreGraphics || !_useVisBuffer || _wireframe || _overdraw || PSOGroups is not { Length: > 0 })
                return false;

            foreach (MaterialPSOGroup group in PSOGroups)
            {
                for (int i = 0; i < group.BinCount; i++)
                {
                    DrawIndirectWithGroup(group, i);
                }
            }

            return true;
        }

        void DrawForMaterialBins(Action<ulong, Entity> draw)
        {
            if (BinIndex >= 0 || PSOGroups is not { Length: > 0 })
            {
                ulong offset = BinIndex >= 0 ? (ulong)(BinIndex * 16) : 0;
                draw(offset, default);
                return;
            }

            foreach (MaterialPSOGroup group in PSOGroups)
            {
                for (int i = 0; i < group.BinCount; i++)
                {
                    int argsBin = group.ArgsBins != null && i < group.ArgsBins.Length
                        ? group.ArgsBins[i]
                        : group.BinStart + i;
                    Entity materialEntity = i < group.Entities.Length ? group.Entities[i] : default;
                    draw((ulong)(argsBin * 16), materialEntity);
                }
            }
        }

        if (_useVisBuffer && HVisBufferTarget.IsValid)
        {
            var vbRtv = rgCtx.GetTextureView(HVisBufferTarget, TextureViewType.RenderTarget);
            var dsv = rgCtx.GetTextureView(HDepthTarget, TextureViewType.DepthStencil);
            var vbPso = _depthWrite
                ? resources.DrawVisBufferPSO
                : resources.DrawVisBufferTransparentPSO;
            var vbPool = _depthWrite
                ? resources.VisBufferPool
                : resources.VisBufferTransparentPool;
            if (vbRtv != null && dsv != null && vbPso != null)
            {
                ctx.SetRenderTargets([vbRtv], dsv, ResourceStateTransitionMode.None);
                if (TryDrawMaterialGraphicsGroups())
                    return;

                DrawForMaterialBins((offset, materialEntity) =>
                    DrawIndirectWithState(vbPso, vbPool, offset, materialEntity));
            }
            return;
        }

        var rtv = rgCtx.GetTextureView(HColorTarget, TextureViewType.RenderTarget);
        var fwdDsv = rgCtx.GetTextureView(HDepthTarget, TextureViewType.DepthStencil);
        if (rtv != null && fwdDsv != null)
            ctx.SetRenderTargets([rtv], fwdDsv, ResourceStateTransitionMode.None);

        if (_overdraw)
        {
            if (resources.DrawDepthOnlyPSO != null)
            {
                DrawForMaterialBins((offset, materialEntity) =>
                    DrawIndirectWithState(resources.DrawDepthOnlyPSO, resources.DepthOnlyPool, offset, materialEntity));
            }

            if (resources.DrawOverdrawPSO != null)
            {
                DrawForMaterialBins((offset, materialEntity) =>
                    DrawIndirectWithState(resources.DrawOverdrawPSO, resources.OverdrawPool, offset, materialEntity));
            }
        }
        else
        {
            IPipelineState? pso = _wireframe ? resources.DrawWireframePSO : resources.DrawPSO;
            var pool = _wireframe ? resources.WireframePool : resources.DrawPool;

            if (pso != null)
            {
                DrawForMaterialBins((offset, materialEntity) =>
                    DrawIndirectWithState(pso, pool, offset, materialEntity));
            }
        }
    }

    public void Dispose() { }
}
