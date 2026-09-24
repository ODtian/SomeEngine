using System.Diagnostics;
using System.Runtime.InteropServices;
using SomeEngine.Rhi;
using SomeEngine.Rhi.D3D12;
using SomeEngine.Rhi.Utilities;
using D3D12InputClassification = Vortice.Direct3D12.InputClassification;
using D3D12InputElementDescription = Vortice.Direct3D12.InputElementDescription;
using ReflectionBindingFlags = System.Reflection.BindingFlags;

namespace SomeEngine.Rhi.Tests;

[Collection(D3D12TestCollection.Name)]
public sealed class NullRhiContractTests
{
    [Fact]
    public void Instance_CanAggregateD3D12FactoryWithoutChangingNullCreation()
    {
        using var instance = SomeEngine.Rhi.Instance.Create(D3D12Backend.Factory);
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        Assert.Equal(Backend.Null, device.AdapterInfo.Backend);
        Assert.Contains(instance.EnumerateAdapters(), adapter => adapter.Backend == Backend.Null);
    }

    [Fact]
    public void WaitFence_DoesNotAdvanceUnsignaledFence()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var fence = device.CreateFence("unsignaled", 3);
        var wait = Assert.Throws<RhiException>(() => device.WaitFence(fence, 4));
        Assert.Equal(ErrorCode.ValidationFailure, wait.Code);
        Assert.Equal(3ul, device.GetFenceValue(fence));
    }

    [Fact]
    public void MapBuffer_RejectsUndefinedMapModeBeforeMemoryKindChecks()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var buffer = device.CreateBuffer(new BufferDesc { SizeInBytes = 16, Memory = MemoryClass.DeviceLocal });

        var invalid = Assert.Throws<RhiException>(() => device.MapBuffer(buffer, (MapMode)999));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalid.Code);
    }

    [Fact]
    public void QueryPool_RejectsUndefinedQueryType()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var invalid = Assert.Throws<RhiException>(() => device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = (QueryType)999, Count = 1 }));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalid.Code);
    }

    [Fact]
    public void ResourceStateCompatibility_TreatsIndirectAsShaderResource()
    {
        Assert.True(ResourceStateCompatibility.Satisfies(ResourceState.IndirectArgument, ResourceState.ShaderResource));
        Assert.False(ResourceStateCompatibility.Satisfies(ResourceState.ShaderResource, ResourceState.IndirectArgument));
        Assert.True(ResourceStateCompatibility.Satisfies(ResourceState.GenericRead, ResourceState.CopySource));
        Assert.True(ResourceStateCompatibility.Satisfies(ResourceState.GenericRead, ResourceState.ShaderResource));
        Assert.False(ResourceStateCompatibility.Satisfies(ResourceState.CopySource, ResourceState.ShaderResource));
    }

    [Fact]
    public void SubresourceRange_CoversFullTexture()
    {
        var texture = new TextureDesc
        {
            Width = 8,
            Height = 8,
            MipLevels = 4,
            ArraySize = 2,
        };

        Assert.True(new SubresourceRange(0, 4, 0, 2).CoversAll(texture));
        Assert.True(SubresourceRange.All.CoversAll(texture));
        Assert.False(new SubresourceRange(0, 3, 0, 2).CoversAll(texture));
        Assert.False(new SubresourceRange(0, 4, 1, 1).CoversAll(texture));
    }

    [Fact]
    public void BindingLayout_AllowsSameBindingNumberAcrossRegisterClasses()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc { Binding = 0, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 0, Type = BindingType.RawBufferRead, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 0, Type = BindingType.RawBufferReadWrite, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 0, Type = BindingType.Sampler, Stages = ShaderStageFlags.Compute },
                ],
            });

        Assert.NotEqual(BindingLayoutHandle.Invalid, layout);
    }

    [Fact]
    public void NullCompressedFormats_DoNotAdvertiseUnsupportedCopy()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var support = device.GetFormatSupport(Format.Bc7RgbaUnorm);

        Assert.True(support.HasFlag(FormatSupport.ShaderSample));
        Assert.False(support.HasFlag(FormatSupport.CopySource));
        Assert.False(support.HasFlag(FormatSupport.CopyDestination));
    }

    [Fact]
    public void NullCommandList_RejectsUndefinedQueueType()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var invalid = Assert.Throws<RhiException>(
            () => device.CreateCommandList(new CommandListDesc { QueueType = (QueueType)999 }));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalid.Code);
    }

    [Fact]
    public void NullQueryPool_RecordsAndResolvesOcclusionAndPipelineStatistics()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var occlusion = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.Occlusion, Count = 1 });
        var statistics = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.PipelineStatistics, Count = 1 });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 8 + 88,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.BeginQuery(occlusion, 0);
        list.EndQuery(occlusion, 0);
        list.BeginQuery(statistics, 0);
        list.EndQuery(statistics, 0);
        list.ResolveQueryData(occlusion, 0, 1, readback, 0);
        list.ResolveQueryData(statistics, 0, 1, readback, 8);
        var commandBuffer = list.Finish();

        device.GetQueue(QueueType.Graphics).Submit([commandBuffer]);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 8 + 88);
        Assert.NotEqual(0ul, MemoryMarshal.Read<ulong>(mapped.Span[..8]));
        Assert.NotEqual(0ul, MemoryMarshal.Read<ulong>(mapped.Span.Slice(8, 8)));
        device.UnmapBuffer(readback);
    }

    [Fact]
    public void NullQueryPool_RejectsResolveBeforeNonTimestampQueryEnd()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var occlusion = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.Occlusion, Count = 1 });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 8,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.BeginQuery(occlusion, 0);
        list.ResolveQueryData(occlusion, 0, 1, readback, 0);

        var failure = Assert.Throws<RhiException>(() => list.Finish());
        Assert.Equal(ErrorCode.ValidationFailure, failure.Code);
    }

    [Fact]
    public void NullDispatchIndirect_ValidatesSingleAndMultiDispatch()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var shader = CreateShader(device, "dispatch indirect", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = layout, ComputeShader = shader });
        var arguments = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = IndirectArgumentSize.Dispatch * 2,
                BindFlags = BindFlags.IndirectArgument,
                InitialState = ResourceState.IndirectArgument,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);

        pass.DispatchIndirect(new IndirectDispatchDesc { Arguments = arguments, ArgumentOffset = 0 });
        pass.DispatchIndirect(new IndirectDispatchDesc { Arguments = arguments, ArgumentOffset = 0, DispatchCount = 2 });
        pass.End();
        device.Destroy(list.Finish());
    }

    [Fact]
    public void NullGraphicsPipeline_AcceptsGeometryAndTessellationStages()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        Assert.True(device.Features.GeometryShader);
        Assert.True(device.Features.TessellationShader);

        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var renderTarget = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var renderTargetView = device.CreateTextureView(renderTarget, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var geometryPipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Layout = layout,
                VertexShader = CreateShader(device, "geometry vs", ShaderStage.Vertex),
                GeometryShader = CreateShader(device, "geometry gs", ShaderStage.Geometry),
                PixelShader = CreateShader(device, "geometry ps", ShaderStage.Pixel),
                ColorFormats = [Format.Rgba8Unorm],
            });
        var tessellationPipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Layout = layout,
                VertexShader = CreateShader(device, "tessellation vs", ShaderStage.Vertex),
                HullShader = CreateShader(device, "tessellation hs", ShaderStage.Hull),
                DomainShader = CreateShader(device, "tessellation ds", ShaderStage.Domain),
                PixelShader = CreateShader(device, "tessellation ps", ShaderStage.Pixel),
                Topology = PrimitiveTopology.PatchList,
                PatchControlPoints = 3,
                ColorFormats = [Format.Rgba8Unorm],
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 4, 4),
                ColorAttachments = [new ColorAttachmentDesc { View = renderTargetView, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store }],
            });
        pass.SetPipeline(geometryPipeline);
        pass.Draw(3);
        pass.SetPipeline(tessellationPipeline);
        pass.Draw(3);
        pass.End();
        device.Destroy(list.Finish());
    }

    [Fact]
    public void NullActiveList_BlocksDirectBufferDestroyBeforeFinish()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var source = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var destination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });

        list.CopyBuffer(source, 0, destination, 0, 16);

        var activeSource = Assert.Throws<RhiException>(() => device.Destroy(source));
        var activeDestination = Assert.Throws<RhiException>(() => device.Destroy(destination));

        Assert.Equal(ErrorCode.ValidationFailure, activeSource.Code);
        Assert.Equal(ErrorCode.ValidationFailure, activeDestination.Code);

        var commandBuffer = list.Finish();
        device.GetQueue(QueueType.Copy).Submit([commandBuffer]);
        device.Destroy(commandBuffer);
        device.Destroy(source);
        device.Destroy(destination);
    }

    [Fact]
    public void FinishFailureWithOpenPass_ReleasesActiveListReferences()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var view = device.CreateTextureView(texture, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 4, 4),
                ColorAttachments = [new ColorAttachmentDesc { View = view, LoadOp = LoadOp.Load, StoreOp = StoreOp.Store }],
            });

        var finish = Assert.Throws<RhiException>(() => list.Finish());

        Assert.Equal(ErrorCode.ValidationFailure, finish.Code);
        var stalePass = Assert.Throws<RhiException>(() => pass.End());
        Assert.Equal(ErrorCode.ValidationFailure, stalePass.Code);
        device.Destroy(view);
        device.Destroy(texture);
        var secondFinish = Assert.Throws<RhiException>(() => list.Finish());
        Assert.Equal(ErrorCode.ValidationFailure, secondFinish.Code);
    }

    [Fact]
    public void MutableBindingSet_ClearRemovesPartiallyBoundDescriptorReference()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var firstBuffer = device.CreateBuffer(new BufferDesc { SizeInBytes = 256, BindFlags = BindFlags.ConstantBuffer, InitialState = ResourceState.ConstantBuffer });
        var secondBuffer = device.CreateBuffer(new BufferDesc { SizeInBytes = 256, BindFlags = BindFlags.ConstantBuffer, InitialState = ResourceState.ConstantBuffer });
        var firstView = device.CreateBufferView(firstBuffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var secondView = device.CreateBufferView(secondBuffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                        Count = 2,
                        Flags = BindingFlags.PartiallyBound,
                    },
                ],
            });
        var strictLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 0, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute }],
            });
        var partiallyBound = device.CreateBindingSet(
            layout,
            new BindingSetDesc
            {
                Flags = BindingSetFlags.Mutable,
                Resources =
                [
                    new BindingResourceDesc { Binding = 0, ArrayElement = 0, ResourceType = BindingType.ConstantBuffer, BufferView = firstView },
                    new BindingResourceDesc { Binding = 0, ArrayElement = 1, ResourceType = BindingType.ConstantBuffer, BufferView = secondView },
                ],
            });
        var strictSet = device.CreateBindingSet(
            strictLayout,
            new BindingSetDesc
            {
                Flags = BindingSetFlags.Mutable,
                Resources = [new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = firstView }],
            });

        device.UpdateBindingSet(partiallyBound, [BindingResourceDesc.Clear(0, 1)]);
        var strictClear = Assert.Throws<RhiException>(() => device.UpdateBindingSet(strictSet, [BindingResourceDesc.Clear(0)]));

        Assert.Equal(ErrorCode.InvalidDescriptor, strictClear.Code);
        device.Destroy(secondView);
        device.Destroy(secondBuffer);
    }

    [Fact]
    public void PartiallyBoundTextureSlots_RequireExplicitNullDescriptorShape()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var missingShape = Assert.Throws<RhiException>(
            () => device.CreateBindingLayout(
                new BindingLayoutDesc
                {
                    Slots =
                    [
                        new BindingSlotDesc
                        {
                            Binding = 0,
                            Type = BindingType.TextureRead,
                            Stages = ShaderStageFlags.Compute,
                            Count = 2,
                            Flags = BindingFlags.PartiallyBound,
                        },
                    ],
                }));

        Assert.Equal(ErrorCode.InvalidDescriptor, missingShape.Code);

        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.TextureRead,
                        Stages = ShaderStageFlags.Compute,
                        Count = 2,
                        Flags = BindingFlags.PartiallyBound,
                        Shape = new BindShapeDesc { TextureDimension = TextureViewDimension.Texture2D, Format = Format.Rgba8Unorm },
                    },
                ],
            });
        var set = device.CreateBindingSet(layout, new BindingSetDesc());

        Assert.True(set.IsValid);
    }

    [Fact]
    public void BindingResourceClear_IsRejectedOutsideMutableUpdate()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.PartiallyBound,
                    },
                ],
            });

        var createClear = Assert.Throws<RhiException>(
            () => device.CreateBindingSet(
                layout,
                new BindingSetDesc { Resources = [BindingResourceDesc.Clear(0)] }));
        Assert.Equal(ErrorCode.InvalidDescriptor, createClear.Code);

        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [layout] });
        var shader = CreateShader(device, "clear transient rejection", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);

        var transientClear = Assert.Throws<RhiException>(() => pass.SetBindings(0, layout, [BindingResourceDesc.Clear(0)]));

        Assert.Equal(ErrorCode.InvalidDescriptor, transientClear.Code);
        pass.End();
        device.Destroy(list.Finish());
    }

    [Fact]
    public void PartiallyBoundSamplerShape_ParticipatesInLayoutCompatibility()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var regular = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.Sampler,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.PartiallyBound,
                        Shape = new BindShapeDesc { Sampler = new SamplerDesc() },
                    },
                ],
            });
        var comparison = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.Sampler,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.PartiallyBound,
                        Shape = new BindShapeDesc { Sampler = new SamplerDesc { Compare = CompareOp.LessOrEqual } },
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [comparison] });
        var shader = CreateShader(device, "comparison sampler shape", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);

        var incompatible = Assert.Throws<RhiException>(() => pass.SetBindings(0, regular, []));
        var transientPartiallyBound = Assert.Throws<RhiException>(() => pass.SetBindings(0, comparison, []));
        var bindingSet = device.CreateBindingSet(comparison, new BindingSetDesc());

        Assert.Equal(ErrorCode.InvalidDescriptor, incompatible.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, transientPartiallyBound.Code);
        pass.SetBindingSet(0, bindingSet);
        pass.End();
        device.Destroy(list.Finish());
    }

    [Fact]
    public void BindlessLayouts_AreRejectedByTransientBindings()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.Sampler,
                        Stages = ShaderStageFlags.Compute,
                        Count = 4,
                        Flags = BindingFlags.Bindless,
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [layout] });
        var shader = CreateShader(device, "bindless transient rejection", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);

        var rejected = Assert.Throws<RhiException>(() => pass.SetBindings(0, layout, []));

        Assert.Equal(ErrorCode.InvalidDescriptor, rejected.Code);
        pass.End();
        device.Destroy(list.Finish());
    }

    [Fact]
    public void DynamicOffsetBuffers_AreValidatedBySetBindingSetAndTransientBindings()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var constants = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 512,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            });
        var constantsView = device.CreateBufferView(constants, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 512 });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.DynamicOffset,
                    },
                ],
            });
        var resources = new[]
        {
            new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = constantsView },
        };
        var bindingSet = device.CreateBindingSet(layout, new BindingSetDesc { Resources = resources });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [layout] });
        var shader = CreateShader(device, "dynamic offset", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet, [new DynamicOffset(0, 0, 256)]);
        pass.SetBindings(0, layout, resources, [new DynamicOffset(0, 0, 256)]);

        var missing = Assert.Throws<RhiException>(() => pass.SetBindingSet(0, bindingSet));
        var unaligned = Assert.Throws<RhiException>(() => pass.SetBindingSet(0, bindingSet, [new DynamicOffset(0, 0, 128)]));
        var outOfRange = Assert.Throws<RhiException>(() => pass.SetBindingSet(0, bindingSet, [new DynamicOffset(0, 0, 512)]));
        var duplicate = Assert.Throws<RhiException>(
            () => pass.SetBindings(
                0,
                layout,
                resources,
                [new DynamicOffset(0, 0, 256), new DynamicOffset(0, 0, 256)]));
        var undeclared = Assert.Throws<RhiException>(() => pass.SetBindings(0, layout, resources, [new DynamicOffset(1, 0, 256)]));
        pass.End();
        device.Destroy(list.Finish());

        Assert.Equal(ErrorCode.InvalidDescriptor, missing.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, unaligned.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, outOfRange.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, duplicate.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, undeclared.Code);
    }

    [Fact]
    public void DynamicOffset_AcceptsRawBufferSlotsAsFormalContract()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var rawBuffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 512,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                Raw = true,
            });
        var rawView = device.CreateBufferView(
            rawBuffer,
            new BufferViewDesc
            {
                Kind = ViewKind.ShaderResource,
                SizeInBytes = 512,
                Raw = true,
            });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.RawBufferRead,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.DynamicOffset,
                    },
                ],
            });
        var resources = new[]
        {
            new BindingResourceDesc { Binding = 0, ResourceType = BindingType.RawBufferRead, BufferView = rawView },
        };
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [layout] });
        var shader = CreateShader(device, "raw dynamic offset", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);

        pass.SetBindings(0, layout, resources, [new DynamicOffset(0, 0, 16)]);

        pass.End();
        device.Destroy(list.Finish());
    }

    [Fact]
    public void DynamicOffset_IsRejectedForNonDynamicSlots()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var constants = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 512,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            });
        var constantsView = device.CreateBufferView(constants, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 512 });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var resources = new[]
        {
            new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = constantsView },
        };
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [layout] });
        var shader = CreateShader(device, "non dynamic offset", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);

        var rejected = Assert.Throws<RhiException>(() => pass.SetBindings(0, layout, resources, [new DynamicOffset(0, 0, 256)]));

        pass.End();
        device.Destroy(list.Finish());
        Assert.Equal(ErrorCode.InvalidDescriptor, rejected.Code);
    }

    [Fact]
    public void PipelineCache_RoundTripsInitialDataAndCanBeReferencedByPipeline()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        byte[] initialData = [1, 2, 3, 4];
        var cache = device.Get<ICacheDevice>()!.CreatePipelineCache(new PipelineCacheDesc { Name = "cache", InitialData = initialData });
        initialData[0] = 9;
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var shader = CreateShader(device, "cached compute", ShaderStage.Compute);

        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = layout, ComputeShader = shader, PipelineCache = cache });

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, device.Get<ICacheDevice>()!.GetPipelineData(cache));
        device.Destroy(pipeline);
        device.Destroy(cache);
    }

    [Fact]
    public void MutableBindingSet_AllowsUpdateWhileImmutableOrLiveSetsFailFast()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var firstBuffer = device.CreateBuffer(new BufferDesc { SizeInBytes = 256, BindFlags = BindFlags.ConstantBuffer, InitialState = ResourceState.ConstantBuffer });
        var secondBuffer = device.CreateBuffer(new BufferDesc { SizeInBytes = 256, BindFlags = BindFlags.ConstantBuffer, InitialState = ResourceState.ConstantBuffer });
        var retainedBuffer = device.CreateBuffer(new BufferDesc { SizeInBytes = 256, BindFlags = BindFlags.ConstantBuffer, InitialState = ResourceState.ConstantBuffer });
        var firstView = device.CreateBufferView(firstBuffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var secondView = device.CreateBufferView(secondBuffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var retainedView = device.CreateBufferView(retainedBuffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc { Binding = 0, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute },
                ],
            });
        var initial = new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = firstView };
        var updated = new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = secondView };
        var retained = new BindingResourceDesc { Binding = 1, ResourceType = BindingType.ConstantBuffer, BufferView = retainedView };

        var immutableSet = device.CreateBindingSet(layout, new BindingSetDesc { Resources = [initial, retained] });
        var mutableSet = device.CreateBindingSet(layout, new BindingSetDesc { Flags = BindingSetFlags.Mutable, Resources = [initial, retained] });

        var immutable = Assert.Throws<RhiException>(() => device.UpdateBindingSet(immutableSet, [updated]));
        device.UpdateBindingSet(mutableSet, [updated]);

        Assert.Equal(ErrorCode.ValidationFailure, immutable.Code);

        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [layout] });
        var shader = CreateShader(device, "binding update live", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, mutableSet);
        var active = Assert.Throws<RhiException>(() => device.UpdateBindingSet(mutableSet, [initial]));
        pass.End();
        _ = list.Finish();

        var live = Assert.Throws<RhiException>(() => device.UpdateBindingSet(mutableSet, [initial]));

        Assert.Equal(ErrorCode.ValidationFailure, live.Code);
        Assert.Equal(ErrorCode.ValidationFailure, active.Code);
    }

    [Fact]
    public void AdvancedCapabilities_CreateOnNullBackend()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        Assert.True(device.Features.MeshShader);
        Assert.True(device.Features.RayTracing);

        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var renderTarget = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var renderTargetView = device.CreateTextureView(renderTarget, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var meshArguments = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = IndirectArgumentSize.Dispatch,
                BindFlags = BindFlags.IndirectArgument,
                InitialState = ResourceState.IndirectArgument,
            });
        var meshPipeline = device.CreateMeshPipeline(
            new MeshPipelineDesc
            {
                Layout = pipelineLayout,
                MeshShader = CreateShader(device, "null mesh", ShaderStage.Mesh),
                PixelShader = CreateShader(device, "null mesh ps", ShaderStage.Pixel),
                ColorFormats = [Format.Rgba8Unorm],
            });

        var raygen = CreateShader(device, "null raygen", ShaderStage.RayGeneration);
        var rayTracingPipeline = device.Get<IRtDevice>()!.CreateRtPipeline(
            new RtPipelineDesc
            {
                Layout = pipelineLayout,
                Shaders = [raygen],
                ShaderGroups =
                [
                    new RtGroupDesc
                    {
                        Name = "RayGen",
                        Kind = RtGroupKind.General,
                        GeneralShader = raygen,
                    },
                ],
            });

        var identifier = new byte[device.Get<IRtDevice>()!.GetRtSize(rayTracingPipeline)];
        device.Get<IRtDevice>()!.GetRtId(rayTracingPipeline, "RayGen", identifier);
        var shaderTable = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = device.Limits.RayTracingShaderTableAlignment,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            });
        var vertices = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 3 * 3 * sizeof(float),
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 3 * sizeof(float),
            });
        var sizes = device.Get<IRtDevice>()!.GetAccelSizes(
            new AccelBuildDesc
            {
                Kind = AccelerationStructureKind.BottomLevel,
                Geometries =
                [
                    new AccelGeomDesc
                    {
                        Kind = AccelGeomKind.Triangles,
                        VertexFormat = Format.Rgb32Float,
                        VertexStrideInBytes = 12,
                        VertexCount = 3,
                        VertexBuffer = vertices,
                    },
                ],
            });
        var accelerationStructure = device.Get<IRtDevice>()!.CreateAccelerationStructure(new AccelerationStructureDesc { SizeInBytes = sizes.AccelerationStructureSizeInBytes });
        var accelerationStructureCopy = device.Get<IRtDevice>()!.CreateAccelerationStructure(new AccelerationStructureDesc { SizeInBytes = sizes.AccelerationStructureSizeInBytes });
        var unalignedAccelerationStructure = Assert.Throws<RhiException>(
            () => device.Get<IRtDevice>()!.CreateAccelerationStructure(new AccelerationStructureDesc { SizeInBytes = sizes.AccelerationStructureSizeInBytes - 1 }));
        var tooSmallAccelerationStructure = device.Get<IRtDevice>()!.CreateAccelerationStructure(
            new AccelerationStructureDesc
            {
                SizeInBytes = sizes.AccelerationStructureSizeInBytes - device.Limits.AccelerationStructureAlignment,
            });
        var scratch = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = sizes.BuildScratchSizeInBytes,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
            });
        var buildDesc = new AccelBuildDesc
        {
            Kind = AccelerationStructureKind.BottomLevel,
            Destination = accelerationStructure,
            ScratchBuffer = scratch,
            Geometries =
            [
                new AccelGeomDesc
                {
                    Kind = AccelGeomKind.Triangles,
                    VertexFormat = Format.Rgb32Float,
                    VertexStrideInBytes = 12,
                    VertexCount = 3,
                    VertexBuffer = vertices,
                },
            ],
        };

        var meshList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var meshPass = meshList.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 4, 4),
                ColorAttachments = [new ColorAttachmentDesc { View = renderTargetView, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store }],
            });
        meshPass.SetPipeline(meshPipeline);
        meshPass.DispatchMesh(1, 1, 1);
        meshPass.DispatchMeshIndirect(new IndirectDispatchDesc { Arguments = meshArguments });
        meshPass.End();
        device.Destroy(meshList.Finish());

        var rtList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var rtPass = rtList.BeginRtPass(new RtPassDesc());
        rtPass.SetPipeline(rayTracingPipeline);
        rtPass.TraceRays(
            new ShaderTableDesc
            {
                RayGeneration = new ShaderTableRegion(shaderTable, 0, device.Limits.RayTracingShaderRecordAlignment, 0),
            },
            1,
            1,
            1);
        rtPass.End();
        device.Destroy(rtList.Finish());

        var invalidRtList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var invalidRtPass = invalidRtList.BeginRtPass(new RtPassDesc());
        invalidRtPass.SetPipeline(rayTracingPipeline);
        var missingRaygen = Assert.Throws<RhiException>(() => invalidRtPass.TraceRays(new ShaderTableDesc(), 1, 1, 1));
        invalidRtPass.End();
        device.Destroy(invalidRtList.Finish());

        var misalignedAsList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var misalignedScratch = Assert.Throws<RhiException>(
            () => misalignedAsList.BuildAccelerationStructure(buildDesc with { ScratchOffset = 16 }));
        device.Destroy(misalignedAsList.Finish());

        var tooSmallAsList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var tooSmallDestination = Assert.Throws<RhiException>(
            () => tooSmallAsList.BuildAccelerationStructure(buildDesc with { Destination = tooSmallAccelerationStructure }));
        device.Destroy(tooSmallAsList.Finish());

        var asList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        asList.BuildAccelerationStructure(buildDesc);
        asList.CopyAccelerationStructure(accelerationStructure, accelerationStructureCopy, AccelCopyMode.Clone);
        device.Destroy(asList.Finish());

        Assert.NotEqual(PipelineHandle.Invalid, meshPipeline);
        Assert.NotEqual(PipelineHandle.Invalid, rayTracingPipeline);
        Assert.Contains(identifier, value => value != 0);
        Assert.True(sizes.AccelerationStructureSizeInBytes > 0);
        Assert.NotEqual(AccelerationStructureHandle.Invalid, accelerationStructure);
        Assert.Equal(ErrorCode.InvalidHandle, missingRaygen.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, unalignedAccelerationStructure.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, misalignedScratch.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, tooSmallDestination.Code);
    }

    [Fact]
    public void MipGenerator_RecordsCoreUtilityCommandsOnNullBackend()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        using var generator = new MipGenerator(
            device,
            new MipGeneratorDesc
            {
                Texture2DShader = new ShaderModuleDesc
                {
                    Name = "null mip generator",
                    Backend = Backend.Null,
                    Stage = ShaderStage.Compute,
                    EntryPoint = "main",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = new byte[] { 1 },
                },
                Texture2DArrayShader = new ShaderModuleDesc
                {
                    Name = "null mip generator array",
                    Backend = Backend.Null,
                    Stage = ShaderStage.Compute,
                    EntryPoint = "main",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = new byte[] { 2 },
                },
            });

        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "null mip chain",
                Width = 4,
                Height = 4,
                MipLevels = 3,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var arrayTexture = device.CreateTexture(
            new TextureDesc
            {
                Name = "null mip array chain",
                Width = 4,
                Height = 4,
                MipLevels = 2,
                ArraySize = 2,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        generator.GenerateMips(
            list,
            new GenerateMipsDesc
            {
                Texture = texture,
                FinalState = ResourceState.ShaderResource,
            });
        generator.GenerateMips(
            list,
            new GenerateMipsDesc
            {
                Texture = arrayTexture,
                FinalState = ResourceState.ShaderResource,
            });
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("null mip generator");
        device.GetQueue(QueueType.Compute).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);
        device.Destroy(commandBuffer);
        device.Destroy(fence);

        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(texture, 0));
        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(texture, 1));
        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(texture, 2));
        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(arrayTexture, 0, 0));
        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(arrayTexture, 1, 0));
        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(arrayTexture, 0, 1));
        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(arrayTexture, 1, 1));
    }

    [Fact]
    public void MipGenerator_FailFastValidationRunsBeforeNoOpReturn()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        using var generator = new MipGenerator(
            device,
            new MipGeneratorDesc
            {
                Texture2DShader = new ShaderModuleDesc
                {
                    Name = "null mip generator",
                    Backend = Backend.Null,
                    Stage = ShaderStage.Compute,
                    EntryPoint = "main",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = new byte[] { 1 },
                },
            });
        var singleMipTexture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                MipLevels = 1,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
            });
        var arrayTexture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                MipLevels = 1,
                ArraySize = 2,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
            });
        var texture3D = device.CreateTexture(
            new TextureDesc
            {
                Dimension = ResourceDimension.Texture3D,
                Width = 4,
                Height = 4,
                Depth = 4,
                MipLevels = 1,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
            });
        var cubeTexture = device.CreateTexture(
            new TextureDesc
            {
                Dimension = ResourceDimension.TextureCube,
                Width = 4,
                Height = 4,
                MipLevels = 1,
                ArraySize = 6,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });

        var invalidSlice = Assert.Throws<RhiException>(
            () => generator.GenerateMips(
                list,
                new GenerateMipsDesc
                {
                    Texture = singleMipTexture,
                    FirstSlice = 1,
                }));
        var missingArrayShader = Assert.Throws<RhiException>(
            () => generator.GenerateMips(
                list,
                new GenerateMipsDesc
                {
                    Texture = arrayTexture,
                }));
        var invalid3DSlice = Assert.Throws<RhiException>(
            () => generator.GenerateMips(
                list,
                new GenerateMipsDesc
                {
                    Texture = texture3D,
                    FirstSlice = 1,
                }));
        var unsupported3D = Assert.Throws<RhiException>(
            () => generator.GenerateMips(
                list,
                new GenerateMipsDesc
                {
                    Texture = texture3D,
                }));
        var unsupportedCube = Assert.Throws<RhiException>(
            () => generator.GenerateMips(
                list,
                new GenerateMipsDesc
                {
                    Texture = cubeTexture,
                }));

        device.Destroy(list.Finish());

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidSlice.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, missingArrayShader.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalid3DSlice.Code);
        Assert.Equal(ErrorCode.UnsupportedFeature, unsupported3D.Code);
        Assert.Equal(ErrorCode.UnsupportedFeature, unsupportedCube.Code);
    }

    [Fact]
    public void NullIndependentBlend_HonorsFeatureFlag()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());

        var unsupported = Assert.Throws<RhiException>(
            () => device.CreateGraphicsPipeline(
                new GraphicsPipelineDesc
                {
                    Layout = layout,
                    VertexShader = CreateShader(device, "vs independent blend", ShaderStage.Vertex),
                    PixelShader = CreateShader(device, "ps independent blend", ShaderStage.Pixel),
                    ColorFormats = [Format.Rgba8Unorm, Format.Rgba8Unorm],
                    Blend = new BlendDesc { Targets = [new BlendTargetDesc(), new BlendTargetDesc()] },
                }));

        Assert.Equal(ErrorCode.UnsupportedFeature, unsupported.Code);
    }

    [Fact]
    public void SharedDescriptorValidation_RejectsInvalidEnumAndStateCombinations()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var invalidBufferState = Assert.Throws<RhiException>(
            () => device.CreateBuffer(
                new BufferDesc
                {
                    SizeInBytes = 16,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.RenderTarget,
                }));
        var invalidTextureState = Assert.Throws<RhiException>(
            () => device.CreateTexture(
                new TextureDesc
                {
                    Width = 4,
                    Height = 4,
                    Format = Format.Rgba8Unorm,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.VertexBuffer,
                }));
        var invalidSampler = Assert.Throws<RhiException>(
            () => device.CreateSampler(new SamplerDesc { MinFilter = (FilterMode)999 }));
        var invalidShader = Assert.Throws<RhiException>(
            () => device.CreateShaderModule(
                new ShaderModuleDesc
                {
                    Backend = Backend.Null,
                    Stage = (ShaderStage)999,
                    EntryPoint = "main",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = new byte[] { 1 },
                }));
        var invalidShaderBackend = Assert.Throws<RhiException>(
            () => device.CreateShaderModule(
                new ShaderModuleDesc
                {
                    Backend = (Backend)999,
                    Stage = ShaderStage.Compute,
                    EntryPoint = "main",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = new byte[] { 1 },
            }));
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            });
        var invalidTextureViewKind = Assert.Throws<RhiException>(
            () => device.CreateTextureView(
                texture,
                new TextureViewDesc
                {
                    Kind = (ViewKind)999,
                    Format = Format.Rgba8Unorm,
                }));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidBufferState.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidTextureState.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidSampler.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidShader.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidShaderBackend.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidTextureViewKind.Code);
    }

    [Fact]
    public void NullDestroy_BlocksLiveDescriptorGraph()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 256,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            });
        var bufferView = device.CreateBufferView(buffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var sampler = device.CreateSampler(new SamplerDesc());
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc { Binding = 0, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.Sampler, Stages = ShaderStageFlags.Compute },
                ],
            });
        var bindingSet = device.CreateBindingSet(
            layout,
            new BindingSetDesc
            {
                Resources =
                [
                    new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = bufferView },
                    new BindingResourceDesc { Binding = 1, ResourceType = BindingType.Sampler, SamplerHandle = sampler },
                ],
            });

        var liveBufferView = Assert.Throws<RhiException>(() => device.Destroy(bufferView));
        var liveBuffer = Assert.Throws<RhiException>(() => device.Destroy(buffer));
        var liveSampler = Assert.Throws<RhiException>(() => device.Destroy(sampler));

        Assert.Equal(ErrorCode.ValidationFailure, liveBufferView.Code);
        Assert.Equal(ErrorCode.ValidationFailure, liveBuffer.Code);
        Assert.Equal(ErrorCode.ValidationFailure, liveSampler.Code);

        device.Destroy(bindingSet);
        device.Destroy(bufferView);
        device.Destroy(buffer);
        device.Destroy(sampler);
    }

    [Fact]
    public void NullBarrier_RejectsResourceKindInvalidStates()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });

        var invalidBufferState = Assert.Throws<RhiException>(
            () => list.Barrier([], [new BufferBarrier(buffer, ResourceState.CopySource, ResourceState.RenderTarget)]));
        var invalidTextureState = Assert.Throws<RhiException>(
            () => list.Barrier([new TextureBarrier(texture, ResourceState.CopySource, ResourceState.VertexBuffer, SubresourceRange.All)], []));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidBufferState.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidTextureState.Code);
    }

    [Fact]
    public void NullBindingValidation_RejectsRawAndStructuredBufferMismatches()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 4,
            });
        var structuredView = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.ShaderResource,
                SizeInBytes = 16,
                StrideInBytes = 4,
            });
        var rawView = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.ShaderResource,
                SizeInBytes = 16,
                Raw = true,
            });
        var rawLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 0, Type = BindingType.RawBufferRead, Stages = ShaderStageFlags.Compute }],
            });
        var structuredLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 0, Type = BindingType.StorageBufferRead, Stages = ShaderStageFlags.Compute }],
            });

        var rawMismatch = Assert.Throws<RhiException>(
            () => device.CreateBindingSet(
                rawLayout,
                new BindingSetDesc { Resources = [new BindingResourceDesc { Binding = 0, ResourceType = BindingType.RawBufferRead, BufferView = structuredView }] }));
        var structuredMismatch = Assert.Throws<RhiException>(
            () => device.CreateBindingSet(
                structuredLayout,
                new BindingSetDesc { Resources = [new BindingResourceDesc { Binding = 0, ResourceType = BindingType.StorageBufferRead, BufferView = rawView }] }));

        Assert.Equal(ErrorCode.InvalidDescriptor, rawMismatch.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, structuredMismatch.Code);
    }

    [Fact]
    public void BindingLayout_AcceptsAccelerationStructureWhenRayTracingSupported()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 0, Type = BindingType.AccelerationStructure, Stages = ShaderStageFlags.AllRayTracing }],
            });
        var accelerationStructure = device.Get<IRtDevice>()!.CreateAccelerationStructure(new AccelerationStructureDesc { SizeInBytes = 256 });
        var set = device.CreateBindingSet(
            layout,
            new BindingSetDesc { Resources = [BindingResourceDesc.AccelerationStructureBinding(0, accelerationStructure)] });

        Assert.NotEqual(BindingLayoutHandle.Invalid, layout);
        Assert.NotEqual(BindingSetHandle.Invalid, set);
    }

    [Fact]
    public void NullGraphicsPipeline_RejectsInvalidEnumDescriptors()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var vertex = CreateShader(device, "invalid enum vs", ShaderStage.Vertex);
        var pixel = CreateShader(device, "invalid enum ps", ShaderStage.Pixel);

        var invalidTopology = Assert.Throws<RhiException>(
            () => device.CreateGraphicsPipeline(
                new GraphicsPipelineDesc
                {
                    Layout = layout,
                    VertexShader = vertex,
                    PixelShader = pixel,
                    Topology = (PrimitiveTopology)999,
                    ColorFormats = [Format.Rgba8Unorm],
                }));
        var invalidBlend = Assert.Throws<RhiException>(
            () => device.CreateGraphicsPipeline(
                new GraphicsPipelineDesc
                {
                    Layout = layout,
                    VertexShader = vertex,
                    PixelShader = pixel,
                    ColorFormats = [Format.Rgba8Unorm],
                    Blend = new BlendDesc { Targets = [new BlendTargetDesc { SourceColor = (BlendFactor)999 }] },
                }));
        var invalidInputRate = Assert.Throws<RhiException>(
            () => device.CreateGraphicsPipeline(
                new GraphicsPipelineDesc
                {
                    Layout = layout,
                    VertexShader = vertex,
                    PixelShader = pixel,
                    VertexBuffers = [new VertexLayoutDesc { Slot = 0, StrideInBytes = 8, InputRate = (VertexInputRate)999 }],
                    VertexAttributes = [new VertexAttributeDesc { Location = 0, BufferSlot = 0, Format = Format.Rg32Float }],
                    ColorFormats = [Format.Rgba8Unorm],
                }));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidTopology.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidBlend.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidInputRate.Code);
    }

    [Fact]
    public void NullSwapchain_RejectsInvalidOrUnsupportedFormats()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var invalidFormat = Assert.Throws<RhiException>(
            () => device.CreateSwapchain(
                new SwapchainDesc
                {
                    Width = 16,
                    Height = 16,
                    Format = (Format)999,
                }));
        var compressedFormat = Assert.Throws<RhiException>(
            () => device.CreateSwapchain(
                new SwapchainDesc
                {
                    Width = 16,
                    Height = 16,
                    Format = Format.Bc1RgbaUnorm,
                }));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidFormat.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, compressedFormat.Code);
    }

    [Fact]
    public void PresentDesc_ValidatesSyncAndTearingPolicy()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var swapchainHandle = device.CreateSwapchain(
            new SwapchainDesc
            {
                Width = 16,
                Height = 16,
                AllowTearing = false,
            });
        var swapchain = device.GetSwapchain(swapchainHandle);

        var invalidTearing = Assert.Throws<RhiException>(
            () => swapchain.Present(new PresentDesc { SyncInterval = 0, AllowTearing = true }));
        swapchain.Present(new PresentDesc { SyncInterval = 1 });

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidTearing.Code);
        Assert.Equal(1u, swapchain.CurrentBackBufferIndex);
    }

    [D3D12Fact]
    public void D3D12Backend_CopiesUploadBufferToReadbackWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        Assert.True(device.Features.PlacedResources);
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "texture views",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                InitialState = ResourceState.ShaderResource,
            });
        var srv = device.CreateTextureView(texture, new TextureViewDesc { Kind = ViewKind.ShaderResource, Format = Format.Rgba8Unorm });
        var uav = device.CreateTextureView(texture, new TextureViewDesc { Kind = ViewKind.UnorderedAccess, Format = Format.Rgba8Unorm });
        device.Destroy(srv);
        device.Destroy(uav);
        device.Destroy(texture);

        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "upload",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.CopyBuffer(upload, 0, readback, 0, 4);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("copy");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
        Assert.Equal([1, 2, 3, 4], mapped.ToArray());
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_RejectsUnsignaledFenceWaitWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var fence = device.CreateFence("unsignaled", 2);

        var unsignaled = Assert.Throws<RhiException>(() => device.WaitFence(fence, 3));

        Assert.Equal(ErrorCode.ValidationFailure, unsignaled.Code);
        Assert.Equal(2ul, device.GetFenceValue(fence));
    }

    [D3D12Fact]
    public void D3D12Backend_SubmitValidationFailureDoesNotConsumeCommandBufferWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "state validation",
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopySource,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier([], [new BufferBarrier(buffer, ResourceState.CopySource, ResourceState.CopyDestination)]);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("timeline", 5);
        var queue = device.GetQueue(QueueType.Graphics);

        var invalidSignal = Assert.Throws<RhiException>(() => queue.Submit([commandBuffer], signals: [new QueueSignal(fence, 5)]));

        Assert.Equal(ErrorCode.ValidationFailure, invalidSignal.Code);
        Assert.Equal(ResourceState.CopySource, device.GetBufferState(buffer));
        queue.Submit([commandBuffer], signals: [new QueueSignal(fence, 6)]);
        device.WaitFence(fence, 6);
        Assert.Equal(ResourceState.CopyDestination, device.GetBufferState(buffer));
    }

    [D3D12Fact]
    public void D3D12Backend_SubmitValidatesFirstUseStateRelativeToEarlierCommandBuffersWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "submit relative state",
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopySource,
            });

        var producer = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        producer.Barrier([], [new BufferBarrier(buffer, ResourceState.CopySource, ResourceState.CopyDestination)]);
        var producerCommand = producer.Finish();

        var consumer = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        consumer.Barrier([], [new BufferBarrier(buffer, ResourceState.CopyDestination, ResourceState.CopySource)]);
        var consumerCommand = consumer.Finish();

        var consumerAlone = Assert.Throws<RhiException>(() => device.GetQueue(QueueType.Graphics).Submit([consumerCommand]));
        Assert.Equal(ErrorCode.ValidationFailure, consumerAlone.Code);

        var fence = device.CreateFence("submit relative");
        device.GetQueue(QueueType.Graphics).Submit([producerCommand, consumerCommand], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        Assert.Equal(ResourceState.CopySource, device.GetBufferState(buffer));
    }

    [D3D12Fact]
    public void D3D12Backend_QueryResolveUsesCommandBufferLocalStateWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var queryPool = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Name = "timestamp", Type = QueryType.Timestamp, Count = 1 });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "query resolve",
                SizeInBytes = sizeof(ulong),
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier([], [new BufferBarrier(readback, ResourceState.CopyDestination, ResourceState.QueryResolve)]);
        list.WriteTimestamp(queryPool, 0);
        list.ResolveQueryData(queryPool, 0, 1, readback, 0);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("query");

        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        Assert.Equal(ResourceState.QueryResolve, device.GetBufferState(readback));
        var mapped = device.MapBuffer(readback, MapMode.Read, 0, sizeof(ulong));
        Assert.Equal(sizeof(ulong), mapped.Length);
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_QueryResolveRejectsUnwrittenTimestampWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var queryPool = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Name = "unwritten", Type = QueryType.Timestamp, Count = 1 });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "unwritten query resolve",
                SizeInBytes = sizeof(ulong),
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.ResolveQueryData(queryPool, 0, 1, readback, 0);
        var commandBuffer = list.Finish();

        var unwritten = Assert.Throws<RhiException>(() => device.GetQueue(QueueType.Graphics).Submit([commandBuffer]));

        Assert.Equal(ErrorCode.ValidationFailure, unwritten.Code);
        device.Destroy(commandBuffer);
    }

    [D3D12Fact]
    public void D3D12Backend_QueryResolveRequiresCopyDestinationBindFlagWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var queryPool = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Name = "timestamp bind flags", Type = QueryType.Timestamp, Count = 1 });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "query resolve without copy bind",
                SizeInBytes = sizeof(ulong),
                Memory = MemoryClass.CpuReadback,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.WriteTimestamp(queryPool, 0);

        var missingBindFlag = Assert.Throws<RhiException>(() => list.ResolveQueryData(queryPool, 0, 1, readback, 0));

        Assert.Equal(ErrorCode.InvalidDescriptor, missingBindFlag.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_BindingSetValidationRejectsDuplicateAndUndeclaredResourcesWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "binding validation",
                SizeInBytes = 256,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            });
        var view = device.CreateBufferView(buffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });

        var duplicate = Assert.Throws<RhiException>(
            () => device.CreateBindingSet(
                layout,
                new BindingSetDesc
                {
                    Resources =
                    [
                        new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = view },
                        new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = view },
                    ],
                }));
        Assert.Equal(ErrorCode.InvalidDescriptor, duplicate.Code);

        var undeclared = Assert.Throws<RhiException>(
            () => device.CreateBindingSet(
                layout,
                new BindingSetDesc
                {
                    Resources =
                    [
                        new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = view },
                        new BindingResourceDesc { Binding = 1, ResourceType = BindingType.ConstantBuffer, BufferView = view },
                    ],
                }));
        Assert.Equal(ErrorCode.InvalidDescriptor, undeclared.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_PlacedAliasingRequiresBarrierWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var desc = new BufferDesc
        {
            Name = "d3d12 alias",
            SizeInBytes = 64,
            BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopySource,
        };
        var requirements = device.Get<IMemoryDevice>()!.GetBufferReqs(desc);
        var heap = device.Get<IMemoryDevice>()!.CreateMemoryHeap(
            new MemoryHeapDesc
            {
                Name = "d3d12 alias heap",
                SizeInBytes = requirements.SizeInBytes,
                Kind = requirements.HeapKind,
                Memory = MemoryClass.DeviceLocal,
                Flags = MemoryHeapFlags.AllowAliasing,
            });
        var first = device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 0, desc);
        var second = device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 0, desc with { Name = "d3d12 alias 2" });

        var missingBarrierList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        missingBarrierList.Barrier([], [new BufferBarrier(second, ResourceState.CopySource, ResourceState.CopySource)]);
        var missingBarrier = Assert.Throws<RhiException>(() => device.GetQueue(QueueType.Graphics).Submit([missingBarrierList.Finish()]));
        Assert.Equal(ErrorCode.ValidationFailure, missingBarrier.Code);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier(
            [],
            [new BufferBarrier(second, ResourceState.CopySource, ResourceState.CopyDestination)],
            [AliasingBarrier.Between(first, second)]);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("d3d12 alias");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        device.Destroy(commandBuffer);
    }

    [D3D12Fact]
    public void D3D12Backend_PlacedBufferInitialDataRejectsInactiveAliasedAllocationWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var desc = new BufferDesc
        {
            Name = "d3d12 inactive alias initial data",
            SizeInBytes = 64,
            BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopySource,
        };
        var requirements = device.Get<IMemoryDevice>()!.GetBufferReqs(desc);
        var heap = device.Get<IMemoryDevice>()!.CreateMemoryHeap(
            new MemoryHeapDesc
            {
                Name = "d3d12 inactive alias initial data heap",
                SizeInBytes = requirements.SizeInBytes,
                Kind = requirements.HeapKind,
                Memory = MemoryClass.DeviceLocal,
                Flags = MemoryHeapFlags.AllowAliasing,
            });
        _ = device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 0, desc);

        var inactiveInitialData = Assert.Throws<RhiException>(
            () => device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 0, desc with { Name = "inactive alias with data" }, [1, 2, 3, 4]));

        Assert.Equal(ErrorCode.ValidationFailure, inactiveInitialData.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_UavBarrierRequiresUnorderedAccessStateWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "uav barrier state",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
                Raw = true,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });

        list.UavBarrier(buffer);
        var commandBuffer = list.Finish();
        var invalidState = Assert.Throws<RhiException>(() => device.GetQueue(QueueType.Compute).Submit([commandBuffer]));

        Assert.Equal(ErrorCode.ValidationFailure, invalidState.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_CopyToBufferHonorsSourceRegionWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        byte[] uploadData = new byte[4 * 256];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                for (int channel = 0; channel < 4; channel++)
                    uploadData[(row * 256) + (column * 4) + channel] = (byte)((row * 64) + (column * 4) + channel + 1);
            }
        }

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "texture region upload",
                SizeInBytes = (ulong)uploadData.Length,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            uploadData);
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "texture region source",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.CopyDestination,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "texture region readback",
                SizeInBytes = 2 * 256,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.CopyToTexture(
            upload,
            new BufferTextureCopy(0, 256, 4 * 256),
            texture,
            new TextureCopyRegion(0, 0, 0, 0, 0, 4, 4, 1));
        list.Barrier([new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(
            texture,
            new TextureCopyRegion(0, 0, 1, 1, 0, 2, 2, 1),
            readback,
            new BufferTextureCopy(0, 256, 2 * 256));
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("texture region copy");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 2 * 256);
        Assert.Equal(uploadData.AsSpan(256 + 4, 8).ToArray(), mapped.Span.Slice(0, 8).ToArray());
        Assert.Equal(uploadData.AsSpan((2 * 256) + 4, 8).ToArray(), mapped.Span.Slice(256, 8).ToArray());
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_BufferTextureCopyRejectsUnalignedFootprintOffsetWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "unaligned texture upload",
                SizeInBytes = 512,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "unaligned texture destination",
                Width = 1,
                Height = 1,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });

        var unaligned = Assert.Throws<RhiException>(
            () => list.CopyToTexture(
                upload,
                new BufferTextureCopy(256, 256, 256),
                texture,
                new TextureCopyRegion(0, 0, 0, 0, 0, 1, 1, 1)));

        Assert.Equal(ErrorCode.InvalidDescriptor, unaligned.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_ResolveTextureRejectsPartialRegionWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var source = device.CreateTexture(
            new TextureDesc
            {
                Name = "partial resolve source",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                SampleCount = 4,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.ResolveSource,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Name = "partial resolve destination",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.ResolveDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });

        var partial = Assert.Throws<RhiException>(
            () => list.ResolveTexture(
                source,
                new TextureCopyRegion(0, 0, 1, 0, 0, 3, 4, 1),
                destination,
                new TextureCopyRegion(0, 0, 0, 0, 0, 3, 4, 1)));

        Assert.Equal(ErrorCode.UnsupportedFeature, partial.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_RenderPassResolveRejectsPartialRenderAreaWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var source = device.CreateTexture(
            new TextureDesc
            {
                Name = "partial render pass resolve source",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                SampleCount = 4,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Name = "partial render pass resolve target",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var sourceView = device.CreateTextureView(
            source,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
                Dimension = TextureViewDimension.Texture2DMultisampled,
            });
        var destinationView = device.CreateTextureView(
            destination,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });

        var partial = Assert.Throws<RhiException>(
            () => list.BeginRenderPass(
                new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 2, 4),
                    ColorAttachments = [new ColorAttachmentDesc { View = sourceView, ResolveTarget = destinationView }],
                }));

        Assert.Equal(ErrorCode.UnsupportedFeature, partial.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_RenderPassResolveRejectsDimensionMismatchWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var source = device.CreateTexture(
            new TextureDesc
            {
                Name = "dimension mismatch resolve source",
                Width = 4,
                Height = 1,
                Format = Format.Rgba8Unorm,
                SampleCount = 4,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Name = "dimension mismatch resolve target",
                Dimension = ResourceDimension.Texture1D,
                Width = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var sourceView = device.CreateTextureView(
            source,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
                Dimension = TextureViewDimension.Texture2DMultisampled,
            });
        var destinationView = device.CreateTextureView(
            destination,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
                Dimension = TextureViewDimension.Texture1D,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });

        var mismatch = Assert.Throws<RhiException>(
            () => list.BeginRenderPass(
                new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 4, 1),
                    ColorAttachments = [new ColorAttachmentDesc { View = sourceView, ResolveTarget = destinationView }],
                }));

        Assert.Equal(ErrorCode.InvalidDescriptor, mismatch.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_FailedRenderPassValidationDoesNotPoisonListReferencesWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var source = device.CreateTexture(
            new TextureDesc
            {
                Name = "poison source",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                SampleCount = 4,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Name = "poison target",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var sourceView = device.CreateTextureView(
            source,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
                Dimension = TextureViewDimension.Texture2DMultisampled,
            });
        var destinationView = device.CreateTextureView(destination, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        _ = Assert.Throws<RhiException>(
            () => list.BeginRenderPass(
                new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 2, 4),
                    ColorAttachments = [new ColorAttachmentDesc { View = sourceView, ResolveTarget = destinationView }],
                }));
        var commandBuffer = list.Finish();

        device.Destroy(sourceView);
        device.Destroy(destinationView);
        device.Destroy(commandBuffer);
    }

    [D3D12DxcFact]
    public void D3D12Backend_ComputeWritesUavAndReadsBackWhenAdapterAndDxcAreAvailable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("D3D12 backend tests require Windows.");

        var dxc = RequireDxc();

        using var instance = CreateD3D12Instance();

        byte[] dxil = CompileComputeShader(dxc);
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var output = device.CreateBuffer(
            new BufferDesc
            {
                Name = "compute output",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            });
        var outputView = device.CreateBufferView(
            output,
            new BufferViewDesc
            {
                Kind = ViewKind.UnorderedAccess,
                SizeInBytes = 16,
                Raw = true,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "compute readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var pipelineBindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.RawBufferReadWrite,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var compatibleBindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.RawBufferReadWrite,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [pipelineBindingLayout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "write uav",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = dxil,
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindings(0, compatibleBindingLayout, [new BindingResourceDesc { Binding = 0, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView }]);
        pass.Dispatch(1, 1, 1);
        pass.End();
        list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
        list.CopyBuffer(output, 0, readback, 0, 4);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("compute uav");

        device.GetQueue(QueueType.Compute).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(mapped.Span));
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_CompatibleBindingSetLayoutAndPipelineLayoutLifetimeWhenAdapterAndDxcAreAvailable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("D3D12 backend tests require Windows.");

        var dxc = RequireDxc();

        using var instance = CreateD3D12Instance();

        byte[] dxil = CompileComputeShader(dxc);
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var output = device.CreateBuffer(
            new BufferDesc
            {
                Name = "compatible layout output",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            });
        var outputView = device.CreateBufferView(
            output,
            new BufferViewDesc
            {
                Kind = ViewKind.UnorderedAccess,
                SizeInBytes = 16,
                Raw = true,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "compatible layout readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var pipelineBindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.RawBufferReadWrite,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var setBindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.RawBufferReadWrite,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [pipelineBindingLayout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "compatible layout write uav",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = dxil,
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var layoutInUse = Assert.Throws<RhiException>(() => device.Destroy(pipelineLayout));
        Assert.Equal(ErrorCode.ValidationFailure, layoutInUse.Code);
        var bindingSet = device.CreateBindingSet(
            setBindingLayout,
            new BindingSetDesc
            {
                Resources =
                [
                    new BindingResourceDesc { Binding = 0, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView },
                ],
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet);
        pass.Dispatch(1, 1, 1);
        pass.End();
        list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
        list.CopyBuffer(output, 0, readback, 0, 4);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("compatible binding layout");

        device.GetQueue(QueueType.Compute).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
        Assert.Equal(0x12345678u, BitConverter.ToUInt32(mapped.Span));
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_DepthSrvAndReadOnlyDsvWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var depth = device.CreateTexture(
            new TextureDesc
            {
                Name = "depth srv",
                Width = 16,
                Height = 16,
                Format = Format.D32Float,
                BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
                InitialState = ResourceState.DepthRead,
                OptimizedClearValue = ClearValue.FromDepthStencil(Format.D32Float, new ClearDepthStencil(1.0f)),
            });
        var depthView = device.CreateTextureView(
            depth,
            new TextureViewDesc
            {
                Kind = ViewKind.DepthStencil,
                Format = Format.D32Float,
            });
        var srv = device.CreateTextureView(
            depth,
            new TextureViewDesc
            {
                Kind = ViewKind.ShaderResource,
                Format = Format.D32Float,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 16, 16),
                DepthStencilAttachment = new DepthAttachDesc { View = depthView, DepthReadOnly = true },
            });
        pass.End();
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("read-only dsv");

        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        Assert.Equal(ResourceState.DepthRead, device.GetTextureState(depth));
        device.Destroy(srv);
    }

    [D3D12Fact]
    public void D3D12Backend_CopyCommandsRejectInvalidDescriptorsWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var source = device.CreateBuffer(
            new BufferDesc
            {
                Name = "missing copy source",
                SizeInBytes = 16,
            });
        var destination = device.CreateBuffer(
            new BufferDesc
            {
                Name = "copy destination",
                SizeInBytes = 16,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });

        var missingBindFlag = Assert.Throws<RhiException>(() => list.CopyBuffer(source, 0, destination, 0, 4));

        Assert.Equal(ErrorCode.InvalidDescriptor, missingBindFlag.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_RenderPassResolveRejectsSingleSampleSourceWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var source = device.CreateTexture(
            new TextureDesc
            {
                Name = "single sample resolve source",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "resolve target",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var sourceView = device.CreateTextureView(source, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var targetView = device.CreateTextureView(target, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });

        var invalidResolve = Assert.Throws<RhiException>(
            () => list.BeginRenderPass(
                new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    ColorAttachments = [new ColorAttachmentDesc { View = sourceView, ResolveTarget = targetView }],
                }));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidResolve.Code);
    }

    [D3D12Fact]
    public void D3D12Backend_GraphicsPipelineRejectsMismatchedMultisampleDescriptorWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });

        var invalid = Assert.Throws<RhiException>(
            () => device.CreateGraphicsPipeline(
                new GraphicsPipelineDesc
                {
                    ColorFormats = [Format.Rgba8Unorm],
                    SampleCount = 1,
                    Multisample = new MultisampleDesc { SampleCount = 4 },
                }));

        Assert.Equal(ErrorCode.InvalidDescriptor, invalid.Code);
    }

    [D3D12DxcFact]
    public void D3D12Backend_GraphicsPipelineUsesAttribVertexInputSemanticsWhenAdapterAndDxcAreAvailable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("D3D12 backend tests require Windows.");

        var dxc = RequireDxc();

        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "attrib vertex input vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileVertexInputShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "attrib vertex input ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGraphicsShader(dxc, ShaderStage.Pixel),
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());

        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Layout = layout,
                VertexShader = vertex,
                PixelShader = pixel,
                VertexBuffers = [new VertexLayoutDesc { Slot = 0, StrideInBytes = 8 }],
                VertexAttributes = [new VertexAttributeDesc { Location = 0, BufferSlot = 0, Format = Format.Rg32Float }],
                ColorFormats = [Format.Rgba8Unorm],
            });

        Assert.True(pipeline.IsValid);
    }

    [Fact]
    public void D3D12InputElements_PreserveInstanceRateLayouts()
    {
        var deviceType = typeof(D3D12Backend).Assembly.GetType("SomeEngine.Rhi.D3D12.D3D12Device")
            ?? throw new InvalidOperationException("D3D12Device type was not found.");
        var createInputElements = deviceType.GetMethod("CreateInputElements", ReflectionBindingFlags.Static | ReflectionBindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateInputElements method was not found.");

        var elements = (D3D12InputElementDescription[])createInputElements.Invoke(
            null,
            [
                new GraphicsPipelineDesc
                {
                    VertexBuffers =
                    [
                        new VertexLayoutDesc
                        {
                            Slot = 3,
                            StrideInBytes = 16,
                            InputRate = VertexInputRate.Instance,
                            InstanceStepRate = 2,
                        },
                    ],
                    VertexAttributes =
                    [
                        new VertexAttributeDesc
                        {
                            Location = 2,
                            BufferSlot = 3,
                            Format = Format.Rg32Float,
                            OffsetInBytes = 12,
                        },
                    ],
                },
            ])!;

        Assert.Single(elements);
        Assert.Equal("ATTRIB", elements[0].SemanticName);
        Assert.Equal(2u, elements[0].SemanticIndex);
        Assert.Equal(3u, elements[0].Slot);
        Assert.Equal(12u, elements[0].AlignedByteOffset);
        Assert.Equal(D3D12InputClassification.PerInstanceData, elements[0].Classification);
        Assert.Equal(2u, elements[0].InstanceDataStepRate);
    }

    [D3D12DxcFact]
    public void D3D12Backend_DrawUsesInstanceRateVertexInputWhenAdapterAndDxcAreAvailable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("D3D12 backend tests require Windows.");

        var dxc = RequireDxc();

        using var instance = CreateD3D12Instance();

        const int Width = 64;
        const int Height = 64;
        const int RowPitch = 256;

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "instance rate vertex input target",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.CopySource,
                InitialState = ResourceState.RenderTarget,
                OptimizedClearValue = ClearValue.FromColor(Format.Rgba8Unorm, Color.Black),
            });
        var targetView = device.CreateTextureView(target, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "instance rate vertex input readback",
                SizeInBytes = RowPitch * Height,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var offsets = device.CreateBuffer(
            new BufferDesc
            {
                Name = "instance rate offsets",
                SizeInBytes = 3 * 2 * sizeof(float),
                BindFlags = BindFlags.VertexBuffer,
                InitialState = ResourceState.VertexBuffer,
                StrideInBytes = 2 * sizeof(float),
            },
            FloatBytes(
                -0.5f, 0.0f,
                0.5f, 0.0f,
                0.0f, -2.0f));
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "instance rate vertex input vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileInstancedOffsetVertexShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "instance rate vertex input ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGraphicsShader(dxc, ShaderStage.Pixel),
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "instance rate vertex input pipeline",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = pixel,
                VertexBuffers =
                [
                    new VertexLayoutDesc
                    {
                        Slot = 0,
                        StrideInBytes = 2 * sizeof(float),
                        InputRate = VertexInputRate.Instance,
                        InstanceStepRate = 1,
                    },
                ],
                VertexAttributes =
                [
                    new VertexAttributeDesc { Location = 0, BufferSlot = 0, Format = Format.Rg32Float },
                ],
                Rasterizer = new RasterizerDesc { CullMode = CullMode.None },
                ColorFormats = [Format.Rgba8Unorm],
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = targetView,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = Color.Black,
                    },
                ],
            });
        pass.SetPipeline(pipeline);
        pass.SetVertexBuffer(0, offsets);
        pass.Draw(3, instanceCount: 2);
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(
            target,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1),
            readback,
            new BufferTextureCopy(0, RowPitch, RowPitch * Height));
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("instance rate vertex input");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, RowPitch * Height);
        AssertRgba8PixelRed(mapped.Span, RowPitch, Width / 4, Height / 2);
        AssertRgba8PixelRed(mapped.Span, RowPitch, (Width * 3) / 4, Height / 2);
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_DrawUsesSingleInstanceColorAttributeWhenAdapterAndDxcAreAvailable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("D3D12 backend tests require Windows.");

        var dxc = RequireDxc();

        using var instance = CreateD3D12Instance();

        const int Width = 64;
        const int Height = 64;
        const int RowPitch = 256;

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "single instance color target",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.CopySource,
                InitialState = ResourceState.RenderTarget,
                OptimizedClearValue = ClearValue.FromColor(Format.Rgba8Unorm, Color.Black),
            });
        var targetView = device.CreateTextureView(target, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "single instance color readback",
                SizeInBytes = RowPitch * Height,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var instanceColor = device.CreateBuffer(
            new BufferDesc
            {
                Name = "single instance color",
                SizeInBytes = 4 * sizeof(float),
                BindFlags = BindFlags.VertexBuffer,
                InitialState = ResourceState.VertexBuffer,
                StrideInBytes = 4 * sizeof(float),
            },
            FloatBytes(0.0f, 1.0f, 0.0f, 1.0f));
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "single instance color vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileInstancedColorVertexShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "single instance color ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileVertexColorPixelShader(dxc),
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "single instance color pipeline",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = pixel,
                VertexBuffers =
                [
                    new VertexLayoutDesc
                    {
                        Slot = 0,
                        StrideInBytes = 4 * sizeof(float),
                        InputRate = VertexInputRate.Instance,
                        InstanceStepRate = 1,
                    },
                ],
                VertexAttributes =
                [
                    new VertexAttributeDesc
                    {
                        Location = 0,
                        BufferSlot = 0,
                        Format = Format.Rgba32Float,
                        OffsetInBytes = 0,
                    },
                ],
                Rasterizer = new RasterizerDesc { CullMode = CullMode.None },
                ColorFormats = [Format.Rgba8Unorm],
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = targetView,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = Color.Black,
                    },
                ],
            });
        pass.SetPipeline(pipeline);
        pass.SetVertexBuffer(0, instanceColor);
        pass.Draw(3, instanceCount: 1);
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(
            target,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1),
            readback,
            new BufferTextureCopy(0, RowPitch, RowPitch * Height));
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("single instance color");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, RowPitch * Height);
        AssertRgba8PixelNear(mapped.Span, RowPitch, Width / 2, Height / 2, 0, 255, 0, 255);
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_RenderPassRejectsIncompatibleGraphicsPipelineWhenAdapterAndDxcAreAvailable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("D3D12 backend tests require Windows.");

        var dxc = RequireDxc();

        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "rgba render target",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var view = device.CreateTextureView(texture, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGraphicsShader(dxc, ShaderStage.Vertex),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGraphicsShader(dxc, ShaderStage.Pixel),
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Layout = layout,
                VertexShader = vertex,
                PixelShader = pixel,
                ColorFormats = [Format.Bgra8Unorm],
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 16, 16),
                ColorAttachments = [new ColorAttachmentDesc { View = view }],
            });

        var incompatible = Assert.Throws<RhiException>(() => pass.SetPipeline(pipeline));

        Assert.Equal(ErrorCode.ValidationFailure, incompatible.Code);
        pass.End();
    }

    [D3D12Fact]
    public void D3D12Backend_VertexAndIndexBindingRejectInvalidDescriptorsWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "binding validation target",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var targetView = device.CreateTextureView(target, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var missingVertexFlag = device.CreateBuffer(new BufferDesc { Name = "missing vb flag", SizeInBytes = 16 });
        var missingIndexFlag = device.CreateBuffer(new BufferDesc { Name = "missing ib flag", SizeInBytes = 16 });
        var vertexBuffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "valid vb",
                SizeInBytes = 16,
                BindFlags = BindFlags.VertexBuffer,
                InitialState = ResourceState.VertexBuffer,
                StrideInBytes = 8,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 4, 4),
                ColorAttachments = [new ColorAttachmentDesc { View = targetView }],
            });

        var missingVertexBind = Assert.Throws<RhiException>(() => pass.SetVertexBuffer(0, missingVertexFlag));
        var missingIndexBind = Assert.Throws<RhiException>(() => pass.SetIndexBuffer(missingIndexFlag, IndexFormat.UInt16));
        var invalidVertexOffset = Assert.Throws<RhiException>(() => pass.SetVertexBuffer(0, vertexBuffer, 16));

        Assert.Equal(ErrorCode.InvalidDescriptor, missingVertexBind.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, missingIndexBind.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidVertexOffset.Code);
        pass.End();
    }

    [D3D12Fact]
    public void D3D12Backend_LiveCommandBufferOwnsReferencedResourceLifetimeWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "upload lifetime",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "readback lifetime",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.CopyBuffer(upload, 0, readback, 0, 4);
        var commandBuffer = list.Finish();

        var liveReference = Assert.Throws<RhiException>(() => device.Destroy(upload));

        Assert.Equal(ErrorCode.ValidationFailure, liveReference.Code);
        device.Destroy(commandBuffer);
        device.Destroy(upload);
        device.Destroy(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_CopyToTextureThenReadbackRoundTripsWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        const int Width = 4;
        const int Height = 4;
        const int RowPitch = 256;
        byte[] uploadData = CreateRgba8Pattern(Width, Height, RowPitch);

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "texture upload source",
                SizeInBytes = (ulong)uploadData.Length,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            uploadData);
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "texture upload round trip",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.CopyDestination,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "texture upload readback",
                SizeInBytes = (ulong)uploadData.Length,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.CopyToTexture(
            upload,
            new BufferTextureCopy(0, RowPitch, RowPitch * Height),
            texture,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1));
        list.Barrier([new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(
            texture,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1),
            readback,
            new BufferTextureCopy(0, RowPitch, RowPitch * Height));
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("texture upload round trip");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, uploadData.Length);
        AssertRgba8PatternPixel(mapped.Span, RowPitch, 0, 0);
        AssertRgba8PatternPixel(mapped.Span, RowPitch, 3, 0);
        AssertRgba8PatternPixel(mapped.Span, RowPitch, 1, 2);
        AssertRgba8PatternPixel(mapped.Span, RowPitch, 3, 3);
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_RenderPassClearCanBeReadBackWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        const int Width = 4;
        const int Height = 4;
        const int RowPitch = 256;
        var clear = new Color(0.25f, 0.5f, 0.75f, 1.0f);

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "clear readback target",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.CopySource,
                InitialState = ResourceState.RenderTarget,
                OptimizedClearValue = ClearValue.FromColor(Format.Rgba8Unorm, clear),
            });
        var targetView = device.CreateTextureView(target, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "clear readback buffer",
                SizeInBytes = RowPitch * Height,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = targetView,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = clear,
                    },
                ],
            });
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(
            target,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1),
            readback,
            new BufferTextureCopy(0, RowPitch, RowPitch * Height));
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("clear readback");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, RowPitch * Height);
        AssertRgba8PixelNear(mapped.Span, RowPitch, 0, 0, 64, 128, 191, 255);
        AssertRgba8PixelNear(mapped.Span, RowPitch, 3, 3, 64, 128, 191, 255);
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_GraphicsTriangleDrawCanBeReadBackWhenAdapterAndDxcAreAvailable()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("D3D12 backend tests require Windows.");

        var dxc = RequireDxc();

        using var instance = CreateD3D12Instance();

        const int Width = 16;
        const int Height = 16;
        const int RowPitch = 256;

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "triangle readback target",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.CopySource,
                InitialState = ResourceState.RenderTarget,
                OptimizedClearValue = ClearValue.FromColor(Format.Rgba8Unorm, Color.Black),
            });
        var targetView = device.CreateTextureView(target, new TextureViewDesc { Kind = ViewKind.RenderTarget, Format = Format.Rgba8Unorm });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "triangle readback buffer",
                SizeInBytes = RowPitch * Height,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "triangle readback vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGraphicsShader(dxc, ShaderStage.Vertex),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "triangle readback ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGraphicsShader(dxc, ShaderStage.Pixel),
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "triangle readback pipeline",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = pixel,
                Rasterizer = new RasterizerDesc { CullMode = CullMode.None },
                ColorFormats = [Format.Rgba8Unorm],
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = targetView,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = Color.Black,
                    },
                ],
            });
        pass.SetPipeline(pipeline);
        pass.Draw(3);
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(
            target,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1),
            readback,
            new BufferTextureCopy(0, RowPitch, RowPitch * Height));
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("triangle readback");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, RowPitch * Height);
        var center = Rgba8Pixel(mapped.Span, RowPitch, Width / 2, Height / 2);
        Assert.InRange(center[0], 200, 255);
        Assert.InRange(center[1], 0, 32);
        Assert.InRange(center[2], 0, 32);
        Assert.InRange(center[3], 200, 255);
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_TimestampResolveProducesOrderedValuesWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var queryPool = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Name = "ordered timestamps", Type = QueryType.Timestamp, Count = 2 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "timestamp work source",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var copyDestination = device.CreateBuffer(
            new BufferDesc
            {
                Name = "timestamp work destination",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var timestampReadback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "ordered timestamp readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.WriteTimestamp(queryPool, 0);
        list.CopyBuffer(upload, 0, copyDestination, 0, 4);
        list.WriteTimestamp(queryPool, 1);
        list.ResolveQueryData(queryPool, 0, 2, timestampReadback, 0);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("ordered timestamps");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        var mapped = device.MapBuffer(timestampReadback, MapMode.Read, 0, 16);
        ulong first = BitConverter.ToUInt64(mapped.Span[..8]);
        ulong second = BitConverter.ToUInt64(mapped.Span.Slice(8, 8));
        Assert.True(second >= first);
        device.UnmapBuffer(timestampReadback);

        var copied = device.MapBuffer(copyDestination, MapMode.Read, 0, 4);
        Assert.Equal(1, copied.Span[0]);
        Assert.Equal(2, copied.Span[1]);
        Assert.Equal(3, copied.Span[2]);
        Assert.Equal(4, copied.Span[3]);
        device.UnmapBuffer(copyDestination);
    }

    [D3D12Fact]
    public void D3D12Backend_PlacedAliasedTexturesRequireAliasingBarrierWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();

        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var desc = new TextureDesc
        {
            Name = "d3d12 alias texture",
            Width = 4,
            Height = 4,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopySource,
        };
        var requirements = device.Get<IMemoryDevice>()!.GetTextureReqs(desc);
        var heap = device.Get<IMemoryDevice>()!.CreateMemoryHeap(
            new MemoryHeapDesc
            {
                Name = "d3d12 alias texture heap",
                SizeInBytes = requirements.SizeInBytes,
                Kind = requirements.HeapKind,
                Memory = MemoryClass.DeviceLocal,
                Flags = MemoryHeapFlags.AllowAliasing,
            });
        var first = device.Get<IMemoryDevice>()!.CreatePlacedTexture(heap, 0, desc);
        var second = device.Get<IMemoryDevice>()!.CreatePlacedTexture(heap, 0, desc with { Name = "d3d12 alias texture 2" });

        var missingBarrierList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        missingBarrierList.Barrier([new TextureBarrier(second, ResourceState.CopySource, ResourceState.CopySource, SubresourceRange.All)], []);
        var missingBarrierCommand = missingBarrierList.Finish();
        var missingBarrier = Assert.Throws<RhiException>(() => device.GetQueue(QueueType.Graphics).Submit([missingBarrierCommand]));
        Assert.Equal(ErrorCode.ValidationFailure, missingBarrier.Code);
        device.Destroy(missingBarrierCommand);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier(
            [new TextureBarrier(second, ResourceState.CopySource, ResourceState.CopyDestination, SubresourceRange.All)],
            [],
            [AliasingBarrier.Between(first, second)]);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("d3d12 alias texture");
        device.GetQueue(QueueType.Graphics).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);

        device.Destroy(commandBuffer);
    }

    [Fact]
    public void CreateBuffer_MapsCpuUploadAndRejectsInvalidDescriptors()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        Assert.Equal(Backend.Null, device.AdapterInfo.Backend);
        Assert.True(device.GetFormatSupport(Format.Rgba8Unorm).HasFlag(FormatSupport.RenderTarget));

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "upload",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
            },
            [1, 2, 3, 4]);

        var mapped = device.MapBuffer(buffer, MapMode.Write);
        Assert.Equal(16, mapped.Length);
        Assert.Equal(1, mapped.Span[0]);
        mapped.Span[4] = 7;
        device.UnmapBuffer(buffer);

        var readOnUpload = Assert.Throws<RhiException>(() => device.MapBuffer(buffer, MapMode.Read));
        Assert.Equal(ErrorCode.ValidationFailure, readOnUpload.Code);

        var emptyBuffer = Assert.Throws<RhiException>(() => device.CreateBuffer(new BufferDesc()));
        Assert.Equal(ErrorCode.InvalidDescriptor, emptyBuffer.Code);
    }

    [Fact]
    public void PlacedBuffers_TrackAllocationAndValidateMappedRanges()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var desc = new BufferDesc
        {
            Name = "placed upload",
            SizeInBytes = 64,
            Memory = MemoryClass.CpuUpload,
            BindFlags = BindFlags.ConstantBuffer,
            InitialState = ResourceState.ConstantBuffer,
        };
        var requirements = device.Get<IMemoryDevice>()!.GetBufferReqs(desc);
        var initialBudget = device.Get<IMemoryDevice>()!.GetMemoryBudget(MemoryClass.CpuUpload);
        var heap = device.Get<IMemoryDevice>()!.CreateMemoryHeap(
            new MemoryHeapDesc
            {
                Name = "upload heap",
                SizeInBytes = requirements.SizeInBytes,
                Memory = MemoryClass.CpuUpload,
                Kind = requirements.HeapKind,
            });

        Assert.Equal(initialBudget.CurrentUsageInBytes + requirements.SizeInBytes, device.Get<IMemoryDevice>()!.GetMemoryBudget(MemoryClass.CpuUpload).CurrentUsageInBytes);

        var unalignedOffset = Assert.Throws<RhiException>(() => device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 1, desc));
        Assert.Equal(ErrorCode.InvalidDescriptor, unalignedOffset.Code);

        var buffer = device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 0, desc, [1, 2, 3, 4]);
        var usedBudget = device.Get<IMemoryDevice>()!.GetMemoryBudget(MemoryClass.CpuUpload);
        Assert.False(usedBudget.IsExact);
        Assert.True(usedBudget.BudgetInBytes >= usedBudget.CurrentUsageInBytes);
        var allocation = device.GetBufferAlloc(buffer);
        Assert.Equal(ResourceOwnership.Placed, allocation.Ownership);
        Assert.Equal(heap, allocation.Heap);
        Assert.Equal(0ul, allocation.HeapOffset);
        Assert.Equal(requirements.SizeInBytes, allocation.SizeInBytes);
        Assert.Equal(initialBudget.CurrentUsageInBytes + requirements.SizeInBytes, device.Get<IMemoryDevice>()!.GetMemoryBudget(MemoryClass.CpuUpload).CurrentUsageInBytes);

        var mapped = device.MapBuffer(buffer, MapMode.Write, offset: 16, sizeInBytes: 4);
        Assert.Equal(4, mapped.Length);
        mapped.Span[0] = 9;
        device.FlushBufferRange(buffer, 16, 4);
        var outsideMappedRange = Assert.Throws<RhiException>(() => device.FlushBufferRange(buffer, 15, 6));
        Assert.Equal(ErrorCode.InvalidDescriptor, outsideMappedRange.Code);
        device.UnmapBuffer(buffer);

        var liveHeapDestroy = Assert.Throws<RhiException>(() => device.Destroy(heap));
        Assert.Equal(ErrorCode.ValidationFailure, liveHeapDestroy.Code);

        device.Destroy(buffer);
        Assert.Equal(initialBudget.CurrentUsageInBytes + requirements.SizeInBytes, device.Get<IMemoryDevice>()!.GetMemoryBudget(MemoryClass.CpuUpload).CurrentUsageInBytes);
        device.Destroy(heap);
        Assert.Equal(initialBudget.CurrentUsageInBytes, device.Get<IMemoryDevice>()!.GetMemoryBudget(MemoryClass.CpuUpload).CurrentUsageInBytes);
    }

    [Fact]
    public void AliasingBarriers_RequirePlacedOverlappingResourcesOnAliasingHeap()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var desc = new BufferDesc
        {
            Name = "aliased",
            SizeInBytes = 64,
            BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopySource,
        };
        var requirements = device.Get<IMemoryDevice>()!.GetBufferReqs(desc);
        var heap = device.Get<IMemoryDevice>()!.CreateMemoryHeap(
            new MemoryHeapDesc
            {
                Name = "alias heap",
                SizeInBytes = requirements.SizeInBytes,
                Kind = requirements.HeapKind,
                Flags = MemoryHeapFlags.AllowAliasing,
            });
        var first = device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 0, desc);
        var second = device.Get<IMemoryDevice>()!.CreatePlacedBuffer(heap, 0, desc with { Name = "aliased 2" });

        var missingBarrierList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        missingBarrierList.Barrier([], [new BufferBarrier(second, ResourceState.CopySource, ResourceState.CopySource)]);
        var missingBarrier = Assert.Throws<RhiException>(() => device.GetQueue(QueueType.Graphics).Submit([missingBarrierList.Finish()]));
        Assert.Equal(ErrorCode.ValidationFailure, missingBarrier.Code);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier(
            [],
            [new BufferBarrier(second, ResourceState.CopySource, ResourceState.CopyDestination)],
            [AliasingBarrier.Between(first, second)]);
        device.GetQueue(QueueType.Graphics).Submit([list.Finish()]);

        var wrongBeforeList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        wrongBeforeList.Barrier([], [], [AliasingBarrier.Between(first, second)]);
        var wrongBefore = Assert.Throws<RhiException>(() => device.GetQueue(QueueType.Graphics).Submit([wrongBeforeList.Finish()]));
        Assert.Equal(ErrorCode.ValidationFailure, wrongBefore.Code);

        var noAliasHeap = device.Get<IMemoryDevice>()!.CreateMemoryHeap(
            new MemoryHeapDesc
            {
                Name = "non alias heap",
                SizeInBytes = requirements.SizeInBytes,
                Kind = requirements.HeapKind,
            });
        var noAliasFirst = device.Get<IMemoryDevice>()!.CreatePlacedBuffer(noAliasHeap, 0, desc with { Name = "no alias 1" });
        var missingFlag = Assert.Throws<RhiException>(() => device.Get<IMemoryDevice>()!.CreatePlacedBuffer(noAliasHeap, 0, desc with { Name = "no alias 2" }));
        Assert.Equal(ErrorCode.InvalidDescriptor, missingFlag.Code);
        device.Destroy(noAliasFirst);
    }

    [Fact]
    public void SwapchainResize_BlocksLiveBackbufferCommandBuffer()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var swapchainHandle = device.CreateSwapchain(
            new SwapchainDesc
            {
                Name = "resize live",
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BufferCount = 2,
            });
        var swapchain = device.GetSwapchain(swapchainHandle);
        var texture = swapchain.CurrentTexture;
        var view = swapchain.CurrentRenderTargetView;
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier([new TextureBarrier(texture, ResourceState.Present, ResourceState.RenderTarget, SubresourceRange.All)], []);
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 16, 16),
                ColorAttachments = [new ColorAttachmentDesc { View = view }],
            });
        pass.End();
        var commandBuffer = list.Finish();

        var liveBackbuffer = Assert.Throws<RhiException>(() => swapchain.Resize(32, 32));

        Assert.Equal(ErrorCode.ValidationFailure, liveBackbuffer.Code);
        device.Destroy(commandBuffer);
        swapchain.Resize(32, 32);
        Assert.Equal(32u, swapchain.Width);
    }

    [Fact]
    public void GraphicsSubmit_PresentsAndResizesSwapchain()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var swapchainHandle = device.CreateSwapchain(
            new SwapchainDesc
            {
                Name = "main",
                Width = 64,
                Height = 32,
                Format = Format.Rgba8Unorm,
                BufferCount = 3,
            });
        var swapchain = device.GetSwapchain(swapchainHandle);
        var firstTexture = swapchain.CurrentTexture;
        var firstRtv = swapchain.CurrentRenderTargetView;

        Assert.Equal(0u, swapchain.CurrentBackBufferIndex);
        Assert.Equal(ResourceState.Present, device.GetTextureState(firstTexture));

        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc { Name = "empty" });
        var vertex = CreateShader(device, "vs", ShaderStage.Vertex);
        var pixel = CreateShader(device, "ps", ShaderStage.Pixel);
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "triangle",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = pixel,
                ColorFormats = [Format.Rgba8Unorm],
            });

        var missingBarrierList = device.CreateCommandList(new CommandListDesc { Name = "missing barrier", QueueType = QueueType.Graphics });
        var missingBarrierPass = missingBarrierList.BeginRenderPass(
            new RenderPassDesc
            {
                Name = "bad",
                RenderArea = new Rect(0, 0, 64, 32),
                ColorAttachments = [new ColorAttachmentDesc { View = firstRtv }],
            });
        missingBarrierPass.End();
        var missingBarrierCommand = missingBarrierList.Finish();
        var missingBarrier = Assert.Throws<RhiException>(
            () => device.GetQueue(QueueType.Graphics).Submit([missingBarrierCommand]));
        Assert.Equal(ErrorCode.ValidationFailure, missingBarrier.Code);
        device.Destroy(missingBarrierCommand);

        var list = device.CreateCommandList(new CommandListDesc { Name = "frame", QueueType = QueueType.Graphics });
        list.Barrier(
            [new TextureBarrier(firstTexture, ResourceState.Present, ResourceState.RenderTarget, SubresourceRange.All)],
            []);

        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                Name = "main",
                RenderArea = new Rect(0, 0, 64, 32),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = firstRtv,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = Color.Black,
                    },
                ],
            });
        pass.SetViewport(new Viewport(0, 0, 64, 32));
        pass.SetScissor(new Rect(0, 0, 64, 32));
        pass.SetPipeline(pipeline);
        pass.Draw(3);
        pass.End();

        list.Barrier(
            [new TextureBarrier(firstTexture, ResourceState.RenderTarget, ResourceState.Present, SubresourceRange.All)],
            []);

        var commandBuffer = list.Finish();
        Assert.Equal(ResourceState.Present, device.GetTextureState(firstTexture));
        var fence = device.CreateFence("frame");
        var queue = device.GetQueue(QueueType.Graphics);
        queue.Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);

        Assert.Equal(1ul, device.GetFenceValue(fence));
        var resubmit = Assert.Throws<RhiException>(() => queue.Submit([commandBuffer]));
        Assert.Equal(ErrorCode.ValidationFailure, resubmit.Code);

        var destroySwapchainTexture = Assert.Throws<RhiException>(() => device.Destroy(firstTexture));
        Assert.Equal(ErrorCode.ValidationFailure, destroySwapchainTexture.Code);
        var destroySwapchainView = Assert.Throws<RhiException>(() => device.Destroy(firstRtv));
        Assert.Equal(ErrorCode.ValidationFailure, destroySwapchainView.Code);

        swapchain.Present(new PresentDesc { SyncInterval = 1, AllowTearing = false });
        var secondTexture = swapchain.CurrentTexture;
        var secondRtv = swapchain.CurrentRenderTargetView;
        Assert.Equal(1u, swapchain.CurrentBackBufferIndex);
        Assert.NotEqual(firstTexture, secondTexture);
        Assert.Equal(ResourceState.Present, device.GetTextureState(firstTexture));
        Assert.Equal(ResourceState.Present, device.GetTextureState(secondTexture));

        swapchain.Resize(128, 64);
        Assert.Equal(128u, swapchain.Width);
        Assert.Equal(64u, swapchain.Height);
        Assert.NotEqual(firstTexture, swapchain.CurrentTexture);

        var oldTexture = Assert.Throws<RhiException>(() => device.GetTextureDesc(firstTexture));
        Assert.Equal(ErrorCode.InvalidHandle, oldTexture.Code);
        var oldView = Assert.Throws<RhiException>(() => device.Destroy(firstRtv));
        Assert.Equal(ErrorCode.InvalidHandle, oldView.Code);
        var oldSecondTexture = Assert.Throws<RhiException>(() => device.GetTextureDesc(secondTexture));
        Assert.Equal(ErrorCode.InvalidHandle, oldSecondTexture.Code);
        var oldSecondView = Assert.Throws<RhiException>(() => device.Destroy(secondRtv));
        Assert.Equal(ErrorCode.InvalidHandle, oldSecondView.Code);
    }

    [Fact]
    public void ComputePass_ValidatesPipelineLayoutAndBindingSet()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                Name = "constants",
                SizeInBytes = 256,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            });
        var bufferView = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Name = "constants",
                Kind = ViewKind.ConstantBuffer,
                SizeInBytes = 256,
            });

        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "frame set",
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(
            new PipelineLayoutDesc
            {
                Name = "compute layout",
                BindingLayouts = [bindingLayout],
                PushConstants =
                [
                    new PushRangeDesc
                    {
                        Stages = ShaderStageFlags.Compute,
                        SizeInBytes = 16,
                    },
                ],
            });
        var bindingSet = device.CreateBindingSet(
            bindingLayout,
            new BindingSetDesc
            {
                Name = "frame bindings",
                Resources =
                [
                    new BindingResourceDesc
                    {
                        Binding = 0,
                        ResourceType = BindingType.ConstantBuffer,
                        BufferView = bufferView,
                    },
                ],
            });

        var shader = CreateShader(device, "cs", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(
            new ComputePipelineDesc
            {
                Name = "compute",
                Layout = pipelineLayout,
                ComputeShader = shader,
            });

        var list = device.CreateCommandList(new CommandListDesc { Name = "compute", QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc { Name = "compute" });
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet);
        pass.SetPushConstants(ShaderStageFlags.Compute, 0, [1, 2, 3, 4]);
        pass.Dispatch(8, 1, 1);
        pass.End();

        var commandBuffer = list.Finish();
        device.GetQueue(QueueType.Compute).Submit([commandBuffer]);

        var transientList = device.CreateCommandList(new CommandListDesc { Name = "transient compute", QueueType = QueueType.Compute });
        var transientPass = transientList.BeginComputePass(new ComputePassDesc { Name = "transient compute" });
        transientPass.SetPipeline(pipeline);
        transientPass.SetBindings(
            0,
            bindingLayout,
            [
                new BindingResourceDesc
                {
                    Binding = 0,
                    ResourceType = BindingType.ConstantBuffer,
                    BufferView = bufferView,
                },
            ]);
        transientPass.Dispatch(1, 1, 1);
        transientPass.End();

        device.GetQueue(QueueType.Compute).Submit([transientList.Finish()]);

        var missingResource = Assert.Throws<RhiException>(
            () => device.CreateBindingSet(bindingLayout, new BindingSetDesc { Name = "missing" }));
        Assert.Equal(ErrorCode.InvalidDescriptor, missingResource.Code);
    }

    [Fact]
    public void QueryAndDebugMarker_AreRecordedAndResolvedAtSubmit()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var queryPool = device.Get<IQueryDevice>()!.CreateQueryPool(
            new QueryPoolDesc
            {
                Name = "timestamps",
                Type = QueryType.Timestamp,
                Count = 1,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "timestamp readback",
                SizeInBytes = 8,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });

        var list = device.CreateCommandList(new CommandListDesc { Name = "timestamp", QueueType = QueueType.Graphics });
        list.PushDebugGroup("timestamp");
        list.WriteTimestamp(queryPool, 0);
        list.ResolveQueryData(queryPool, 0, 1, readback, 0);
        list.PopDebugGroup();
        var commandBuffer = list.Finish();

        device.GetQueue(QueueType.Graphics).Submit([commandBuffer]);

        var mapped = device.MapBuffer(readback, MapMode.Read);
        Assert.True(BitConverter.ToUInt64(mapped.Span[..8]) > 0);
        device.UnmapBuffer(readback);

        var unwritten = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Name = "unwritten", Type = QueryType.Timestamp, Count = 1 });
        var badList = device.CreateCommandList(new CommandListDesc { Name = "bad timestamp", QueueType = QueueType.Graphics });
        badList.ResolveQueryData(unwritten, 0, 1, readback, 0);
        var badCommandBuffer = badList.Finish();
        var missingWrite = Assert.Throws<RhiException>(
            () => device.GetQueue(QueueType.Graphics).Submit([badCommandBuffer]));
        Assert.Equal(ErrorCode.ValidationFailure, missingWrite.Code);
    }

    [Fact]
    public void QueueSubmit_IsAtomicAndValidatesQueueClass()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var computeList = device.CreateCommandList(new CommandListDesc { Name = "empty compute", QueueType = QueueType.Compute });
        var computeCommand = computeList.Finish();

        var wrongQueue = Assert.Throws<RhiException>(
            () => device.GetQueue(QueueType.Graphics).Submit([computeCommand]));
        Assert.Equal(ErrorCode.ValidationFailure, wrongQueue.Code);

        var fence = device.CreateFence("signal", 5);
        var signalFailure = Assert.Throws<RhiException>(
            () => device.GetQueue(QueueType.Compute).Submit([computeCommand], signals: [new QueueSignal(fence, 4)]));
        Assert.Equal(ErrorCode.ValidationFailure, signalFailure.Code);

        var equalSignalFailure = Assert.Throws<RhiException>(
            () => device.GetQueue(QueueType.Compute).Submit([computeCommand], signals: [new QueueSignal(fence, 5)]));
        Assert.Equal(ErrorCode.ValidationFailure, equalSignalFailure.Code);

        var backwardSignalFailure = Assert.Throws<RhiException>(
            () => device.GetQueue(QueueType.Compute).Submit([computeCommand], signals: [new QueueSignal(fence, 7), new QueueSignal(fence, 6)]));
        Assert.Equal(ErrorCode.ValidationFailure, backwardSignalFailure.Code);

        device.GetQueue(QueueType.Compute).Submit([computeCommand], signals: [new QueueSignal(fence, 6)]);
        Assert.Equal(6ul, device.GetFenceValue(fence));
    }

    [Fact]
    public void QueueSubmit_SignalsWithoutCommandBuffer()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);
        var source = device.CreateFence("empty submit source", 2);
        var target = device.CreateFence("empty submit target");

        queue.Submit(
            ReadOnlySpan<CommandBufferHandle>.Empty,
            [new QueueWait(source, 2)],
            [new QueueSignal(target, 1)]);

        Assert.Equal(1ul, device.GetFenceValue(target));
    }

    [Fact]
    public void QueueSubmit_RejectsEmptyNoop()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var ex = Assert.Throws<RhiException>(() =>
            device.GetQueue(QueueType.Graphics).Submit(ReadOnlySpan<CommandBufferHandle>.Empty));

        Assert.Equal(ErrorCode.InvalidDescriptor, ex.Code);
    }

    [Fact]
    public void HandlesDoNotCrossDevices()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var first = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        using var second = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var firstBuffer = first.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });

        var crossDeviceUse = Assert.Throws<RhiException>(() => second.GetBufferDesc(firstBuffer));
        Assert.Equal(ErrorCode.InvalidHandle, crossDeviceUse.Code);
    }

    [Fact]
    public void CopyBuffer_RequiresStatesAndValidRanges()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var destination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var invalidRange = device.CreateCommandList(new CommandListDesc { Name = "bad copy", QueueType = QueueType.Copy });
        var rangeFailure = Assert.Throws<RhiException>(() => invalidRange.CopyBuffer(source, 12, destination, 0, 8));
        Assert.Equal(ErrorCode.InvalidDescriptor, rangeFailure.Code);

        var list = device.CreateCommandList(new CommandListDesc { Name = "copy", QueueType = QueueType.Copy });
        list.CopyBuffer(source, 0, destination, 4, 4);
        var commandBuffer = list.Finish();
        device.GetQueue(QueueType.Copy).Submit([commandBuffer]);

        var mapped = device.MapBuffer(destination, MapMode.Read);
        Assert.Equal(1, mapped.Span[4]);
        Assert.Equal(2, mapped.Span[5]);
        Assert.Equal(3, mapped.Span[6]);
        Assert.Equal(4, mapped.Span[7]);
        device.UnmapBuffer(destination);
    }

    [Fact]
    public void Submit_SimulatesResourceStatesAcrossCommandBuffers()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var destination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var first = device.CreateCommandList(new CommandListDesc { Name = "first", QueueType = QueueType.Copy });
        first.Barrier([], [new BufferBarrier(source, ResourceState.CopySource, ResourceState.CopyDestination)]);
        var firstCommand = first.Finish();

        var second = device.CreateCommandList(new CommandListDesc { Name = "second", QueueType = QueueType.Copy });
        second.Barrier([], [new BufferBarrier(source, ResourceState.CopyDestination, ResourceState.CopySource)]);
        second.CopyBuffer(source, 0, destination, 0, 4);
        var secondCommand = second.Finish();

        device.GetQueue(QueueType.Copy).Submit([firstCommand, secondCommand]);
        Assert.Equal(ResourceState.CopySource, device.GetBufferState(source));

        var upload = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [9, 8, 7, 6]);
        var laterDestination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopySource,
            });
        var producer = device.CreateCommandList(new CommandListDesc { Name = "produce destination state", QueueType = QueueType.Copy });
        producer.Barrier([], [new BufferBarrier(laterDestination, ResourceState.CopySource, ResourceState.CopyDestination)]);
        var producerCommand = producer.Finish();

        var consumer = device.CreateCommandList(new CommandListDesc { Name = "consume future state", QueueType = QueueType.Copy });
        consumer.CopyBuffer(upload, 0, laterDestination, 0, 4);
        var consumerCommand = consumer.Finish();

        device.GetQueue(QueueType.Copy).Submit([producerCommand, consumerCommand]);
        var mapped = device.MapBuffer(laterDestination, MapMode.Read);
        Assert.Equal(9, mapped.Span[0]);
        Assert.Equal(8, mapped.Span[1]);
        Assert.Equal(7, mapped.Span[2]);
        Assert.Equal(6, mapped.Span[3]);
        device.UnmapBuffer(laterDestination);

        var badSource = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopySource,
            });
        var badDestination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var badFirst = device.CreateCommandList(new CommandListDesc { Name = "bad first", QueueType = QueueType.Copy });
        badFirst.Barrier([], [new BufferBarrier(badSource, ResourceState.CopySource, ResourceState.CopyDestination)]);
        var badFirstCommand = badFirst.Finish();

        var badSecond = device.CreateCommandList(new CommandListDesc { Name = "bad second", QueueType = QueueType.Copy });
        badSecond.CopyBuffer(badSource, 0, badDestination, 0, 4);
        var badSecondCommand = badSecond.Finish();

        var invalidSubmit = Assert.Throws<RhiException>(
            () => device.GetQueue(QueueType.Copy).Submit([badFirstCommand, badSecondCommand]));
        Assert.Equal(ErrorCode.ValidationFailure, invalidSubmit.Code);
        Assert.Equal(ResourceState.CopySource, device.GetBufferState(badSource));
    }

    [Fact]
    public void DescriptorLists_AreSnapshotAtCreation()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 256,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            });
        var bufferView = device.CreateBufferView(
            buffer,
            new BufferViewDesc
            {
                Kind = ViewKind.ConstantBuffer,
                SizeInBytes = 256,
            });

        var slots = new[]
        {
            new BindingSlotDesc
            {
                Binding = 0,
                Type = BindingType.ConstantBuffer,
                Stages = ShaderStageFlags.Compute,
            },
        };
        var bindingLayout = device.CreateBindingLayout(slots);
        slots[0] = new BindingSlotDesc
        {
            Binding = 1,
            Type = BindingType.Sampler,
            Stages = ShaderStageFlags.Compute,
        };

        var layouts = new[] { bindingLayout };
        var pipelineLayout = device.CreatePipelineLayout(layouts);
        layouts[0] = default;

        var resources = new[]
        {
            new BindingResourceDesc
            {
                Binding = 0,
                ResourceType = BindingType.ConstantBuffer,
                BufferView = bufferView,
            },
        };
        var bindingSet = device.CreateBindingSet(bindingLayout, new BindingSetDesc { Resources = resources });
        resources[0] = new BindingResourceDesc
        {
            Binding = 1,
            ResourceType = BindingType.Sampler,
        };

        var shader = CreateShader(
            device,
            "cs",
            ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(
            new ComputePipelineDesc
            {
                Layout = pipelineLayout,
                ComputeShader = shader,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet);
        pass.Dispatch(1, 1, 1);
        pass.End();

        device.GetQueue(QueueType.Compute).Submit([list.Finish()]);
    }

    [Fact]
    public void PipelineCreation_UsesExplicitPipelineLayoutWithoutShaderMetadata()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var emptyLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var shader = CreateShader(device, "cs", ShaderStage.Compute);

        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = emptyLayout, ComputeShader = shader });

        Assert.True(pipeline.IsValid);
    }

    [Fact]
    public void FinishedCommandBuffer_BlocksDestroyOfReferencedObjects()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var view = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Layout = layout,
                VertexShader = CreateShader(device, "vs", ShaderStage.Vertex),
                PixelShader = CreateShader(device, "ps", ShaderStage.Pixel),
                ColorFormats = [Format.Rgba8Unorm],
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 16, 16),
                ColorAttachments = [new ColorAttachmentDesc { View = view }],
            });
        pass.SetPipeline(pipeline);
        pass.Draw(3);
        pass.End();
        var commandBuffer = list.Finish();

        var livePipeline = Assert.Throws<RhiException>(() => device.Destroy(pipeline));
        var liveTextureView = Assert.Throws<RhiException>(() => device.Destroy(view));

        Assert.Equal(ErrorCode.ValidationFailure, livePipeline.Code);
        Assert.Equal(ErrorCode.ValidationFailure, liveTextureView.Code);

        device.Destroy(commandBuffer);
        device.Destroy(pipeline);
        device.Destroy(view);
        device.Destroy(texture);
    }

    [Fact]
    public void RenderPass_RejectsIncompatibleGraphicsPipelineFormats()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var view = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Layout = layout,
                VertexShader = CreateShader(device, "vs incompatible", ShaderStage.Vertex),
                PixelShader = CreateShader(device, "ps incompatible", ShaderStage.Pixel),
                ColorFormats = [Format.Rgba16Float],
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 16, 16),
                ColorAttachments = [new ColorAttachmentDesc { View = view }],
            });
        var incompatiblePipeline = Assert.Throws<RhiException>(() => pass.SetPipeline(pipeline));
        Assert.Equal(ErrorCode.ValidationFailure, incompatiblePipeline.Code);
        pass.End();
    }

    [Fact]
    public void RenderPass_RejectsDepthWritePipelineForReadOnlyDepthAndInvalidResolve()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var depth = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                Format = Format.D32Float,
                BindFlags = BindFlags.DepthStencil,
                InitialState = ResourceState.DepthRead,
            });
        var depthView = device.CreateTextureView(
            depth,
            new TextureViewDesc
            {
                Kind = ViewKind.DepthStencil,
                Format = Format.D32Float,
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Layout = layout,
                VertexShader = CreateShader(device, "vs depth", ShaderStage.Vertex),
                PixelShader = CreateShader(device, "ps depth", ShaderStage.Pixel),
                DepthStencilFormat = Format.D32Float,
                DepthStencil = new DepthStencilDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                },
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 16, 16),
                DepthStencilAttachment = new DepthAttachDesc { View = depthView, DepthReadOnly = true },
            });
        var depthWritePipeline = Assert.Throws<RhiException>(() => pass.SetPipeline(pipeline));
        Assert.Equal(ErrorCode.ValidationFailure, depthWritePipeline.Code);
        pass.End();

        var color = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var resolve = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var colorView = device.CreateTextureView(color, new TextureViewDesc { Kind = ViewKind.RenderTarget });
        var resolveView = device.CreateTextureView(resolve, new TextureViewDesc { Kind = ViewKind.RenderTarget });
        var invalidResolveList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var invalidResolve = Assert.Throws<RhiException>(
            () => invalidResolveList.BeginRenderPass(
                new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 16, 16),
                    ColorAttachments = [new ColorAttachmentDesc { View = colorView, ResolveTarget = resolveView }],
                }));
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidResolve.Code);
    }

    [Fact]
    public void CopyToTexture_RequiresAlignedRowPitch()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 1024,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        var rowPitchFailure = Assert.Throws<RhiException>(
            () => list.CopyToTexture(
                source,
                new BufferTextureCopy(0, 16, 64),
                destination,
                new TextureCopyRegion(0, 0, 0, 0, 0, 4, 4, 1)));
        Assert.Equal(ErrorCode.InvalidDescriptor, rowPitchFailure.Code);
    }

    [Fact]
    public void TextureViewAndFormatValidation_RejectsInvalidShapesAndFormats()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        Assert.Equal(3, device.QueueFamilies.Count);
        Assert.Contains(device.QueueFamilies, queue => queue.Type == QueueType.Graphics && queue.Count == 1 && queue.SupportsGraphics);

        var invalidFormat = Assert.Throws<RhiException>(
            () => device.GetFormatCapabilities((Format)999));
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidFormat.Code);
        Assert.True(device.GetFormatSupport(Format.Bgra8Unorm).HasFlag(FormatSupport.RenderTarget));
        Assert.True(device.GetFormatSupport(Format.Rgb10A2Unorm).HasFlag(FormatSupport.RenderTarget));
        Assert.True(device.GetFormatSupport(Format.Bc7RgbaUnorm).HasFlag(FormatSupport.ShaderSample));
        Assert.False(device.GetFormatSupport(Format.Bc7RgbaUnorm).HasFlag(FormatSupport.RenderTarget));

        var invalidMipCount = Assert.Throws<RhiException>(
            () => device.CreateTexture(
                new TextureDesc
                {
                    Width = 4,
                    Height = 4,
                    MipLevels = 8,
                    Format = Format.Rgba8Unorm,
                    BindFlags = BindFlags.ShaderResource,
                }));
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidMipCount.Code);

        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                ArraySize = 2,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            });
        var invalidViewShape = Assert.Throws<RhiException>(
            () => device.CreateTextureView(
                texture,
                new TextureViewDesc
                {
                    Kind = ViewKind.ShaderResource,
                    Dimension = TextureViewDimension.Texture2D,
                    SliceCount = 2,
                }));
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidViewShape.Code);

        var invalidViewFormat = Assert.Throws<RhiException>(
            () => device.CreateTextureView(
                texture,
                new TextureViewDesc
                {
                    Kind = ViewKind.ShaderResource,
                    Format = Format.Rgba16Float,
                }));
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidViewFormat.Code);

        var depthTexture = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                Format = Format.D32Float,
                BindFlags = BindFlags.DepthStencil,
                InitialState = ResourceState.DepthRead,
            });
        var invalidDepthSrv = Assert.Throws<RhiException>(
            () => device.CreateTextureView(
                depthTexture,
                new TextureViewDesc
                {
                    Kind = ViewKind.ShaderResource,
                    Format = Format.D32Float,
                }));
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidDepthSrv.Code);

        var d24Texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 16,
                Height = 16,
                Format = Format.D24UnormS8UInt,
                BindFlags = BindFlags.DepthStencil,
                InitialState = ResourceState.DepthRead,
            });
        var d24View = device.CreateTextureView(
            d24Texture,
            new TextureViewDesc
            {
                Kind = ViewKind.DepthStencil,
                Format = Format.D24UnormS8UInt,
            });
        Assert.True(d24View.IsValid);
    }

    [Fact]
    public void TextureCopies_MoveObservableData()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        byte[] uploadData = new byte[1024];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 16; column++)
                uploadData[(row * 256) + column] = (byte)((row * 16) + column + 1);
        }

        var upload = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 1024,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            uploadData);
        var firstTexture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var secondTexture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 1024,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var region = new TextureCopyRegion(0, 0, 0, 0, 0, 4, 4, 1);
        var layout = new BufferTextureCopy(0, 256, 1024);
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        list.CopyToTexture(upload, layout, firstTexture, region);
        list.Barrier([new TextureBarrier(firstTexture, ResourceState.CopyDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyTexture(firstTexture, region, secondTexture, region);
        list.Barrier([new TextureBarrier(secondTexture, ResourceState.CopyDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(secondTexture, region, readback, layout);

        device.GetQueue(QueueType.Copy).Submit([list.Finish()]);

        var mapped = device.MapBuffer(readback, MapMode.Read);
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 16; column++)
                Assert.Equal(uploadData[(row * 256) + column], mapped.Span[(row * 256) + column]);
        }
        device.UnmapBuffer(readback);
    }

    [Fact]
    public void ResolveTexture_AcceptsMultisampledSourceAndSingleSampleDestination()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                SampleCount = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.ResolveSource,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.ResolveDestination,
            });
        var region = new TextureCopyRegion(0, 0, 0, 0, 0, 4, 4, 1);
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });

        list.ResolveTexture(source, region, destination, region);

        device.GetQueue(QueueType.Graphics).Submit([list.Finish()]);
    }

    [Fact]
    public void ResolveTexture_RejectsPartialRegionOnNullBackend()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                SampleCount = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.ResolveSource,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.ResolveDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });

        var partial = Assert.Throws<RhiException>(
            () => list.ResolveTexture(
                source,
                new TextureCopyRegion(0, 0, 1, 0, 0, 3, 4, 1),
                destination,
                new TextureCopyRegion(0, 0, 0, 0, 0, 3, 4, 1)));

        device.Destroy(list.Finish());
        Assert.Equal(ErrorCode.UnsupportedFeature, partial.Code);
    }

    [Fact]
    public void RenderPassResolve_RejectsPartialRenderAreaOnNullBackend()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                SampleCount = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var sourceView = device.CreateTextureView(
            source,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
                Dimension = TextureViewDimension.Texture2DMultisampled,
            });
        var destinationView = device.CreateTextureView(
            destination,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });

        var partial = Assert.Throws<RhiException>(
            () => list.BeginRenderPass(
                new RenderPassDesc
                {
                    RenderArea = new Rect(0, 0, 2, 4),
                    ColorAttachments = [new ColorAttachmentDesc { View = sourceView, ResolveTarget = destinationView }],
                }));

        device.Destroy(list.Finish());
        Assert.Equal(ErrorCode.UnsupportedFeature, partial.Code);
    }

    [Fact]
    public void TextureCopyRecording_TracksOnlyCopiedSubresource()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var upload = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 256,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4,
                Height = 4,
                MipLevels = 2,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination | BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            });
        var mipOneView = device.CreateTextureView(
            texture,
            new TextureViewDesc
            {
                Kind = ViewKind.ShaderResource,
                FirstMip = 1,
                MipCount = 1,
            });
        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.TextureRead,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var bindingSet = device.CreateBindingSet(
            bindingLayout,
            new BindingSetDesc
            {
                Resources =
                [
                    new BindingResourceDesc
                    {
                        Binding = 0,
                        ResourceType = BindingType.TextureRead,
                        TextureView = mipOneView,
                    },
                ],
            });
        var shader = CreateShader(
            device,
            "cs mip",
            ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        list.Barrier(
            [new TextureBarrier(texture, ResourceState.ShaderResource, ResourceState.CopyDestination, new SubresourceRange(0, 1, 0, 1))],
            []);
        list.CopyToTexture(
            upload,
            new BufferTextureCopy(0, 256, 256),
            texture,
            new TextureCopyRegion(0, 0, 0, 0, 0, 4, 1, 1));
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet);
        pass.Dispatch(1, 1, 1);
        pass.End();

        device.GetQueue(QueueType.Compute).Submit([list.Finish()]);
        Assert.Equal(ResourceState.CopyDestination, device.GetTextureState(texture, 0, 0));
        Assert.Equal(ResourceState.ShaderResource, device.GetTextureState(texture, 1, 0));
    }

    [Fact]
    public void StaticSampler_IsPartOfPipelineLayoutState()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var bindingLayout = device.CreateBindingLayout(new BindingLayoutDesc());
        var pipelineLayout = device.CreatePipelineLayout(
            new PipelineLayoutDesc
            {
                BindingLayouts = [bindingLayout],
                StaticSamplers =
                [
                    new StaticSamplerDesc
                    {
                        Set = 0,
                        Binding = 0,
                        Stages = ShaderStageFlags.Compute,
                        Sampler = new SamplerDesc(),
                    },
                ],
            });
        var shader = CreateShader(
            device,
            "cs static sampler",
            ShaderStage.Compute);

        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.Dispatch(1, 1, 1);
        pass.End();

        device.GetQueue(QueueType.Compute).Submit([list.Finish()]);
    }

    [Fact]
    public void UavBarrier_BlocksDestroyOfReferencedResource()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        list.UavBarrier(buffer);
        var commandBuffer = list.Finish();

        var liveBuffer = Assert.Throws<RhiException>(() => device.Destroy(buffer));

        Assert.Equal(ErrorCode.ValidationFailure, liveBuffer.Code);
        device.Destroy(commandBuffer);
        device.Destroy(buffer);

        var notUav = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
            });
        var invalidUav = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var missingBindFlag = Assert.Throws<RhiException>(() => invalidUav.UavBarrier(notUav));
        Assert.Equal(ErrorCode.InvalidDescriptor, missingBindFlag.Code);
    }

    [Fact]
    public void BufferBarrier_UnorderedAccessToUnorderedAccessActsAsUavDependency()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        list.Barrier([], [new BufferBarrier(buffer, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess)]);
        var commandBuffer = list.Finish();

        var liveBuffer = Assert.Throws<RhiException>(() => device.Destroy(buffer));

        Assert.Equal(ErrorCode.ValidationFailure, liveBuffer.Code);
        device.GetQueue(QueueType.Compute).Submit([commandBuffer]);
        device.Destroy(buffer);

        var notUav = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
            });
        var invalid = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var missingBindFlag = Assert.Throws<RhiException>(
            () => invalid.Barrier([], [new BufferBarrier(notUav, ResourceState.UnorderedAccess, ResourceState.UnorderedAccess)]));

        Assert.Equal(ErrorCode.InvalidDescriptor, missingBindFlag.Code);
    }

    [Fact]
    public void QueueSubmit_EmptyCommandBufferDoesNotAllocateManagedMemory()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Compute);

        var warmupList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        queue.Submit([warmupList.Finish()]);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var commandBuffer = list.Finish();

        long before = GC.GetAllocatedBytesForCurrentThread();
        queue.Submit([commandBuffer]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void QueueSubmit_StatefulCopyDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Copy);

        queue.Submit([CreateCopyCommand(device, 1)]);
        var commandBuffer = CreateCopyCommand(device, 2);

        long before = GC.GetAllocatedBytesForCurrentThread();
        queue.Submit([commandBuffer]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void QueueSubmit_QueryResolveDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Graphics);

        queue.Submit([CreateTimestampResolveCommand(device, "warmup 1")]);
        queue.Submit([CreateTimestampResolveCommand(device, "warmup 2")]);
        var commandBuffer = CreateTimestampResolveCommand(device, "measured");
        ReadOnlySpan<CommandBufferHandle> commands = [commandBuffer];

        long before = GC.GetAllocatedBytesForCurrentThread();
        queue.Submit(commands);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 64, $"Query resolve submit allocated {allocated} bytes.");
    }

    [Fact]
    public void NullTextureBacking_IsAllocatedLazily()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = device.CreateTexture(
            new TextureDesc
            {
                Width = 4096,
                Height = 4096,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4 * 1024 * 1024, $"Texture creation allocated {allocated} bytes.");
    }

    [Fact]
    public void CommandRecording_ReusesOperationStorageAfterCommandBufferDestroy()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 0, 0, 0]);
        var destination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var warmupList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        warmupList.CopyBuffer(source, 0, destination, 0, 4);
        device.Destroy(warmupList.Finish());

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        long before = GC.GetAllocatedBytesForCurrentThread();
        list.CopyBuffer(source, 0, destination, 0, 4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        device.Destroy(list.Finish());
        Assert.True(allocated <= 128, $"Recording one warmed command allocated {allocated} bytes.");
    }

    [Fact]
    public void CommandRecording_ReusesTransientBindingStorageAfterCommandBufferDestroy()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var buffer = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 256,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            });
        var bufferView = device.CreateBufferView(buffer, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var shader = CreateShader(device, "transient allocation", ShaderStage.Compute);
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var resource = new BindingResourceDesc
        {
            Binding = 0,
            ResourceType = BindingType.ConstantBuffer,
            BufferView = bufferView,
        };

        var warmupList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var warmupPass = warmupList.BeginComputePass(new ComputePassDesc());
        warmupPass.SetPipeline(pipeline);
        warmupPass.SetBindings(0, bindingLayout, [resource]);
        warmupPass.End();
        device.Destroy(warmupList.Finish());

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        long before = GC.GetAllocatedBytesForCurrentThread();
        pass.SetBindings(0, bindingLayout, [resource]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        pass.End();

        device.Destroy(list.Finish());
        Assert.True(allocated <= 128, $"Recording warmed transient bindings allocated {allocated} bytes.");
    }

    [Fact]
    public void CommandRecording_ReusesLargeOperationStorageAfterCommandBufferDestroy()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var source = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 0, 0, 0]);
        var destination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var warmupList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        for (int index = 0; index < 12; index++)
            warmupList.CopyBuffer(source, 0, destination, 0, 4);
        device.Destroy(warmupList.Finish());

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 12; index++)
            list.CopyBuffer(source, 0, destination, 0, 4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        device.Destroy(list.Finish());
        Assert.True(allocated <= 1536, $"Recording warmed operation-array growth allocated {allocated} bytes.");
    }

    [Fact]
    public void QueueSubmit_ManyBufferStatesDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Copy);

        queue.Submit([CreateManyCopyCommand(device, 16, 1)]);
        var commandBuffer = CreateManyCopyCommand(device, 16, 2);

        long before = GC.GetAllocatedBytesForCurrentThread();
        queue.Submit([commandBuffer]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 128, $"Submit with many buffer states allocated {allocated} bytes.");
    }

    [Fact]
    public void QueueSubmit_ManyTextureSubresourcesDoesNotAllocateManagedMemoryAfterWarmup()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });
        var queue = device.GetQueue(QueueType.Copy);

        queue.Submit([CreateWideTextureBarrierCommand(device)]);
        var commandBuffer = CreateWideTextureBarrierCommand(device);

        long before = GC.GetAllocatedBytesForCurrentThread();
        queue.Submit([commandBuffer]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated <= 128, $"Submit with many texture subresources allocated {allocated} bytes.");
    }

    [Fact]
    public void QueueSubmit_TextureSmallWriteDoesNotAllocateFullSubresource()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        var upload = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 256,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4096,
                Height = 4096,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        list.CopyToTexture(
            upload,
            new BufferTextureCopy(0, 256, 256),
            texture,
            new TextureCopyRegion(0, 0, 0, 0, 0, 1, 1, 1));
        var commandBuffer = list.Finish();

        long before = GC.GetAllocatedBytesForCurrentThread();
        device.GetQueue(QueueType.Copy).Submit([commandBuffer]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1024 * 1024, $"Small texture write allocated {allocated} bytes.");
    }

    [Fact]
    public void TextureSparseBacking_RepeatedOverwriteKeepsOnlyLatestCoveredSegment()
    {
        using var instance = SomeEngine.Rhi.Instance.Create();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.Null });

        const int writeCount = 32;
        BufferHandle[] uploads = new BufferHandle[writeCount];
        for (int index = 0; index < writeCount; index++)
        {
            uploads[index] = device.CreateBuffer(
                new BufferDesc
                {
                    SizeInBytes = 256,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.CopySource,
                },
                [(byte)index, (byte)(index + 1), (byte)(index + 2), (byte)(index + 3)]);
        }

        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 4096,
                Height = 4096,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 256,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var region = new TextureCopyRegion(0, 0, 0, 0, 0, 1, 1, 1);
        var layout = new BufferTextureCopy(0, 256, 256);
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        for (int index = 0; index < writeCount; index++)
            list.CopyToTexture(uploads[index], layout, texture, region);
        list.Barrier([new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(texture, region, readback, layout);

        device.GetQueue(QueueType.Copy).Submit([list.Finish()]);

        var mapped = device.MapBuffer(readback, MapMode.Read);
        Assert.Equal((byte)(writeCount - 1), mapped.Span[0]);
        Assert.Equal((byte)writeCount, mapped.Span[1]);
        Assert.Equal((byte)(writeCount + 1), mapped.Span[2]);
        Assert.Equal((byte)(writeCount + 2), mapped.Span[3]);
        device.UnmapBuffer(readback);
        Assert.Equal(1, CountTextureSegments(device, texture));
    }

    private static CommandBufferHandle CreateCopyCommand(IDevice device, byte value)
    {
        var source = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [value, 0, 0, 0]);
        var destination = device.CreateBuffer(
            new BufferDesc
            {
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        list.CopyBuffer(source, 0, destination, 0, 4);
        return list.Finish();
    }

    private static CommandBufferHandle CreateManyCopyCommand(IDevice device, int copyCount, byte value)
    {
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        for (int index = 0; index < copyCount; index++)
        {
            var source = device.CreateBuffer(
                new BufferDesc
                {
                    SizeInBytes = 16,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.CopySource,
                },
                [checked((byte)(value + index)), 0, 0, 0]);
            var destination = device.CreateBuffer(
                new BufferDesc
                {
                    SizeInBytes = 16,
                    Memory = MemoryClass.CpuReadback,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                });
            list.CopyBuffer(source, 0, destination, 0, 4);
        }

        return list.Finish();
    }

    private static CommandBufferHandle CreateWideTextureBarrierCommand(IDevice device)
    {
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Width = 32,
                Height = 32,
                MipLevels = 5,
                ArraySize = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopySource,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        list.Barrier([new TextureBarrier(texture, ResourceState.CopySource, ResourceState.CopyDestination, SubresourceRange.All)], []);
        return list.Finish();
    }

    private static int CountTextureSegments(IDevice device, TextureHandle texture)
    {
        var textureStore = device.GetType().GetField("Textures", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!.GetValue(device);
        var textureRecord = textureStore!.GetType().GetMethod("Get")!.Invoke(textureStore, [texture, "Texture"]);
        var subresources = (Array)textureRecord!.GetType().GetField("_subresources", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!.GetValue(textureRecord)!;
        var subresource = subresources.GetValue(0);
        if (subresource == null)
            return 0;

        object? segment = subresource.GetType().GetField("_first", ReflectionBindingFlags.Instance | ReflectionBindingFlags.NonPublic)!.GetValue(subresource);
        int count = 0;
        while (segment != null)
        {
            count++;
            segment = segment.GetType().GetProperty("Next")!.GetValue(segment);
        }

        return count;
    }

    private static byte[] CreateRgba8Pattern(int width, int height, int rowPitch)
    {
        var data = new byte[rowPitch * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * rowPitch) + (x * 4);
                data[offset] = (byte)(16 + x);
                data[offset + 1] = (byte)(32 + y);
                data[offset + 2] = (byte)(64 + x + y);
                data[offset + 3] = 255;
            }
        }

        return data;
    }

    private static void AssertRgba8PatternPixel(ReadOnlySpan<byte> data, int rowPitch, int x, int y)
    {
        var pixel = Rgba8Pixel(data, rowPitch, x, y);
        Assert.Equal((byte)(16 + x), pixel[0]);
        Assert.Equal((byte)(32 + y), pixel[1]);
        Assert.Equal((byte)(64 + x + y), pixel[2]);
        Assert.Equal(255, pixel[3]);
    }

    private static void AssertRgba8PixelNear(ReadOnlySpan<byte> data, int rowPitch, int x, int y, byte r, byte g, byte b, byte a)
    {
        var pixel = Rgba8Pixel(data, rowPitch, x, y);
        Assert.InRange(pixel[0], Math.Max(0, r - 1), Math.Min(255, r + 1));
        Assert.InRange(pixel[1], Math.Max(0, g - 1), Math.Min(255, g + 1));
        Assert.InRange(pixel[2], Math.Max(0, b - 1), Math.Min(255, b + 1));
        Assert.InRange(pixel[3], Math.Max(0, a - 1), Math.Min(255, a + 1));
    }

    private static void AssertRgba8PixelRed(ReadOnlySpan<byte> data, int rowPitch, int x, int y)
    {
        var pixel = Rgba8Pixel(data, rowPitch, x, y);
        Assert.InRange(pixel[0], 200, 255);
        Assert.InRange(pixel[1], 0, 32);
        Assert.InRange(pixel[2], 0, 32);
        Assert.InRange(pixel[3], 200, 255);
    }

    private static ReadOnlySpan<byte> Rgba8Pixel(ReadOnlySpan<byte> data, int rowPitch, int x, int y)
        => data.Slice((y * rowPitch) + (x * 4), 4);

    private static byte[] FloatBytes(params float[] values)
        => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static CommandBufferHandle CreateTimestampResolveCommand(IDevice device, string name)
    {
        const uint queryCount = 8;
        var queryPool = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Name = name, Type = QueryType.Timestamp, Count = queryCount });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = name,
                SizeInBytes = queryCount * sizeof(ulong),
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });

        var list = device.CreateCommandList(new CommandListDesc { Name = name, QueueType = QueueType.Graphics });
        for (uint index = 0; index < queryCount; index++)
            list.WriteTimestamp(queryPool, index);
        list.ResolveQueryData(queryPool, 0, queryCount, readback, 0);
        return list.Finish();
    }

    private static string RequireDxc()
        => D3D12TestEnvironment.FindDxc()
            ?? throw new InvalidOperationException("D3D12 DXC test was discovered without dxc.exe.");

    private static IInstance CreateD3D12Instance()
    {
        if (D3D12TestEnvironment.BackendSkipReason is { } reason)
            throw new InvalidOperationException(reason);

        return SomeEngine.Rhi.Instance.Create(D3D12Backend.Factory);
    }

    private static byte[] CompileComputeShader(string dxc)
        => CompileShader(
            dxc,
            """
            RWByteAddressBuffer Output : register(u0);

            [numthreads(1, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                Output.Store(0, 0x12345678);
            }
            """,
            "cs_6_0",
            "write_uav");

    private static byte[] CompileGraphicsShader(string dxc, ShaderStage stage)
    {
        if (stage == ShaderStage.Vertex)
        {
            return CompileShader(
                dxc,
                """
                float4 main(uint id : SV_VertexID) : SV_Position
                {
                    float2 positions[3] =
                    {
                        float2(0.0, 0.5),
                        float2(0.5, -0.5),
                        float2(-0.5, -0.5),
                    };
                    return float4(positions[id], 0.0, 1.0);
                }
                """,
                "vs_6_0",
                "triangle_vs");
        }

        if (stage == ShaderStage.Pixel)
        {
            return CompileShader(
                dxc,
                """
                float4 main() : SV_Target0
                {
                    return float4(1.0, 0.0, 0.0, 1.0);
                }
                """,
                "ps_6_0",
                "triangle_ps");
        }

        throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
    }

    private static byte[] CompileInstancedOffsetVertexShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct VertexInput
            {
                float2 InstanceOffset : ATTRIB0;
            };

            float4 main(VertexInput input, uint id : SV_VertexID) : SV_Position
            {
                float2 positions[3] =
                {
                    float2(0.0, 0.18),
                    float2(0.18, -0.18),
                    float2(-0.18, -0.18),
                };

                return float4(positions[id] + input.InstanceOffset, 0.0, 1.0);
            }
            """,
            "vs_6_0",
            "instance_rate_vs");

    private static byte[] CompileInstancedColorVertexShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct VertexInput
            {
                float4 InstanceColor : ATTRIB0;
            };

            struct PixelInput
            {
                float4 Position : SV_Position;
                float4 Color : COLOR0;
            };

            PixelInput main(VertexInput input, uint id : SV_VertexID)
            {
                float2 positions[3] =
                {
                    float2(0.0, 0.5),
                    float2(0.5, -0.5),
                    float2(-0.5, -0.5),
                };

                PixelInput output;
                output.Position = float4(positions[id], 0.0, 1.0);
                output.Color = input.InstanceColor;
                return output;
            }
            """,
            "vs_6_0",
            "single_instance_color_vs");

    private static byte[] CompileVertexColorPixelShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct PixelInput
            {
                float4 Position : SV_Position;
                float4 Color : COLOR0;
            };

            float4 main(PixelInput input) : SV_Target0
            {
                return input.Color;
            }
            """,
            "ps_6_0",
            "vertex_color_ps");

    private static byte[] CompileVertexInputShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct VertexInput
            {
                float2 Position : ATTRIB0;
            };

            float4 main(VertexInput input) : SV_Position
            {
                return float4(input.Position, 0.0, 1.0);
            }
            """,
            "vs_6_0",
            "attrib_vertex_input_vs");

    private static byte[] CompileShader(string dxc, string sourceText, string target, string fileName)
    {
        var directory = TestDirectories.CreateTempDir();
        var source = Path.Combine(directory, fileName + ".hlsl");
        var output = Path.Combine(directory, fileName + ".dxil");
        try
        {
            File.WriteAllText(source, sourceText);
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(dxc)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process.StartInfo.ArgumentList.Add("-T");
            process.StartInfo.ArgumentList.Add(target);
            process.StartInfo.ArgumentList.Add("-E");
            process.StartInfo.ArgumentList.Add("main");
            process.StartInfo.ArgumentList.Add("-Fo");
            process.StartInfo.ArgumentList.Add(output);
            process.StartInfo.ArgumentList.Add(source);
            if (!process.Start())
                throw new InvalidOperationException("Failed to start dxc.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"dxc failed with exit code {process.ExitCode}: {stdout}{stderr}");
            return File.ReadAllBytes(output);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static ShaderModuleHandle CreateShader(
        IDevice device,
        string name,
        ShaderStage stage)
        => device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = name,
                Backend = Backend.Null,
                Stage = stage,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = new byte[] { 1, 2, 3, 4 },
            });
}
