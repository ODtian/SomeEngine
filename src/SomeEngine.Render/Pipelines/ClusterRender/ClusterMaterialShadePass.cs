using System;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Per-material shade dispatch pass.
/// Iterates over ShaderTypes (PSO switch) and Materials (SRB switch + DispatchIndirect).
/// PSO and SRB are owned by MaterialRegistry, not by this pass.
/// </summary>
public class ClusterMaterialShadePass(RenderContext context, MaterialRegistry registry) : IRenderGraphPass
{
    public string Name => "Cluster Material Shade";

    // RenderGraph handles
    public RenderGraphHandle HVisBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HVisibleClusters = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPageHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstances = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceHeaders = RenderGraphHandle.Invalid;
    public RenderGraphHandle HInstanceDataHeap = RenderGraphHandle.Invalid;
    public RenderGraphHandle HShadeUniforms = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPixelCoordBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinIndirectArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HOutputColor = RenderGraphHandle.Invalid;

    /// <summary>
    /// Base shade uniform data. MaterialID is overwritten per dispatch iteration.
    /// Set by ClusterRenderFeature before adding the pass.
    /// </summary>
    public ShadeUniforms ShadeUniformData;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisBuffer, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HInstances, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HInstanceDataHeap, ResourceState.ShaderResource);
        builder.Read(HShadeUniforms, ResourceState.ConstantBuffer);
        builder.Read(HPixelCoordBuffer, ResourceState.ShaderResource);
        builder.Read(HBinOffsets, ResourceState.ShaderResource);
        builder.Read(HBinIndirectArgs, ResourceState.IndirectArgument);
        builder.Write(HOutputColor, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null)
            return;

        var visBufferSRV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.ShaderResource);
        var visibleClusters = rgCtx.GetBuffer(HVisibleClusters);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var instances = rgCtx.GetBuffer(HInstances);
        var instanceHeaders = rgCtx.GetBuffer(HInstanceHeaders);
        var instanceDataHeap = rgCtx.GetBuffer(HInstanceDataHeap);
        var uniformBuf = rgCtx.GetBuffer(HShadeUniforms);
        var pixelCoordBuffer = rgCtx.GetBuffer(HPixelCoordBuffer);
        var binOffsets = rgCtx.GetBuffer(HBinOffsets);
        var binIndirectArgs = rgCtx.GetBuffer(HBinIndirectArgs);
        var outputColor = rgCtx.GetTexture(HOutputColor);

        if (visBufferSRV == null || visibleClusters == null || pageHeap == null
            || instances == null || instanceHeaders == null || instanceDataHeap == null || uniformBuf == null || pixelCoordBuffer == null
            || binOffsets == null || binIndirectArgs == null || outputColor == null)
            return;

        var outputColorUAV = outputColor.GetDefaultView(TextureViewType.UnorderedAccess);
        if (outputColorUAV == null)
            return;

        // Fill pipeline params from RenderGraph resources
        var pipelineParams = new ClusterShadePipelineParams
        {
            VisBuffer = visBufferSRV,
            VisibleClusters = visibleClusters.GetDefaultView(BufferViewType.ShaderResource),
            PageHeap = pageHeap.GetDefaultView(BufferViewType.ShaderResource),
            Instances = instances.GetDefaultView(BufferViewType.ShaderResource),
            InstanceHeaders = instanceHeaders.GetDefaultView(BufferViewType.ShaderResource),
            InstanceDataHeap = instanceDataHeap.GetDefaultView(BufferViewType.ShaderResource),
            PixelCoordBuffer = pixelCoordBuffer.GetDefaultView(BufferViewType.ShaderResource),
            BinOffsets = binOffsets.GetDefaultView(BufferViewType.ShaderResource),
            OutputColor = outputColorUAV,
            Uniforms = uniformBuf,
        };

        var uniformData = ShadeUniformData;

        // Outer loop: per ShaderType (PSO switch)
        foreach (var shaderType in registry.ShaderTypes)
        {
            ctx.SetPipelineState(shaderType.PSO);

            // Inner loop: per Material (SRB switch + dispatch)
            foreach (var material in registry.GetMaterialsByShaderType(shaderType))
            {
                // 1. Pipeline resources (Dynamic — source generated)
                pipelineParams.ApplyToSRB(material.SRB);

                // 2. Material resources + composed params (Mutable — source generated)
                material.CommitBindings();

                // 3. Update MaterialID in uniform buffer
                uniformData.MaterialID = material.MaterialID;
                var mapped = ctx.MapBuffer<ShadeUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
                mapped[0] = uniformData;
                ctx.UnmapBuffer(uniformBuf, MapType.Write);

                // 4. Dispatch
                ctx.CommitShaderResources(material.SRB, ResourceStateTransitionMode.Verify);
                ctx.DispatchComputeIndirect(
                    new DispatchComputeIndirectAttribs
                    {
                        AttribsBuffer = binIndirectArgs,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                        DispatchArgsByteOffset = (ulong)(material.MaterialID * 12),
                    }
                );
            }
        }
    }
}
