using System;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Per-material shade dispatch pass.
/// Iterates over PSOGroups, each group shares a PSO. SRBs are per-bin within the group.
/// </summary>
public class ClusterMaterialShadePass(
    RenderContext context,
    MaterialRegistry registry
) : IRenderGraphPass
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
    public RenderGraphHandle HBinCounts = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinIndirectArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HOutputColor = RenderGraphHandle.Invalid;

    /// <summary>
    /// Base shade uniform data. ShadingBin is overwritten per dispatch iteration.
    /// Set by Feature before adding the pass.
    /// </summary>
    public ShadeUniforms ShadeUniformData;

    /// <summary>
    /// Feature-owned PSOGroups. Each group shares a PSO, contains per-bin SRBs.
    /// </summary>
    public ShadePSOGroup[]? PSOGroups;

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
        builder.Read(HBinCounts, ResourceState.ShaderResource);
        builder.Read(HBinIndirectArgs, ResourceState.IndirectArgument);
        builder.Write(HOutputColor, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = context.ImmediateContext;
        if (ctx == null || PSOGroups == null || PSOGroups.Length == 0)
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
        var binCounts = rgCtx.GetBuffer(HBinCounts);
        var binIndirectArgs = rgCtx.GetBuffer(HBinIndirectArgs);
        var outputColor = rgCtx.GetTexture(HOutputColor);

        if (visBufferSRV == null || visibleClusters == null || pageHeap == null
            || instances == null || instanceHeaders == null || instanceDataHeap == null || uniformBuf == null || pixelCoordBuffer == null
            || binOffsets == null || binCounts == null || binIndirectArgs == null || outputColor == null)
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
            BinCounts = binCounts.GetDefaultView(BufferViewType.ShaderResource),
            OutputColor = outputColorUAV,
            Uniforms = uniformBuf,
        };

        var uniformData = ShadeUniformData;

        // Grouped dispatch — outer loop per PSO group, inner loop per bin
        foreach (var group in PSOGroups)
        {
            if (group.PSO == null || group.SRBs == null)
                continue;

            ctx.SetPipelineState(group.PSO);

            for (int i = 0; i < group.BinCount; i++)
            {
                var srb = group.SRBs[i];
                if (srb == null) continue;

                int bin = group.BinStart + i;
                var pass = group.Passes[i];

                // 1. Pipeline resources (Dynamic)
                pipelineParams.ApplyToSRB(srb);

                // 2. Material resources (Mutable — already bound at rebuild time via pass.ApplyToSRB)

                // 3. Update ShadingBin in uniform buffer
                uniformData.ShadingBin = (uint)bin;
                var mapped = ctx.MapBuffer<ShadeUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
                mapped[0] = uniformData;
                ctx.UnmapBuffer(uniformBuf, MapType.Write);

                // 4. Dispatch
                ctx.CommitShaderResources(srb, ResourceStateTransitionMode.Verify);
                ctx.DispatchComputeIndirect(
                    new DispatchComputeIndirectAttribs
                    {
                        AttribsBuffer = binIndirectArgs,
                        AttribsBufferStateTransitionMode = ResourceStateTransitionMode.Verify,
                        DispatchArgsByteOffset = (ulong)(bin * 12),
                    }
                );
            }
        }
    }
}

