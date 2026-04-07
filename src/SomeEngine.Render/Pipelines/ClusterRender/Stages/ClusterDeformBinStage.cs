using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public static class ClusterDeformBinStage
{
    public static ClusterDeformBinOutput AddPasses(
        RenderGraph graph,
        RenderContext context,
        in ClusterCullOutput cull,
        RenderGraphHandle hInstanceHeaders,
        RenderGraphHandle hDrawArgs,
        RenderGraphHandle hMaterialSlotBuffer,
        RenderGraphHandle hPageHeap,
        uint slotCapacity,
        uint vertexEvalFieldIndex,
        uint totalBins,
        string? tag = null
    )
    {
        ClusterDeformBinPSOs.EnsureInitialized(context);

        string prefix = tag != null ? $"{tag}_" : "";
        uint maxBins = totalBins > 0 ? totalBins : 1;

        var hDeformBinMeta = graph.CreateBuffer($"{prefix}DeformBinMeta", new BufferDesc
        {
            Size = maxBins * 16, // BinCount, Cursor, ClusterOffset, Padding
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 16,
        });

        var hDeformBinnedClusterIndex = graph.CreateBuffer($"{prefix}DeformBinnedClusterIndexBuffer", new BufferDesc
        {
            Size = (ulong)(ClusterLimits.MaxDraws * 8 * 3), // uint2 per entry, max ~3 ranges fast path per cluster
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 8,
        });

        // Used by count/scatter to loop over visible clusters
        var hDeformBinningDispatchArgs = graph.CreateBuffer($"{prefix}DeformBinningDispatchArgs", new BufferDesc
        {
            Size = 12,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        // The actual dispatch args for the final PreDeform passes (one ThreadGroup(X, 1, 1) per bin entry)
        var hPreDeformDispatchArgs = graph.CreateBuffer($"{prefix}PreDeformDispatchArgs", new BufferDesc
        {
            Size = maxBins * 12, // 3 uints per bin
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Structured,
            ElementByteStride = 12,
        });

        var hBinningUniforms = graph.CreateBuffer($"{prefix}DeformBinningUniforms", new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<BinningUniforms>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        var binUniData = new BinningUniforms
        {
            MaxBins = maxBins,
            SlotCapacity = slotCapacity,
            BinFieldIndex = vertexEvalFieldIndex,
            MaxVisibleClusters = ClusterLimits.MaxDraws,
        };

        graph.AddPass<object>(
            $"{prefix}UploadDeformBinningUniforms",
            (builder, _) => { builder.Write(hBinningUniforms, ResourceState.ConstantBuffer); },
            (rgCtx, _) =>
            {
                var ctx = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(hBinningUniforms);
                if (ctx != null && buf != null)
                {
                    var span = ctx.MapBuffer<BinningUniforms>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = binUniData;
                    ctx.UnmapBuffer(buf, MapType.Write);
                }
            }
        );

        graph.AddPass(new ClusterDeformBinInitPass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HDrawArgs = hDrawArgs,
            HDeformBinningDispatchArgs = hDeformBinningDispatchArgs,
            HDeformBinMeta = hDeformBinMeta,
            MaxBins = maxBins,
        });

        graph.AddPass(new ClusterDeformBinCountPass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = hInstanceHeaders,
            HDrawArgs = hDrawArgs,
            HDeformBinningDispatchArgs = hDeformBinningDispatchArgs,
            HDeformBinMeta = hDeformBinMeta,
            HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = hPageHeap,
        });

        graph.AddPass(new ClusterDeformBinReservePass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HDeformBinMeta = hDeformBinMeta,
            HDeformBinningDispatchArgs = hPreDeformDispatchArgs, // Reserve writes the final pre-deform dispatch args here
            MaxBins = maxBins,
        });

        graph.AddPass(new ClusterDeformBinScatterPass(context)
        {
            HBinningUniforms = hBinningUniforms,
            HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = hInstanceHeaders,
            HDrawArgs = hDrawArgs,
            HDeformBinningDispatchArgs = hDeformBinningDispatchArgs,
            HDeformBinMeta = hDeformBinMeta,
            HDeformBinnedClusterBuffer = hDeformBinnedClusterIndex,
            HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = hPageHeap,
        });

        return new ClusterDeformBinOutput(hDeformBinnedClusterIndex, hDeformBinMeta, hPreDeformDispatchArgs);
    }
}
