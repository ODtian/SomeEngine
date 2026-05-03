using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Pipelines;

public static partial class ClusterDeformBinStage
{
    public static ClusterDeformBinOutput AddPasses(
        RenderGraph graph,
        RenderContext context,
        Resources resources,
        in ClusterCullOutput cull,
        RenderGraphHandle hInstanceHeaders,
        RenderGraphHandle hDrawArgs,
        RenderGraphHandle hReadOffsetArgs,
        RenderGraphHandle hMaterialSlotBuffer,
        RenderGraphHandle hPageHeap,
        uint slotCapacity,
        uint vertexEvalFieldIndex,
        uint totalBins,
        string? tag = null
    )
    {
        resources.EnsureInitialized(context);

        string prefix = tag != null ? $"{tag}_" : "";
        uint maxBins = totalBins > 0 ? totalBins : 1;

        var hDeformBinMeta = graph.CreateBuffer($"{prefix}DeformBinMeta", new BufferDesc
        {
            Size = maxBins * 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 16,
        });

        var hDeformBinnedClusterIndex = graph.CreateBuffer($"{prefix}DeformBinnedClusterIndexBuffer", new BufferDesc
        {
            Size = ClusterLimits.MaxDraws * ClusterLimits.MaxBinnedEntriesPerCluster * 8UL,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 8,
        });

        var hDeformBinningDispatchArgs = graph.CreateBuffer($"{prefix}DeformBinningDispatchArgs", new BufferDesc
        {
            Size = 12,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        var hPreDeformDispatchArgs = graph.CreateBuffer($"{prefix}PreDeformDispatchArgs", new BufferDesc
        {
            Size = maxBins * 12,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hReserveCounters = graph.CreateBuffer($"{prefix}DeformBinReserveCounters", new BufferDesc
        {
            Size = 4,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 4,
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

        graph.AddPass(new ClusterDeformBinInitPass(context, resources)
        {
            HBinningUniforms = hBinningUniforms,
            HDrawArgs = hDrawArgs,
            HDeformBinningDispatchArgs = hDeformBinningDispatchArgs,
            HDeformBinMeta = hDeformBinMeta,
            MaxBins = maxBins,
            HReserveCounters = hReserveCounters,
        });

        graph.AddPass(new ClusterDeformBinCountPass(context, resources)
        {
            HBinningUniforms = hBinningUniforms,
            HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = hInstanceHeaders,
            HDrawArgs = hDrawArgs,
            HReadOffsetArgs = hReadOffsetArgs,
            HDeformBinningDispatchArgs = hDeformBinningDispatchArgs,
            HDeformBinMeta = hDeformBinMeta,
            HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = hPageHeap,
        });

        graph.AddPass(new ClusterDeformBinReservePass(context, resources)
        {
            HBinningUniforms = hBinningUniforms,
            HDeformBinMeta = hDeformBinMeta,
            HDeformBinningDispatchArgs = hPreDeformDispatchArgs,
            HReserveCounters = hReserveCounters,
            MaxBins = maxBins,
        });

        graph.AddPass(new ClusterDeformBinScatterPass(context, resources)
        {
            HBinningUniforms = hBinningUniforms,
            HVisibleClusters = cull.VisibleClusters,
            HInstanceHeaders = hInstanceHeaders,
            HDrawArgs = hDrawArgs,
            HReadOffsetArgs = hReadOffsetArgs,
            HDeformBinningDispatchArgs = hDeformBinningDispatchArgs,
            HDeformBinMeta = hDeformBinMeta,
            HDeformBinnedClusterBuffer = hDeformBinnedClusterIndex,
            HMaterialSlotBuffer = hMaterialSlotBuffer,
            HPageHeap = hPageHeap,
        });

        return new ClusterDeformBinOutput(hDeformBinnedClusterIndex, hDeformBinMeta, hPreDeformDispatchArgs);
    }
}
