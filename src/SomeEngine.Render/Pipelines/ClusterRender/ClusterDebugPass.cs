using System;
using System.IO;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

public class ClusterDebugPass : IDisposable
{
    public sealed class Resources : IDisposable
    {
        internal const string CopyShaderFile = "debug_args_copy.cs.hlsl";
        internal const string CopyEntryPoint = "main";
        internal const string SphereShaderFile = "debug_sphere.hlsl";
        internal const string SphereVertexEntryPoint = "VSMain";
        internal const string SpherePixelEntryPoint = "PSMain";

        internal IPipelineState? CopyPSO;
        internal IPipelineState? SpherePSO;
        internal ShaderAsset? CopyShaderAsset;
        internal ShaderAsset? SphereShaderAsset;

        internal readonly SRBPool CopyPool = new();
        internal readonly SRBPool SpherePool = new();

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

                string copyPath = ClusterStageUtils.ShaderPath(CopyShaderFile);
                var copyAsset = SlangShaderImporter.Import(copyPath);
                CopyShaderAsset = copyAsset;
                var cs = copyAsset.CreateShader(context, CopyEntryPoint);

                CopyPSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc
                    {
                        Name = "Debug Copy PSO",
                        PipelineType = PipelineType.Compute,
                        ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Dynamic },
                    },
                    Cs = cs,
                });

                string spherePath = ClusterStageUtils.ShaderPath(SphereShaderFile);
                var sphereAsset = SlangShaderImporter.Import(spherePath);
                SphereShaderAsset = sphereAsset;
                var vs = sphereAsset.CreateShader(context, SphereVertexEntryPoint);
                var ps = sphereAsset.CreateShader(context, SpherePixelEntryPoint);

                var graphicsCi = new GraphicsPipelineStateCreateInfo
                {
                    PSODesc = new PipelineStateDesc
                    {
                        Name = "Debug Sphere PSO",
                        PipelineType = PipelineType.Graphics,
                        ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Dynamic },
                    },
                    GraphicsPipeline = new GraphicsPipelineDesc
                    {
                        NumRenderTargets = 1,
                        RTVFormats = [TextureFormat.RGBA8_UNorm],
                        DSVFormat = TextureFormat.D32_Float,
                        InputLayout = new InputLayoutDesc { LayoutElements = Array.Empty<LayoutElement>() },
                        PrimitiveTopology = PrimitiveTopology.TriangleList,
                        RasterizerDesc = new RasterizerStateDesc { FillMode = FillMode.Wireframe, CullMode = CullMode.None },
                        DepthStencilDesc = new DepthStencilStateDesc { DepthEnable = true, DepthWriteEnable = false },
                    },
                    Vs = vs,
                    Ps = ps,
                };
                graphicsCi.GraphicsPipeline.BlendDesc.RenderTargets[0].BlendEnable = true;
                graphicsCi.GraphicsPipeline.BlendDesc.RenderTargets[0].SrcBlend = BlendFactor.SrcAlpha;
                graphicsCi.GraphicsPipeline.BlendDesc.RenderTargets[0].DestBlend = BlendFactor.InvSrcAlpha;

                SpherePSO = device.CreateGraphicsPipelineState(graphicsCi);
                _initialized = true;
            }
        }

        public void Dispose()
        {
            CopyPool.Dispose();
            SpherePool.Dispose();
            CopyPSO?.Dispose();
            SpherePSO?.Dispose();
        }
    }

    private readonly RenderContext _context;
    private readonly Resources _resources;

    public ClusterDebugPass(RenderContext context, Resources resources)
    {
        _context = context;
        _resources = resources;
        _resources.EnsureInitialized(context);
    }

    public void Init() => _resources.EnsureInitialized(_context);

    public void SetupSphereCopy(RenderGraphBuilder builder, RenderGraphHandle hIndirectDrawArgs, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hCopyUB)
    {
        builder.Read(hIndirectDrawArgs, ResourceState.UnorderedAccess);
        builder.Write(hDebugIndirectArgs, ResourceState.UnorderedAccess);
        builder.Read(hCopyUB, ResourceState.ConstantBuffer);
    }

    public void ExecuteSphereCopy(RenderContext context, RenderGraphContext rgCtx, RenderGraphHandle hIndirectDrawArgs, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hCopyUB)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || _resources.CopyPSO == null) return;

        var drawArgs = rgCtx.GetBuffer(hIndirectDrawArgs);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        var copyUniformBuffer = rgCtx.GetBuffer(hCopyUB);
        if (drawArgs == null || debugIndirectArgs == null || copyUniformBuffer == null) return;

        var srb = _resources.CopyPool.Rent(_resources.CopyPSO);

        srb.GetVariableByReflectedBinding(context, _resources.CopyShaderAsset, ShaderType.Compute, "CopyUniforms")?.Set(copyUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, _resources.CopyShaderAsset, ShaderType.Compute, "IndirectArgs")?.Set(drawArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, _resources.CopyShaderAsset, ShaderType.Compute, "DebugArgs")?.Set(debugIndirectArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(_resources.CopyPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

        _resources.CopyPool.Return(srb);
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
        if (ctx == null || _resources.SpherePSO == null) return;

        var visible = rgCtx.GetBuffer(hVisibleClusters);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        var pageHeap = rgCtx.GetBuffer(hPageHeap);
        var drawUniformBuffer = rgCtx.GetBuffer(hDrawUB);

        if (visible == null || debugIndirectArgs == null || pageHeap == null || drawUniformBuffer == null) return;

        var srb = _resources.SpherePool.Rent(_resources.SpherePSO);

        srb.GetVariableByReflectedBinding(context, _resources.SphereShaderAsset, ShaderType.Vertex, "DrawUniforms")?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, _resources.SphereShaderAsset, ShaderType.Vertex, "RequestBuffer")?.Set(visible.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(context, _resources.SphereShaderAsset, ShaderType.Vertex, "PageHeap")?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        ctx.SetPipelineState(_resources.SpherePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DrawIndirect(new DrawIndirectAttribs
        {
            AttribsBuffer = debugIndirectArgs,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
            DrawArgsOffset = 0,
            DrawCount = 1,
            Flags = DrawFlags.None,
        });

        _resources.SpherePool.Return(srb);
    }

    public void Dispose() { }
}
