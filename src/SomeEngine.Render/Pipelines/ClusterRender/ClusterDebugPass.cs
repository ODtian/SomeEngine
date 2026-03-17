using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal static class ClusterDebugPSOs
{
    internal static IPipelineState? CopyPSO;
    internal static IPipelineState? SpherePSO;

    internal static readonly ConcurrentBag<IShaderResourceBinding> CopySRBPool = [];
    internal static readonly ConcurrentBag<IShaderResourceBinding> SphereSRBPool = [];

    private static bool s_initialized;
    private static readonly Lock s_initLock = new();

    internal static void EnsureInitialized(RenderContext context)
    {
        if (s_initialized) return;
        lock (s_initLock)
        {
            if (s_initialized) return;
            var device = context.Device;
            if (device == null) return;

            // Copy PSO
            string copyPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/debug_args_copy.cs.hlsl"));
            var copyAsset = SlangShaderImporter.Import(copyPath);
            using var cs = copyAsset.CreateShader(context, "main");
            
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

            // Sphere PSO
            string spherePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/debug_sphere.hlsl"));
            var sphereAsset = SlangShaderImporter.Import(spherePath);
            using var vs = sphereAsset.CreateShader(context, "VSMain");
            using var ps = sphereAsset.CreateShader(context, "PSMain");

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

            s_initialized = true;
        }
    }

    internal static IShaderResourceBinding RentSRB(IPipelineState pso, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.TryTake(out var srb) ? srb : pso.CreateShaderResourceBinding(false);

    internal static void ReturnSRB(IShaderResourceBinding srb, ConcurrentBag<IShaderResourceBinding> pool)
        => pool.Add(srb);
}

public class ClusterDebugPass : IDisposable
{
    private readonly RenderContext _context;

    public ClusterDebugPass(RenderContext context)
    {
        _context = context;
        ClusterDebugPSOs.EnsureInitialized(context);
    }

    public void Init() => ClusterDebugPSOs.EnsureInitialized(_context);

    public void SetupSphereCopy(RenderGraphBuilder builder, RenderGraphHandle hIndirectDrawArgs, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hCopyUB)
    {
        builder.Read(hIndirectDrawArgs, ResourceState.UnorderedAccess); // Still read/write? Actually it's written in this pass logic but setup says Read. Let's use Write if we modify it.
        builder.Write(hDebugIndirectArgs, ResourceState.UnorderedAccess);
        builder.Read(hCopyUB, ResourceState.ConstantBuffer);
    }

    public void ExecuteSphereCopy(RenderContext context, RenderGraphContext rgCtx, RenderGraphHandle hIndirectDrawArgs, RenderGraphHandle hDebugIndirectArgs, RenderGraphHandle hCopyUB)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || ClusterDebugPSOs.CopyPSO == null) return;

        var drawArgs = rgCtx.GetBuffer(hIndirectDrawArgs);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        var copyUniformBuffer = rgCtx.GetBuffer(hCopyUB);
        if (drawArgs == null || debugIndirectArgs == null || copyUniformBuffer == null) return;

        var srb = ClusterDebugPSOs.RentSRB(ClusterDebugPSOs.CopyPSO, ClusterDebugPSOs.CopySRBPool);

        srb.GetVariableByName(ShaderType.Compute, "CopyUniforms")?.Set(copyUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "IndirectArgs")?.Set(drawArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "DebugArgs")?.Set(debugIndirectArgs.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterDebugPSOs.CopyPSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = 1, ThreadGroupCountY = 1, ThreadGroupCountZ = 1 });

        ClusterDebugPSOs.ReturnSRB(srb, ClusterDebugPSOs.CopySRBPool);
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
        if (ctx == null || ClusterDebugPSOs.SpherePSO == null) return;

        var visible = rgCtx.GetBuffer(hVisibleClusters);
        var debugIndirectArgs = rgCtx.GetBuffer(hDebugIndirectArgs);
        var pageHeap = rgCtx.GetBuffer(hPageHeap);
        var drawUniformBuffer = rgCtx.GetBuffer(hDrawUB);

        if (visible == null || debugIndirectArgs == null || pageHeap == null || drawUniformBuffer == null) return;

        var srb = ClusterDebugPSOs.RentSRB(ClusterDebugPSOs.SpherePSO, ClusterDebugPSOs.SphereSRBPool);

        srb.GetVariableByName(ShaderType.Vertex, "DrawUniforms")?.Set(drawUniformBuffer, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Vertex, "RequestBuffer")?.Set(visible.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Vertex, "PageHeap")?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);

        ctx.SetPipelineState(ClusterDebugPSOs.SpherePSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DrawIndirect(new DrawIndirectAttribs
        {
            AttribsBuffer = debugIndirectArgs,
            AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
            DrawArgsOffset = 0,
            DrawCount = 1,
            Flags = DrawFlags.None,
        });

        ClusterDebugPSOs.ReturnSRB(srb, ClusterDebugPSOs.SphereSRBPool);
    }

    public void Dispose() { }
}
