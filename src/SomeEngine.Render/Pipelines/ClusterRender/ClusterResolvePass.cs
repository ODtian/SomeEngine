using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public class ClusterResolvePass(RenderContext context) : IRenderGraphPass, IDisposable
{
    public string Name => "Cluster Resolve";
    private ShaderAsset? _shaderAsset;
    private IPipelineState? _pso;
    private IShaderResourceBinding? _srb;
    private bool _initialized;

    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDepthTarget = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HGlobalTransformBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDrawUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HColorTarget = RenderGraphHandle.Invalid;

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
                "../../../../../../assets/Shaders/cluster_resolve.slang"
            )
        );
        _shaderAsset = SlangShaderImporter.Import(path);
        using var cs = _shaderAsset.CreateShader(context, "CSResolve");

        var ci = new ComputePipelineStateCreateInfo()
        {
            PSODesc = new PipelineStateDesc()
            {
                Name = "Cluster Resolve PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = new PipelineResourceLayoutDesc()
                {
                    DefaultVariableType = ShaderResourceVariableType.Dynamic,
                },
            },
            Cs = cs,
        };

        _pso = device.CreateComputePipelineState(ci);
        if (_pso != null)
            _srb = _pso.CreateShaderResourceBinding(false);

        _initialized = true;
    }

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
        if (_pso == null || _srb == null)
            return;
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var visBuffer = rgCtx.GetTexture(HVisBuffer);
        var depthTex = rgCtx.GetTexture(HDepthTarget);
        var visibleClusters = rgCtx.GetBuffer(HVisibleClusters);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var drawUniforms = rgCtx.GetBuffer(HDrawUniforms);
        var colorTarget = rgCtx.GetTexture(HColorTarget);

        if (visBuffer == null || visibleClusters == null || pageHeap == null || drawUniforms == null || colorTarget == null)
            return;

        var visBufferSRV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.ShaderResource);
        var depthSRV = rgCtx.GetTextureView(HDepthTarget, TextureViewType.ShaderResource);
        var colorUAV = rgCtx.GetTextureView(HColorTarget, TextureViewType.UnorderedAccess);

        if (visBufferSRV == null || colorUAV == null)
            return;

        var globalTransformView = rgCtx.GetBufferView(
            HGlobalTransformBuffer,
            BufferViewType.ShaderResource
        );

        _srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(drawUniforms, SetShaderResourceFlags.None);
        _srb.GetVariableByName(ShaderType.Compute, "VisBuffer")
            ?.Set(visBufferSRV, SetShaderResourceFlags.None);
        if (depthSRV != null)
        {
            _srb.GetVariableByName(ShaderType.Compute, "DepthBuffer")
                ?.Set(depthSRV, SetShaderResourceFlags.None);
        }
        _srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
            ?.Set(
                visibleClusters.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        _srb.GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(
                pageHeap.GetDefaultView(BufferViewType.ShaderResource),
                SetShaderResourceFlags.None
            );
        if (globalTransformView != null)
        {
            _srb.GetVariableByName(ShaderType.Compute, "Instances")
                ?.Set(globalTransformView, SetShaderResourceFlags.None);
        }
        _srb.GetVariableByName(ShaderType.Compute, "OutputColor")
            ?.Set(colorUAV, SetShaderResourceFlags.None);

        var desc = colorTarget.GetDesc();
        uint width = desc.Width;
        uint height = desc.Height;

        ctx.SetPipelineState(_pso);
        ctx.CommitShaderResources(_srb, ResourceStateTransitionMode.Verify);
        ctx.DispatchCompute(
            new DispatchComputeAttribs
            {
                ThreadGroupCountX = (width + 7) / 8,
                ThreadGroupCountY = (height + 7) / 8,
                ThreadGroupCountZ = 1,
            }
        );
    }

    public void Dispose()
    {
        _srb?.Dispose();
        _pso?.Dispose();
    }
}
