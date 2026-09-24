using SomeEngine.Render.Graph;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Pipelines;

internal static class ClusterBinGpu
{
    public const int RasterMetaStride = 32;
    public const int DeformMetaStride = 16;
    public const int IndexStride = 8;
    public const int UintStride = 4;

    public static RenderGraphHandle Meta(
        RenderGraph graph,
        string name,
        uint count,
        int stride)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (stride <= 0)
            throw new ArgumentOutOfRangeException(nameof(stride), "cluster bin meta stride must be positive.");

        return graph.CreateBuffer(
            name,
            new BufferDesc
            {
                Name = name,
                SizeInBytes = One(count) * checked((ulong)stride),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                InitialState = ResourceState.UnorderedAccess,
                StrideInBytes = checked((uint)stride),
            });
    }

    public static RenderGraphHandle Index(RenderGraph graph, string name)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.CreateBuffer(
            name,
            new BufferDesc
            {
                Name = name,
                SizeInBytes = ClusterLimits.MaxDraws
                    * ClusterLimits.MaxBinnedEntriesPerCluster
                    * IndexStride,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                InitialState = ResourceState.UnorderedAccess,
                StrideInBytes = IndexStride,
            });
    }

    public static RenderGraphHandle Args(
        RenderGraph graph,
        string name,
        ulong bytes,
        bool copySource = false,
        ResourceLifetime lifetime = ResourceLifetime.Pooled)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (bytes == 0)
            throw new ArgumentOutOfRangeException(nameof(bytes), "cluster bin args buffer size must be non-zero.");

        return graph.CreateBuffer(
            name,
            new BufferDesc
            {
                Name = name,
                SizeInBytes = bytes,
                BindFlags = BindFlags.UnorderedAccess
                    | BindFlags.ShaderResource
                    | BindFlags.IndirectArgument
                    | BindFlags.CopyDestination
                    | (copySource ? BindFlags.CopySource : BindFlags.None),
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            },
            lifetime);
    }

    public static RenderGraphHandle Uint(
        RenderGraph graph,
        string name,
        uint count,
        bool copy,
        ResourceLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ResourceLifetime actualLifetime = lifetime ?? (copy ? ResourceLifetime.Transient : ResourceLifetime.Pooled);
        return graph.CreateBuffer(
            name,
            new BufferDesc
            {
                Name = name,
                SizeInBytes = One(count) * UintStride,
                BindFlags = BindFlags.UnorderedAccess
                    | BindFlags.ShaderResource
                    | (copy ? BindFlags.CopyDestination : BindFlags.None),
                InitialState = copy ? ResourceState.CopyDestination : ResourceState.UnorderedAccess,
                StrideInBytes = UintStride,
            },
            actualLifetime);
    }

    private static ulong One(uint count)
        => Math.Max(count, 1u);
}
