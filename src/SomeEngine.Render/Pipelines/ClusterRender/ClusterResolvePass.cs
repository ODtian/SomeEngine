using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

internal static class ClusterResolvePSOs
{
    internal static IPipelineState? PSO;
    internal static readonly ConcurrentBag<IShaderResourceBinding> SRBPool = [];
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

            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../assets/Shaders/cluster_resolve.slang"));
            var shaderAsset = SlangShaderImporter.Import(path);
            using var cs = shaderAsset.CreateShader(context, "CSResolve");

            PSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
            {
                PSODesc = new PipelineStateDesc
                {
                    Name = "Cluster Resolve PSO",
                    PipelineType = PipelineType.Compute,
                    ResourceLayout = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Dynamic },
                },
                Cs = cs,
            });

            s_initialized = true;
        }
    }

    internal static IShaderResourceBinding RentSRB()
    {
        if (PSO == null) throw new InvalidOperationException("PSO is not initialized.");
        return SRBPool.TryTake(out var srb) ? srb : PSO.CreateShaderResourceBinding(false);
    }

    internal static void ReturnSRB(IShaderResourceBinding srb) => SRBPool.Add(srb);
}

public class ClusterResolvePass : IRenderGraphPass, IDisposable
{
    public string Name => "Cluster Resolve";
    private readonly RenderContext _context;

    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDepthTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid;

    public ClusterResolvePass(RenderContext context)
    {
        _context = context;
        ClusterResolvePSOs.EnsureInitialized(context);
    }

    public void Init() => ClusterResolvePSOs.EnsureInitialized(_context);

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisBuffer, ResourceState.ShaderResource);
        builder.Read(HDepthTarget, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HGlobalTransformBuffer, ResourceState.ShaderResource);
        builder.Read(HDrawUniforms, ResourceState.ConstantBuffer);
        builder.Write(HColorTarget, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        if (ClusterResolvePSOs.PSO == null) return;
        var ctx = _context.ImmediateContext;
        if (ctx == null) return;

        var visBuffer = rgCtx.GetTexture(HVisBuffer);
        var depthTex = rgCtx.GetTexture(HDepthTarget);
        var visibleClusters = rgCtx.GetBuffer(HVisibleClusters);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var drawUniforms = rgCtx.GetBuffer(HDrawUniforms);
        var colorTarget = rgCtx.GetTexture(HColorTarget);

        if (visBuffer == null || visibleClusters == null || pageHeap == null || drawUniforms == null || colorTarget == null) return;

        var visBufferSRV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.ShaderResource);
        var depthSRV = rgCtx.GetTextureView(HDepthTarget, TextureViewType.ShaderResource);
        var colorUAV = rgCtx.GetTextureView(HColorTarget, TextureViewType.UnorderedAccess);

        if (visBufferSRV == null || colorUAV == null) return;

        var globalTransformView = rgCtx.GetBufferView(HGlobalTransformBuffer, BufferViewType.ShaderResource);

        var srb = ClusterResolvePSOs.RentSRB();

        srb.GetVariableByName(ShaderType.Compute, "Uniforms")?.Set(drawUniforms, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisBuffer")?.Set(visBufferSRV, SetShaderResourceFlags.None);
        if (depthSRV != null)
        {
            srb.GetVariableByName(ShaderType.Compute, "DepthBuffer")?.Set(depthSRV, SetShaderResourceFlags.None);
        }
        srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")?.Set(visibleClusters.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageHeap")?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (globalTransformView != null)
        {
            srb.GetVariableByName(ShaderType.Compute, "Instances")?.Set(globalTransformView, SetShaderResourceFlags.None);
        }
        srb.GetVariableByName(ShaderType.Compute, "OutputColor")?.Set(colorUAV, SetShaderResourceFlags.None);

        var desc = colorTarget.GetDesc();
        uint width = desc.Width;
        uint height = desc.Height;

        ctx.SetPipelineState(ClusterResolvePSOs.PSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = (width + 7) / 8,
            ThreadGroupCountY = (height + 7) / 8,
            ThreadGroupCountZ = 1,
        });

        ClusterResolvePSOs.ReturnSRB(srb);
    }

    public void Dispose() { }
}
