using System;
using Diligent;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public class ClusterMaterialShadePass(RenderContext context) : IRenderGraphPass, IDisposable
{
    public string Name => "Cluster Material Shade";

    private ShaderAsset? _shaderAsset;
    private IPipelineState? _shadePSO;
    private IShaderResourceBinding? _shadeSRB;
    private bool _initialized;

    // RenderGraph handles
    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstances = RenderGraphHandle.Invalid;
    public RenderGraphHandle HShadeUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPixelCoordBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinIndirectArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HOutputColor = RenderGraphHandle.Invalid;

    /// <summary>
    /// Number of active materials this frame. Set before Execute.
    /// </summary>
    public uint ActiveMaterialCount { get; set; } = 1;

    /// <summary>
    /// Base shade uniform data. MaterialID is overwritten per dispatch iteration.
    /// Set by ClusterRenderFeature before adding the pass.
    /// </summary>
    public ShadeUniforms ShadeUniformData;

    public void Init()
    {
        if (_initialized) return;
        var device = context.Device;
        if (device == null) return;

        string path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../../assets/Shaders/cluster_shade_material.slang"
            )
        );
        _shaderAsset = SlangShaderImporter.Import(path);

        var layout = new PipelineResourceLayoutDesc
        {
            DefaultVariableType = ShaderResourceVariableType.Dynamic,
        };

        using var cs = _shaderAsset.CreateShader(context, "CSMaterialShade");
        _shadePSO = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
        {
            PSODesc = new PipelineStateDesc
            {
                Name = "Material Shade PSO",
                PipelineType = PipelineType.Compute,
                ResourceLayout = layout,
            },
            Cs = cs,
        });
        if (_shadePSO != null)
            _shadeSRB = _shadePSO.CreateShaderResourceBinding(false);

        _initialized = true;
    }

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisBuffer, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HInstances, ResourceState.ShaderResource);
        builder.Read(HShadeUniforms, ResourceState.ConstantBuffer);
        builder.Read(HPixelCoordBuffer, ResourceState.ShaderResource);
        builder.Read(HBinOffsets, ResourceState.ShaderResource);
        builder.Read(HBinIndirectArgs, ResourceState.IndirectArgument);
        builder.Write(HOutputColor, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || _shadePSO == null || _shadeSRB == null)
            return;

        var visBufferSRV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.ShaderResource);
        var visibleClusters = rgCtx.GetBuffer(HVisibleClusters);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var instances = rgCtx.GetBuffer(HInstances);
        var uniformBuf = rgCtx.GetBuffer(HShadeUniforms);
        var pixelCoordBuffer = rgCtx.GetBuffer(HPixelCoordBuffer);
        var binOffsets = rgCtx.GetBuffer(HBinOffsets);
        var binIndirectArgs = rgCtx.GetBuffer(HBinIndirectArgs);
        var outputColor = rgCtx.GetTexture(HOutputColor);

        if (visBufferSRV == null || visibleClusters == null || pageHeap == null
            || instances == null || uniformBuf == null || pixelCoordBuffer == null
            || binOffsets == null || binIndirectArgs == null || outputColor == null)
            return;

        var outputColorUAV = outputColor.GetDefaultView(TextureViewType.UnorderedAccess);
        if (outputColorUAV == null)
            return;

        // Bind common resources (unchanged across materials)
        var srb = _shadeSRB;
        srb.GetVariableByName(ShaderType.Compute, "VisBuffer")
            ?.Set(visBufferSRV, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "VisibleClusters")
            ?.Set(visibleClusters.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PageHeap")
            ?.Set(pageHeap.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Instances")
            ?.Set(instances.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "PixelCoordBuffer")
            ?.Set(pixelCoordBuffer.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "BinOffsets")
            ?.Set(binOffsets.GetDefaultView(BufferViewType.ShaderResource), SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "OutputColor")
            ?.Set(outputColorUAV, SetShaderResourceFlags.None);
        srb.GetVariableByName(ShaderType.Compute, "Uniforms")
            ?.Set(uniformBuf, SetShaderResourceFlags.None);

        ctx.SetPipelineState(_shadePSO);

        // Dispatch each material with indirect args
        var uniformData = ShadeUniformData;
        for (uint matID = 0; matID < ActiveMaterialCount; matID++)
        {
            // Update MaterialID in the Dynamic uniform buffer via Map/Unmap
            uniformData.MaterialID = matID;
            var mapped = ctx.MapBuffer<ShadeUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
            mapped[0] = uniformData;
            ctx.UnmapBuffer(uniformBuf, MapType.Write);

            ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
            ctx.DispatchComputeIndirect(
                new DispatchComputeIndirectAttribs
                {
                    AttribsBuffer = binIndirectArgs,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                    DispatchArgsByteOffset = (ulong)(matID * 12),
                }
            );
        }
    }

    public void Dispose()
    {
        _shadeSRB?.Dispose();
        _shadePSO?.Dispose();
    }
}
