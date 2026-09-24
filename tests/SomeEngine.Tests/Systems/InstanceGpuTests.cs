using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;
using SomeEngine.Rhi;
using SomeECS.Core;
using SomeECS.Core.Entities;
using SomeECS.Core.Queries;
using static SomeEngine.Tests.RenderGraphTestHelpers;

namespace SomeEngine.Tests.Systems;

public sealed class InstanceGpuTests
{
    [Fact]
    public void AddBuffers()
    {
        World world = CreateWorld(1);

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        InstanceFrame frame = gpu.Add(graph, world);

        graph.AddRasterPass(
            "Read instance frame",
            builder =>
            {
                builder.Read(frame.Transform, ResourceState.ShaderResource);
                builder.Read(frame.PrevTransform, ResourceState.ShaderResource);
                builder.Read(frame.Header, ResourceState.ShaderResource);
                builder.Read(frame.Data, ResourceState.ShaderResource);
            },
            context =>
            {
                BufferDesc transform = context.GetBufferDesc(frame.Transform);
                BufferDesc previous = context.GetBufferDesc(frame.PrevTransform);
                BufferDesc header = context.GetBufferDesc(frame.Header);
                BufferDesc heap = context.GetBufferDesc(frame.Data);

                Assert.Equal("Instance Transform", transform.Name);
                Assert.Equal("Instance PrevTransform", previous.Name);
                Assert.Equal("Instance Header", header.Name);
                Assert.Equal("Instance Data", heap.Name);
                Assert.Equal(MemoryClass.DeviceLocal, transform.Memory);
                Assert.Equal(MemoryClass.DeviceLocal, previous.Memory);
                Assert.Equal(MemoryClass.DeviceLocal, header.Memory);
                Assert.Equal(MemoryClass.DeviceLocal, heap.Memory);
                Assert.True(transform.BindFlags.HasFlag(BindFlags.CopyDestination));
                Assert.True(header.BindFlags.HasFlag(BindFlags.CopyDestination));
                Assert.Equal((ulong)GpuTransform.SizeInBytes, transform.SizeInBytes);
                Assert.Equal((ulong)GpuTransform.SizeInBytes, previous.SizeInBytes);
                Assert.Equal((ulong)InstanceHeaderLayout.StrideBytes, header.SizeInBytes);
                Assert.Equal(16ul, heap.SizeInBytes);
                Assert.True(context.GetBufferView(frame.Transform, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetBufferView(frame.PrevTransform, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetBufferView(frame.Header, ViewKind.ShaderResource, raw: true).IsValid);
                Assert.True(context.GetBufferView(frame.Data, ViewKind.ShaderResource, raw: true).IsValid);
            });

        graph.Execute(device, queue);
        Assert.Equal(1, gpu.Count);
        Assert.Equal(0, gpu.MetadataByteCount);
        Assert.Equal(0u, InstanceHeaderLayout.ReadU32(gpu.GetHeader(0), InstanceHeaderLayout.BVHRootIndex));
        Assert.Equal(0u, InstanceHeaderLayout.ReadU32(gpu.GetHeader(0), InstanceHeaderLayout.SlotOffset));
    }

    [Fact]
    public void RecordFrameRequiresPrepareFrame()
    {
        World world = CreateWorld(1);

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();
        using var gpu = new InstanceGpu(device);

        graph.BeginFrame();
        Assert.Throws<InvalidOperationException>(() => gpu.RecordFrame(graph, world));
    }

    [Fact]
    public void ReuseBuffers()
    {
        World world = CreateWorld(0);
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);
        BufferHandle transform = gpu.Transform;
        BufferHandle previous = gpu.PrevTransform;
        BufferHandle header = gpu.Header;
        BufferHandle heap = gpu.Data;
        int buffers = LiveHandleCount(device, "Buffers");
        int views = LiveHandleCount(device, "BufferViews");

        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(transform, gpu.Transform);
        Assert.Equal(previous, gpu.PrevTransform);
        Assert.Equal(header, gpu.Header);
        Assert.Equal(heap, gpu.Data);
        Assert.Equal(buffers, LiveHandleCount(device, "Buffers"));
        Assert.Equal(views, LiveHandleCount(device, "BufferViews"));

        graph.Dispose();
        gpu.Dispose();

        Assert.Equal(0, LiveHandleCount(device, "Buffers"));
        Assert.Equal(0, LiveHandleCount(device, "BufferViews"));
    }

    [Fact]
    public void WriteMarks()
    {
        var world = new World();
        EntityId entity = world.CreateEntity();

        InstanceMarks.Write(
            world,
            entity,
            new RenderInstance
            {
                InstanceIndex = 4,
            },
            InstanceDirtyFlags.Header);

        Assert.Equal(4, world.Read<RenderInstance>(entity).InstanceIndex);
        Assert.Equal(InstanceDirtyFlags.Header, world.Read<InstanceDirty>(entity).Flags);
    }

    [Fact]
    public void SkipsCleanUploads()
    {
        World world = CreateWorld(1);
        EntityId entity = FindInstance(world, 0);
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);
        Assert.True(gpu.LastUploadCount > 0);
        Assert.False(world.IsEnabled<InstanceDirty>(entity));
        Assert.Equal(InstanceDirtyFlags.None, world.Read<InstanceDirty>(entity).Flags);

        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(0, gpu.LastUploadCount);
        Assert.Equal(0, gpu.LastUploadBytes);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void UploadsDirtyTransform()
    {
        World world = CreateWorld(3);
        EntityId entity = FindInstance(world, 1);
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);
        RenderInstance renderInstance = world.Read<RenderInstance>(entity);
        renderInstance.PrevTransform = renderInstance.Transform;
        renderInstance.Transform = GpuTransform.FromQvvs(new TransformQvvs(new Vector3(100, 2, 3), Quaternion.Identity));
        InstanceMarks.Write(world, entity, renderInstance, InstanceDirtyFlags.Transform);

        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(2, gpu.LastUploadCount);
        Assert.Equal(checked(2 * GpuTransform.SizeInBytes), gpu.LastUploadBytes);
        Assert.False(world.IsEnabled<InstanceDirty>(entity));
        Assert.Equal(InstanceDirtyFlags.None, world.Read<InstanceDirty>(entity).Flags);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void RenderWorldPathUploadsOnlyDirtyTransform_WhenVersionChangesWithoutShapeChange()
    {
        RenderWorld renderWorld = new();
        AddInstance(renderWorld, 0);
        EntityId moved = AddInstance(renderWorld, 1);
        AddInstance(renderWorld, 2);
        renderWorld.Version = 1;
        renderWorld.ShapeVersion = 1;
        renderWorld.InstanceShapeVersion = 1;

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, renderWorld, device, queue);
        Assert.True(gpu.LastUploadCount > 0);

        RenderInstance renderInstance = renderWorld.World.Read<RenderInstance>(moved);
        renderInstance.PrevTransform = renderInstance.Transform;
        renderInstance.Transform = GpuTransform.FromQvvs(new TransformQvvs(new Vector3(100, 2, 3), Quaternion.Identity));
        InstanceMarks.Write(renderWorld.World, moved, renderInstance, InstanceDirtyFlags.Transform);
        renderWorld.Version++;

        RunFrame(graph, gpu, renderWorld, device, queue);

        Assert.Equal(2, gpu.LastUploadCount);
        Assert.Equal(checked(2 * GpuTransform.SizeInBytes), gpu.LastUploadBytes);
        Assert.False(renderWorld.World.IsEnabled<InstanceDirty>(moved));
        Assert.Equal(InstanceDirtyFlags.None, renderWorld.World.Read<InstanceDirty>(moved).Flags);

        renderWorld.Version++;
        RunFrame(graph, gpu, renderWorld, device, queue);

        Assert.Equal(0, gpu.LastUploadCount);
        Assert.Equal(0, gpu.LastUploadBytes);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void PrepareFrameThenRecordFrameUploadsDirtyTransform()
    {
        RenderWorld renderWorld = new();
        EntityId moved = AddInstance(renderWorld, 0);
        renderWorld.Version = 1;
        renderWorld.ShapeVersion = 1;
        renderWorld.InstanceShapeVersion = 1;

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        using var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, renderWorld, device, queue);

        RenderInstance renderInstance = renderWorld.World.Read<RenderInstance>(moved);
        renderInstance.PrevTransform = renderInstance.Transform;
        renderInstance.Transform = GpuTransform.FromQvvs(new TransformQvvs(new Vector3(10, 20, 30), Quaternion.Identity));
        InstanceMarks.Write(renderWorld.World, moved, renderInstance, InstanceDirtyFlags.Transform);
        renderWorld.Version++;

        gpu.PrepareFrame(renderWorld);

        graph.BeginFrame();
        InstanceFrame frame = gpu.RecordFrame(graph, renderWorld);
        graph.AddRasterPass(
            "Use prepared instance frame",
            builder =>
            {
                builder.Read(frame.Transform, ResourceState.ShaderResource);
                builder.Read(frame.PrevTransform, ResourceState.ShaderResource);
                builder.Read(frame.Header, ResourceState.ShaderResource);
                builder.Read(frame.Data, ResourceState.ShaderResource);
            },
            _ => { });
        graph.Execute(device, queue);

        Assert.False(renderWorld.World.IsEnabled<InstanceDirty>(moved));
        Assert.Equal(InstanceDirtyFlags.None, renderWorld.World.Read<InstanceDirty>(moved).Flags);
        Assert.Equal(2, gpu.LastUploadCount);
        Assert.Equal(checked(2 * GpuTransform.SizeInBytes), gpu.LastUploadBytes);
    }

