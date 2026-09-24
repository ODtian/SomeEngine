using System.Runtime.InteropServices;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Pipelines;
using SomeEngine.Rhi;
using static SomeEngine.Tests.RenderGraphTestHelpers;

namespace SomeEngine.Tests.Pipelines;

public sealed class SlotGpuTests
{
    [Fact]
    public void AddBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new ClusterSlotGpu(device);
        using var graph = new RenderGraph();
        ClusterSlotBuffer slots = CreateSlots();

        graph.BeginFrame();
        RenderGraphHandle handle = gpu.Add(graph, slots);
        graph.AddRasterPass(
            "Read Slots",
            builder => builder.Read(handle, ResourceState.ShaderResource),
            context =>
            {
                BufferDesc desc = context.GetBufferDesc(handle);
                Assert.Equal(MemoryClass.DeviceLocal, desc.Memory);
                Assert.True(desc.BindFlags.HasFlag(BindFlags.ShaderResource));
                Assert.True(desc.BindFlags.HasFlag(BindFlags.CopyDestination));
                Assert.Equal(4u, desc.StrideInBytes);
                Assert.True(context.GetBufferView(handle, ViewKind.ShaderResource).IsValid);
            });

        graph.Execute(device, queue);
    }

    [Fact]
    public void ReuseBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new ClusterSlotGpu(device);
        using var graph = new RenderGraph();
        ClusterSlotBuffer slots = CreateSlots();

        string[] first = RunFrame(graph, gpu, slots, device, queue);
        BufferHandle buffer = gpu.Buffer;
        int buffers = LiveHandleCount(device, "Buffers");
        string[] steady = RunFrame(graph, gpu, slots, device, queue);

        Assert.Contains("SlotBuffer Upload", first);
        Assert.DoesNotContain("SlotBuffer Upload", steady);
        Assert.Equal(buffer, gpu.Buffer);
        Assert.Equal(buffers, LiveHandleCount(device, "Buffers"));
    }

    [Fact]
    public void DirtyUploads()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new ClusterSlotGpu(device);
        using var graph = new RenderGraph();
        ClusterSlotBuffer slots = CreateSlots();

        RunFrame(graph, gpu, slots, device, queue);
        BufferHandle buffer = gpu.Buffer;
        slots.SetField(0, 1, 1, 7);
        string[] changed = RunFrame(graph, gpu, slots, device, queue);

        Assert.Contains("SlotBuffer Upload", changed);
        Assert.Equal(buffer, gpu.Buffer);
    }

    [Fact]
    public void GrowBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new ClusterSlotGpu(device);
        using var graph = new RenderGraph();
        ClusterSlotBuffer slots = CreateSlots();

        RunFrame(graph, gpu, slots, device, queue);
        BufferHandle small = gpu.Buffer;
        slots.AllocateRange(8);
        RunFrame(graph, gpu, slots, device, queue);

        Assert.NotEqual(small, gpu.Buffer);
        Assert.Equal((ulong)MemoryMarshal.AsBytes(slots.GetData()).Length, device.GetBufferDesc(gpu.Buffer).SizeInBytes);
    }

    [Fact]
    public void DirtyFields()
    {
        var slots = new ClusterSlotBuffer(fields: 2, initialCapacity: 2);

        Assert.True(slots.NeedsFull);

        slots.AllocateRange(2);
        slots.ClearDirty();
        slots.SetField(0, 1, 1, 7);

        Assert.False(slots.NeedsFull);
        Assert.False(slots.TryDirty(0, out _, out _));
        Assert.True(slots.TryDirty(1, out int min, out int max));
        Assert.Equal(1, min);
        Assert.Equal(1, max);

        slots.AllocateRange(4);

        Assert.True(slots.NeedsFull);
    }

    [Fact]
    public void ExactGrow()
    {
        var slots = new ClusterSlotBuffer(fields: 2, initialCapacity: 4);

        slots.AllocateRange(4);
        slots.ClearDirty();
        slots.AllocateRange(1);

        Assert.Equal(6, slots.Capacity);
        Assert.True(slots.NeedsFull);
    }

    [Fact]
    public void FreeRangeWritesInvalid()
    {
        var slots = new ClusterSlotBuffer(fields: 2, initialCapacity: 4);
        int offset = slots.AllocateRange(2);
        slots.SetField(offset, 0, 0, 3);
        slots.SetField(offset, 1, 0, 4);
        slots.SetField(offset, 0, 1, 5);
        slots.SetField(offset, 1, 1, 6);

        slots.FreeRange(offset, 2);

        Assert.Equal(ushort.MaxValue, slots.GetField(offset, 0, 0));
        Assert.Equal(ushort.MaxValue, slots.GetField(offset, 1, 0));
        Assert.Equal(ushort.MaxValue, slots.GetField(offset, 0, 1));
        Assert.Equal(ushort.MaxValue, slots.GetField(offset, 1, 1));
    }

    [Fact]
    public void LayoutSpans()
    {
        var layout = new ClusterSlotLayout(fields: 2, capacity: 3);

        Assert.Equal(4, layout.Capacity);
        Assert.Equal(8, layout.ElementCount);
        Assert.Equal(16, layout.ByteCount);
        Assert.Equal(6, layout.Grow(5).Capacity);

        ClusterSlotSpan span = layout.Span(field: 1, minSlot: 1, maxSlot: 1);

        Assert.Equal(4, span.Offset);
        Assert.Equal(2, span.Count);
    }

    private static string[] RunFrame(
        RenderGraph graph,
        ClusterSlotGpu gpu,
        ClusterSlotBuffer slots,
        IDevice device,
        IQueue queue)
    {
        graph.BeginFrame();
        RenderGraphHandle handle = gpu.Add(graph, slots);
        graph.AddRasterPass(
            "Read Slots",
            builder => builder.Read(handle, ResourceState.ShaderResource),
            context => Assert.True(context.GetBufferView(handle, ViewKind.ShaderResource).IsValid));
        graph.Execute(device, queue);
        return RenderGraphTestHelpers.ExecutedPassNames(graph);
    }

    private static ClusterSlotBuffer CreateSlots()
    {
        var slots = new ClusterSlotBuffer(fields: 2, initialCapacity: 2);
        slots.AllocateRange(2);
        slots.SetField(0, 0, 0, 1);
        slots.SetField(0, 1, 0, 2);
        slots.SetField(0, 0, 1, 3);
        slots.SetField(0, 1, 1, 4);
        return slots;
    }

}
