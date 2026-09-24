using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Rhi;

namespace SomeEngine.Tests.Pipelines;

public class BinGpuTests
{
    [Fact]
    public void MetaDesc()
    {
        using var graph = new RenderGraph();

        RenderGraphHandle handle = ClusterBinGpu.Meta(
            graph,
            "RasterMeta",
            3,
            ClusterBinGpu.RasterMetaStride);
        BufferDesc desc = graph.GetBufferDesc(handle);

        Assert.Equal(96ul, desc.SizeInBytes);
        Assert.Equal(32u, desc.StrideInBytes);
        Assert.True(desc.BindFlags.HasFlag(BindFlags.UnorderedAccess));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.ShaderResource));
    }

    [Fact]
    public void IndexDesc()
    {
        using var graph = new RenderGraph();

        RenderGraphHandle handle = ClusterBinGpu.Index(graph, "Index");
        BufferDesc desc = graph.GetBufferDesc(handle);

        ulong expected = (ulong)ClusterLimits.MaxDraws
            * ClusterLimits.MaxBinnedEntriesPerCluster
            * (ulong)ClusterBinGpu.IndexStride;
        Assert.Equal(expected, desc.SizeInBytes);
        Assert.Equal(8u, desc.StrideInBytes);
        Assert.True(desc.BindFlags.HasFlag(BindFlags.UnorderedAccess));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.ShaderResource));
    }

    [Fact]
    public void ArgsDesc()
    {
        using var graph = new RenderGraph();

        RenderGraphHandle handle = ClusterBinGpu.Args(graph, "Args", 16, copySource: true);
        BufferDesc desc = graph.GetBufferDesc(handle);

        Assert.Equal(16ul, desc.SizeInBytes);
        Assert.True(desc.Raw);
        Assert.True(desc.BindFlags.HasFlag(BindFlags.UnorderedAccess));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.IndirectArgument));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.CopyDestination));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.CopySource));
    }

    [Fact]
    public void UintDesc()
    {
        using var graph = new RenderGraph();

        RenderGraphHandle handle = ClusterBinGpu.Uint(graph, "Count", 0, copy: true);
        BufferDesc desc = graph.GetBufferDesc(handle);

        Assert.Equal(4ul, desc.SizeInBytes);
        Assert.Equal(4u, desc.StrideInBytes);
        Assert.True(desc.BindFlags.HasFlag(BindFlags.UnorderedAccess));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.ShaderResource));
        Assert.True(desc.BindFlags.HasFlag(BindFlags.CopyDestination));
    }

    [Fact]
    public void RejectsZero()
    {
        using var graph = new RenderGraph();

        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterBinGpu.Args(graph, "Bad", 0));
    }
}