    [Fact]
    public void RenderWorldPathConsumesDirectTransformUpdate()
    {
        RenderWorld renderWorld = new();
        AddInstance(renderWorld, 0);
        EntityId moved = AddInstance(renderWorld, 1);
        AddInstance(renderWorld, 2);
        renderWorld.Version = 1;
        renderWorld.ShapeVersion = 1;
        renderWorld.InstanceShapeVersion = 1;

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, renderWorld, device, queue);
        Assert.True(gpu.LastUploadCount > 0);

        RenderInstance renderInstance = renderWorld.World.Read<RenderInstance>(moved);
        renderInstance.PrevTransform = renderInstance.Transform;
        renderInstance.Transform = GpuTransform.FromQvvs(new TransformQvvs(new Vector3(100, 2, 3), Quaternion.Identity));
        renderWorld.World.Get<RenderInstance>(moved) = renderInstance;
        renderWorld.StoreInstance(moved, in renderInstance, hasOverride: false, materialOverride: default);
        renderWorld.AddInstanceUpdate(
            moved,
            renderInstance.InstanceIndex,
            InstanceDirtyFlags.Transform);
        renderWorld.Version++;

        RunFrame(graph, gpu, renderWorld, device, queue);

        Assert.Equal(2, gpu.LastUploadCount);
        Assert.Equal(checked(2 * GpuTransform.SizeInBytes), gpu.LastUploadBytes);
        Assert.Equal(0, renderWorld.InstanceUpdateCount);
        Assert.False(renderWorld.World.IsEnabled<InstanceDirty>(moved));

