using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Per-material shade dispatch pass with all-Dynamic resource binding.
/// Each group's SRB binds both per-pass and per-material resources.
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
    public RenderGraphHandle HMaterialScalarRegion = RenderGraphHandle.Invalid;
    public RenderGraphHandle HPixelCoordBuffer = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinOffsets = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinCounts = RenderGraphHandle.Invalid;
    public RenderGraphHandle HBinIndirectArgs = RenderGraphHandle.Invalid;
    public RenderGraphHandle HOutputColor = RenderGraphHandle.Invalid;
    public RenderGraphHandle HDeformCache = RenderGraphHandle.Invalid;
    public RenderGraphHandle HCacheOffsets = RenderGraphHandle.Invalid;

    public ShadeUniforms ShadeUniformData;
    public MaterialPSOGroup[]? PSOGroups;
    public MaterialResourceFallbacks? MaterialFallbacks;

    public void Setup(RenderGraphBuilder builder)
    {
        builder.Read(HVisBuffer, ResourceState.ShaderResource);
        builder.Read(HVisibleClusters, ResourceState.ShaderResource);
        builder.Read(HPageHeap, ResourceState.ShaderResource);
        builder.Read(HInstances, ResourceState.ShaderResource);
        builder.Read(HInstanceHeaders, ResourceState.ShaderResource);
        builder.Read(HInstanceDataHeap, ResourceState.ShaderResource);
        builder.Read(HShadeUniforms, ResourceState.ConstantBuffer);
        builder.Read(HMaterialScalarRegion, ResourceState.ShaderResource);
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
        if (ctx == null || PSOGroups == null || PSOGroups.Length == 0)
            return;

        // Resolve per-pass resource views
        var visBufferSRV = rgCtx.GetTextureView(HVisBuffer, TextureViewType.ShaderResource);
        var visibleClusters = rgCtx.GetBuffer(HVisibleClusters);
        var pageHeap = rgCtx.GetBuffer(HPageHeap);
        var instances = rgCtx.GetBuffer(HInstances);
        var instanceHeaders = rgCtx.GetBuffer(HInstanceHeaders);
        var instanceDataHeap = rgCtx.GetBuffer(HInstanceDataHeap);
        var uniformBuf = rgCtx.GetBuffer(HShadeUniforms);
        var materialScalarRegion = rgCtx.GetBuffer(HMaterialScalarRegion);
        var pixelCoordBuffer = rgCtx.GetBuffer(HPixelCoordBuffer);
        var binOffsets = rgCtx.GetBuffer(HBinOffsets);
        var binCounts = rgCtx.GetBuffer(HBinCounts);
        var binIndirectArgs = rgCtx.GetBuffer(HBinIndirectArgs);
        var outputColor = rgCtx.GetTexture(HOutputColor);

        if (visBufferSRV == null || visibleClusters == null || pageHeap == null
            || instances == null || instanceHeaders == null || instanceDataHeap == null
            || uniformBuf == null || materialScalarRegion == null || pixelCoordBuffer == null
            || binOffsets == null || binCounts == null || binIndirectArgs == null || outputColor == null)
            return;

        var outputColorUAV = outputColor.GetDefaultView(TextureViewType.UnorderedAccess);
        if (outputColorUAV == null)
            return;

        // Resolve per-pass buffer views once
        var visibleClustersSRV = visibleClusters.GetDefaultView(BufferViewType.ShaderResource);
        var pageHeapSRV = pageHeap.GetDefaultView(BufferViewType.ShaderResource);
        var instancesSRV = instances.GetDefaultView(BufferViewType.ShaderResource);
        var instanceHeadersSRV = instanceHeaders.GetDefaultView(BufferViewType.ShaderResource);
        var instanceDataHeapSRV = instanceDataHeap.GetDefaultView(BufferViewType.ShaderResource);
        var pixelCoordSRV = pixelCoordBuffer.GetDefaultView(BufferViewType.ShaderResource);
        var binOffsetsSRV = binOffsets.GetDefaultView(BufferViewType.ShaderResource);
        var binCountsSRV = binCounts.GetDefaultView(BufferViewType.ShaderResource);
        var materialScalarRegionSRV = materialScalarRegion.GetDefaultView(BufferViewType.ShaderResource);
        if (materialScalarRegionSRV == null)
            return;

        IBufferView? deformCacheSRV = null;
        if (HDeformCache.IsValid)
        {
            var buf = rgCtx.GetBuffer(HDeformCache);
            deformCacheSRV = buf?.GetDefaultView(BufferViewType.ShaderResource);
        }
        IBufferView? cacheOffsetsSRV = null;
        if (HCacheOffsets.IsValid)
        {
            var buf = rgCtx.GetBuffer(HCacheOffsets);
            cacheOffsetsSRV = buf?.GetDefaultView(BufferViewType.ShaderResource);
        }

        var uniformData = ShadeUniformData;

        // ── Grouped dispatch ──
        for (int gi = 0; gi < PSOGroups.Length; gi++)
        {
            var group = PSOGroups[gi];
            if (group.PSO == null || group.SRB == null) continue;
            var shaderAsset = group.ComputeVariant.Shader;
            if (shaderAsset == null) continue;

            ctx.SetPipelineState(group.PSO);

            for (int i = 0; i < group.BinCount; i++)
            {
                int bin = group.BinStart + i;
                int argsBin = group.ArgsBins != null && i < group.ArgsBins.Length ? group.ArgsBins[i] : bin;
                uniformData.ShadingBin = (uint)bin;
                var mapped = ctx.MapBuffer<ShadeUniforms>(uniformBuf, MapType.Write, MapFlags.Discard);
                mapped[0] = uniformData;
                ctx.UnmapBuffer(uniformBuf, MapType.Write);

                // Bind all resources (per-pass + per-material) to the single SRB
                var srb = group.SRB;
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "VisBuffer")?.Set(visBufferSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "VisibleClusters")?.Set(visibleClustersSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "PageHeap")?.Set(pageHeapSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "Instances")?.Set(instancesSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "InstanceHeaders")?.Set(instanceHeadersSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "InstanceDataHeap")?.Set(instanceDataHeapSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "PixelCoordBuffer")?.Set(pixelCoordSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "BinOffsets")?.Set(binOffsetsSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "BinCounts")?.Set(binCountsSRV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "OutputColor")?.Set(outputColorUAV, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "Uniforms")?.Set(uniformBuf, SetShaderResourceFlags.None);
                srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "MaterialScalarRegion")?.Set(materialScalarRegionSRV, SetShaderResourceFlags.AllowOverwrite);

                if (deformCacheSRV != null)
                    srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "DeformCache")?.Set(deformCacheSRV, SetShaderResourceFlags.None);
                if (cacheOffsetsSRV != null)
                    srb.GetVariableByReflectedBinding(_context, shaderAsset, ShaderType.Compute, "CacheOffsets")?.Set(cacheOffsetsSRV, SetShaderResourceFlags.None);

                // Bind per-material resources
                if (group.Entities[i].TryGetComponent<MaterialRef>(out var materialRef) && materialRef.Owner != null)
                {
                    UploadMaterialScalarRegion(ctx, materialScalarRegion, materialRef.Owner);
                    materialRef.Owner.Params.ApplyTo(
                        srb,
                        _context,
                        shaderAsset,
                        ShaderType.Compute,
                        MaterialFallbacks);
                }
                else
                {
                    UploadMaterialScalarRegion(ctx, materialScalarRegion, null);
                }

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

    internal static int GetMaxScalarRegionByteSize(MaterialPSOGroup[]? groups)
    {
        int maxBytes = MaterialScalarRegionLayout.HeaderByteSize;
        if (groups == null)
        {
            return maxBytes;
        }

        foreach (MaterialPSOGroup group in groups)
        {
            foreach (var entity in group.Entities)
            {
                if (entity.TryGetComponent<MaterialRef>(out var materialRef) && materialRef.Owner != null)
                {
                    maxBytes = Math.Max(maxBytes, materialRef.Owner.ScalarRegionByteSize);
                }
            }
        }

        return AlignUp(maxBytes, MaterialScalarRegionLayout.PayloadAlignment);
    }

    private static void UploadMaterialScalarRegion(IDeviceContext ctx, IBuffer buffer, Material? material)
    {
        var mapped = ctx.MapBuffer<byte>(buffer, MapType.Write, MapFlags.Discard);
        mapped.Clear();
        material?.WriteScalarRegion(mapped);
        ctx.UnmapBuffer(buffer, MapType.Write);
    }

    private static int AlignUp(int value, int alignment)
        => ((value + alignment - 1) / alignment) * alignment;
}
