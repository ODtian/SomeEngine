using System;
using System.IO;
using System.Threading;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public static partial class ClusterShade
{
internal sealed class ResolveResources : IDisposable
{
    internal const string ShaderFile = "cluster_resolve.slang";
    internal const string ResolveEntryPoint = "CSResolve";

    internal IPipelineState? PSO;
    internal ShaderAsset? ShaderAsset;
    internal readonly SRBPool Pool = new();
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
            var shaderAsset = SlangShaderImporter.Import(path);
            ShaderAsset = shaderAsset;
            var cs = shaderAsset.CreateShader(context, ResolveEntryPoint);

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
            _initialized = true;
        }
    }

    public void Dispose()
    {
        Pool.Dispose();
        PSO?.Dispose();
    }
}
}

internal class ClusterResolvePass : IRenderGraphPass, IDisposable
{
    public string Name => "Cluster Resolve";
    private readonly RenderContext _context;
    private readonly ClusterShade.ResolveResources _resources;

    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDepthTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid;

    public ClusterResolvePass(RenderContext context, ClusterShade.ResolveResources resources)
    {
        _context = context;
        _resources = resources;
        _resources.EnsureInitialized(context);
    }

    public void Init() => _resources.EnsureInitialized(_context);

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
        if (_resources.PSO == null) return;
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

        var srb = _resources.Pool.Rent(_resources.PSO);

        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "Uniforms")?.Set(drawUniforms, SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "VisBuffer")?.Set(visBufferSRV, SetShaderResourceFlags.None);
        if (depthSRV != null)
        {
            srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "DepthBuffer")?.Set(depthSRV, SetShaderResourceFlags.None);
        }
        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "VisibleClusters")?.Set(visibleClusters.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "PageHeap")?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        if (globalTransformView != null)
        {
            srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "Instances")?.Set(globalTransformView, SetShaderResourceFlags.None);
        }
        srb.GetVariableByReflectedBinding(_context, _resources.ShaderAsset, ShaderType.Compute, "OutputColor")?.Set(colorUAV, SetShaderResourceFlags.None);

        var desc = colorTarget.GetDesc();
        uint width = desc.Width;
        uint height = desc.Height;

        ctx.SetPipelineState(_resources.PSO);
        ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
        ctx.DispatchCompute(new DispatchComputeAttribs
        {
            ThreadGroupCountX = (width + 7) / 8,
            ThreadGroupCountY = (height + 7) / 8,
            ThreadGroupCountZ = 1,
        });

        _resources.Pool.Return(srb);
    }

    public void Dispose() { }
}