        renderWorld.Version++;
        RunFrame(graph, gpu, renderWorld, device, queue);

        Assert.Equal(0, gpu.LastUploadCount);
        Assert.Equal(0, gpu.LastUploadBytes);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void RenderWorldPathIgnoresSceneShape_WhenPatchingInstances()
    {
        RenderWorld renderWorld = new();
        AddInstance(renderWorld, 0);
        EntityId moved = AddInstance(renderWorld, 1);
        AddInstance(renderWorld, 2);
        renderWorld.Version = 1;
        renderWorld.ShapeVersion = 1;
        renderWorld.InstanceShapeVersion = 1;

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, renderWorld, device, queue);
        Assert.True(gpu.LastUploadCount > 0);

        RenderInstance renderInstance = renderWorld.World.Read<RenderInstance>(moved);
        renderInstance.PrevTransform = renderInstance.Transform;
        renderInstance.Transform = GpuTransform.FromQvvs(new TransformQvvs(new Vector3(100, 2, 3), Quaternion.Identity));
        InstanceMarks.Write(renderWorld.World, moved, renderInstance, InstanceDirtyFlags.Transform);
        renderWorld.Version++;
        renderWorld.ShapeVersion++;

        RunFrame(graph, gpu, renderWorld, device, queue);

        Assert.Equal(2, gpu.LastUploadCount);
        Assert.Equal(checked(2 * GpuTransform.SizeInBytes), gpu.LastUploadBytes);
        Assert.False(renderWorld.World.IsEnabled<InstanceDirty>(moved));
        Assert.Equal(InstanceDirtyFlags.None, renderWorld.World.Read<InstanceDirty>(moved).Flags);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void RenderWorldPath_MergesUniformFullRangeWithEcsHeaderDirty()
    {
        RenderWorld renderWorld = new();
        EntityId first = AddInstance(renderWorld, 0);
        EntityId changedHeader = AddInstance(renderWorld, 1);
        EntityId third = AddInstance(renderWorld, 2);
        renderWorld.Version = 1;
        renderWorld.ShapeVersion = 1;
        renderWorld.InstanceShapeVersion = 1;

        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, renderWorld, device, queue);
        Assert.True(gpu.LastUploadCount > 0);

