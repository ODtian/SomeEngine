using ReflectionBindingFlags = System.Reflection.BindingFlags;
using SomeEngine.Core.Collections;
using SomeEngine.Render.Graph;
using SomeEngine.Render.Frame;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;
using static SomeEngine.Tests.RenderGraphTestHelpers;

namespace SomeEngine.Tests;

public sealed class RenderGraphRhiTests
{
    private readonly record struct TestUniform(float X, float Y, float Z, float W);

    [Fact]
    public void ExecuteAndPresent_UsesGraphBarriersAndLeavesSwapchainPresentable()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var swapchainHandle = device.CreateSwapchain(
            new SwapchainDesc
            {
                Name = "render-graph-test-swapchain",
                NativeWindowHandle = 1,
                Width = 64,
                Height = 32,
                Format = Format.Bgra8Unorm,
                BufferCount = 2,
            });
        var swapchain = device.GetSwapchain(swapchainHandle);
        using var graph = new RenderGraph();

        var firstTexture = swapchain.CurrentTexture;
        uint firstIndex = swapchain.CurrentBackBufferIndex;

        graph.BeginFrame();
        var backBuffer = graph.ImportTexture(
            "BackBuffer",
            swapchain.CurrentTexture,
            device.GetTextureDesc(swapchain.CurrentTexture),
            new ImportDesc(ResourceState.Present)
            {
                FinalState = ResourceState.Present,
                AllowWrite = true,
            },
            [swapchain.CurrentRenderTargetView]);
        FrameResources.AddClearPass(graph, backBuffer, new Color(0.1f, 0.2f, 0.3f, 1.0f));
        graph.Execute(device, queue, swapchain);

        Assert.Equal(ResourceState.Present, device.GetTextureState(firstTexture));
        Assert.NotEqual(firstIndex, swapchain.CurrentBackBufferIndex);

