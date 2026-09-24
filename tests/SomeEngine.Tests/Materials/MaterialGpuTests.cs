using SomeEngine.Render.Graph;
using SomeEngine.Render.Materials;
using SomeEngine.Rhi;
using static SomeEngine.Tests.RenderGraphTestHelpers;

namespace SomeEngine.Tests.Materials;

public sealed class MaterialGpuTests
{
    private const byte Float32Scalar = 8;

    [Fact]
    public void AddReusesHandle()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new MaterialGpu(device);
        using var graph = new RenderGraph();
        Material material = CreateMat(36);

        graph.BeginFrame();
        RenderGraphHandle first = gpu.Add(graph, material, "ScalarA");
        RenderGraphHandle second = gpu.Add(graph, material, "ScalarB");

        Assert.Equal(first, second);
        graph.AddRasterPass(
            "Read Scalar",
            builder => builder.Read(first, ResourceState.ShaderResource),
            context =>
            {
                BufferDesc desc = context.GetBufferDesc(first);
                Assert.Equal(MemoryClass.DeviceLocal, desc.Memory);
                Assert.True(desc.BindFlags.HasFlag(BindFlags.CopyDestination));
                Assert.True(desc.BindFlags.HasFlag(BindFlags.ShaderResource));
                Assert.True(desc.Raw);
                Assert.True(context.GetBufferView(first, ViewKind.ShaderResource, raw: true).IsValid);
            });

        graph.Execute(device, queue);
    }

    [Fact]
    public void ReuseBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new MaterialGpu(device);
        using var graph = new RenderGraph();
        Material material = CreateMat(36);

        RunFrame(graph, gpu, material, device, queue, "Scalar");
        BufferHandle buffer = gpu.Get(material);
        int buffers = LiveHandleCount(device, "Buffers");
        int views = LiveHandleCount(device, "BufferViews");

        RunFrame(graph, gpu, material, device, queue, "Scalar");

        Assert.Equal(buffer, gpu.Get(material));
        Assert.Equal(buffers, LiveHandleCount(device, "Buffers"));
        Assert.Equal(views, LiveHandleCount(device, "BufferViews"));
    }

    [Fact]
    public void VersionUploads()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new MaterialGpu(device);
        using var graph = new RenderGraph();
        Material material = CreateMat(36);

        string[] first = RunFrame(graph, gpu, material, device, queue, "Scalar");
        string[] steady = RunFrame(graph, gpu, material, device, queue, "Scalar");
        BufferHandle buffer = gpu.Get(material);

        material.Roughness = 0.75f;
        material.TouchScalars();
        string[] changed = RunFrame(graph, gpu, material, device, queue, "Scalar");

        Assert.Contains("Scalar Upload", first);
        Assert.DoesNotContain("Scalar Upload", steady);
        Assert.Contains("Scalar Upload", changed);
        Assert.Equal(buffer, gpu.Get(material));
    }

    [Fact]
    public void GrowBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new MaterialGpu(device);
        using var graph = new RenderGraph();
        Material material = CreateMat(36);

        RunFrame(graph, gpu, material, device, queue, "Scalar");
        BufferHandle small = gpu.Get(material);

        material.SetScalarLayout(Layout(80));
        RunFrame(graph, gpu, material, device, queue, "Scalar");

        BufferHandle large = gpu.Get(material);
        Assert.NotEqual(small, large);
        Assert.Equal(96ul, device.GetBufferDesc(large).SizeInBytes);

        graph.BeginFrame();
        RenderGraphHandle fallback = gpu.Add(graph, null, Layout(36), "Scalar Fallback");
        graph.AddRasterPass(
            "Read Scalar Fallback",
            builder => builder.Read(fallback, ResourceState.ShaderResource),
            context => Assert.Equal(64ul, context.GetBufferDesc(fallback).SizeInBytes));
        graph.Execute(device, queue);
    }

    [Fact]
    public void RemoveDestroys()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new MaterialGpu(device);
        using var graph = new RenderGraph();
        Material material = CreateMat(36);

        RunFrame(graph, gpu, material, device, queue, "Scalar");
        Assert.True(gpu.Get(material).IsValid);
        graph.BeginFrame();
        int buffers = LiveHandleCount(device, "Buffers");

        gpu.Remove(material);

        Assert.False(gpu.Get(material).IsValid);
        Assert.Equal(buffers - 1, LiveHandleCount(device, "Buffers"));
    }

    private static string[] RunFrame(
        RenderGraph graph,
        MaterialGpu gpu,
        Material material,
        IDevice device,
        IQueue queue,
        string name)
    {
        graph.BeginFrame();
        RenderGraphHandle handle = gpu.Add(graph, material, name);
        graph.AddRasterPass(
            $"Read {name}",
            builder => builder.Read(handle, ResourceState.ShaderResource),
            context => Assert.True(context.GetBufferView(handle, ViewKind.ShaderResource, raw: true).IsValid));
        graph.Execute(device, queue);
        return RenderGraphTestHelpers.ExecutedPassNames(graph);
    }

    private static Material CreateMat(uint payloadBytes)
    {
        var material = new Material();
        material.SetScalarLayout(Layout(payloadBytes));
        material.Roughness = 0.35f;
        return material;
    }

    private static ScalarLayout Layout(uint payloadBytes)
        => ScalarLayout.FromFields(
            [
                new ScalarFieldLayout("Roughness", 0, 4, 1, 1, Float32Scalar),
            ],
            payloadBytes);

}