        UpdateTransform(renderWorld, first, 10);
        RenderInstance headerInstance = UpdateTransform(renderWorld, changedHeader, 20);
        UpdateTransform(renderWorld, third, 30);
        renderWorld.AddInstanceUpdatesForAllSlots(
            InstanceDirtyFlags.Transform,
            allDataHaveOverride: true);
        headerInstance.BoundsExpansion = 2.5f;
        InstanceMarks.Write(renderWorld.World, changedHeader, headerInstance, InstanceDirtyFlags.Header);
        renderWorld.Version++;

        RunFrame(graph, gpu, renderWorld, device, queue);

        Assert.Equal(3, gpu.LastUploadCount);
        Assert.Equal(
            checked(2 * 3 * GpuTransform.SizeInBytes + InstanceHeaderLayout.StrideBytes),
            gpu.LastUploadBytes);
        Assert.Equal(
            2.5f,
            InstanceHeaderLayout.ReadFloat32(
                gpu.GetHeader(1),
                InstanceHeaderLayout.BoundsExpansionWorld));
        Assert.False(renderWorld.World.IsEnabled<InstanceDirty>(changedHeader));
        Assert.Equal(InstanceDirtyFlags.None, renderWorld.World.Read<InstanceDirty>(changedHeader).Flags);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void UploadsDirtyHeader()
    {
        World world = CreateWorld(2);
        EntityId entity = FindInstance(world, 1);
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);
        RenderInstance renderInstance = world.Read<RenderInstance>(entity);
        renderInstance.BoundsExpansion = 1.25f;
        InstanceMarks.Write(world, entity, renderInstance, InstanceDirtyFlags.Header);
        var headerData = new InstanceHeaderData();
        headerData.SetU32(1, InstanceHeaderLayout.SlotOffset, 96);

        RunFrame(graph, gpu, world, device, queue, headerData);

