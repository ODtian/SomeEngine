using System;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Per-material shade dispatch pass with dual-signature binding.
/// Sig0 (per-pass) committed once per PSO group via ClusterShade.PerPassSRB.
/// Sig1 (per-material) committed per bin via group SRBs.
/// </summary>
public class ClusterMaterialShadePass : IRenderGraphPass
{
    private readonly RenderContext _context;

    public ClusterMaterialShadePass(RenderContext context)
    {
        _context = context;
    }

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
    public RenderGraphHandle HDeformCache = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;

    public ShadeUniforms ShadeUniformData;
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
        if (HDeformCache.IsValid)
            builder.Read(HDeformCache, ResourceState.ShaderResource);
        if (HCacheOffsets.IsValid)
            builder.Read(HCacheOffsets, ResourceState.ShaderResource);
        builder.Write(HOutputColor, ResourceState.UnorderedAccess);
    }

    public void Execute(RenderGraphContext rgCtx)
    {
        var ctx = _context.ImmediateContext;
        var perPassSRB = ClusterShade.PerPassSRB;
        if (ctx == null || PSOGroups == null || PSOGroups.Length == 0 || perPassSRB == null)
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
            || instances == null || instanceHeaders == null || instanceDataHeap == null
            || uniformBuf == null || pixelCoordBuffer == null
            || binOffsets == null || binCounts == null || binIndirectArgs == null || outputColor == null)
            return;

        var outputColorUAV = outputColor.GetDefaultView(TextureViewType.UnorderedAccess);
        if (outputColorUAV == null)
            return;

        // ── Bind Sig0 pipeline resources once ──
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
        };

        if (HDeformCache.IsValid)
        {
            var buf = rgCtx.GetBuffer(HDeformCache);
            if (buf != null) pipelineParams.DeformCache = buf.GetDefaultView(BufferViewType.ShaderResource);
        }
        if (HCacheOffsets.IsValid)
        {
            var buf = rgCtx.GetBuffer(HCacheOffsets);
            if (buf != null) pipelineParams.CacheOffsets = buf.GetDefaultView(BufferViewType.ShaderResource);
        }

        pipelineParams.ApplyToSRB(perPassSRB);


        // Set Uniforms on all Sig1 SRBs (Dynamic, same buffer)
        foreach (var group in PSOGroups)
        {
            if (group.SRBs == null) continue;
            foreach (var srb in group.SRBs)
                srb?.GetVariableByName(ShaderType.Compute, "Uniforms")?.Set(uniformBuf, SetShaderResourceFlags.None);
        }

        var uniformData = ShadeUniformData;

        // ── Grouped dispatch ──
        for (int gi = 0; gi < PSOGroups.Length; gi++)
        {
            var group = PSOGroups[gi];
            if (group.PSO == null || group.SRBs == null) continue;

            ctx.SetPipelineState(group.PSO);
            ctx.CommitShaderResources(perPassSRB, ResourceStateTransitionMode.None);

            for (int i = 0; i < group.BinCount; i++)
            {
                var srb = group.SRBs[i];
                if (srb == null) continue;

                int bin = group.BinStart + i;
                int argsBin = group.ArgsBins != null && i < group.ArgsBins.Length ? group.ArgsBins[i] : bin;
                uniformData.ShadingBin = (uint)bin;
                var mapped = ctx.MapBuffer<ShadeUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
                mapped[0] = uniformData;
                ctx.UnmapBuffer(uniformBuf, MapType.Write);

                ctx.CommitShaderResources(srb, ResourceStateTransitionMode.None);
                ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
                {
                    AttribsBuffer = binIndirectArgs,
                    AttribsBufferStateTransitionMode = ResourceStateTransitionMode.None,
                    DispatchArgsByteOffset = (ulong)(argsBin * 12),
                });
            }
        }
    }
}