        device.Destroy(swapchainHandle);
    }

    [Fact]
    public void Execute_UsesComputeQueueForComputePasses()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var computeOutput = graph.CreateBuffer("ComputeQueueOutput", StorageBufferDesc("ComputeQueueOutput"));
        var graphOutput = graph.CreateBuffer("GraphicsQueueOutput", StorageBufferDesc("GraphicsQueueOutput"));
        graph.AddComputePass(
            "Async Compute",
            builder => builder.Write(computeOutput, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.AddRasterPass(
            "Graphics Consumer",
            builder =>
            {
                builder.Read(computeOutput, ResourceState.ShaderResource);
                builder.Write(graphOutput, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.ExtractBuffer(graphOutput, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Execute(device, new GraphQueues(device, queue));

        Assert.Single(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true));
        Assert.True(RenderGraphTestHelpers.CommandCount(device, "Compute") > 0);
        Assert.True(RenderGraphTestHelpers.CommandCount(device, "Graphics") > 0);
    }

    [Fact]
    public void Execute_ResolvesImportedTextureViewsLazilyOnColdGraph()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "ImportedSrvTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle srv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.ShaderResource,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });

        TextureViewHandle resolved = default;
        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportTexture(
            "ImportedSrvTexture",
            texture,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            [srv]);
        graph.AddRasterPass(
            "Observe Imported Texture View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context => resolved = context.GetTextureView(imported, ViewKind.ShaderResource, desc.Format));

        graph.Execute(device, queue);

        Assert.Equal(srv, resolved);
        device.Destroy(srv);
        device.Destroy(texture);
    }

    [Fact]
    public void Execute_TransitionsImportedTextureCommonToShaderResourceBinding()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "ImportedCommonTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.Common,
        };
        TextureHandle texture = device.CreateTexture(desc);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.TextureRead,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Imported Common Texture Layout");
        var pipeline = StoragePipeline(device, layout);

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportTexture(
            "ImportedCommonTexture",
            texture,
            desc,
            new ImportDesc(ResourceState.Common));
        graph.AddComputePass(
            "Read Imported Common Texture",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Texture(0, imported, BindingType.TextureRead));
            });

        try
        {
            graph.Execute(device, queue);

            Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(texture));
            Assert.Equal(1, RenderGraphTestHelpers.OperationCount(device, "TextureBarrier"));
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
            device.Destroy(texture);
        }
    }

    [Fact]
    public void Execute_ResolvesImportedBufferViewsLazilyOnColdGraph()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "ImportedSrvBuffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
            Raw = true,
        };
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferViewHandle srv = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.ShaderResource,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                Raw = true,
            });

        BufferViewHandle resolved = default;
        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportBuffer(
            "ImportedSrvBuffer",
            buffer,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            [srv]);
        graph.AddRasterPass(
            "Observe Imported Buffer View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context => resolved = context.GetBufferView(imported, ViewKind.ShaderResource, raw: true));

        graph.Execute(device, queue);

        Assert.Equal(srv, resolved);
        device.Destroy(srv);
        device.Destroy(buffer);
    }

    [Fact]
    public void Execute_UsesDeviceQueuesByDefault()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var computeOutput = graph.CreateBuffer("DefaultQueueOutput", StorageBufferDesc("DefaultQueueOutput"));
        graph.AddComputePass(
            "Default Queue Compute",
            builder => builder.Write(computeOutput, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.ExtractBuffer(computeOutput, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Execute(device, queue);

        Assert.Equal(["Compute:Compute:0:1"], RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true, asyncCopy: true));
        Assert.True(RenderGraphTestHelpers.CommandCount(device, "Compute") > 0);
    }

    [Fact]
    public void Execute_UsesComputeQueueForFinalWork()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var output = graph.CreateBuffer(
            "ComputeFinalOutput",
            new BufferDesc
            {
                Name = "ComputeFinalOutput",
                SizeInBytes = 16,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 4,
            });
        graph.AddComputePass(
            "Compute Final Source",
            builder => builder.Write(output, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.ExtractBuffer(output, ResourceState.ShaderResource, (_, _) => { });

        graph.Execute(device, new GraphQueues(device, queue));

        Assert.Equal(["Compute:Compute:0:1"], RenderGraphTestHelpers.QueueBatches(graph, asyncCompute: true, asyncCopy: true));
        Assert.Equal(1, RenderGraphTestHelpers.CommandCount(device, "Compute"));
        Assert.Equal(0, RenderGraphTestHelpers.CommandCount(device, "Graphics"));
    }

    [Fact]
    public void Execute_MergesSameQueueFinalTransitionsIntoLastBatch()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "MergedFinalStateBuffer",
                SizeInBytes = 16,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 4,
            });
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var imported = graph.ImportBuffer(
            "MergedFinalStateImported",
            buffer,
            device.GetBufferDesc(buffer),
            new ImportDesc(ResourceState.ShaderResource)
            {
                FinalState = ResourceState.ShaderResource,
                AllowWrite = true,
            });
        graph.AddRasterPass(
            "Write Imported",
            builder => builder.Write(imported, ResourceState.UnorderedAccess),
            _ => { });

        graph.Execute(device, queue);

        Assert.Equal(1, RenderGraphTestHelpers.CommandCount(device, "Graphics"));
        Assert.Equal(ResourceState.ShaderResource, device.GetBufferState(buffer));
        Assert.Equal(2, RenderGraphTestHelpers.OperationCount(device, "BufferBarrier"));
        device.Destroy(buffer);
    }

    [Fact]
    public void Execute_AcceptsSingleQueue()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var computeOutput = graph.CreateBuffer("SingleQueueOutput", StorageBufferDesc("SingleQueueOutput"));
        graph.AddComputePass(
            "Single Queue Compute",
            builder => builder.Write(computeOutput, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.ExtractBuffer(computeOutput, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Execute(device, new GraphQueues(queue));

        Assert.Equal(["Compute:Graphics:0:1"], RenderGraphTestHelpers.QueueBatches(graph));
        Assert.Empty(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: false));
    }

    [Fact]
    public void Execute_ReusesCompileResult()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        int setupCount = 0;

        graph.BeginFrame();
        graph.AddRasterPass(
            "Compiled Side Effect",
            builder =>
            {
                setupCount++;
                builder.SideEffect();
            },
            _ => { });

        graph.Compile();
        graph.Execute(device, new GraphQueues(queue));

        Assert.Equal(1, setupCount);
    }

    [Fact]
    public void Execute_MaterializesMutableTransitionStateFromCachedCompile()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var output = graph.CreateBuffer("CompiledTransitionOutput", StorageBufferDesc("CompiledTransitionOutput"));
        graph.AddRasterPass(
            "Compiled Transition Writer",
            builder => builder.Write(output, ResourceState.UnorderedAccess),
            _ => { });
        graph.ExtractBuffer(output, ResourceState.ShaderResource, (_, _) => { });

        graph.Compile();
        graph.Execute(device, new GraphQueues(queue));

        object compiler = typeof(RenderGraph).GetField(
            "_compiler",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance)!.GetValue(graph)!;
        object compileResult = compiler.GetType().GetField(
            "CompileResult",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public)!.GetValue(compiler)!;
        object compileWork = compiler.GetType().GetField(
            "CompileWork",
            ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public)!.GetValue(compiler)!;

        Assert.NotSame(compileResult, compileWork);

        var transitionsField = compileResult.GetType().GetField(
            "Transitions",
            ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!;
        var finalTransitionsField = compileResult.GetType().GetField(
            "FinalTransitions",
            ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!;

        var cachedTransitions = (Array)transitionsField.GetValue(compileResult)!;
        var workTransitions = (Array)transitionsField.GetValue(compileWork)!;
        Assert.Equal(cachedTransitions.Length, workTransitions.Length);
        for (int passIndex = 0; passIndex < cachedTransitions.Length; passIndex++)
            Assert.NotSame(cachedTransitions.GetValue(passIndex), workTransitions.GetValue(passIndex));

        Assert.NotSame(
            finalTransitionsField.GetValue(compileResult),
            finalTransitionsField.GetValue(compileWork));
    }

    [Fact]
    public void Execute_RejectsMismatchedTextureUavAccessTrackerShape()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = TargetTextureDesc("MismatchedUavTracker") with
        {
            BindFlags = BindFlags.UnorderedAccess,
            InitialState = ResourceState.UnorderedAccess,
            MipLevels = 2,
        };
        TextureHandle texture = device.CreateTexture(desc);

        try
        {
            graph.BeginFrame();
            var imported = graph.ImportTexture(
                "MismatchedUavTracker",
                texture,
                desc,
                new ImportDesc(ResourceState.UnorderedAccess)
                {
                    AllowWrite = true,
                });
            graph.AddRasterPass(
                "Use Mismatched Tracker",
                builder =>
                {
                    builder.SideEffect();
                    builder.Write(imported, ResourceState.UnorderedAccess);
                },
                _ => { });

            var resources = (System.Collections.IList)typeof(RenderGraph)
                .GetField("_resources", ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance)!
                .GetValue(graph)!;
            object resource = resources[resources.Count - 1]!;
            resource.GetType()
                .GetField("TextureUavAccesses", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
                .SetValue(resource, new[] { RenderGraphAccess.WriteOnly });

            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("UAV access tracker shape changed before execution", ex.Message);
        }
        finally
        {
            device.Destroy(texture);
        }
    }

    [Fact]
    public void Execute_SkipsEmptyFinalSubmit()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass(
            "Single Queue Work",
            builder => builder.SideEffect(),
            _ => { });

        graph.Execute(device, queue);

        Assert.Equal(1, RenderGraphTestHelpers.CommandCount(device, "Graphics"));
    }

    [Fact]
    public void Execute_RejectsDeclaredReadOnlyBufferUsedAsWriteBinding()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Declared ReadOnly Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("DeclaredReadOnlyBuffer", StorageBufferDesc("DeclaredReadOnlyBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Declared ReadOnly Buffer",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, buffer, BindingType.StorageBufferReadWrite));
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("DeclaredReadOnlyBuffer", ex.Message);
            Assert.Contains("incompatible contract", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsDeclaredWriteOnlyBufferReboundAsReadWrite()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Declared WriteOnly Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("DeclaredWriteOnlyBuffer", StorageBufferDesc("DeclaredWriteOnlyBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Declared WriteOnly Buffer",
            builder =>
            {
                builder.SideEffect();
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, buffer, BindingType.StorageBufferReadWrite));
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("DeclaredWriteOnlyBuffer", ex.Message);
            Assert.Contains("incompatible contract", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AllowsDeclaredWriteOnlyBufferBoundAsWriteOnly()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Declared WriteOnly Bound Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "DeclaredWriteOnlyBoundBuffer",
            new BufferDesc
            {
                Name = "DeclaredWriteOnlyBoundBuffer",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
                StrideInBytes = 4,
            });
        graph.AddComputePass(
            "Declared WriteOnly Bound Buffer",
            builder =>
            {
                builder.SideEffect();
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(
                    0,
                    context.Bindings(layout).Buffer(
                        0,
                        buffer,
                        BindingType.StorageBufferReadWrite,
                        RenderGraphAccess.WriteOnly));
            });

        try
        {
            graph.Execute(device, queue);

            Assert.Equal(1, RenderGraphTestHelpers.CommandCount(device, "Compute"));
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsImportedRawBufferViewReboundAsReadWrite()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Imported Raw Buffer Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "ImportedRawBuffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            InitialState = ResourceState.ShaderResource,
            Raw = true,
        };
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferViewHandle uav = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.UnorderedAccess,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                Raw = true,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportBuffer(
            "ImportedRawBuffer",
            buffer,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            [uav]);
        graph.AddComputePass(
            "Imported Raw Buffer Rebind",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, uav, BindingType.StorageBufferReadWrite));
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("ImportedRawBuffer", ex.Message);
            Assert.Contains("incompatible contract", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
            device.Destroy(uav);
            device.Destroy(buffer);
        }
    }

    [Fact]
    public void Execute_RejectsFreshImportedBufferViewReboundAsReadWrite()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Fresh Imported Buffer Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "FreshImportedBuffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            InitialState = ResourceState.ShaderResource,
            Raw = true,
        };
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferViewHandle uav = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.UnorderedAccess,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                Raw = true,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportBuffer(
            "FreshImportedBuffer",
            buffer,
            desc,
            new ImportDesc(ResourceState.ShaderResource));
        graph.AddComputePass(
            "Fresh Imported Buffer Rebind",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, uav, BindingType.StorageBufferReadWrite));
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("FreshImportedBuffer", ex.Message);
            Assert.Contains("incompatible contract", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
            device.Destroy(uav);
            device.Destroy(buffer);
        }
    }

    [Fact]
    public void Execute_RejectsUndeclaredTextureSubresourceAccess()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var texture = graph.CreateTexture(
            "SubresourceTexture",
            new TextureDesc
            {
                Name = "SubresourceTexture",
                Dimension = ResourceDimension.Texture2D,
                Width = 32,
                Height = 32,
                MipLevels = 2,
                Format = Format.R32Float,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
            });
        graph.AddRasterPass(
            "Init Mip0",
            builder => builder.Write(texture, ResourceState.UnorderedAccess, SubResourceRange.Mip(0)),
            _ => { });
        graph.AddRasterPass(
            "Read Wrong Mip",
            builder =>
            {
                builder.SideEffect();
                builder.Read(texture, ResourceState.ShaderResource, SubResourceRange.Mip(0));
            },
            context => _ = context.GetTextureView(texture, ViewKind.ShaderResource, Format.R32Float, firstMip: 1, mipCount: 1));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("SubresourceTexture", ex.Message);
        Assert.Contains("incompatible contract", ex.Message);
    }

    [Fact]
    public void Execute_RejectsRasterAttachmentBypassForDeclaredReadOnlyTexture()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "AttachmentBypassTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle srv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.ShaderResource,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });
        TextureViewHandle rtv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportTexture(
            "AttachmentBypassTexture",
            texture,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            [srv, rtv]);
        graph.AddRasterPass(
            "Attachment Bypass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context =>
            {
                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    ColorAttachments =
                    [
                        new ColorAttachmentDesc
                        {
                            View = rtv,
                            LoadOp = LoadOp.Load,
                            StoreOp = StoreOp.Store,
                        },
                    ],
                });
                pass.End();
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("AttachmentBypassTexture", ex.Message);
            Assert.Contains("incompatible contract", ex.Message);
        }
        finally
        {
            device.Destroy(rtv);
            device.Destroy(srv);
            device.Destroy(texture);
        }
    }

    [Fact]
    public void Execute_RejectsFreshImportedRasterAttachmentBypass()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "FreshAttachmentBypassTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle rtv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportTexture(
            "FreshAttachmentBypassTexture",
            texture,
            desc,
            new ImportDesc(ResourceState.ShaderResource));
        graph.AddRasterPass(
            "Fresh Attachment Bypass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context =>
            {
                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    ColorAttachments =
                    [
                        new ColorAttachmentDesc
                        {
                            View = rtv,
                            LoadOp = LoadOp.Load,
                            StoreOp = StoreOp.Store,
                        },
                    ],
                });
                pass.End();
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("FreshAttachmentBypassTexture", ex.Message);
            Assert.Contains("incompatible contract", ex.Message);
        }
        finally
        {
            device.Destroy(rtv);
            device.Destroy(texture);
        }
    }

    [Fact]
    public void Execute_RejectsImportedTextureWithForeignView()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "ImportedTextureA",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle textureA = device.CreateTexture(desc with { Name = "ImportedTextureA" });
        TextureHandle textureB = device.CreateTexture(desc with { Name = "ImportedTextureB" });
        TextureViewHandle viewOfB = device.CreateTextureView(
            textureB,
            new TextureViewDesc
            {
                Kind = ViewKind.ShaderResource,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportTexture(
            "ImportedTextureA",
            textureA,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            [viewOfB]);
        graph.AddRasterPass(
            "Foreign Imported Texture View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context => _ = context.GetTextureView(imported, ViewKind.ShaderResource, desc.Format));

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("must belong to the imported texture handle", ex.Message);
        }
        finally
        {
            device.Destroy(viewOfB);
            device.Destroy(textureA);
            device.Destroy(textureB);
        }
    }

    [Fact]
    public void Execute_RejectsFreshImportedTextureViewWithWrongKind()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "ImportedTextureWrongKind",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle rtv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportTexture(
            "ImportedTextureWrongKind",
            texture,
            desc,
            new ImportDesc(ResourceState.ShaderResource));
        graph.AddRasterPass(
            "Wrong Kind Imported Texture View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context => _ = context.Bindings(default).Texture(0, rtv));

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("expected ShaderResource", ex.Message);
        }
        finally
        {
            device.Destroy(rtv);
            device.Destroy(texture);
        }
    }

    [Fact]
    public void Execute_RejectsImportedBufferWithForeignView()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "ImportedBufferA",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
            Raw = true,
        };
        BufferHandle bufferA = device.CreateBuffer(desc with { Name = "ImportedBufferA" });
        BufferHandle bufferB = device.CreateBuffer(desc with { Name = "ImportedBufferB" });
        BufferViewHandle viewOfB = device.CreateBufferView(
            bufferB,
            new BufferViewDesc
            {
                Kind = ViewKind.ShaderResource,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                Raw = true,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportBuffer(
            "ImportedBufferA",
            bufferA,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            [viewOfB]);
        graph.AddRasterPass(
            "Foreign Imported Buffer View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context => _ = context.GetBufferView(imported, ViewKind.ShaderResource, raw: true));

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("must belong to the imported buffer handle", ex.Message);
        }
        finally
        {
            device.Destroy(viewOfB);
            device.Destroy(bufferA);
            device.Destroy(bufferB);
        }
    }

    [Fact]
    public void Execute_RejectsFreshImportedBufferViewWithWrongKind()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "ImportedBufferWrongKind",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            InitialState = ResourceState.ShaderResource,
            Raw = true,
        };
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferViewHandle uav = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.UnorderedAccess,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                Raw = true,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportBuffer(
            "ImportedBufferWrongKind",
            buffer,
            desc,
            new ImportDesc(ResourceState.ShaderResource));
        graph.AddRasterPass(
            "Wrong Kind Imported Buffer View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            context => _ = context.Bindings(default).Buffer(0, uav, BindingType.RawBufferRead));

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("expected ShaderResource", ex.Message);
        }
        finally
        {
            device.Destroy(uav);
            device.Destroy(buffer);
        }
    }

    [Fact]
    public void Execute_D3D12RejectsImportedBufferWithForeignViewWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var instance = Instance.Create();
        IDevice? device = null;
        try
        {
            device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        }
        catch (RhiException)
        {
            return;
        }

        using (device)
        {
            var queue = device.GetQueue(QueueType.Graphics);
            using var graph = new RenderGraph();

            BufferDesc desc = new()
            {
                Name = "D3D12ImportedBufferA",
                SizeInBytes = 64,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                Raw = true,
            };
            BufferHandle bufferA = device.CreateBuffer(desc with { Name = "D3D12ImportedBufferA" });
            BufferHandle bufferB = device.CreateBuffer(desc with { Name = "D3D12ImportedBufferB" });
            BufferViewHandle viewOfB = device.CreateBufferView(
                bufferB,
                new BufferViewDesc
                {
                    Kind = ViewKind.ShaderResource,
                    Offset = 0,
                    SizeInBytes = desc.SizeInBytes,
                    Raw = true,
                });

            graph.BeginFrame();
            RenderGraphHandle imported = graph.ImportBuffer(
                "D3D12ImportedBufferA",
                bufferA,
                desc,
                new ImportDesc(ResourceState.ShaderResource),
                [viewOfB]);
            graph.AddRasterPass(
                "Foreign D3D12 Imported Buffer View",
                builder =>
                {
                    builder.SideEffect();
                    builder.Read(imported, ResourceState.ShaderResource);
                },
                context => _ = context.GetBufferView(imported, ViewKind.ShaderResource, raw: true));

            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
                Assert.Contains("must belong to the imported buffer handle", ex.Message);
            }
            finally
            {
                device.Destroy(viewOfB);
                device.Destroy(bufferA);
                device.Destroy(bufferB);
            }
        }
    }

    [Fact]
    public void Execute_D3D12RejectsImportedTextureWithForeignViewWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var instance = Instance.Create();
        IDevice? device = null;
        try
        {
            device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        }
        catch (RhiException)
        {
            return;
        }

        using (device)
        {
            var queue = device.GetQueue(QueueType.Graphics);
            using var graph = new RenderGraph();

            TextureDesc desc = new()
            {
                Name = "D3D12ImportedTextureA",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            };
            TextureHandle textureA = device.CreateTexture(desc with { Name = "D3D12ImportedTextureA" });
            TextureHandle textureB = device.CreateTexture(desc with { Name = "D3D12ImportedTextureB" });
            TextureViewHandle viewOfB = device.CreateTextureView(
                textureB,
                new TextureViewDesc
                {
                    Kind = ViewKind.ShaderResource,
                    Dimension = TextureViewDimension.Texture2D,
                    Format = desc.Format,
                    FirstMip = 0,
                    MipCount = 1,
                    FirstSlice = 0,
                    SliceCount = 1,
                });

            graph.BeginFrame();
            RenderGraphHandle imported = graph.ImportTexture(
                "D3D12ImportedTextureA",
                textureA,
                desc,
                new ImportDesc(ResourceState.ShaderResource),
                [viewOfB]);
            graph.AddRasterPass(
                "Foreign D3D12 Imported Texture View",
                builder =>
                {
                    builder.SideEffect();
                    builder.Read(imported, ResourceState.ShaderResource);
                },
                context => _ = context.GetTextureView(imported, ViewKind.ShaderResource, desc.Format));

            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
                Assert.Contains("must belong to the imported texture handle", ex.Message);
            }
            finally
            {
                device.Destroy(viewOfB);
                device.Destroy(textureA);
                device.Destroy(textureB);
            }
        }
    }

    [Fact]
    public void Execute_D3D12RejectsFreshImportedBufferViewRebindWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var instance = Instance.Create();
        IDevice? device = null;
        try
        {
            device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        }
        catch (RhiException)
        {
            return;
        }

        using (device)
        {
            var queue = device.GetQueue(QueueType.Graphics);
            using var graph = new RenderGraph();

            BufferDesc desc = new()
            {
                Name = "D3D12FreshImportedBuffer",
                SizeInBytes = 64,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
                Raw = true,
            };
            BufferHandle buffer = device.CreateBuffer(desc);
            BufferViewHandle uav = device.CreateBufferView(
                buffer,
                new BufferViewDesc
                {
                    Kind = ViewKind.UnorderedAccess,
                    Offset = 0,
                    SizeInBytes = desc.SizeInBytes,
                    Raw = true,
                });

            graph.BeginFrame();
            RenderGraphHandle imported = graph.ImportBuffer(
                "D3D12FreshImportedBuffer",
                buffer,
                desc,
                new ImportDesc(ResourceState.ShaderResource));
            BindingLayoutHandle layout = device.CreateBindingLayout(
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.StorageBufferReadWrite,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
                "D3D12 Fresh Imported Layout");
            var pipeline = StoragePipeline(device, layout);
            graph.AddComputePass(
                "D3D12 Fresh Imported Rebind",
                builder =>
                {
                    builder.SideEffect();
                    builder.Read(imported, ResourceState.ShaderResource);
                },
                (context, pass) =>
                {
                    pass.SetPipeline(pipeline.Pipeline);
                    pass.SetParameters(0, context.Bindings(layout).Buffer(0, uav, BindingType.StorageBufferReadWrite));
                });

            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
                Assert.Contains("incompatible contract", ex.Message);
            }
            finally
            {
                graph.BeginFrame();
                graph.ClearBindSets(waitForGpu: true);
                DestroyPipeline(device, pipeline);
                device.Destroy(layout);
                device.Destroy(uav);
                device.Destroy(buffer);
            }
        }
    }

    [Fact]
    public void Execute_D3D12RejectsFreshImportedTextureViewRebindWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var instance = Instance.Create();
        IDevice? device = null;
        try
        {
            device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        }
        catch (RhiException)
        {
            return;
        }

        using (device)
        {
            var queue = device.GetQueue(QueueType.Graphics);
            using var graph = new RenderGraph();

            TextureDesc desc = new()
            {
                Name = "D3D12FreshImportedTexture",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.R32Float,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
            };
            TextureHandle texture = device.CreateTexture(desc);
            TextureViewHandle uav = device.CreateTextureView(
                texture,
                new TextureViewDesc
                {
                    Kind = ViewKind.UnorderedAccess,
                    Dimension = TextureViewDimension.Texture2D,
                    Format = desc.Format,
                    FirstMip = 0,
                    MipCount = 1,
                    FirstSlice = 0,
                    SliceCount = 1,
                });

            graph.BeginFrame();
            RenderGraphHandle imported = graph.ImportTexture(
                "D3D12FreshImportedTexture",
                texture,
                desc,
                new ImportDesc(ResourceState.ShaderResource));
            BindingLayoutHandle layout = device.CreateBindingLayout(
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.TextureReadWrite,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
                "D3D12 Fresh Imported Texture Layout");
            var pipeline = StoragePipeline(device, layout);
            graph.AddComputePass(
                "D3D12 Fresh Imported Texture Rebind",
                builder =>
                {
                    builder.SideEffect();
                    builder.Read(imported, ResourceState.ShaderResource);
                },
                (context, pass) =>
                {
                    pass.SetPipeline(pipeline.Pipeline);
                    pass.SetParameters(0, context.Bindings(layout).Texture(0, uav, BindingType.TextureReadWrite));
                });

            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
                Assert.Contains("incompatible contract", ex.Message);
            }
            finally
            {
                graph.BeginFrame();
                graph.ClearBindSets(waitForGpu: true);
                DestroyPipeline(device, pipeline);
                device.Destroy(layout);
                device.Destroy(uav);
                device.Destroy(texture);
            }
        }
    }

    [Fact]
    public void ImportBuffer_RejectsDuplicateExternalHandle()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "DuplicateImportedBuffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            InitialState = ResourceState.ShaderResource,
            StrideInBytes = 4,
        };
        BufferHandle buffer = device.CreateBuffer(desc);
        graph.BeginFrame();
        graph.ImportBuffer(
            "DuplicateImportedBuffer.ReadOnly",
            buffer,
            desc,
            new ImportDesc(ResourceState.ShaderResource));
        var ex = Assert.Throws<InvalidOperationException>(() => graph.ImportBuffer(
            "DuplicateImportedBuffer.ReadWrite",
            buffer,
            desc,
            new ImportDesc(ResourceState.ShaderResource)
            {
                AllowWrite = true,
            }));

        Assert.Contains("cannot import the same external buffer handle twice", ex.Message);
        device.Destroy(buffer);
    }

    [Fact]
    public void ImportTexture_RejectsDuplicateExternalHandle()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "DuplicateImportedTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.R32Float,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle texture = device.CreateTexture(desc);
        graph.BeginFrame();
        graph.ImportTexture(
            "DuplicateImportedTexture.ReadOnly",
            texture,
            desc,
            new ImportDesc(ResourceState.ShaderResource));
        var ex = Assert.Throws<InvalidOperationException>(() => graph.ImportTexture(
            "DuplicateImportedTexture.ReadWrite",
            texture,
            desc,
            new ImportDesc(ResourceState.ShaderResource)
            {
                AllowWrite = true,
            }));

        Assert.Contains("cannot import the same external texture handle twice", ex.Message);
        device.Destroy(texture);
    }

    [Fact]
    public void Execute_RejectsAliveExternalBufferViewThatWasNeverImported()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "External View Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "ExternalOnlyBuffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            InitialState = ResourceState.ShaderResource,
            StrideInBytes = 4,
        };
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferViewHandle uav = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.UnorderedAccess,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                StrideInBytes = 4,
            });

        graph.BeginFrame();
        graph.AddComputePass(
            "External View Consumer",
            builder => builder.SideEffect(),
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, uav, BindingType.StorageBufferReadWrite));
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("not registered to the active graph", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
            device.Destroy(uav);
            device.Destroy(buffer);
        }
    }

    [Fact]
    public void Execute_RejectsBufferBarrierOnDeclaredReadOnlyBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("BarrierReadOnlyBuffer", StorageBufferDesc("BarrierReadOnlyBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Barrier ReadOnly Buffer",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (_, pass) => pass.BufferBarrier(buffer, ResourceState.UnorderedAccess, ResourceState.IndirectArgument));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("BarrierReadOnlyBuffer", ex.Message);
        Assert.Contains("undeclared transition", ex.Message);
    }

    [Fact]
    public void Execute_RejectsBufferBarrierUndeclaredAfterState()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("BarrierAfterStateBuffer", StorageBufferDesc("BarrierAfterStateBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Barrier After State",
            builder =>
            {
                builder.SideEffect();
                builder.ReadWrite(buffer, ResourceState.UnorderedAccess);
            },
            (_, pass) => pass.BufferBarrier(buffer, ResourceState.UnorderedAccess, ResourceState.IndirectArgument));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("BarrierAfterStateBuffer", ex.Message);
        Assert.Contains("undeclared transition", ex.Message);
    }

    [Fact]
    public void Execute_AcceptsBufferBarrierForDeclaredTransition()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("BarrierDeclaredBuffer", StorageBufferDesc("BarrierDeclaredBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Barrier Declared Transition",
            builder =>
            {
                builder.SideEffect();
                builder.ReadWrite(buffer, ResourceState.UnorderedAccess, ResourceState.IndirectArgument);
            },
            (_, pass) => pass.BufferBarrier(buffer, ResourceState.UnorderedAccess, ResourceState.IndirectArgument));

        graph.Execute(device, queue);
    }

    [Fact]
    public void Compile_BufferPassEpilogueUsesFirstUseEntryState()
    {
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var arguments = graph.CreateBuffer(
            "BufferPassEpilogueArguments",
            new BufferDesc
            {
                Name = "BufferPassEpilogueArguments",
                SizeInBytes = 16,
                BindFlags = BindFlags.IndirectArgument | BindFlags.ShaderResource,
                InitialState = ResourceState.IndirectArgument,
                StrideInBytes = 4,
            },
            [1, 1, 1, 0]);
        graph.AddComputePass(
            "Buffer Epilogue Order",
            builder =>
            {
                builder.SideEffect();
                builder.Read(arguments, ResourceState.IndirectArgument);
                builder.Read(arguments, ResourceState.ShaderResource, ResourceState.IndirectArgument, SubResourceRange.All);
            },
            (_, _) => { });

        object compiled = typeof(RenderGraph)
            .GetMethod("CompileGraph", ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance)!
            .Invoke(graph, [false, false])!;
        var passEpilogues = (Array)compiled.GetType()
            .GetField("PassEpilogues", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compiled)!;
        Array epilogue = (Array)passEpilogues.GetValue(0)!;
        Assert.Single(epilogue);

        object state = epilogue.GetValue(0)!;
        ResourceState entryState = Assert.IsType<ResourceState>(state.GetType()
            .GetProperty("EntryState", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(state)!);
        ResourceState exitState = Assert.IsType<ResourceState>(state.GetType()
            .GetProperty("ExitState", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(state)!);

        Assert.Equal(ResourceState.IndirectArgument, entryState);
        Assert.Equal(ResourceState.IndirectArgument, exitState);
    }

    [Fact]
    public void Execute_RejectsShaderResourceBindingWhenOnlyIndirectArgumentWasDeclared()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Indirect Only Declaration Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "IndirectOnlyBuffer",
            new BufferDesc
            {
                Name = "IndirectOnlyBuffer",
                SizeInBytes = 16,
                BindFlags = BindFlags.IndirectArgument | BindFlags.ShaderResource,
                InitialState = ResourceState.IndirectArgument,
                StrideInBytes = 4,
            },
            [1, 1, 1, 0]);
        graph.AddComputePass(
            "Indirect Only Declaration",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.IndirectArgument);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, buffer, BindingType.StorageBufferRead));
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("IndirectOnlyBuffer", ex.Message);
            Assert.Contains("incompatible contract", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsUavBarrierOnUndeclaredTextureSubresource()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var texture = graph.CreateTexture(
            "BarrierTexture",
            new TextureDesc
            {
                Name = "BarrierTexture",
                Dimension = ResourceDimension.Texture2D,
                Width = 32,
                Height = 32,
                MipLevels = 2,
                Format = Format.R32Float,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
            });
        graph.AddComputePass(
            "Barrier Wrong Mip",
            builder =>
            {
                builder.SideEffect();
                builder.Write(texture, ResourceState.UnorderedAccess, SubResourceRange.Mip(0));
            },
            (_, pass) => pass.Barrier([new UavBarrier(texture, SubResourceRange.Mip(1))]));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("BarrierTexture", ex.Message);
        Assert.Contains("incompatible contract", ex.Message);
    }

    [Fact]
    public void Execute_AcceptsDepthReadOnlyAttachment()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "DepthReadOnlyTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.D32Float,
            BindFlags = BindFlags.DepthStencil,
            InitialState = ResourceState.DepthRead,
        };
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle dsv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.DepthStencil,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });

        graph.BeginFrame();
        RenderGraphHandle imported = graph.ImportTexture(
            "DepthReadOnlyTexture",
            texture,
            desc,
            new ImportDesc(ResourceState.DepthRead),
            [dsv]);
        graph.AddRasterPass(
            "Depth ReadOnly Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.DepthRead);
            },
            context =>
            {
                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    DepthStencilAttachment = new DepthAttachDesc
                    {
                        View = dsv,
                        DepthReadOnly = true,
                    },
                });
                pass.End();
            });

        graph.Execute(device, queue);
        device.Destroy(dsv);
        device.Destroy(texture);
    }

    [Fact]
    public void Execute_AcceptsGraphOwnedDepthReadOnlyAttachment()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var depth = graph.CreateTexture(
            "GraphDepthReadOnlyTexture",
            new TextureDesc
            {
                Name = "GraphDepthReadOnlyTexture",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.D32Float,
                BindFlags = BindFlags.DepthStencil,
                InitialState = ResourceState.DepthRead,
            });
        graph.AddRasterPass(
            "Initialize Graph Depth",
            builder => builder.Write(depth, ResourceState.DepthWrite),
            _ => { });
        graph.AddRasterPass(
            "Graph Depth ReadOnly Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(depth, ResourceState.DepthRead);
            },
            context =>
            {
                var dsv = context.GetTextureView(depth, ViewKind.DepthStencil, Format.D32Float, depthReadOnly: true);
                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    DepthStencilAttachment = new DepthAttachDesc
                    {
                        View = dsv,
                        DepthReadOnly = true,
                    },
                });
                pass.End();
            });

        graph.Execute(device, queue);
    }

    [Fact]
    public void Execute_RejectsEscapedBuilderDuringPassExecute()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        RenderGraphBuilder captured = default;

        graph.BeginFrame();
        graph.AddRasterPass(
            "Escaped Builder Execute",
            builder =>
            {
                captured = builder;
                builder.SideEffect();
            },
            _ => captured.SideEffect());

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("RenderGraphBuilder is only valid", ex.Message);
    }

    [Fact]
    public void Execute_RejectsGraphMutationFromWorkerThreadDuringPassExecute()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        Exception? mutationError = null;

        graph.BeginFrame();
        graph.AddRasterPass(
            "Worker Mutation",
            builder => builder.SideEffect(),
            _ =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    mutationError = Record.Exception(
                        () => graph.CreateBuffer("IllegalWorkerBuffer", AliasBufferDesc("IllegalWorkerBuffer")));
                }).GetAwaiter().GetResult();
            });

        graph.Execute(device, queue);

        var ex = Assert.IsType<InvalidOperationException>(mutationError);
        Assert.Contains("CreateBuffer", ex.Message);
        Assert.Contains("pass execute", ex.Message);
    }

    [Fact]
    public void Execute_RejectsSecondSubmitWithoutBeginFrame()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass(
            "Single Submit",
            builder => builder.SideEffect(),
            _ => { });
        graph.Execute(device, queue);

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("BeginFrame", ex.Message);
    }

    [Fact]
    public void Execute_RejectsSecondSubmitForEmptyFrameWithoutBeginFrame()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.Execute(device, queue);

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("BeginFrame", ex.Message);
    }

    [Fact]
    public void Execute_RejectsLoadAttachmentDeclaredWriteOnly()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var target = graph.CreateTexture("LoadRequiresRead", TargetTextureDesc("LoadRequiresRead"));
        graph.AddRasterPass(
            "Load With WriteOnly",
            builder =>
            {
                builder.SideEffect();
                builder.Write(target, ResourceState.RenderTarget);
            },
            context =>
            {
                TextureDesc desc = context.GetTextureDesc(target);
                TextureViewHandle rtv = context.GetTextureView(target, ViewKind.RenderTarget, desc.Format);
                Span<ColorAttachmentDesc> colorAttachments = stackalloc ColorAttachmentDesc[1];
                colorAttachments[0] = new ColorAttachmentDesc
                {
                    View = rtv,
                    LoadOp = LoadOp.Load,
                    StoreOp = StoreOp.Store,
                };
                context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, checked((int)desc.Width), checked((int)desc.Height)),
                    ColorAttachments = colorAttachments,
                });
            });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("LoadRequiresRead", ex.Message);
        Assert.Contains("incompatible contract", ex.Message);
    }

    [Fact]
    public void RenderPassCommands_RejectsUseAfterEnd()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var target = graph.CreateTexture("EndedRenderPassTarget", TargetTextureDesc("EndedRenderPassTarget"));
        graph.AddRasterPass(
            "Use After End",
            builder =>
            {
                builder.SideEffect();
                builder.Write(target, ResourceState.RenderTarget);
            },
            context =>
            {
                TextureDesc desc = context.GetTextureDesc(target);
                TextureViewHandle rtv = context.GetTextureView(target, ViewKind.RenderTarget, desc.Format);
                Span<ColorAttachmentDesc> colorAttachments = stackalloc ColorAttachmentDesc[1];
                colorAttachments[0] = new ColorAttachmentDesc
                {
                    View = rtv,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                };
                RenderPassCommands pass = context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, checked((int)desc.Width), checked((int)desc.Height)),
                    ColorAttachments = colorAttachments,
                });
                pass.End();

                var ex = Assert.Throws<InvalidOperationException>(() => pass.Draw(3));
                Assert.Contains("RenderPassCommands cannot be used", ex.Message);
            });

        graph.Execute(device, queue);
    }

    [Fact]
    public void RenderGraphContext_RejectsUseAfterPassCallbackReturns()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        RenderGraphContext? captured = null;

        graph.BeginFrame();
        graph.AddRasterPass(
            "Escaped Context",
            builder => builder.SideEffect(),
            context => captured = context);

        graph.Execute(device, queue);

        var renderPassError = Assert.Throws<InvalidOperationException>(
            () => captured!.BeginRenderPass(new RenderPassDesc()));
        var bindingsError = Assert.Throws<InvalidOperationException>(
            () => captured!.Bindings(default));

        Assert.Contains("active RenderGraph pass callback", renderPassError.Message);
        Assert.Contains("active RenderGraph pass callback", bindingsError.Message);
    }

    [Fact]
    public void ComputeCommands_RejectUseAfterPassCallbackReturns()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        IComputeCommands? captured = null;

        graph.BeginFrame();
        graph.AddComputePass(
            "Escaped Compute Commands",
            builder => builder.SideEffect(),
            (_, pass) => captured = pass);

        graph.Execute(device, queue);

        var ex = Assert.Throws<InvalidOperationException>(() => captured!.Dispatch(1, 1, 1));

        Assert.Contains("IComputeCommands cannot be used", ex.Message);
    }

    [Fact]
    public void Execute_RejectsRenderPassLeftOpenByCallback()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var target = graph.CreateTexture("OpenRenderPassTarget", TargetTextureDesc("OpenRenderPassTarget"));
        graph.AddRasterPass(
            "Open Render Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Write(target, ResourceState.RenderTarget);
            },
            context =>
            {
                TextureDesc desc = context.GetTextureDesc(target);
                TextureViewHandle rtv = context.GetTextureView(target, ViewKind.RenderTarget, desc.Format);
                Span<ColorAttachmentDesc> colorAttachments = stackalloc ColorAttachmentDesc[1];
                colorAttachments[0] = new ColorAttachmentDesc
                {
                    View = rtv,
                    LoadOp = LoadOp.DontCare,
                    StoreOp = StoreOp.Store,
                };
                context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, checked((int)desc.Width), checked((int)desc.Height)),
                    ColorAttachments = colorAttachments,
                });
            });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("returned with an active render pass", ex.Message);
    }

    [Fact]
    public void Execute_RejectsCopyCommandFromGraphicsPass()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var source = graph.CreateBuffer(
            "CopyWrongModeSource",
            new BufferDesc
            {
                Name = "CopyWrongModeSource",
                SizeInBytes = 4,
                Memory = MemoryClass.DeviceLocal,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var destination = graph.CreateBuffer(
            "CopyWrongModeDestination",
            new BufferDesc
            {
                Name = "CopyWrongModeDestination",
                SizeInBytes = 4,
                Memory = MemoryClass.DeviceLocal,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        graph.AddRasterPass(
            "Copy Wrong Mode",
            builder =>
            {
                builder.SideEffect();
                builder.Read(source, ResourceState.CopySource);
                builder.Write(destination, ResourceState.CopyDestination);
            },
            context => context.CopyBuffer(source, 0, destination, 0, 4));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("AddCopyPass", ex.Message);
    }

    [Fact]
    public void Execute_RejectsDepthReadOnlyClearAttachment()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "DepthReadOnlyClearTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.D32Float,
            BindFlags = BindFlags.DepthStencil,
            InitialState = ResourceState.DepthRead,
        };
        TextureHandle texture = device.CreateTexture(desc);
        TextureViewHandle dsv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.DepthStencil,
                Dimension = TextureViewDimension.Texture2D,
                Format = desc.Format,
                FirstMip = 0,
                MipCount = 1,
                FirstSlice = 0,
                SliceCount = 1,
            });

        graph.BeginFrame();
        var depth = graph.ImportTexture(
            "DepthReadOnlyClearTexture",
            texture,
            desc,
            new ImportDesc(ResourceState.DepthRead),
            [dsv]);
        graph.AddRasterPass(
            "Depth ReadOnly Clear",
            builder =>
            {
                builder.SideEffect();
                builder.Read(depth, ResourceState.DepthRead);
            },
            context =>
            {
                context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    DepthStencilAttachment = new DepthAttachDesc
                    {
                        View = dsv,
                        DepthReadOnly = true,
                        DepthLoadOp = LoadOp.Clear,
                    },
                });
            });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("read-only attachments cannot use LoadOp.Clear", ex.Message);
        device.Destroy(dsv);
        device.Destroy(texture);
    }

    [Fact]
    public void Execute_AcceptsGraphOwnedResolveTarget()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var msaa = graph.CreateTexture(
            "ResolveSourceMsaa",
            new TextureDesc
            {
                Name = "ResolveSourceMsaa",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                SampleCount = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.Undefined,
            });
        var resolve = graph.CreateTexture(
            "ResolveTarget",
            new TextureDesc
            {
                Name = "ResolveTarget",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.Undefined,
            });
        graph.AddRasterPass(
            "Resolve Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Write(msaa, ResourceState.RenderTarget);
                builder.Write(resolve, ResourceState.RenderTarget);
            },
            context =>
            {
                var colorView = context.GetTextureView(
                    msaa,
                    ViewKind.RenderTarget,
                    Format.Rgba8Unorm,
                    TextureViewDimension.Texture2DMultisampled);
                var resolveView = context.GetTextureView(resolve, ViewKind.RenderTarget, Format.Rgba8Unorm);
                var pass = context.BeginRenderPass(new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    ColorAttachments =
                    [
                        new ColorAttachmentDesc
                        {
                            View = colorView,
                            ResolveTarget = resolveView,
                            LoadOp = LoadOp.Clear,
                            StoreOp = StoreOp.Store,
                            ClearColor = Color.Black,
                        },
                    ],
                });
                pass.End();
            });

        graph.Execute(device, queue);
    }

    [Fact]
    public void Execute_UsesCopyQueueForCopyPasses()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var source = graph.CreateBuffer(
            "CopyQueueSource",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination | BindFlags.ShaderResource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var target = graph.CreateBuffer(
            "CopyQueueTarget",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopyDestination | BindFlags.ShaderResource,
                InitialState = ResourceState.Undefined,
            });
        BufferCopyPasses.AddCopyPass(graph, "Copy Queue Transfer", source, target, 0, 0, 4);
        graph.AddComputePass(
            "Copy Queue Consumer",
            builder =>
            {
                builder.SideEffect();
                builder.Read(target, ResourceState.ShaderResource);
            },
            (_, _) => { });

        graph.Execute(device, new GraphQueues(device, queue));

        Assert.Single(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true, asyncCopy: true));
        Assert.True(RenderGraphTestHelpers.CommandCount(device, "Copy") > 0);
        Assert.True(RenderGraphTestHelpers.CommandCount(device, "Compute") > 0);
        Assert.Equal(0, RenderGraphTestHelpers.CommandCount(device, "Graphics"));
    }

    [Fact]
    public void Execute_WaitsForSourceBatchSignal()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var source = graph.CreateBuffer("ExactWaitSource", StorageBufferDesc("ExactWaitSource"));
        var output = graph.CreateBuffer("ExactWaitOutput", StorageBufferDesc("ExactWaitOutput"));
        graph.AddComputePass(
            "Exact Wait Source",
            builder => builder.Write(source, ResourceState.UnorderedAccess),
            (_, _) => { });
        graph.AddRasterPass(
            "Exact Wait Middle",
            builder => builder.SideEffect(),
            _ => { });
        graph.AddComputePass(
            "Exact Wait Later",
            builder => builder.SideEffect(),
            (_, _) => { });
        graph.AddRasterPass(
            "Exact Wait Consumer",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(output, ResourceState.UnorderedAccess);
            },
            _ => { });
        graph.ExtractBuffer(output, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Execute(device, new GraphQueues(device, queue));

        Assert.Single(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true));
    }

    [Fact]
    public void Execute_TextureCopyBatchUsesSingleCopyPass()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        TextureHandle firstSourceTexture = device.CreateTexture(
            new TextureDesc
            {
                Name = "BatchCopySourceA",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var firstSource = graph.ImportTexture(
            "BatchCopySourceA",
            firstSourceTexture,
            device.GetTextureDesc(firstSourceTexture),
            new ImportDesc(ResourceState.CopySource));
        var firstDestination = graph.CreateTexture(
            "BatchCopyDestinationA",
            new TextureDesc
            {
                Name = "BatchCopyDestinationA",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        TextureHandle secondSourceTexture = device.CreateTexture(
            new TextureDesc
            {
                Name = "BatchCopySourceB",
                Dimension = ResourceDimension.Texture2D,
                Width = 8,
                Height = 8,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var secondSource = graph.ImportTexture(
            "BatchCopySourceB",
            secondSourceTexture,
            device.GetTextureDesc(secondSourceTexture),
            new ImportDesc(ResourceState.CopySource));
        var secondDestination = graph.CreateTexture(
            "BatchCopyDestinationB",
            new TextureDesc
            {
                Name = "BatchCopyDestinationB",
                Dimension = ResourceDimension.Texture2D,
                Width = 8,
                Height = 8,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        TextureCopyPasses.AddCopyBatch(
            graph,
            "Batch Copy Textures",
            [
                new TextureCopyRequest(
                    firstSource,
                    new TextureCopyRegion(0, 0, 0, 0, 0, 16, 16, 1),
                    firstDestination,
                    new TextureCopyRegion(0, 0, 0, 0, 0, 16, 16, 1)),
                new TextureCopyRequest(
                    secondSource,
                    new TextureCopyRegion(0, 0, 0, 0, 0, 8, 8, 1),
                    secondDestination,
                    new TextureCopyRegion(0, 0, 0, 0, 0, 8, 8, 1)),
            ]);
        graph.SetFinalState(firstDestination, ResourceState.CopyDestination);
        graph.SetFinalState(secondDestination, ResourceState.CopyDestination);

        graph.Execute(device, queue);

        Assert.Contains("Batch Copy Textures", RenderGraphTestHelpers.ExecutedPassNames(graph));
        Assert.Equal(2, RenderGraphTestHelpers.OperationCount(device, "CopyTexture"));
        device.Destroy(firstSourceTexture);
        device.Destroy(secondSourceTexture);
    }

    [Fact]
    public void Execute_RejectsRenderPassInCopyPass()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddCopyPass(
            "Copy Surface Guard",
            builder => builder.SideEffect(),
            context =>
            {
                context.BeginRenderPass(new RenderPassDesc { Name = "Illegal Copy RenderPass" });
            });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, new GraphQueues(device, queue)));
        Assert.Contains("copy pass", ex.Message);
        Assert.Contains("Copy Surface Guard", ex.Message);
    }

    [Fact]
    public void Execute_RejectsRenderPassInComputePass()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddComputePass(
            "Compute Surface Guard",
            builder => builder.SideEffect(),
            (context, _) =>
            {
                context.BeginRenderPass(new RenderPassDesc { Name = "Illegal Compute RenderPass" });
            });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, new GraphQueues(device, queue)));
        Assert.Contains("compute pass", ex.Message);
        Assert.Contains("Compute Surface Guard", ex.Message);
    }

    [Fact]
    public void Execute_RejectsBindingsInCopyPass()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddCopyPass(
            "Copy Binding Guard",
            builder => builder.SideEffect(),
            context =>
            {
                context.Bindings(default);
            });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, new GraphQueues(device, queue)));
        Assert.Contains("copy pass", ex.Message);
        Assert.Contains("Copy Binding Guard", ex.Message);
    }

    [Fact]
    public void Execute_AcceptsPassParameters()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Parameter Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("ParameterBuffer", StorageBufferDesc("ParameterBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Parameter Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                BufferViewHandle view = context.GetBufferView(buffer, ViewKind.ShaderResource);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(
                    0,
                    context.Bindings(layout)
                        .Buffer(0, view));
            });
        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsPassParameterReuse()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Parameter Fast Path Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("ParameterFastPathBuffer", StorageBufferDesc("ParameterFastPathBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Parameter Fast Path Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                BufferViewHandle view = context.GetBufferView(buffer, ViewKind.ShaderResource);
                var parameters = context.Bindings(layout)
                    .Buffer(0, view)
                    .Build();
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, parameters);
                pass.SetParameters(0, parameters);
            });
        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsPassBindingReuse()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Binding Fast Path Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("BindingFastPathBuffer", StorageBufferDesc("BindingFastPathBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Binding Fast Path Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                PassBindings bindings = context.Bindings(layout)
                    .Buffer(0, buffer);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, bindings);
                pass.SetParameters(0, bindings);
            });
        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsCrossPassSameFramePassBindingReuse()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Cross Pass Binding Reuse Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();
        PassBindings cached = null!;

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("CrossPassBindingBuffer", StorageBufferDesc("CrossPassBindingBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Build Pass Bindings",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                cached = context.Bindings(layout).Buffer(0, buffer);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });
        graph.AddComputePass(
            "Replay Pass Bindings",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (_, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsExplicitWriteOnlyBindingOverride()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "WriteOnly Override Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "WriteOnlyOverrideBuffer",
            new BufferDesc
            {
                Name = "WriteOnlyOverrideBuffer",
                SizeInBytes = 64,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
                StrideInBytes = 4,
            });
        graph.AddComputePass(
            "WriteOnly Override Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                PassBindings bindings = context.Bindings(layout)
                    .Buffer(0, buffer, BindingType.StorageBufferReadWrite, RenderGraphAccess.WriteOnly);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, bindings);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsExplicitWriteOnlyTextureOverride()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.TextureReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "WriteOnly Texture Override Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var texture = graph.CreateTexture(
            "WriteOnlyOverrideTexture",
            new TextureDesc
            {
                Name = "WriteOnlyOverrideTexture",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.R32Float,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
            });
        graph.AddComputePass(
            "WriteOnly Texture Override Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Write(texture, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                PassBindings bindings = context.Bindings(layout)
                    .Texture(0, texture, BindingType.TextureReadWrite, RenderGraphAccess.WriteOnly);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, bindings);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsReflectedTextureWriteOnlyOverride()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.TextureReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Reflected Texture WriteOnly Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        ReflectedBinding binding = new(
            "OutputColor",
            Set: 0,
            Binding: 0,
            BindingType.TextureReadWrite,
            ShaderStageFlags.Compute);

        graph.BeginFrame();
        var texture = graph.CreateTexture(
            "ReflectedWriteOnlyTexture",
            new TextureDesc
            {
                Name = "ReflectedWriteOnlyTexture",
                Dimension = ResourceDimension.Texture2D,
                Width = 16,
                Height = 16,
                Format = Format.R32Float,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
            });
        graph.AddComputePass(
            "Reflected WriteOnly Texture Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Write(texture, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                PassBindings bindings = context.Bindings(layout)
                    .Texture(binding, texture, RenderGraphAccess.WriteOnly);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, bindings);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsReflectedBufferWriteOnlyOverride()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Reflected Buffer WriteOnly Default Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        ReflectedBinding binding = new(
            "OutputBuffer",
            Set: 0,
            Binding: 0,
            BindingType.StorageBufferReadWrite,
            ShaderStageFlags.Compute);

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "ReflectedWriteOnlyDefaultBuffer",
            new BufferDesc
            {
                Name = "ReflectedWriteOnlyDefaultBuffer",
                SizeInBytes = 64,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
                StrideInBytes = 4,
            });
        graph.AddComputePass(
            "Reflected WriteOnly Default Buffer Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                PassBindings bindings = context.Bindings(layout)
                    .Buffer(binding, buffer, RenderGraphAccess.WriteOnly);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, bindings);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsReflectedBufferWriteOnlyOverrideWithCustomViewDesc()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Reflected Buffer WriteOnly Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        ReflectedBinding binding = new(
            "OutputBuffer",
            Set: 0,
            Binding: 0,
            BindingType.StorageBufferReadWrite,
            ShaderStageFlags.Compute);

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "ReflectedWriteOnlyBuffer",
            new BufferDesc
            {
                Name = "ReflectedWriteOnlyBuffer",
                SizeInBytes = 64,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
                StrideInBytes = 4,
            });
        graph.AddComputePass(
            "Reflected WriteOnly Buffer Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Write(buffer, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                PassBindings bindings = context.Bindings(layout)
                    .Buffer(
                        binding,
                        buffer,
                        new BufferViewDesc
                        {
                            Kind = ViewKind.UnorderedAccess,
                            Offset = 16,
                            SizeInBytes = 16,
                            StrideInBytes = 4,
                        },
                        RenderGraphAccess.WriteOnly);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, bindings);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsPassBindingsSampler()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.Sampler,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Sampler Layout");
        var pipeline = StoragePipeline(device, layout);
        SamplerHandle sampler = device.CreateSampler(new SamplerDesc());
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddComputePass(
            "Sampler Binding Pass",
            builder => builder.SideEffect(),
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Sampler(0, sampler));
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
            device.Destroy(sampler);
        }
    }

    [Fact]
    public void Execute_AcceptsRawBufferBindingForStridedSource()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.RawBufferRead,
                    Stages = ShaderStageFlags.Compute,
                    Shape = new BindShapeDesc(),
                },
            ],
            "Raw Buffer Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "RawBufferSource",
            new BufferDesc
            {
                Name = "RawBufferSource",
                SizeInBytes = 64,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 16,
            },
            [1, 2, 3, 4]);
        graph.AddComputePass(
            "Raw Buffer Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, buffer, BindingType.RawBufferRead));
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void PassBindings_Clear_BuildsPlaceholderPacket()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferRead,
                    Stages = ShaderStageFlags.Compute,
                    Flags = BindingFlags.PartiallyBound,
                    Shape = new BindShapeDesc
                    {
                        StrideInBytes = 4,
                    },
                },
            ],
            "Clear Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddComputePass(
            "Clear Binding Pass",
            builder => builder.SideEffect(),
            (context, _) =>
            {
                PassParameters bindings = context.Bindings(layout).Clear(0).Build();
                Assert.Equal(1, bindings.ResourceSpan.Length);
                Assert.Equal(BindingType.None, bindings.ResourceSpan[0].ResourceType);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsIllegalWeakerThanUavAccessOverride()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferReadWrite,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Illegal Override Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("IllegalOverrideBuffer", StorageBufferDesc("IllegalOverrideBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Illegal Override Pass",
            builder =>
            {
                builder.SideEffect();
                builder.ReadWrite(buffer, ResourceState.UnorderedAccess);
            },
            (context, pass) =>
            {
                var ex = Assert.Throws<InvalidOperationException>(() => context.Bindings(layout)
                    .Buffer(0, buffer, BindingType.StorageBufferReadWrite, RenderGraphAccess.None));
                Assert.Contains("only permits", ex.Message);
                pass.SetPipeline(pipeline.Pipeline);
            });

        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsCrossFramePassParameterReplay()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "CrossFrame Parameter Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();
        PassParameters cached = default;

        graph.BeginFrame();
        var first = graph.CreateBuffer("CrossFrameParameterSource", StorageBufferDesc("CrossFrameParameterSource"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Build Cached Parameters",
            builder =>
            {
                builder.SideEffect();
                builder.Read(first, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                BufferViewHandle view = context.GetBufferView(first, ViewKind.ShaderResource);
                cached = context.Bindings(layout).Buffer(0, view).Build();
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });
        graph.Execute(device, queue);

        graph.BeginFrame();
        var second = graph.CreateBuffer("CrossFrameParameterTarget", StorageBufferDesc("CrossFrameParameterTarget"), [5, 6, 7, 8]);
        graph.AddComputePass(
            "Replay Cached Parameters",
            builder =>
            {
                builder.SideEffect();
                builder.Read(second, ResourceState.ShaderResource);
            },
            (_, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("cannot reuse parameter packets across frames", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsCrossGraphPassParameterReplay()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "CrossGraph Parameter Layout");
        var pipeline = StoragePipeline(device, layout);
        using var first = new RenderGraph();
        using var second = new RenderGraph();
        PassParameters cached = default;

        first.BeginFrame();
        var source = first.CreateBuffer("CrossGraphParameterSource", StorageBufferDesc("CrossGraphParameterSource"), [1, 2, 3, 4]);
        first.AddComputePass(
            "Build CrossGraph Parameters",
            builder =>
            {
                builder.SideEffect();
                builder.Read(source, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                BufferViewHandle view = context.GetBufferView(source, ViewKind.ShaderResource);
                cached = context.Bindings(layout).Buffer(0, view).Build();
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });
        first.Execute(device, queue);

        second.BeginFrame();
        second.AddComputePass(
            "Replay CrossGraph Parameters",
            builder => builder.SideEffect(),
            (_, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => second.Execute(device, queue));
            Assert.Contains("cannot reuse parameter packets across frames", ex.Message);
        }
        finally
        {
            first.BeginFrame();
            first.ClearBindSets(waitForGpu: true);
            second.BeginFrame();
            second.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsCrossFramePassBindingReplay()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "CrossFrame Binding Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();
        PassBindings cached = null!;

        graph.BeginFrame();
        var first = graph.CreateBuffer("CrossFrameBindingSource", StorageBufferDesc("CrossFrameBindingSource"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Build Cached Bindings",
            builder =>
            {
                builder.SideEffect();
                builder.Read(first, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                BufferViewHandle view = context.GetBufferView(first, ViewKind.ShaderResource);
                cached = context.Bindings(layout).Buffer(0, view);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });
        graph.Execute(device, queue);

        graph.BeginFrame();
        var second = graph.CreateBuffer("CrossFrameBindingTarget", StorageBufferDesc("CrossFrameBindingTarget"), [5, 6, 7, 8]);
        graph.AddComputePass(
            "Replay Cached Bindings",
            builder =>
            {
                builder.SideEffect();
                builder.Read(second, ResourceState.ShaderResource);
            },
            (_, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, cached);
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("cannot reuse binding packets across frames", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_RejectsDetachedPassParameters()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Detached Parameter Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "DetachedParameterBuffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
            Raw = true,
        };
        BufferHandle buffer = device.CreateBuffer(desc);
        BufferViewHandle view = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.ShaderResource,
                Offset = 0,
                SizeInBytes = desc.SizeInBytes,
                Raw = true,
            });
        PassParameters detached = new(
            layout,
            [BindingResourceDesc.Buffer(0, view)]);

        graph.BeginFrame();
        graph.AddComputePass(
            "Detached Parameters",
            builder => builder.SideEffect(),
            (_, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, detached);
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("requires parameter packets built through RenderGraphContext.Bindings", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
            device.Destroy(view);
            device.Destroy(buffer);
        }
    }

    [Fact]
    public void Execute_RejectsUndeclaredPassBindings()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Undeclared Binding Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var declared = graph.CreateBuffer("DeclaredBindingBuffer", StorageBufferDesc("DeclaredBindingBuffer"), [1, 2, 3, 4]);
        var hidden = graph.CreateBuffer("HiddenBindingBuffer", StorageBufferDesc("HiddenBindingBuffer"), [5, 6, 7, 8]);
        graph.AddComputePass(
            "Undeclared Binding Pass",
            builder =>
            {
                builder.SideEffect();
                builder.Read(declared, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, hidden));
            });
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("HiddenBindingBuffer", ex.Message);
            Assert.Contains("did not declare it in setup", ex.Message);
        }
        finally
        {
            graph.BeginFrame();
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    [Fact]
    public void Execute_AcceptsSetFinalStateWithoutWriter()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "unwritten output",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var rtv = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
            });
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var output = graph.ImportTexture(
            "Output",
            texture,
            device.GetTextureDesc(texture),
            new ImportDesc(ResourceState.RenderTarget),
            rtv);
        graph.SetFinalState(output, ResourceState.RenderTarget);

        graph.Execute(device, queue);

        Assert.Equal(ResourceState.RenderTarget, device.GetTextureState(texture));

        device.Destroy(rtv);
        device.Destroy(texture);
    }

    [Fact]
    public void Execute_RejectsUndeclaredTextureAccess()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var declared = graph.CreateTexture("DeclaredTexture", ShaderTextureDesc("DeclaredTexture"));
        var hidden = graph.CreateTexture("HiddenTexture", ShaderTextureDesc("HiddenTexture"));
        var output = graph.CreateTexture("TextureAccessOutput", TargetTextureDesc("TextureAccessOutput"));
        WriteTexture(graph, declared, "Initialize declared texture", ResourceState.RenderTarget);
        graph.AddRasterPass(
            "Access hidden texture",
            builder =>
            {
                builder.Read(declared, ResourceState.ShaderResource);
                builder.Write(output, ResourceState.RenderTarget);
            },
            context => _ = context.GetTextureDesc(hidden));
        graph.ExtractTexture(output, ResourceState.RenderTarget, (_, _) => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
        Assert.Contains("HiddenTexture", ex.Message);
        Assert.Contains("did not declare it in setup", ex.Message);
    }

    [Fact]
    public void Execute_RejectsUndeclaredBufferAccess()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var declared = graph.CreateBuffer("DeclaredBuffer", StorageBufferDesc("DeclaredBuffer"), [1, 2, 3, 4]);
        var hidden = graph.CreateBuffer("HiddenBuffer", StorageBufferDesc("HiddenBuffer"));
        var output = graph.CreateTexture("BufferAccessOutput", TargetTextureDesc("BufferAccessOutput"));
        graph.AddRasterPass(
            "Access hidden buffer",
            builder =>
            {
                builder.Read(declared, ResourceState.ShaderResource);
                builder.Write(output, ResourceState.RenderTarget);
            },
            context => _ = context.GetBufferDesc(hidden));
        graph.ExtractTexture(output, ResourceState.RenderTarget, (_, _) => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
        Assert.Contains("HiddenBuffer", ex.Message);
        Assert.Contains("did not declare it in setup", ex.Message);
    }

    [Fact]
    public void Execute_RejectsResourceCreationDuringPassExecute()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass(
            "Mutate Execute Resources",
            builder => builder.SideEffect(),
            _ => graph.CreateTexture("IllegalExecuteTexture", TargetTextureDesc("IllegalExecuteTexture")));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("CreateTexture", ex.Message);
        Assert.Contains("pass execute", ex.Message);
        Assert.Contains("Mutate Execute Resources", ex.Message);
    }

    [Fact]
    public void Execute_RejectsPassCreationDuringPassExecute()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass(
            "Mutate Execute Passes",
            builder => builder.SideEffect(),
            _ => graph.AddRasterPass("Illegal Execute Pass", _ => { }, _ => { }));

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));

        Assert.Contains("AddRasterPass", ex.Message);
        Assert.Contains("pass execute", ex.Message);
        Assert.Contains("Mutate Execute Passes", ex.Message);
    }

    [Fact]
    public void Execute_FinalizeResourcesResolvesTransientBeforePassExecution()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var future = graph.CreateBuffer(
            "FutureBuffer",
            new BufferDesc
            {
                Name = "FutureBuffer",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.Undefined,
                Raw = true,
            });
        bool resolvedBeforeUse = false;
        graph.AddRasterPass(
            "Observe Before Use",
            builder => builder.SideEffect(),
            _ => resolvedBeforeUse = IsBufferResolved(graph, future));
        graph.AddRasterPass(
            "Use Future Buffer",
            builder => builder.Write(future, ResourceState.UnorderedAccess),
            _ => Assert.True(IsBufferResolved(graph, future)));
        graph.ExtractBuffer(future, ResourceState.UnorderedAccess, (_, _) => { });

        graph.Execute(device, queue);

        Assert.True(resolvedBeforeUse);
    }

    [Fact]
    public void AliasesBuffers()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        graph.EnableResourceAliasing = true;

        graph.BeginFrame();
        var first = graph.CreateBuffer("AliasFirst", AliasBufferDesc("AliasFirst"));
        var second = graph.CreateBuffer("AliasSecond", AliasBufferDesc("AliasSecond"));
        ResourceAllocationInfo firstAllocation = default;
        ResourceAllocationInfo secondAllocation = default;

        graph.AddRasterPass(
            "Write first alias",
            builder => builder.Write(first, ResourceState.UnorderedAccess),
            context => firstAllocation = device.GetBufferAlloc(context.GetBuffer(first)));
        graph.AddRasterPass(
            "Write second alias",
            builder => builder.Write(second, ResourceState.UnorderedAccess),
            context => secondAllocation = device.GetBufferAlloc(context.GetBuffer(second)));
        graph.SetFinalState(first, ResourceState.UnorderedAccess);
        graph.SetFinalState(second, ResourceState.UnorderedAccess);

        graph.Execute(device, queue);

        Assert.Equal(ResourceOwnership.Placed, firstAllocation.Ownership);
        Assert.Equal(ResourceOwnership.Placed, secondAllocation.Ownership);
        Assert.Equal(firstAllocation.Heap, secondAllocation.Heap);
        Assert.Equal(firstAllocation.HeapOffset, secondAllocation.HeapOffset);
    }

    [Fact]
    public void AliasesBuffers_DoesNotAliasIndependentAsyncQueues()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        graph.EnableResourceAliasing = true;

        graph.BeginFrame();
        var compute = graph.CreateBuffer("AsyncAliasCompute", AliasBufferDesc("AsyncAliasCompute"));
        var graphics = graph.CreateBuffer("AsyncAliasGraphics", AliasBufferDesc("AsyncAliasGraphics"));
        ResourceAllocationInfo computeAllocation = default;
        ResourceAllocationInfo graphicsAllocation = default;

        graph.AddComputePass(
            "Write async compute alias",
            builder => builder.Write(compute, ResourceState.UnorderedAccess),
            (context, _) => computeAllocation = device.GetBufferAlloc(context.GetBuffer(compute)));
        graph.AddRasterPass(
            "Write async graphics alias",
            builder => builder.Write(graphics, ResourceState.UnorderedAccess),
            context => graphicsAllocation = device.GetBufferAlloc(context.GetBuffer(graphics)));
        graph.SetFinalState(compute, ResourceState.UnorderedAccess);
        graph.SetFinalState(graphics, ResourceState.UnorderedAccess);

        graph.Execute(device, new GraphQueues(device, queue));

        Assert.False(AllocationsOverlap(computeAllocation, graphicsAllocation));
        Assert.Empty(RenderGraphTestHelpers.QueueLinks(graph, asyncCompute: true));
    }

    [Fact]
    public void Compile_CacheKeepsReusableAfterAliasExecute()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        graph.EnableResourceAliasing = true;
        ResourceAllocationInfo firstAllocation = default;
        ResourceAllocationInfo secondAllocation = default;

        graph.BeginFrame();
        BuildFrame(capture: true);
        graph.Execute(device, queue);

        Assert.True(RenderGraphTestHelpers.OperationCount(device, "AliasingBarrier") >= 2);
        Assert.Equal(1, AliasCacheCount(graph));
        Assert.Equal(ResourceOwnership.Placed, firstAllocation.Ownership);
        Assert.Equal(ResourceOwnership.Placed, secondAllocation.Ownership);
        Assert.Equal(firstAllocation.Heap, secondAllocation.Heap);
        Assert.Equal(firstAllocation.HeapOffset, secondAllocation.HeapOffset);

        graph.BeginFrame();
        var next = BuildFrame(capture: false);

        Assert.True(RenderGraphTestHelpers.ResourceReusable(graph, next.First));
        Assert.True(RenderGraphTestHelpers.ResourceReusable(graph, next.Second));
        Assert.Equal(1, CacheCount(graph));
        Assert.Equal(1, AliasCacheCount(graph));
        graph.Execute(device, queue);
        Assert.Equal(1, AliasCacheCount(graph));
        Assert.True(RenderGraphTestHelpers.OperationCount(device, "AliasingBarrier") >= 2);

        (RenderGraphHandle First, RenderGraphHandle Second) BuildFrame(bool capture)
        {
            var first = graph.CreateBuffer("AliasCacheFirst", AliasBufferDesc("AliasCacheFirst"));
            var second = graph.CreateBuffer("AliasCacheSecond", AliasBufferDesc("AliasCacheSecond"));
            graph.AddRasterPass(
                "Write first cache alias",
                builder => builder.Write(first, ResourceState.UnorderedAccess),
                context =>
                {
                    if (capture)
                        firstAllocation = device.GetBufferAlloc(context.GetBuffer(first));
                });
            graph.AddRasterPass(
                "Write second cache alias",
                builder => builder.Write(second, ResourceState.UnorderedAccess),
                context =>
                {
                    if (capture)
                        secondAllocation = device.GetBufferAlloc(context.GetBuffer(second));
                });
            graph.SetFinalState(first, ResourceState.UnorderedAccess);
            graph.SetFinalState(second, ResourceState.UnorderedAccess);
            return (first, second);
        }
    }

    [Fact]
    public void BuildFrameSync_UsesGraphicsQueueWhenFinalAliasWorkExists()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();
        graph.EnableResourceAliasing = false;

        graph.BeginFrame();
        var source = graph.CreateBuffer(
            "FrameSyncSource",
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var target = graph.CreateBuffer(
            "FrameSyncTarget",
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.Undefined,
            });

        BufferCopyPasses.AddCopyPass(graph, "Copy Tail", source, target, 0, 0, 4);
        graph.SetFinalState(target, ResourceState.CopySource);

        object compile = typeof(RenderGraph)
            .GetMethod("CompileGraph", ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Instance)!
            .Invoke(graph, [false, true])!;
        object finalTransitions = compile.GetType()
            .GetField("FinalTransitions", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!
            .GetValue(compile)!;

        Assert.Equal(QueueType.Copy, compile.GetType().GetField("FrameQueue", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!.GetValue(compile));

        finalTransitions.GetType().GetField("SharedAliasResources", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!.SetValue(finalTransitions, new[] { 0 });
        finalTransitions.GetType().GetField("HasAliases", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!.SetValue(finalTransitions, true);

        object compiler = typeof(RenderGraph)
            .GetField("_compiler", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
            .GetValue(graph)!;
        compiler.GetType()
            .GetMethod("BuildFrameSync", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Public)!
            .Invoke(compiler, [compile]);

        Assert.Equal(QueueType.Graphics, compile.GetType().GetField("FrameQueue", ReflectionBindingFlags.Public | ReflectionBindingFlags.Instance)!.GetValue(compile));
    }

    [Fact]
    public void Execute_AppliesExportFinalStateWithoutPasses()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var texture = graph.CreateTexture("ExportedFinalState", TargetTextureDesc("ExportedFinalState"));
        bool sinkCalled = false;
        TextureHandle extracted = default;
        ResourceState finalState = ResourceState.Undefined;
        graph.ExtractTexture(
            texture,
            ResourceState.ShaderResource,
            (handle, state) =>
            {
                sinkCalled = true;
                extracted = handle;
                finalState = state;
            });

        graph.Execute(device, queue);
        graph.BeginFrame();

        Assert.True(sinkCalled);
        Assert.True(extracted.IsValid);
        Assert.Equal(ResourceState.ShaderResource, finalState);
        device.Destroy(extracted);
    }

    [Fact]
    public void Execute_AppliesImportedFinalStateWithoutProducer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        BufferHandle buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "ImportedFinalStateBuffer",
                SizeInBytes = 64,
                BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
                Raw = true,
            });

        graph.BeginFrame();
        var imported = graph.ImportBuffer(
            "ImportedFinalStateBuffer",
            buffer,
            device.GetBufferDesc(buffer),
            new ImportDesc(ResourceState.CopyDestination));
        graph.SetFinalState(imported, ResourceState.ShaderResource);

        graph.Execute(device, queue);
        graph.BeginFrame();

        Assert.Equal(ResourceState.ShaderResource, device.GetBufferState(buffer));
        device.Destroy(buffer);
    }

    [Fact]
    public void Execute_RejectsDeviceMigration()
    {
        using var instance = Instance.Create();
        using var deviceA = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var deviceB = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queueA = deviceA.GetQueue(QueueType.Graphics);
        var queueB = deviceB.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass("Device A Pass", builder => builder.SideEffect(), _ => { });
        graph.Execute(deviceA, queueA);

        graph.BeginFrame();
        graph.AddRasterPass("Device B Pass", builder => builder.SideEffect(), _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(deviceB, queueB));
        Assert.Contains("device-affine", ex.Message);
    }

    [Fact]
    public void Execute_RejectsImportedBufferFromForeignDevice()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var foreignDevice = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "ForeignImportedBuffer",
            SizeInBytes = 16,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
            StrideInBytes = 4,
        };
        BufferHandle foreignBuffer = foreignDevice.CreateBuffer(desc);

        graph.BeginFrame();
        var imported = graph.ImportBuffer("ForeignImportedBuffer", foreignBuffer, desc, new ImportDesc(ResourceState.ShaderResource));
        graph.AddComputePass(
            "Foreign Imported Buffer",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            (_, _) => { });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("ForeignImportedBuffer", ex.Message);
            Assert.Contains("does not belong to the executing device", ex.Message);
        }
        finally
        {
            foreignDevice.Destroy(foreignBuffer);
        }
    }

    [Fact]
    public void Execute_RejectsImportedTextureFromForeignDevice()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var foreignDevice = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "ForeignImportedTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle foreignTexture = foreignDevice.CreateTexture(desc);

        graph.BeginFrame();
        var imported = graph.ImportTexture("ForeignImportedTexture", foreignTexture, desc, new ImportDesc(ResourceState.ShaderResource));
        graph.AddComputePass(
            "Foreign Imported Texture",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            (_, _) => { });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("ForeignImportedTexture", ex.Message);
            Assert.Contains("does not belong to the executing device", ex.Message);
        }
        finally
        {
            foreignDevice.Destroy(foreignTexture);
        }
    }

    [Fact]
    public void Execute_RejectsRetainedImportedBufferViewWithForeignOwner()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        BufferDesc desc = new()
        {
            Name = "RetainedImportedBuffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
            StrideInBytes = 4,
        };
        BufferHandle owner = device.CreateBuffer(desc with { Name = "RetainedImportedOwner" });
        BufferHandle foreign = device.CreateBuffer(desc with { Name = "RetainedImportedForeign" });
        BufferViewDesc viewDesc = new()
        {
            Kind = ViewKind.ShaderResource,
            Offset = 0,
            SizeInBytes = desc.SizeInBytes,
            StrideInBytes = desc.StrideInBytes,
        };
        BufferViewHandle foreignView = device.CreateBufferView(foreign, viewDesc);
        var retainedViews = new FlatDictionary<BufferViewKey, BufferViewHandle>();
        retainedViews[BufferViewKey.From(viewDesc)] = foreignView;

        graph.BeginFrame();
        var imported = graph.ImportBuffer(
            "RetainedImportedBuffer",
            owner,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            retainedViews);
        graph.AddComputePass(
            "Retained Imported Buffer View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            (context, _) =>
            {
                context.GetBufferView(imported, ViewKind.ShaderResource);
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("imported buffer views must belong", ex.Message);
        }
        finally
        {
            device.Destroy(foreignView);
            device.Destroy(foreign);
            device.Destroy(owner);
        }
    }

    [Fact]
    public void Execute_RejectsRetainedImportedTextureViewWithForeignOwner()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        TextureDesc desc = new()
        {
            Name = "RetainedImportedTexture",
            Dimension = ResourceDimension.Texture2D,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
        };
        TextureHandle owner = device.CreateTexture(desc with { Name = "RetainedImportedTextureOwner" });
        TextureHandle foreign = device.CreateTexture(desc with { Name = "RetainedImportedTextureForeign" });
        TextureViewDesc viewDesc = new()
        {
            Kind = ViewKind.ShaderResource,
            Dimension = TextureViewDimension.Texture2D,
            Format = desc.Format,
            FirstMip = 0,
            MipCount = 1,
            FirstSlice = 0,
            SliceCount = 1,
        };
        TextureViewHandle foreignView = device.CreateTextureView(foreign, viewDesc);
        var retainedViews = new FlatDictionary<TextureViewKey, TextureViewHandle>();
        retainedViews[TextureViewKey.From(viewDesc)] = foreignView;

        graph.BeginFrame();
        var imported = graph.ImportTexture(
            "RetainedImportedTexture",
            owner,
            desc,
            new ImportDesc(ResourceState.ShaderResource),
            retainedViews);
        graph.AddComputePass(
            "Retained Imported Texture View",
            builder =>
            {
                builder.SideEffect();
                builder.Read(imported, ResourceState.ShaderResource);
            },
            (context, _) =>
            {
                context.GetTextureView(imported, ViewKind.ShaderResource, desc.Format);
            });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => graph.Execute(device, queue));
            Assert.Contains("imported texture views must belong", ex.Message);
        }
        finally
        {
            device.Destroy(foreignView);
            device.Destroy(foreign);
            device.Destroy(owner);
        }
    }

    [Fact]
    public void Execute_RejectsGraphQueuesWithForeignQueueOwner()
    {
        using var instance = Instance.Create();
        using var deviceA = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var deviceB = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass("Foreign Queue Pass", builder => builder.SideEffect(), _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            graph.Execute(new GraphQueues(deviceA, deviceB.GetQueue(QueueType.Graphics))));
        Assert.Contains("does not belong to the supplied device", ex.Message);
    }

    [Fact]
    public void Compile_RejectsGraphQueuesWithForeignQueueOwner()
    {
        using var instance = Instance.Create();
        using var deviceA = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var deviceB = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass("Foreign Compile Queue Pass", builder => builder.SideEffect(), _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            graph.Compile(new GraphQueues(deviceA, deviceB.GetQueue(QueueType.Graphics))));
        Assert.Contains("does not belong to the supplied device", ex.Message);
    }

    [Fact]
    public void Execute_CreatesDynamicUniformBufferAndFullRangeConstantView()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var uniforms = RenderGraphUniforms.AddDynamicUniform(
            graph,
            "TestUniforms",
            new TestUniform(1, 2, 3, 4));
        graph.AddRasterPass(
            "Read uniforms",
            builder =>
            {
                builder.SideEffect();
                builder.Read(uniforms, ResourceState.ConstantBuffer);
            },
            context =>
            {
                var desc = context.GetBufferDesc(uniforms);
                Assert.Equal(256ul, desc.SizeInBytes);
                Assert.Equal(BindFlags.ConstantBuffer, desc.BindFlags);

                var view = context.GetBufferView(uniforms, ViewKind.ConstantBuffer);
                Assert.True(view.IsValid);
            });

        graph.Execute(device, queue);
    }

    [Fact]
    public void Execute_ReusesTransientBufferAndViewAcrossFrames()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        ExecuteTransientBufferFrame(graph, device, queue, "FirstTransient");
        int buffersAfterFirstFrame = LiveHandleCount(device, "Buffers");
        int viewsAfterFirstFrame = LiveHandleCount(device, "BufferViews");

        ExecuteTransientBufferFrame(graph, device, queue, "SecondTransient");

        Assert.Equal(buffersAfterFirstFrame, LiveHandleCount(device, "Buffers"));
        Assert.Equal(viewsAfterFirstFrame, LiveHandleCount(device, "BufferViews"));
    }

    [Fact]
    public void BindSetOwnerDefers()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        var layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferRead,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Deferred bind set owner test");

        ExecuteCachedStorageBufferFrame(graph, device, queue, layout, "CachedBuffer");

        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));

        SetRenderGraphFrameFenceCompletedValue(graph, device, 0);
        graph.ClearBindSets();

        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));

        SetRenderGraphFrameFenceCompletedValue(graph, device, 1);
        graph.ClearBindSets();

        Assert.Equal(0, LiveHandleCount(device, "BindingSets"));
        graph.BeginFrame();
        device.Destroy(layout);
    }

    [Fact]
    public void RetireBindingSets_ReturnsFalseUntilFenceCompletes()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        var layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferRead,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Deferred binding retire test");

        ExecuteCachedStorageBufferFrame(graph, device, queue, layout, "RetiredBindingBuffer");

        SetRenderGraphFrameFenceCompletedValue(graph, device, 0);
        graph.ClearBindSets();

        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));
        Assert.False(graph.RetireBindingSets());
        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));

        SetRenderGraphFrameFenceCompletedValue(graph, device, 1);

        Assert.True(graph.RetireBindingSets());
        Assert.Equal(0, LiveHandleCount(device, "BindingSets"));
        graph.BeginFrame();
        device.Destroy(layout);
    }

    [Fact]
    public void RetiredFrameViewEviction_RemovesBindingSetLastUse()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        var layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferRead,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Binding last-use eviction test");

        ExecuteCachedStorageBufferFrame(
            graph,
            device,
            queue,
            layout,
            "EvictedCachedBuffer",
            reusable: false);

        Assert.Equal(1, BindingSetLastUseCount(graph));

        graph.BeginFrame();

        Assert.Equal(0, LiveHandleCount(device, "BindingSets"));
        Assert.Equal(0, BindingSetLastUseCount(graph));
        device.Destroy(layout);
    }

    [Fact]
    public void RepeatedParameters_ReuseBindingSet()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Repeated parameter layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer("RepeatedParameterBuffer", StorageBufferDesc("RepeatedParameterBuffer"), [1, 2, 3, 4]);
        graph.AddComputePass(
            "Repeated Parameters",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                var parameters = context.Bindings(layout)
                    .Buffer(0, context.GetBufferView(buffer, ViewKind.ShaderResource))
                    .Build();
                pass.SetParameters(0, parameters);
                pass.SetParameters(0, parameters);
                pass.Dispatch(1, 1, 1);
            });

        graph.Execute(device, queue);

        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));

        graph.ClearBindSets(waitForGpu: true);
        DestroyPipeline(device, pipeline);
        device.Destroy(layout);
    }

    [Fact]
    public void ViewEvictIndexed()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        var layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferRead,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Indexed view eviction test");

        RenderBindings(graph, device, queue, layout);

        Assert.Equal(2, BindSetOwnerCount(graph));
        Assert.Equal(2, BindingIndexCount(graph));

        graph.BeginFrame();

        Assert.Equal(1, BindSetOwnerCount(graph));
        Assert.Equal(1, BindingIndexCount(graph));
        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));

        graph.ClearBindSets(waitForGpu: true);
        Assert.Equal(0, BindSetOwnerCount(graph));
        Assert.Equal(0, BindingIndexCount(graph));
        device.Destroy(layout);
    }

    [Fact]
    public void LayoutEvictIndexed()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        var layoutA = StorageLayout(device, "Layout eviction A");
        var layoutB = StorageLayout(device, "Layout eviction B");

        RenderBindings(graph, device, queue, layoutA, layoutB);

        Assert.Equal(2, BindSetOwnerCount(graph));
        Assert.Equal(2, BindingIndexCount(graph));
        Assert.Equal(2, LiveHandleCount(device, "BindingSets"));

        SetRenderGraphFrameFenceCompletedValue(graph, device, 1);
        graph.ClearBindings([layoutA]);

        Assert.Equal(1, BindSetOwnerCount(graph));
        Assert.Equal(1, BindingIndexCount(graph));
        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));

        graph.ClearBindings([layoutB], waitForGpu: true);

        Assert.Equal(0, BindSetOwnerCount(graph));
        Assert.Equal(0, BindingIndexCount(graph));
        Assert.Equal(0, LiveHandleCount(device, "BindingSets"));
        device.Destroy(layoutB);
        device.Destroy(layoutA);
    }

    [Fact]
    public void BindSetOwnerWaits()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();
        var layout = device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferRead,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            "Wait bind set owner test");

        ExecuteCachedStorageBufferFrame(
            graph,
            device,
            queue,
            layout,
            "WaitClearCachedBuffer",
            reusable: false);

        Assert.Equal(1, LiveHandleCount(device, "BindingSets"));
        Assert.Equal(1, LiveHandleCount(device, "Buffers"));
        Assert.Equal(1, LiveHandleCount(device, "BufferViews"));

        graph.ClearBindSets(waitForGpu: true);

        Assert.Equal(0, LiveHandleCount(device, "BindingSets"));
        Assert.Equal(0, LiveHandleCount(device, "Buffers"));
        Assert.Equal(0, LiveHandleCount(device, "BufferViews"));
        device.Destroy(layout);
    }

    [Fact]
    public void Execute_ReusesPersistentHistoryTextureAndViewAcrossFrames()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var histories = new ViewHistory();
        using var graph = new RenderGraph();

        ExecuteHistoryTextureFrame(graph, histories, device, queue, "FirstHistorySource");
        ExecuteHistoryTextureFrame(graph, histories, device, queue, "SecondHistorySource");
        int texturesAfterWarmup = LiveHandleCount(device, "Textures");
        int viewsAfterWarmup = LiveHandleCount(device, "TextureViews");

        ExecuteHistoryTextureFrame(graph, histories, device, queue, "ThirdHistorySource");

        Assert.Equal(texturesAfterWarmup, LiveHandleCount(device, "Textures"));
        Assert.Equal(viewsAfterWarmup, LiveHandleCount(device, "TextureViews"));
    }

    [Fact]
    public void CreateBuffer_RejectsInitialDataLargerThanBuffer()
    {
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var ex = Assert.Throws<ArgumentException>(() => graph.CreateBuffer(
            "TooSmall",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            },
            [1, 2, 3, 4, 5]));

        Assert.Contains("initial data", ex.Message);
    }

    [Fact]
    public void Execute_CopiesCreatedUploadBufferIntoImportedBuffer()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var readbackDesc = new BufferDesc
        {
            Name = "CopyReadback",
            SizeInBytes = 4,
            Memory = MemoryClass.CpuReadback,
            BindFlags = BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
        };
        var readback = device.CreateBuffer(readbackDesc);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var source = graph.CreateBuffer(
            "UploadSource",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination | BindFlags.ShaderResource,
                InitialState = ResourceState.CopySource,
            },
            [7, 8, 9, 10]);
        ResourceState? finalState = null;
        var destination = graph.ImportBuffer(
            "ImportedReadback",
            readback,
            readbackDesc,
            new ImportDesc(ResourceState.Common)
            {
                AllowWrite = true,
            });
        BufferCopyPasses.AddCopyPass(graph, "Upload to imported readback", source, destination, 0, 0, 4);
        graph.ExtractBuffer(destination, ResourceState.CopyDestination, (_, state) => finalState = state);

        graph.Execute(device, queue);

        Assert.Equal(ResourceState.CopyDestination, finalState);
        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
        Assert.Equal([7, 8, 9, 10], mapped.ToArray());
        device.UnmapBuffer(readback);
        device.Destroy(readback);
    }

    [Fact]
    public void Execute_BatchedBufferUploadsCopyAllRequestsInOrder()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var readbackDesc = new BufferDesc
        {
            SizeInBytes = 4,
            Memory = MemoryClass.CpuReadback,
            BindFlags = BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
        };
        var readbackA = device.CreateBuffer(readbackDesc with { Name = "BatchReadbackA" });
        var readbackB = device.CreateBuffer(readbackDesc with { Name = "BatchReadbackB" });
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var targetA = graph.CreateBuffer(
            "BatchTargetA",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.Undefined,
            });
        var targetB = graph.CreateBuffer(
            "BatchTargetB",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.Undefined,
            });
        var importedA = graph.ImportBuffer(
            "ImportedBatchReadbackA",
            readbackA,
            readbackDesc,
            new ImportDesc(ResourceState.Common)
            {
                FinalState = ResourceState.CopyDestination,
                AllowWrite = true,
            });
        var importedB = graph.ImportBuffer(
            "ImportedBatchReadbackB",
            readbackB,
            readbackDesc,
            new ImportDesc(ResourceState.Common)
            {
                FinalState = ResourceState.CopyDestination,
                AllowWrite = true,
            });

        var uploadBatch = new BufferUploadBatch(graph, "Batched Uploads");
        uploadBatch.AddUpload(targetA, 0, [1, 2, 3, 4]);
        uploadBatch.AddUpload(targetB, 0, [10, 11, 12, 13]);
        uploadBatch.AddUpload(targetA, 2, [8, 9]);
        uploadBatch.AddPass();

        BufferCopyPasses.AddCopyBatch(
            graph,
            "Read back batched uploads",
            [
                new BufferCopyRequest(targetA, importedA, 0, 0, 4),
                new BufferCopyRequest(targetB, importedB, 0, 0, 4),
            ]);

        graph.Execute(device, queue);

        var mappedA = device.MapBuffer(readbackA, MapMode.Read, 0, 4);
        Assert.Equal([1, 2, 8, 9], mappedA.ToArray());
        device.UnmapBuffer(readbackA);
        var mappedB = device.MapBuffer(readbackB, MapMode.Read, 0, 4);
        Assert.Equal([10, 11, 12, 13], mappedB.ToArray());
        device.UnmapBuffer(readbackB);
        device.Destroy(readbackA);
        device.Destroy(readbackB);
    }

    [Fact]
    public void Execute_WarmedUploadPassesShareFrameUploadBufferHandle()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var readbackDesc = new BufferDesc
        {
            SizeInBytes = 4,
            Memory = MemoryClass.CpuReadback,
            BindFlags = BindFlags.CopyDestination,
            InitialState = ResourceState.Common,
        };
        var readbackA = device.CreateBuffer(readbackDesc with { Name = "WarmReadbackA" });
        var readbackB = device.CreateBuffer(readbackDesc with { Name = "WarmReadbackB" });
        using var graph = new RenderGraph();

        graph.BeginFrame();
        graph.AddRasterPass("Warmup", builder => builder.SideEffect(), _ => { });
        graph.Execute(device, queue);

        graph.BeginFrame();
        var targetA = graph.CreateBuffer(
            "WarmTargetA",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.Undefined,
            });
        var targetB = graph.CreateBuffer(
            "WarmTargetB",
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.Undefined,
            });
        var importedA = graph.ImportBuffer(
            "ImportedWarmReadbackA",
            readbackA,
            readbackDesc,
            new ImportDesc(ResourceState.Common)
            {
                FinalState = ResourceState.CopyDestination,
                AllowWrite = true,
            });
        var importedB = graph.ImportBuffer(
            "ImportedWarmReadbackB",
            readbackB,
            readbackDesc,
            new ImportDesc(ResourceState.Common)
            {
                FinalState = ResourceState.CopyDestination,
                AllowWrite = true,
            });

        BufferUploadPasses.AddUploadPass(graph, "Warm Upload A", targetA, 0, [11, 12, 13, 14]);
        BufferUploadPasses.AddUploadPass(graph, "Warm Upload B", targetB, 0, [21, 22, 23, 24]);
        BufferCopyPasses.AddCopyPass(graph, "Copy warm target A", targetA, importedA, 0, 0, 4);
        BufferCopyPasses.AddCopyPass(graph, "Copy warm target B", targetB, importedB, 0, 0, 4);

        graph.Execute(device, queue);

        byte[] mappedA = device.MapBuffer(readbackA, MapMode.Read, 0, 4).ToArray();
        byte[] mappedB = device.MapBuffer(readbackB, MapMode.Read, 0, 4).ToArray();
        device.UnmapBuffer(readbackA);
        device.UnmapBuffer(readbackB);

        Assert.Equal([11, 12, 13, 14], mappedA);
        Assert.Equal([21, 22, 23, 24], mappedB);

        device.Destroy(readbackA);
        device.Destroy(readbackB);
    }

    [Fact]
    public void ImportBuffer_RejectsInvalidExternalHandle()
    {
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var ex = Assert.Throws<ArgumentException>(
            () => graph.ImportBuffer(
                "InvalidImport",
                default,
                new BufferDesc
                {
                    SizeInBytes = 4,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.CopySource,
                },
                new ImportDesc(ResourceState.Common)));

        Assert.Contains("valid", ex.Message);
    }

    [Fact]
    public void ImportBuffer_RejectsInvalidImportedViewHandle()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();
        BufferDesc desc = StorageBufferDesc("InvalidImportedBufferView");
        BufferHandle buffer = device.CreateBuffer(desc);

        try
        {
            graph.BeginFrame();
            var ex = Assert.Throws<ArgumentException>(() =>
                graph.ImportBuffer(
                    "InvalidImportedBufferView",
                    buffer,
                    desc,
                    new ImportDesc(ResourceState.ShaderResource),
                    [default]));

            Assert.Contains("view handles must be valid", ex.Message);
        }
        finally
        {
            device.Destroy(buffer);
        }
    }

    [Fact]
    public void ImportTexture_RejectsInvalidImportedViewHandle()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var graph = new RenderGraph();
        TextureDesc desc = ShaderTextureDesc("InvalidImportedTextureView");
        TextureHandle texture = device.CreateTexture(desc);

        try
        {
            graph.BeginFrame();
            var ex = Assert.Throws<ArgumentException>(() =>
                graph.ImportTexture(
                    "InvalidImportedTextureView",
                    texture,
                    desc,
                    new ImportDesc(ResourceState.ShaderResource),
                    [default]));

            Assert.Contains("view handles must be valid", ex.Message);
        }
        finally
        {
            device.Destroy(texture);
        }
    }

    [Fact]
    public void ImportBuffer_WithFinalStateKeepsWriterLiveAndBuildsFinalBarrier()
    {
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var imported = graph.ImportBuffer(
            "Imported",
            new BufferHandle(42, 1),
            new BufferDesc
            {
                SizeInBytes = 4,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination | BindFlags.ShaderResource,
                InitialState = ResourceState.CopySource,
            },
            new ImportDesc(ResourceState.CopySource)
            {
                FinalState = ResourceState.ShaderResource,
                AllowWrite = true,
            });

        WriteTexture(graph, imported, "Write Imported", ResourceState.CopyDestination);
        graph.Compile();

        Assert.Equal(["Write Imported"], RenderGraphTestHelpers.ExecutedPassNames(graph));
        Assert.Equal(1, RenderGraphTestHelpers.FinalTransitionCount(graph));
    }

    [Fact]
    public void Execute_FinalStateUsesExactBarrier()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "ExactFinalStateBuffer",
                SizeInBytes = 16,
                BindFlags = BindFlags.IndirectArgument | BindFlags.ShaderResource,
                InitialState = ResourceState.IndirectArgument,
            });
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var imported = graph.ImportBuffer(
            "ExactFinalStateImported",
            buffer,
            device.GetBufferDesc(buffer),
            new ImportDesc(ResourceState.IndirectArgument)
            {
                FinalState = ResourceState.ShaderResource,
            });
        graph.SetFinalState(imported, ResourceState.ShaderResource);

        graph.Execute(device, queue);

        Assert.Equal(ResourceState.ShaderResource, device.GetBufferState(buffer));
        Assert.Equal(1, RenderGraphTestHelpers.OperationCount(device, "BufferBarrier"));
        device.Destroy(buffer);
    }

    [Fact]
    public void Execute_KeepsOwnedStateTrusted()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "TrustedReadBuffer",
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            },
            [0, 0, 0, 0]);
        graph.AddRasterPass(
            "Trusted Read A",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });
        graph.AddRasterPass(
            "Trusted Read B",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            _ => { });

        graph.Execute(device, queue);

        Assert.True(RenderGraphTestHelpers.ResourceStateTrusted(graph, buffer));
        Assert.Equal(0, RenderGraphTestHelpers.OperationCount(device, "BufferBarrier"));
    }

    [Fact]
    public void Execute_TransitionsBetweenIndirectArgumentAndShaderResourceAcrossPasses()
    {
        using var instance = Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        BindingLayoutHandle layout = StorageLayout(device, "Cross Pass Exact State Layout");
        var pipeline = StoragePipeline(device, layout);
        using var graph = new RenderGraph();

        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            "CrossPassExactStateBuffer",
            new BufferDesc
            {
                Name = "CrossPassExactStateBuffer",
                SizeInBytes = 16,
                BindFlags = BindFlags.IndirectArgument | BindFlags.ShaderResource,
                InitialState = ResourceState.IndirectArgument,
                StrideInBytes = 4,
            },
            [1, 1, 1, 0]);
        graph.AddComputePass(
            "Indirect Producer",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.IndirectArgument);
            },
            (_, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.DispatchIndirect(buffer);
            });
        graph.AddComputePass(
            "Shader Consumer",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(0, context.Bindings(layout).Buffer(0, buffer, BindingType.StorageBufferRead));
                pass.Dispatch(1, 1, 1);
            });

        try
        {
            graph.Execute(device, queue);
            Assert.Equal(1, RenderGraphTestHelpers.RequiredTransitionCount(graph, "Shader Consumer"));
            Assert.Equal(1, RenderGraphTestHelpers.OperationCount(device, "BufferBarrier"));
        }
        finally
        {
            graph.BeginFrame();
            graph.ClearBindSets(waitForGpu: true);
            DestroyPipeline(device, pipeline);
            device.Destroy(layout);
        }
    }

    private static void ExecuteTransientBufferFrame(RenderGraph graph, IDevice device, IQueue queue, string bufferName)
    {
        graph.BeginFrame();
        var buffer = graph.CreateBuffer(
            bufferName,
            new BufferDesc
            {
                SizeInBytes = 256,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            },
            [0]);
        graph.AddRasterPass(
            $"Read {bufferName}",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ConstantBuffer);
            },
            context =>
            {
                var view = context.GetBufferView(buffer, ViewKind.ConstantBuffer);
                Assert.True(view.IsValid);
            });
        graph.Execute(device, queue);
        graph.BeginFrame();
    }

    private static void ExecuteCachedStorageBufferFrame(
        RenderGraph graph,
        IDevice device,
        IQueue queue,
        BindingLayoutHandle layout,
        string bufferName,
        bool reusable = true)
    {
        var pipeline = StoragePipeline(device, layout);
        graph.BeginFrame();
        byte[] initialData = [1, 2, 3, 4];
        var buffer = graph.CreateBuffer(
            bufferName,
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 4,
            },
            reusable ? default : initialData);
        if (reusable)
        {
            graph.AddRasterPass(
                $"Initialize {bufferName}",
                builder => builder.Write(buffer, ResourceState.UnorderedAccess),
                _ => { });
        }

        graph.AddComputePass(
            "Create cached binding set",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                var view = context.GetBufferView(buffer, ViewKind.ShaderResource);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(
                    0,
                    context.Bindings(layout)
                        .Buffer(0, view));
            });
        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            DestroyPipeline(device, pipeline);
        }
    }

    private static void RenderBindings(
        RenderGraph graph,
        IDevice device,
        IQueue queue,
        BindingLayoutHandle layout)
    {
        var pipeline = StoragePipeline(device, layout);
        graph.BeginFrame();
        var reusable = graph.CreateBuffer("ReusableBindingBuffer", StorageBufferDesc("ReusableBindingBuffer"));
        var singleUse = graph.CreateBuffer("SingleUseBindingBuffer", StorageBufferDesc("SingleUseBindingBuffer"), [1, 2, 3, 4]);
        graph.AddRasterPass(
            "Initialize reusable binding buffer",
            builder => builder.Write(reusable, ResourceState.UnorderedAccess),
            _ => { });

        graph.AddComputePass(
            "Create mixed binding sets",
            builder =>
            {
                builder.SideEffect();
                builder.Read(reusable, ResourceState.ShaderResource);
                builder.Read(singleUse, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                var reusableView = context.GetBufferView(reusable, ViewKind.ShaderResource);
                var singleUseView = context.GetBufferView(singleUse, ViewKind.ShaderResource);
                pass.SetPipeline(pipeline.Pipeline);
                pass.SetParameters(
                    0,
                    context.Bindings(layout)
                        .Buffer(0, reusableView));
                pass.SetParameters(
                    0,
                    context.Bindings(layout)
                        .Buffer(0, singleUseView));
            });
        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            DestroyPipeline(device, pipeline);
        }
    }

    private static void RenderBindings(
        RenderGraph graph,
        IDevice device,
        IQueue queue,
        BindingLayoutHandle layoutA,
        BindingLayoutHandle layoutB)
    {
        var pipelineA = StoragePipeline(device, layoutA);
        var pipelineB = StoragePipeline(device, layoutB);
        graph.BeginFrame();
        var buffer = graph.CreateBuffer("LayoutBindingBuffer", StorageBufferDesc("LayoutBindingBuffer"));
        graph.AddRasterPass(
            "Initialize layout binding buffer",
            builder => builder.Write(buffer, ResourceState.UnorderedAccess),
            _ => { });

        graph.AddComputePass(
            "Create layout binding sets",
            builder =>
            {
                builder.SideEffect();
                builder.Read(buffer, ResourceState.ShaderResource);
            },
            (context, pass) =>
            {
                var view = context.GetBufferView(buffer, ViewKind.ShaderResource);
                pass.SetPipeline(pipelineA.Pipeline);
                pass.SetParameters(
                    0,
                    context.Bindings(layoutA)
                        .Buffer(0, view));
                pass.SetPipeline(pipelineB.Pipeline);
                pass.SetParameters(
                    0,
                    context.Bindings(layoutB)
                        .Buffer(0, view));
            });
        try
        {
            graph.Execute(device, queue);
        }
        finally
        {
            DestroyPipeline(device, pipelineB);
            DestroyPipeline(device, pipelineA);
        }
    }

    private readonly record struct TestPipeline(
        ShaderModuleHandle Shader,
        PipelineLayoutHandle Layout,
        PipelineHandle Pipeline);

    private static TestPipeline StoragePipeline(IDevice device, BindingLayoutHandle layout)
    {
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "storage binding cache test",
                Backend = Backend.Null,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = new byte[] { 1, 2, 3, 4 },
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc
        {
            BindingLayouts = [layout],
        });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc
        {
            Layout = pipelineLayout,
            ComputeShader = shader,
        });
        return new TestPipeline(shader, pipelineLayout, pipeline);
    }

    private static void DestroyPipeline(IDevice device, TestPipeline pipeline)
    {
        device.Destroy(pipeline.Pipeline);
        device.Destroy(pipeline.Layout);
        device.Destroy(pipeline.Shader);
    }

    private static BindingLayoutHandle StorageLayout(IDevice device, string name)
        => device.CreateBindingLayout(
            [
                new BindingSlotDesc
                {
                    Binding = 0,
                    Type = BindingType.StorageBufferRead,
                    Stages = ShaderStageFlags.Compute,
                },
            ],
            name);

    private static void ExecuteHistoryTextureFrame(
        RenderGraph graph,
        ViewHistory histories,
        IDevice device,
        IQueue queue,
        string sourceName)
    {
        graph.BeginFrame();
        var history = histories.ImportTemporalSceneColor(
            graph,
            device,
            new TextureDesc
            {
                Name = "PersistentHistory",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                InitialState = ResourceState.Common,
            },
            ResourceState.ShaderResource);
        var source = graph.CreateTexture(
            sourceName,
            new TextureDesc
            {
                Name = sourceName,
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.CopySource,
                InitialState = ResourceState.RenderTarget,
            });
        WriteTexture(graph, source, $"Initialize {sourceName}", ResourceState.RenderTarget);
        graph.AddRasterPass(
            $"Use {sourceName}",
            builder =>
            {
                builder.Read(source, ResourceState.ShaderResource);
                builder.Write(history.Current, ResourceState.RenderTarget);
            },
            context =>
            {
                Assert.True(context.GetTextureView(source, ViewKind.ShaderResource).IsValid);
                Assert.True(context.GetTextureView(history.Current, ViewKind.RenderTarget).IsValid);
            });
        graph.Execute(device, queue);
    }

    private static TextureDesc ShaderTextureDesc(string name)
        => new()
        {
            Name = name,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
        };

    private static TextureDesc TargetTextureDesc(string name)
        => new()
        {
            Name = name,
            Width = 16,
            Height = 16,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.RenderTarget,
            InitialState = ResourceState.Common,
        };

    private static BufferDesc StorageBufferDesc(string name)
        => new()
        {
            Name = name,
            SizeInBytes = 16,
            BindFlags = BindFlags.ShaderResource,
            InitialState = ResourceState.ShaderResource,
            StrideInBytes = 4,
        };

    private static BufferDesc AliasBufferDesc(string name)
        => new()
        {
            Name = name,
            SizeInBytes = 64,
            BindFlags = BindFlags.UnorderedAccess,
            InitialState = ResourceState.Undefined,
            Raw = true,
        };

    private static bool AllocationsOverlap(ResourceAllocationInfo first, ResourceAllocationInfo second)
    {
        if (first.Ownership != ResourceOwnership.Placed || second.Ownership != ResourceOwnership.Placed)
            return false;
        if (first.Heap != second.Heap)
            return false;

        ulong firstEnd = checked(first.HeapOffset + first.SizeInBytes);
        ulong secondEnd = checked(second.HeapOffset + second.SizeInBytes);
        return first.HeapOffset < secondEnd && second.HeapOffset < firstEnd;
    }

    private static int BindingSetLastUseCount(RenderGraph graph)
    {
        object lastUse = typeof(RenderGraph)
            .GetField("_bindingSetLastUse", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)?
            .GetValue(graph)
            ?? typeof(RenderGraph)
                .GetProperty("_bindingSetLastUse", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
                .GetValue(graph)!;
        return (int)lastUse.GetType().GetProperty("Count")!.GetValue(lastUse)!;
    }

    private static int BindSetOwnerCount(RenderGraph graph)
    {
        object cache = typeof(RenderGraph)
            .GetField("_bindingSetOwner", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)?
            .GetValue(graph)
            ?? typeof(RenderGraph)
                .GetProperty("_bindingSetOwner", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
                .GetValue(graph)!;
        int count = 0;
        foreach (object pair in (System.Collections.IEnumerable)cache)
        {
            object bucket = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            count += (int)bucket.GetType().GetProperty("Count")!.GetValue(bucket)!;
        }

        return count;
    }

    private static int BindingIndexCount(RenderGraph graph)
    {
        object refs = typeof(RenderGraph)
            .GetField("_bindingIndex", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)?
            .GetValue(graph)
            ?? typeof(RenderGraph)
                .GetProperty("_bindingIndex", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
                .GetValue(graph)!;
        object sets = refs.GetType()
            .GetField("_sets", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
            .GetValue(refs)!;
        return (int)sets.GetType().GetProperty("Count")!.GetValue(sets)!;
    }

    private static int CacheCount(RenderGraph graph)
        => CacheCount(graph, "_graphCache");

    private static int AliasCacheCount(RenderGraph graph)
        => CacheCount(graph, "_aliasCache");

    private static int CacheCount(RenderGraph graph, string fieldName)
    {
        object owner;
        if (fieldName == "_aliasCache")
        {
            owner = graph;
        }
        else
        {
            owner = typeof(RenderGraph)
                .GetField("_compiler", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
                .GetValue(graph)!;
            fieldName = fieldName switch
            {
                "_graphCache" => "GraphCache",
                _ => fieldName,
            };
        }

        object cache = owner.GetType()
            .GetField(fieldName, ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Public)!
            .GetValue(owner)!;
        object entries = cache.GetType()
            .GetField("_entries", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
            .GetValue(cache)!;
        return ((System.Collections.ICollection)entries).Count;
    }

    private static bool IsBufferResolved(RenderGraph graph, RenderGraphHandle handle)
    {
        var resources = (System.Collections.IList)typeof(RenderGraph)
            .GetField("_resources", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
            .GetValue(graph)!;
        object resource = resources[RenderGraphTestHelpers.ResourceIndex(graph, handle)]!;
        var buffer = (BufferHandle)resource.GetType()
            .GetField("Buffer", ReflectionBindingFlags.Instance | ReflectionBindingFlags.Public)!
            .GetValue(resource)!;
        return buffer.IsValid;
    }

    private static void SetRenderGraphFrameFenceCompletedValue(RenderGraph graph, IDevice device, ulong completedValue)
    {
        object executor = typeof(RenderGraph)
            .GetField("_executor", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!
            .GetValue(graph)!;
        var fence = (FenceHandle)executor.GetType()
            .GetField("FrameFence", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Public)!
            .GetValue(executor)!;
        object store = device.GetType()
            .GetField("Fences", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic | ReflectionBindingFlags.Public)!
            .GetValue(device)!;
        var getMethod = store.GetType().GetMethod("Get")!;
        object record = getMethod.Invoke(store, [fence, "Fence"])!;
        record.GetType().GetProperty("CompletedValue")!.SetValue(record, completedValue);
    }
}