        Assert.Equal(1, gpu.LastUploadCount);
        Assert.Equal(gpu.Count * InstanceHeaderLayout.StrideBytes, gpu.LastUploadBytes);
        Assert.Equal(96u, InstanceHeaderLayout.ReadU32(gpu.GetHeader(1), InstanceHeaderLayout.SlotOffset));
        Assert.Equal(1.25f, InstanceHeaderLayout.ReadFloat32(gpu.GetHeader(1), InstanceHeaderLayout.BoundsExpansionWorld));
        Assert.False(world.IsEnabled<InstanceDirty>(entity));
        Assert.Equal(InstanceDirtyFlags.None, world.Read<InstanceDirty>(entity).Flags);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void ClearsInactive()
    {
        World world = CreateWorld(0);
        EntityId first = AddInstance(world, 0);
        AddInstance(world, 3);
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(0u, InstanceHeaderLayout.ReadU32(gpu.GetHeader(0), InstanceHeaderLayout.BVHRootIndex));

        world.DestroyEntity(first);
        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(4, gpu.Count);
        Assert.Equal(0u, InstanceHeaderLayout.ReadU32(gpu.GetHeader(0), InstanceHeaderLayout.BVHRootIndex));
        Assert.Equal(0u, InstanceHeaderLayout.ReadU32(gpu.GetHeader(0), InstanceHeaderLayout.SlotOffset));

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void UploadsDirtyData()
    {
        World world = CreateWorld(2);
        EntityId first = FindInstance(world, 0);
        EntityId second = FindInstance(world, 1);
        world.Add(first, new MaterialOverride { BaseColorTint = Vector4.One });
        world.Add(second, new MaterialOverride { BaseColorTint = new Vector4(2, 2, 2, 2) });
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);
        world.AddOrSet(first, new MaterialOverride { BaseColorTint = new Vector4(3, 3, 3, 3) });
        InstanceMarks.Mark(world, first, InstanceDirtyFlags.Data);

        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(1, gpu.LastUploadCount);
        Assert.Equal(UnsafeSize.MaterialOverride, gpu.LastUploadBytes);
        Assert.False(world.IsEnabled<InstanceDirty>(first));
        Assert.Equal(InstanceDirtyFlags.None, world.Read<InstanceDirty>(first).Flags);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void MetadataGrowUploadsData()
    {
        World world = CreateWorld(3);
        EntityId first = FindInstance(world, 0);
        EntityId second = FindInstance(world, 1);
        EntityId third = FindInstance(world, 2);
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);
        BufferHandle transform = gpu.Transform;
        BufferHandle previous = gpu.PrevTransform;
        BufferHandle header = gpu.Header;
        BufferHandle data = gpu.Data;

        world.Add(first, new MaterialOverride { BaseColorTint = Vector4.One });
        world.Add(second, new MaterialOverride { BaseColorTint = new Vector4(2, 2, 2, 2) });
        world.Add(third, new MaterialOverride { BaseColorTint = new Vector4(3, 3, 3, 3) });
        InstanceMarks.Mark(world, first, InstanceDirtyFlags.Header | InstanceDirtyFlags.Data);
        InstanceMarks.Mark(world, second, InstanceDirtyFlags.Header | InstanceDirtyFlags.Data);
        InstanceMarks.Mark(world, third, InstanceDirtyFlags.Header | InstanceDirtyFlags.Data);

        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(transform, gpu.Transform);
        Assert.Equal(previous, gpu.PrevTransform);
        Assert.Equal(header, gpu.Header);
        Assert.NotEqual(data, gpu.Data);
        Assert.Equal(2, gpu.LastUploadCount);
        Assert.Equal(
            checked(3 * InstanceHeaderLayout.StrideBytes + 3 * UnsafeSize.MaterialOverride),
            gpu.LastUploadBytes);
        Assert.False(world.IsEnabled<InstanceDirty>(first));
        Assert.False(world.IsEnabled<InstanceDirty>(second));
        Assert.False(world.IsEnabled<InstanceDirty>(third));
        Assert.Equal(InstanceDirtyFlags.None, world.Read<InstanceDirty>(first).Flags);
        Assert.Equal(InstanceDirtyFlags.None, world.Read<InstanceDirty>(second).Flags);
        Assert.Equal(InstanceDirtyFlags.None, world.Read<InstanceDirty>(third).Flags);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void OffsetsStayStable()
    {
        World world = CreateWorld(3);
        EntityId first = FindInstance(world, 0);
        EntityId third = FindInstance(world, 2);
        world.Add(first, new MaterialOverride { BaseColorTint = Vector4.One });
        world.Add(third, new MaterialOverride { BaseColorTint = new Vector4(3, 3, 3, 3) });
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);

        uint thirdOffset = checked((uint)(2 * UnsafeSize.MaterialOverride));
        Assert.Equal(thirdOffset, world.Read<RenderInstance>(third).DataOffset);
        Assert.Equal(thirdOffset, InstanceHeaderLayout.ReadU32(gpu.GetHeader(2), InstanceHeaderLayout.InstanceDataOffset));

        world.Remove<MaterialOverride>(first);
        InstanceMarks.Mark(world, first, InstanceDirtyFlags.Header | InstanceDirtyFlags.Data);
        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(thirdOffset, world.Read<RenderInstance>(third).DataOffset);
        Assert.Equal(thirdOffset, InstanceHeaderLayout.ReadU32(gpu.GetHeader(2), InstanceHeaderLayout.InstanceDataOffset));
        Assert.Equal(1, gpu.LastUploadCount);
        Assert.Equal(InstanceHeaderLayout.StrideBytes, gpu.LastUploadBytes);

        graph.Dispose();
        gpu.Dispose();
    }

