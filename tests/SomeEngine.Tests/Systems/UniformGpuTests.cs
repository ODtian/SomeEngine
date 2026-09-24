using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using static SomeEngine.Tests.RenderGraphTestHelpers;

namespace SomeEngine.Tests.Systems;

public sealed class UniformGpuTests
{
    [Fact]
    public void AddBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new UniformGpu<TestUniform>(device);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        UniformFrame frame = gpu.Add(graph, [new TestUniform(1), new TestUniform(2)], "TestUniforms");
        graph.AddRasterPass(
            "Read Uniforms",
            builder => builder.Read(frame.Buffer, ResourceState.ConstantBuffer),
            context =>
            {
                BufferDesc desc = context.GetBufferDesc(frame.Buffer);
                Assert.Equal(2, frame.Count);
                Assert.Equal(256ul, frame.SlotBytes);
                Assert.Equal(512ul, desc.SizeInBytes);
                Assert.Equal(MemoryClass.CpuUpload, desc.Memory);
                Assert.True(desc.BindFlags.HasFlag(BindFlags.ConstantBuffer));
                Assert.False(desc.BindFlags.HasFlag(BindFlags.CopyDestination));
                Assert.Equal(ResourceState.ConstantBuffer, desc.InitialState);
                Assert.True(context.GetBufferView(frame.Buffer, ViewKind.ConstantBuffer, frame.Offset(0), frame.SlotBytes).IsValid);
                Assert.True(context.GetBufferView(frame.Buffer, ViewKind.ConstantBuffer, frame.Offset(1), frame.SlotBytes).IsValid);
            });

        graph.Execute(device, queue);
        Assert.DoesNotContain("TestUniforms Upload", ExecutedPassNames(graph));
    }

    [Fact]
    public void ReuseBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new UniformGpu<TestUniform>(device);
        using var graph = new RenderGraph();

        RenderFrame(graph, gpu, device, queue, [new TestUniform(1), new TestUniform(2)]);
        int buffers = LiveHandleCount(device, "Buffers");
        int views = LiveHandleCount(device, "BufferViews");

        RenderFrame(graph, gpu, device, queue, [new TestUniform(3), new TestUniform(4)]);

        Assert.Equal(buffers, LiveHandleCount(device, "Buffers"));
        Assert.Equal(views, LiveHandleCount(device, "BufferViews"));
    }

    [Fact]
    public void GrowBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new UniformGpu<TestUniform>(device);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        UniformFrame frame = gpu.Add(graph, [new TestUniform(1), new TestUniform(2), new TestUniform(3)], "TestUniforms");
        graph.AddRasterPass(
            "Read Uniforms",
            builder => builder.Read(frame.Buffer, ResourceState.ConstantBuffer),
            context => Assert.Equal(768ul, context.GetBufferDesc(frame.Buffer).SizeInBytes));
        graph.Execute(device, queue);
    }

    [Fact]
    public void RejectDuplicate()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var gpu = new UniformGpu<TestUniform>(device);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        gpu.Add(graph, [new TestUniform(1)], "TestUniforms");

        Assert.Throws<InvalidOperationException>(() =>
            gpu.Add(graph, [new TestUniform(2)], "TestUniforms"));
    }

    [Fact]
    public void PoolAddsTwice()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var pool = new UniformPool<TestUniform>(device);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        UniformFrame first = pool.Add(graph, [new TestUniform(1)], "FirstUniform");
        UniformFrame second = pool.Add(graph, [new TestUniform(2)], "SecondUniform");
        Assert.NotEqual(first.Buffer, second.Buffer);

        graph.AddRasterPass(
            "Read Uniforms",
            builder =>
            {
                builder.Read(first.Buffer, ResourceState.ConstantBuffer);
                builder.Read(second.Buffer, ResourceState.ConstantBuffer);
            },
            context =>
            {
                Assert.True(context.GetBufferView(first.Buffer, ViewKind.ConstantBuffer).IsValid);
                Assert.True(context.GetBufferView(second.Buffer, ViewKind.ConstantBuffer).IsValid);
            });
        graph.Execute(device, queue);
    }

    [Fact]
    public void PoolReuses()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var pool = new UniformPool<TestUniform>(device);
        using var graph = new RenderGraph();

        RenderFrame(graph, pool, device, queue);
        int buffers = LiveHandleCount(device, "Buffers");
        int views = LiveHandleCount(device, "BufferViews");

        RenderFrame(graph, pool, device, queue);

        Assert.Equal(buffers, LiveHandleCount(device, "Buffers"));
        Assert.Equal(views, LiveHandleCount(device, "BufferViews"));
    }

    private static void RenderFrame(
        RenderGraph graph,
        UniformGpu<TestUniform> gpu,
        IDevice device,
        IQueue queue,
        ReadOnlySpan<TestUniform> data)
    {
        graph.BeginFrame();
        UniformFrame frame = gpu.Add(graph, data, "TestUniforms");
        graph.AddRasterPass(
            "Read Uniforms",
            builder => builder.Read(frame.Buffer, ResourceState.ConstantBuffer),
            context => Assert.True(context.GetBufferView(frame.Buffer, ViewKind.ConstantBuffer, frame.Offset(0), frame.SlotBytes).IsValid));
        graph.Execute(device, queue);
    }

    private static void RenderFrame(
        RenderGraph graph,
        UniformPool<TestUniform> pool,
        IDevice device,
        IQueue queue)
    {
        graph.BeginFrame();
        UniformFrame first = pool.Add(graph, [new TestUniform(1)], "FirstUniform");
        UniformFrame second = pool.Add(graph, [new TestUniform(2)], "SecondUniform");
        graph.AddRasterPass(
            "Read Uniforms",
            builder =>
            {
                builder.Read(first.Buffer, ResourceState.ConstantBuffer);
                builder.Read(second.Buffer, ResourceState.ConstantBuffer);
            },
            context => Assert.True(context.GetBufferView(first.Buffer, ViewKind.ConstantBuffer).IsValid));
        graph.Execute(device, queue);
    }

    private readonly record struct TestUniform(uint Value);
}