    [Fact]
    public void GrowBuffers()
    {
        World world = CreateWorld(1);
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);
        BufferHandle transform = gpu.Transform;
        BufferHandle header = gpu.Header;

        AddInstance(world, 1);
        AddInstance(world, 2);
        RunFrame(graph, gpu, world, device, queue);

        Assert.NotEqual(transform, gpu.Transform);
        Assert.NotEqual(header, gpu.Header);
        Assert.Equal(checked(3ul * GpuTransform.SizeInBytes), device.GetBufferDesc(gpu.Transform).SizeInBytes);
        Assert.Equal(checked(3ul * InstanceHeaderLayout.StrideBytes), device.GetBufferDesc(gpu.Header).SizeInBytes);

        graph.Dispose();
        gpu.Dispose();

        Assert.Equal(0, LiveHandleCount(device, "Buffers"));
    }

    [Fact]
    public void WritesMetadata()
    {
        World world = CreateWorld(1);
        EntityId entity = FindInstance(world, 0);
        world.Add(entity, new MaterialOverride { BaseColorTint = Vector4.One });
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        IQueue queue = device.GetQueue(QueueType.Graphics);
        var gpu = new InstanceGpu(device);
        using var graph = new RenderGraph();

        RunFrame(graph, gpu, world, device, queue);

        Assert.Equal(UnsafeSize.MaterialOverride, gpu.MetadataByteCount);
        Assert.Equal(
            (uint)InstanceFlags.MaterialOverride,
            InstanceHeaderLayout.ReadU32(gpu.GetHeader(0), InstanceHeaderLayout.InstanceDataFlags));
        Assert.Equal(0u, InstanceHeaderLayout.ReadU32(gpu.GetHeader(0), InstanceHeaderLayout.InstanceDataOffset));
        RenderInstance renderInstance = world.Read<RenderInstance>(entity);
        Assert.Equal(InstanceFlags.MaterialOverride, renderInstance.DataFlags & InstanceFlags.MaterialOverride);
        Assert.Equal(0u, renderInstance.DataOffset);

        graph.Dispose();
        gpu.Dispose();
    }

    private static void RunFrame(
        RenderGraph graph,
        InstanceGpu gpu,
        World world,
        IDevice device,
        IQueue queue,
        InstanceHeaderData? headers = null)
    {
        graph.BeginFrame();
        InstanceFrame frame = gpu.Add(graph, world, headers);
        graph.AddRasterPass(
            "Use instance frame",
            builder =>
            {
                builder.Read(frame.Transform, ResourceState.ShaderResource);
                builder.Read(frame.PrevTransform, ResourceState.ShaderResource);
                builder.Read(frame.Header, ResourceState.ShaderResource);
                builder.Read(frame.Data, ResourceState.ShaderResource);
            },
            context =>
            {
                Assert.True(context.GetBufferView(frame.Transform, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetBufferView(frame.PrevTransform, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetBufferView(frame.Header, ViewKind.ShaderResource, raw: true).IsValid);
                Assert.True(context.GetBufferView(frame.Data, ViewKind.ShaderResource, raw: true).IsValid);
            });
        graph.Execute(device, queue);
        graph.BeginFrame();
    }

    private static void RunFrame(
        RenderGraph graph,
        InstanceGpu gpu,
        RenderWorld renderWorld,
        IDevice device,
        IQueue queue,
        InstanceHeaderData? headers = null)
    {
        graph.BeginFrame();
        InstanceFrame frame = gpu.Add(graph, renderWorld, headers);
        graph.AddRasterPass(
            "Use instance frame",
            builder =>
            {
                builder.Read(frame.Transform, ResourceState.ShaderResource);
                builder.Read(frame.PrevTransform, ResourceState.ShaderResource);
                builder.Read(frame.Header, ResourceState.ShaderResource);
                builder.Read(frame.Data, ResourceState.ShaderResource);
            },
            context =>
            {
                Assert.True(context.GetBufferView(frame.Transform, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetBufferView(frame.PrevTransform, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetBufferView(frame.Header, ViewKind.ShaderResource, raw: true).IsValid);
                Assert.True(context.GetBufferView(frame.Data, ViewKind.ShaderResource, raw: true).IsValid);
            });
        graph.Execute(device, queue);
        graph.BeginFrame();
    }

    private static World CreateWorld(int count)
    {
        var world = new World();
        for (int index = 0; index < count; index++)
            AddInstance(world, index);

        return world;
    }

    private static EntityId AddInstance(World world, int index)
    {
        EntityId source = world.CreateEntity();
        EntityId entity = world.CreateEntity();
        InstanceMarks.Write(
            world,
            entity,
            new RenderInstance
            {
                SourceEntity = source,
                InstanceIndex = index,
                Transform = GpuTransform.FromQvvs(new TransformQvvs(new Vector3(index + 1, 2, 3), Quaternion.Identity)),
                PrevTransform = GpuTransform.FromQvvs(new TransformQvvs(new Vector3(index + 4, 5, 6), Quaternion.Identity)),
                DataOffset = 0,
                DataFlags = 0,
                BoundsExpansion = 0.5f,
            },
            InstanceDirtyFlags.All);
        return entity;
    }

    private static EntityId AddInstance(RenderWorld renderWorld, int index)
    {
        EntityId entity = AddInstance(renderWorld.World, index);
        RenderInstance instance = renderWorld.World.Read<RenderInstance>(entity);
        renderWorld.StoreInstance(entity, in instance, hasOverride: false, materialOverride: default);
        renderWorld.InstanceCount++;
        return entity;
    }

    private static RenderInstance UpdateTransform(RenderWorld renderWorld, EntityId entity, int offset)
    {
        RenderInstance renderInstance = renderWorld.World.Read<RenderInstance>(entity);
        renderInstance.PrevTransform = renderInstance.Transform;
        renderInstance.Transform = GpuTransform.FromQvvs(new TransformQvvs(
            new Vector3(offset, offset + 1, offset + 2),
            Quaternion.Identity));
        renderWorld.World.Get<RenderInstance>(entity) = renderInstance;
        renderWorld.StoreInstance(entity, in renderInstance, hasOverride: false, materialOverride: default);
        return renderInstance;
    }

    private static EntityId FindInstance(World world, int index)
    {
        var query = world.Query(new QueryDefinitionBuilder().Read<RenderInstance>());
        foreach (var chunk in world.RunQuery(query).Chunks)
        {
            ReadOnlySpan<EntityId> entities = chunk.Entities;
            ReadOnlySpan<RenderInstance> instances = chunk.Read<RenderInstance>();
            for (int i = 0; i < instances.Length; i++)
            {
                if (instances[i].InstanceIndex == index)
                    return entities[i];
            }
        }

        throw new InvalidOperationException("test instance was not found.");
    }

    private static class UnsafeSize
    {
        public static int MaterialOverride => System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialOverride>();
    }
}
