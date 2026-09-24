using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SomeEngine.Rhi;
using SomeEngine.Rhi.D3D12;
using SomeEngine.Rhi.Utilities;

namespace SomeEngine.Rhi.Tests;

[Collection(D3D12TestCollection.Name)]
public sealed class D3D12BackendCoverageTests
{
    private const int TextureRowPitch = 256;

    [D3D12Fact]
    public void D3D12Backend_SwapchainClearPresentResizeAndProtectsBackBuffersWhenWindowIsAvailable()
    {
        using var window = HiddenWindow.Create(64, 32);
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });

        var swapchainHandle = device.CreateSwapchain(
            new SwapchainDesc
            {
                Name = "hidden hwnd",
                NativeWindowHandle = window.Handle,
                Width = 64,
                Height = 32,
                Format = Format.Bgra8Unorm,
                BufferCount = 3,
            });
        var swapchain = device.GetSwapchain(swapchainHandle);
        var firstTexture = swapchain.CurrentTexture;
        var firstView = swapchain.CurrentRenderTargetView;

        Assert.Equal(64u, swapchain.Width);
        Assert.Equal(32u, swapchain.Height);
        Assert.Equal(ResourceState.Present, device.GetTextureState(firstTexture));
        Assert.Equal(ResourceOwnership.Swapchain, device.GetTextureAlloc(firstTexture).Ownership);

        var destroyTexture = Assert.Throws<RhiException>(() => device.Destroy(firstTexture));
        var destroyView = Assert.Throws<RhiException>(() => device.Destroy(firstView));
        Assert.Equal(ErrorCode.ValidationFailure, destroyTexture.Code);
        Assert.Equal(ErrorCode.ValidationFailure, destroyView.Code);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier([new TextureBarrier(firstTexture, ResourceState.Present, ResourceState.RenderTarget, SubresourceRange.All)], []);
        var pass = list.BeginRenderPass(
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, 64, 32),
                ColorAttachments =
                [
                    new ColorAttachmentDesc
                    {
                        View = firstView,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = new Color(0.1f, 0.2f, 0.3f, 1.0f),
                    },
                ],
            });
        pass.End();
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "swapchain render target");

        Assert.Equal(ResourceState.RenderTarget, device.GetTextureState(firstTexture));
        var missingPresentBarrier = Assert.Throws<RhiException>(() => swapchain.Present(new PresentDesc { SyncInterval = 1 }));
        Assert.Equal(ErrorCode.ValidationFailure, missingPresentBarrier.Code);

        var presentList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        presentList.Barrier([new TextureBarrier(firstTexture, ResourceState.RenderTarget, ResourceState.Present, SubresourceRange.All)], []);
        SubmitAndWait(device, QueueType.Graphics, presentList.Finish(), "swapchain present");

        swapchain.Present(new PresentDesc { SyncInterval = 1 });
        Assert.Equal(ResourceState.Present, device.GetTextureState(firstTexture));
        Assert.InRange(swapchain.CurrentBackBufferIndex, 0u, 2u);

        var zeroResize = Assert.Throws<RhiException>(() => swapchain.Resize(0, 32));
        Assert.Equal(ErrorCode.InvalidDescriptor, zeroResize.Code);

        swapchain.Resize(80, 40);
        Assert.Equal(80u, swapchain.Width);
        Assert.Equal(40u, swapchain.Height);

        var oldTexture = Assert.Throws<RhiException>(() => device.GetTextureDesc(firstTexture));
        var oldView = Assert.Throws<RhiException>(() => device.Destroy(firstView));
        Assert.Equal(ErrorCode.InvalidHandle, oldTexture.Code);
        Assert.Equal(ErrorCode.InvalidHandle, oldView.Code);

        device.Destroy(swapchainHandle);
    }

    [D3D12Fact]
    public void D3D12Backend_SwapchainValidatesHdrFormatAndAcceptsBorderlessWindowModeWhenWindowIsAvailable()
    {
        using var window = HiddenWindow.Create(64, 32);
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });

        var invalidHdrFormat = Assert.Throws<RhiException>(
            () => device.CreateSwapchain(
                new SwapchainDesc
                {
                    NativeWindowHandle = window.Handle,
                    Width = 64,
                    Height = 32,
                    Format = Format.Bgra8Unorm,
                    ColorSpace = ColorSpace.Hdr10,
                    Hdr10Metadata = TestHdr10Metadata(),
                }));
        var invalidHdr10ScRgbFormat = Assert.Throws<RhiException>(
            () => device.CreateSwapchain(
                new SwapchainDesc
                {
                    NativeWindowHandle = window.Handle,
                    Width = 64,
                    Height = 32,
                    Format = Format.Rgba16Float,
                    ColorSpace = ColorSpace.Hdr10,
                    Hdr10Metadata = TestHdr10Metadata(),
                }));
        var invalidScRgbHdr10Format = Assert.Throws<RhiException>(
            () => device.CreateSwapchain(
                new SwapchainDesc
                {
                    NativeWindowHandle = window.Handle,
                    Width = 64,
                    Height = 32,
                    Format = Format.Rgb10A2Unorm,
                    ColorSpace = ColorSpace.ScRgbLinear,
                }));

        var borderless = device.CreateSwapchain(
            new SwapchainDesc
            {
                NativeWindowHandle = window.Handle,
                Width = 64,
                Height = 32,
                Format = Format.Bgra8Unorm,
                Mode = SwapchainMode.BorderlessFullscreen,
                AllowTearing = false,
            });
        var swapchain = device.GetSwapchain(borderless);

        Assert.Equal(ErrorCode.InvalidDescriptor, invalidHdrFormat.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidHdr10ScRgbFormat.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidScRgbHdr10Format.Code);
        Assert.Equal(64u, swapchain.Width);
        Assert.Equal(32u, swapchain.Height);
        Assert.NotEqual(TextureHandle.Invalid, swapchain.CurrentTexture);

        device.Destroy(borderless);
    }

    [D3D12Fact]
    public void D3D12Backend_AccelerationStructureBuildAndCopyExecuteWhenRayTracingIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var buildTemplate = new AccelBuildDesc
        {
            Kind = AccelerationStructureKind.BottomLevel,
            Geometries =
            [
                new AccelGeomDesc
                {
                    Kind = AccelGeomKind.Triangles,
                    VertexFormat = Format.Rgb32Float,
                    VertexStrideInBytes = 3 * sizeof(float),
                    VertexCount = 3,
                },
            ],
        };

        if (!device.Features.RayTracing)
        {
            var unsupported = Assert.Throws<RhiException>(() => device.Get<IRtDevice>()!.GetAccelSizes(buildTemplate));
            Assert.Equal(ErrorCode.UnsupportedFeature, unsupported.Code);
            return;
        }

        var vertices = device.CreateBuffer(
            new BufferDesc
            {
                Name = "blas vertices",
                SizeInBytes = 3 * 3 * sizeof(float),
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 3 * sizeof(float),
            },
            FloatBytes(-1.0f, -1.0f, 0.0f, 1.0f, -1.0f, 0.0f, 0.0f, 1.0f, 0.0f));
        var sizes = device.Get<IRtDevice>()!.GetAccelSizes(
            buildTemplate with
            {
                Geometries =
                [
                    buildTemplate.Geometries[0] with { VertexBuffer = vertices },
                ],
            });
        var source = device.Get<IRtDevice>()!.CreateAccelerationStructure(
            new AccelerationStructureDesc
            {
                Name = "blas source",
                Kind = AccelerationStructureKind.BottomLevel,
                SizeInBytes = sizes.AccelerationStructureSizeInBytes,
            });
        var destination = device.Get<IRtDevice>()!.CreateAccelerationStructure(
            new AccelerationStructureDesc
            {
                Name = "blas copy",
                Kind = AccelerationStructureKind.BottomLevel,
                SizeInBytes = sizes.AccelerationStructureSizeInBytes,
            });
        var tooSmall = device.Get<IRtDevice>()!.CreateAccelerationStructure(
            new AccelerationStructureDesc
            {
                Name = "blas too small",
                Kind = AccelerationStructureKind.BottomLevel,
                SizeInBytes = sizes.AccelerationStructureSizeInBytes - device.Limits.AccelerationStructureAlignment,
            });
        var scratch = device.CreateBuffer(
            new BufferDesc
            {
                Name = "blas scratch",
                SizeInBytes = sizes.BuildScratchSizeInBytes,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
            });
        var buildDesc = new AccelBuildDesc
        {
            Kind = AccelerationStructureKind.BottomLevel,
            Destination = source,
            ScratchBuffer = scratch,
            Geometries =
            [
                new AccelGeomDesc
                {
                    Kind = AccelGeomKind.Triangles,
                    VertexFormat = Format.Rgb32Float,
                    VertexStrideInBytes = 3 * sizeof(float),
                    VertexCount = 3,
                    VertexBuffer = vertices,
                },
            ],
        };

        var misalignedList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var misalignedScratch = Assert.Throws<RhiException>(
            () => misalignedList.BuildAccelerationStructure(buildDesc with { ScratchOffset = 16 }));
        device.Destroy(misalignedList.Finish());
        Assert.Equal(ErrorCode.InvalidDescriptor, misalignedScratch.Code);

        var tooSmallList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var tooSmallDestination = Assert.Throws<RhiException>(
            () => tooSmallList.BuildAccelerationStructure(buildDesc with { Destination = tooSmall }));
        device.Destroy(tooSmallList.Finish());
        Assert.Equal(ErrorCode.InvalidDescriptor, tooSmallDestination.Code);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.BuildAccelerationStructure(buildDesc);
        list.CopyAccelerationStructure(source, destination, AccelCopyMode.Clone);
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "d3d12 blas build copy");
    }

    [D3D12DxcFact]
    public void D3D12Backend_PipelineCacheSerializesAndReloadsComputePipelineWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "pipeline cache cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileNoopComputeShader(dxc),
            });

        var cache = device.Get<ICacheDevice>()!.CreatePipelineCache(new PipelineCacheDesc { Name = "cold cache" });
        var firstPipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader, PipelineCache = cache });
        var data = device.Get<ICacheDevice>()!.GetPipelineData(cache);

        Assert.NotEmpty(data);

        var warmCache = device.Get<ICacheDevice>()!.CreatePipelineCache(new PipelineCacheDesc { Name = "warm cache", InitialData = data });
        var secondPipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader, PipelineCache = warmCache });

        Assert.NotEqual(PipelineHandle.Invalid, firstPipeline);
        Assert.NotEqual(PipelineHandle.Invalid, secondPipeline);
        Assert.NotEmpty(device.Get<ICacheDevice>()!.GetPipelineData(warmCache));
    }

    [D3D12DxcFact]
    public void D3D12Backend_SetBindingsRejectsIncompatibleAndMissingPipelineLayoutSetsWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var pipelineBindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.Sampler,
                        Stages = ShaderStageFlags.Compute,
                    },
                ],
            });
        var incompatibleLayout = device.CreateBindingLayout(new BindingLayoutDesc());
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [pipelineBindingLayout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "layout compatibility noop cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileNoopComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var incompatibleSet = device.CreateBindingSet(incompatibleLayout, new BindingSetDesc());

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);

        var transientMismatch = Assert.Throws<RhiException>(() => pass.SetBindings(0, incompatibleLayout, []));
        var persistentMismatch = Assert.Throws<RhiException>(() => pass.SetBindingSet(0, incompatibleSet));
        var transientMissingSet = Assert.Throws<RhiException>(() => pass.SetBindings(1, pipelineBindingLayout, []));
        var persistentMissingSet = Assert.Throws<RhiException>(() => pass.SetBindingSet(1, incompatibleSet));

        Assert.Equal(ErrorCode.InvalidDescriptor, transientMismatch.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, persistentMismatch.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, transientMissingSet.Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, persistentMissingSet.Code);

        pass.End();
        device.Destroy(list.Finish());
    }

    [D3D12DxcFact]
    public void D3D12Backend_SetPipelineRejectsRenderPassCompatibilityMismatchesWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        const int Width = 16;
        const int Height = 16;

        var color = device.CreateTexture(
            new TextureDesc
            {
                Name = "compat color",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var colorView = device.CreateTextureView(
            color,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Format = Format.Rgba8Unorm,
            });
        var colorMsaa = device.CreateTexture(
            new TextureDesc
            {
                Name = "compat color msaa",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                SampleCount = 4,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
            });
        var colorMsaaView = device.CreateTextureView(
            colorMsaa,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Dimension = TextureViewDimension.Texture2DMultisampled,
                Format = Format.Rgba8Unorm,
            });
        var depth = device.CreateTexture(
            new TextureDesc
            {
                Name = "compat depth",
                Width = Width,
                Height = Height,
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
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "compat vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGeneratedTriangleVertexShader(dxc),
            });
        var redPixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "compat red ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileRedPixelShader(dxc),
            });
        var emptyPixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "compat empty ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileEmptyPixelShader(dxc),
            });
        var colorFormatMismatch = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "compat color format mismatch",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = redPixel,
                ColorFormats = [Format.Rgba16Float],
            });
        var colorCountMismatch = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "compat color count mismatch",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = redPixel,
                ColorFormats = [Format.Rgba8Unorm, Format.Rgba8Unorm],
            });
        var sampleMismatch = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "compat sample mismatch",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = redPixel,
                ColorFormats = [Format.Rgba8Unorm],
            });
        var depthFormatMismatch = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "compat depth format mismatch",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = redPixel,
                ColorFormats = [Format.Rgba8Unorm],
            });
        var depthWriteMismatch = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "compat depth write mismatch",
                Layout = layout,
                VertexShader = vertex,
                PixelShader = emptyPixel,
                DepthStencilFormat = Format.D32Float,
                DepthStencil = new DepthStencilDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                },
            });

        AssertSetPipelineValidationFailure(
            device,
            colorFormatMismatch,
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments = [new ColorAttachmentDesc { View = colorView }],
            });
        AssertSetPipelineValidationFailure(
            device,
            colorCountMismatch,
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments = [new ColorAttachmentDesc { View = colorView }],
            });
        AssertSetPipelineValidationFailure(
            device,
            sampleMismatch,
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments = [new ColorAttachmentDesc { View = colorMsaaView }],
            });
        AssertSetPipelineValidationFailure(
            device,
            depthFormatMismatch,
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                ColorAttachments = [new ColorAttachmentDesc { View = colorView }],
                DepthStencilAttachment = new DepthAttachDesc { View = depthView, DepthReadOnly = true },
            });
        AssertSetPipelineValidationFailure(
            device,
            depthWriteMismatch,
            new RenderPassDesc
            {
                RenderArea = new Rect(0, 0, Width, Height),
                DepthStencilAttachment = new DepthAttachDesc { View = depthView, DepthReadOnly = true },
            });
    }

    [D3D12DxcFact]
    public void D3D12Backend_MeshPipelineDispatchesDirectAndIndirectWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        if (!device.Features.MeshShader)
            return;

        const int Width = 64;
        const int Height = 64;
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "mesh target",
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
                Name = "mesh readback",
                SizeInBytes = TextureRowPitch * Height,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var arguments = device.CreateBuffer(
            new BufferDesc
            {
                Name = "mesh indirect arguments",
                SizeInBytes = IndirectArgumentSize.Dispatch,
                BindFlags = BindFlags.IndirectArgument,
                InitialState = ResourceState.IndirectArgument,
            },
            UIntBytes(1, 1, 1));
        var mesh = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "mesh shader",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Mesh,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileFullscreenMeshShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "mesh ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileRedPixelShader(dxc),
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateMeshPipeline(
            new MeshPipelineDesc
            {
                Name = "mesh pipeline",
                Layout = pipelineLayout,
                MeshShader = mesh,
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
        pass.SetViewport(new Viewport(0, 0, Width, Height));
        pass.SetScissor(new Rect(0, 0, Width, Height));
        pass.SetPipeline(pipeline);
        pass.DispatchMesh(1, 1, 1);
        pass.DispatchMeshIndirect(new IndirectDispatchDesc { Arguments = arguments });
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(target, new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1), readback, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * Height));
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "mesh dispatch");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, TextureRowPitch * Height);
        AssertRgba8PixelRed(mapped.Span, TextureRowPitch, Width / 2, Height / 2);
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_RayTracingPipelineCreatesStateObjectAndShaderIdentifiersWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        if (!device.Features.RayTracing)
            return;

        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "rt acceleration structure bindings",
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.AccelerationStructure,
                        Stages = ShaderStageFlags.AllRayTracing,
                    },
                ],
            });
        var layout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var raygen = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "rt raygen",
                Backend = Backend.D3D12,
                Stage = ShaderStage.RayGeneration,
                EntryPoint = "RayGen",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileRayGenerationLibrary(dxc),
            });
        var miss = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "rt miss",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Miss,
                EntryPoint = "Miss",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileMissLibrary(dxc),
            });
        var closestHit = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "rt closest hit",
                Backend = Backend.D3D12,
                Stage = ShaderStage.ClosestHit,
                EntryPoint = "ClosestHit",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileClosestHitLibrary(dxc),
            });
        var desc = new RtPipelineDesc
        {
            Name = "rt pipeline",
            Layout = layout,
            MaxPayloadSizeInBytes = 16,
            Shaders = [raygen, miss, closestHit],
            ShaderGroups =
            [
                new RtGroupDesc
                {
                    Name = "RayGenGroup",
                    Kind = RtGroupKind.General,
                    GeneralShader = raygen,
                },
                new RtGroupDesc
                {
                    Name = "MissGroup",
                    Kind = RtGroupKind.General,
                    GeneralShader = miss,
                },
                new RtGroupDesc
                {
                    Name = "HitGroup",
                    Kind = RtGroupKind.TrianglesHitGroup,
                    ClosestHitShader = closestHit,
                },
            ],
        };
        var pipeline = device.Get<IRtDevice>()!.CreateRtPipeline(desc);
        var identifier = new byte[device.Get<IRtDevice>()!.GetRtSize(pipeline)];
        device.Get<IRtDevice>()!.GetRtId(pipeline, "RayGenGroup", identifier);
        var buildSizes = device.Get<IRtDevice>()!.GetAccelSizes(
            new AccelBuildDesc
            {
                Kind = AccelerationStructureKind.BottomLevel,
                Geometries =
                [
                    new AccelGeomDesc
                    {
                        Kind = AccelGeomKind.Triangles,
                        VertexFormat = Format.Rgb32Float,
                        VertexStrideInBytes = 3 * sizeof(float),
                        VertexCount = 3,
                    },
                ],
            });
        var accelerationStructure = device.Get<IRtDevice>()!.CreateAccelerationStructure(
            new AccelerationStructureDesc
            {
                Name = "rt bound blas",
                Kind = AccelerationStructureKind.BottomLevel,
                SizeInBytes = buildSizes.AccelerationStructureSizeInBytes,
            });
        var bindingSet = device.CreateBindingSet(
            bindingLayout,
            new BindingSetDesc
            {
                Resources = [BindingResourceDesc.AccelerationStructureBinding(0, accelerationStructure)],
            });
        var shaderTableBytes = new byte[checked((int)device.Limits.RayTracingShaderTableAlignment)];
        identifier.CopyTo(shaderTableBytes.AsSpan());
        var shaderTable = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt raygen shader table",
                SizeInBytes = (ulong)shaderTableBytes.Length,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            },
            shaderTableBytes);
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRtPass(new RtPassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet);
        pass.TraceRays(
            new ShaderTableDesc
            {
                RayGeneration = new ShaderTableRegion(shaderTable, 0, device.Limits.RayTracingShaderRecordAlignment, 0),
            },
            1,
            1,
            1);
        pass.End();
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "rt acceleration structure binding");
        var cache = device.Get<ICacheDevice>()!.CreatePipelineCache(new PipelineCacheDesc { Name = "rt cache" });
        var cachedRayTracingPipeline = Assert.Throws<RhiException>(() => device.Get<IRtDevice>()!.CreateRtPipeline(desc with { PipelineCache = cache }));

        Assert.Contains(identifier, value => value != 0);
        Assert.Equal(ErrorCode.UnsupportedFeature, cachedRayTracingPipeline.Code);
    }

    [D3D12DxcFact]
    public void D3D12Backend_RayTracingTraceRayConsumesTlasAndWritesUavWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        if (!device.Features.RayTracing)
            return;

        var vertices = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt traversal triangle",
                SizeInBytes = 3 * 3 * sizeof(float),
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
                StrideInBytes = 3 * sizeof(float),
            },
            FloatBytes(-1.0f, -1.0f, 0.0f, 1.0f, -1.0f, 0.0f, 0.0f, 1.0f, 0.0f));
        var blasBuild = new AccelBuildDesc
        {
            Kind = AccelerationStructureKind.BottomLevel,
            Geometries =
            [
                new AccelGeomDesc
                {
                    Kind = AccelGeomKind.Triangles,
                    VertexFormat = Format.Rgb32Float,
                    VertexStrideInBytes = 3 * sizeof(float),
                    VertexCount = 3,
                    VertexBuffer = vertices,
                },
            ],
        };
        var blasSizes = device.Get<IRtDevice>()!.GetAccelSizes(blasBuild);
        var blas = device.Get<IRtDevice>()!.CreateAccelerationStructure(
            new AccelerationStructureDesc
            {
                Name = "rt traversal blas",
                Kind = AccelerationStructureKind.BottomLevel,
                SizeInBytes = blasSizes.AccelerationStructureSizeInBytes,
            });
        var blasScratch = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt traversal blas scratch",
                SizeInBytes = blasSizes.BuildScratchSizeInBytes,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
            });

        var interop = device.Get<ID3D12DeviceInterop>()!;
        var instances = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt traversal instances",
                SizeInBytes = 64,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            },
            D3D12RayTracingInstanceBytes(interop.GetNativeAcceleration(blas).GPUVirtualAddress));
        var tlasBuild = new AccelBuildDesc
        {
            Kind = AccelerationStructureKind.TopLevel,
            Geometries =
            [
                new AccelGeomDesc
                {
                    Kind = AccelGeomKind.Instances,
                    InstanceBuffer = instances,
                    InstanceCount = 1,
                },
            ],
        };
        var tlasSizes = device.Get<IRtDevice>()!.GetAccelSizes(tlasBuild);
        var tlas = device.Get<IRtDevice>()!.CreateAccelerationStructure(
            new AccelerationStructureDesc
            {
                Name = "rt traversal tlas",
                Kind = AccelerationStructureKind.TopLevel,
                SizeInBytes = tlasSizes.AccelerationStructureSizeInBytes,
            });
        var tlasScratch = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt traversal tlas scratch",
                SizeInBytes = tlasSizes.BuildScratchSizeInBytes,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
            });

        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "rt traversal bindings",
                Slots =
                [
                    new BindingSlotDesc { Binding = 0, Type = BindingType.AccelerationStructure, Stages = ShaderStageFlags.AllRayTracing },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.RawBufferReadWrite, Stages = ShaderStageFlags.AllRayTracing },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var raygen = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "rt traversal raygen",
                Backend = Backend.D3D12,
                Stage = ShaderStage.RayGeneration,
                EntryPoint = "RayGen",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileTraceRayGenerationLibrary(dxc),
            });
        var miss = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "rt traversal miss",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Miss,
                EntryPoint = "Miss",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileTraceRayMissLibrary(dxc),
            });
        var closestHit = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "rt traversal closest hit",
                Backend = Backend.D3D12,
                Stage = ShaderStage.ClosestHit,
                EntryPoint = "ClosestHit",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileTraceRayClosestHitLibrary(dxc),
            });
        var pipeline = device.Get<IRtDevice>()!.CreateRtPipeline(
            new RtPipelineDesc
            {
                Name = "rt traversal pipeline",
                Layout = pipelineLayout,
                MaxPayloadSizeInBytes = 4,
                Shaders = [raygen, miss, closestHit],
                ShaderGroups =
                [
                    new RtGroupDesc { Name = "RayGenGroup", Kind = RtGroupKind.General, GeneralShader = raygen },
                    new RtGroupDesc { Name = "MissGroup", Kind = RtGroupKind.General, GeneralShader = miss },
                    new RtGroupDesc { Name = "HitGroup", Kind = RtGroupKind.TrianglesHitGroup, ClosestHitShader = closestHit },
                ],
            });

        var output = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt traversal output",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            });
        var outputView = device.CreateBufferView(output, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt traversal readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var bindingSet = device.CreateBindingSet(
            bindingLayout,
            new BindingSetDesc
            {
                Resources =
                [
                    BindingResourceDesc.AccelerationStructureBinding(0, tlas),
                    new BindingResourceDesc { Binding = 1, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView },
                ],
            });
        var shaderTable = device.CreateBuffer(
            new BufferDesc
            {
                Name = "rt traversal shader table",
                SizeInBytes = device.Limits.RayTracingShaderTableAlignment * 3,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ShaderResource,
                InitialState = ResourceState.ShaderResource,
            },
            RayTracingShaderTableBytes(device, pipeline));

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.BuildAccelerationStructure(blasBuild with { Destination = blas, ScratchBuffer = blasScratch });
        list.BuildAccelerationStructure(tlasBuild with { Destination = tlas, ScratchBuffer = tlasScratch });
        var pass = list.BeginRtPass(new RtPassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet);
        pass.TraceRays(
            new ShaderTableDesc
            {
                RayGeneration = new ShaderTableRegion(shaderTable, 0, device.Limits.RayTracingShaderRecordAlignment, 0),
                Miss = new ShaderTableRegion(shaderTable, device.Limits.RayTracingShaderTableAlignment, device.Limits.RayTracingShaderRecordAlignment, device.Limits.RayTracingShaderRecordAlignment),
                HitGroup = new ShaderTableRegion(shaderTable, device.Limits.RayTracingShaderTableAlignment * 2, device.Limits.RayTracingShaderRecordAlignment, device.Limits.RayTracingShaderRecordAlignment),
            },
            1,
            1,
            1);
        pass.End();
        list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
        list.CopyBuffer(output, 0, readback, 0, 4);
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "rt traversal");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
        Assert.Equal(1u, BitConverter.ToUInt32(mapped.Span));
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_MipGeneratorBuildsMipChainWithCoreCommandsWhenAdapterAndDxcAreAvailable()
    {
        const int Width = 4;
        const int Height = 4;
        const int MipCount = 3;
        const int ArraySize = 2;
        const int SlicePitch = TextureRowPitch * Height;

        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        if (!device.GetFormatCapabilities(Format.Rgba32Float).Support.HasFlag(FormatSupport.UnorderedAccess))
            throw new InvalidOperationException("D3D12 RGBA32_FLOAT UAV support is required for this mip-generation test.");

        var uploadBytes = MipGeneratorUploadBytesRgba32Float();
        var arrayUploadBytes = MipGeneratorUploadBytesRgba32FloatArray(ArraySize);
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "mip source upload",
                SizeInBytes = (ulong)uploadBytes.Length,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            uploadBytes);
        var arrayUpload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "mip array source upload",
                SizeInBytes = (ulong)arrayUploadBytes.Length,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            arrayUploadBytes);
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "generated mip texture",
                Width = Width,
                Height = Height,
                MipLevels = MipCount,
                Format = Format.Rgba32Float,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.CopyDestination,
            });
        var arrayTexture = device.CreateTexture(
            new TextureDesc
            {
                Name = "generated mip array texture",
                Width = Width,
                Height = Height,
                MipLevels = MipCount,
                ArraySize = ArraySize,
                Format = Format.Rgba32Float,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess | BindFlags.CopyDestination | BindFlags.CopySource,
                InitialState = ResourceState.CopyDestination,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "mip readback",
                SizeInBytes = TextureRowPitch,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var arrayReadback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "mip array readback",
                SizeInBytes = TextureRowPitch,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        using var generator = new MipGenerator(
            device,
            new MipGeneratorDesc
            {
                Texture2DShader = new ShaderModuleDesc
                {
                    Name = "mip generator 2d",
                    Backend = Backend.D3D12,
                    Stage = ShaderStage.Compute,
                    EntryPoint = "main",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = CompileMipGeneratorTexture2DShader(dxc),
                },
                Texture2DArrayShader = new ShaderModuleDesc
                {
                    Name = "mip generator 2d array",
                    Backend = Backend.D3D12,
                    Stage = ShaderStage.Compute,
                    EntryPoint = "main",
                    BytecodeFormat = ShaderBytecodeFormat.Dxil,
                    Bytecode = CompileMipGeneratorTexture2DArrayShader(dxc),
                },
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.CopyToTexture(
            upload,
            new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * Height),
            texture,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1));
        for (uint slice = 0; slice < ArraySize; slice++)
        {
            list.CopyToTexture(
                arrayUpload,
                new BufferTextureCopy(checked((ulong)slice * (ulong)SlicePitch), TextureRowPitch, SlicePitch),
                arrayTexture,
                new TextureCopyRegion(0, slice, 0, 0, 0, Width, Height, 1));
        }

        generator.GenerateMips(
            list,
            new GenerateMipsDesc
            {
                Texture = texture,
                FinalState = ResourceState.CopySource,
            });
        generator.GenerateMips(
            list,
            new GenerateMipsDesc
            {
                Texture = arrayTexture,
                FinalState = ResourceState.CopySource,
            });
        list.CopyToBuffer(
            texture,
            new TextureCopyRegion(2, 0, 0, 0, 0, 1, 1, 1),
            readback,
            new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch));
        list.CopyToBuffer(
            arrayTexture,
            new TextureCopyRegion(2, 1, 0, 0, 0, 1, 1, 1),
            arrayReadback,
            new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch));

        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "mip generator");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, TextureRowPitch);
        var floats = MemoryMarshal.Cast<byte, float>(mapped.Span);
        Assert.InRange(floats[0], 7.49f, 7.51f);
        Assert.Equal(0.0f, floats[1]);
        Assert.Equal(0.0f, floats[2]);
        Assert.InRange(floats[3], 0.99f, 1.01f);
        device.UnmapBuffer(readback);

        var arrayMapped = device.MapBuffer(arrayReadback, MapMode.Read, 0, TextureRowPitch);
        var arrayFloats = MemoryMarshal.Cast<byte, float>(arrayMapped.Span);
        Assert.InRange(arrayFloats[0], 107.49f, 107.51f);
        Assert.Equal(0.0f, arrayFloats[1]);
        Assert.Equal(0.0f, arrayFloats[2]);
        Assert.InRange(arrayFloats[3], 0.99f, 1.01f);
        device.UnmapBuffer(arrayReadback);
    }

    [D3D12Fact]
    public void D3D12Backend_ImportsExternalNativeResourcesWithoutTakingOwnership()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var interop = device.Get<ID3D12DeviceInterop>()!;
        var bufferDesc = new BufferDesc
        {
            Name = "owned buffer",
            SizeInBytes = 64,
            BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopyDestination,
        };
        var textureDesc = new TextureDesc
        {
            Name = "owned texture",
            Width = 4,
            Height = 4,
            Format = Format.Rgba8Unorm,
            BindFlags = BindFlags.ShaderResource | BindFlags.CopyDestination,
            InitialState = ResourceState.CopyDestination,
        };
        var ownedBuffer = device.CreateBuffer(bufferDesc);
        var ownedTexture = device.CreateTexture(textureDesc);
        var nativeBuffer = interop.GetNativeBuffer(ownedBuffer);
        var nativeTexture = interop.GetNativeTexture(ownedTexture);

        var importedBuffer = interop.ImportBuffer(
            new ExternalBufferDesc
            {
                Name = "imported buffer",
                Resource = nativeBuffer,
                Desc = bufferDesc with { Name = "imported buffer" },
            });
        var importedTexture = interop.ImportTexture(
            new ExternalTextureDesc
            {
                Name = "imported texture",
                Resource = nativeTexture,
                Desc = textureDesc with { Name = "imported texture" },
            });

        Assert.Same(nativeBuffer, interop.GetNativeBuffer(importedBuffer));
        Assert.Same(nativeTexture, interop.GetNativeTexture(importedTexture));
        Assert.Equal(ResourceOwnership.External, device.GetBufferAlloc(importedBuffer).Ownership);
        Assert.Equal(ResourceOwnership.External, device.GetTextureAlloc(importedTexture).Ownership);

        device.Destroy(importedBuffer);
        device.Destroy(importedTexture);
        Assert.Equal(ResourceOwnership.Committed, device.GetBufferAlloc(ownedBuffer).Ownership);
        Assert.Equal(ResourceOwnership.Committed, device.GetTextureAlloc(ownedTexture).Ownership);
    }

    [D3D12Fact]
    public void D3D12Backend_FinishFailureWithOpenPass_ReleasesActiveListReferences()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
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
    }

    [D3D12Fact]
    public void D3D12Backend_PartiallyBoundTextureSlotsUseDeclaredNullDescriptorShape()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });

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
        device.Destroy(set);
        device.Destroy(layout);

        var samplerLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.Sampler,
                        Stages = ShaderStageFlags.Compute,
                        Count = 2,
                        Flags = BindingFlags.PartiallyBound,
                        Shape = new BindShapeDesc { Sampler = new SamplerDesc { Compare = CompareOp.LessOrEqual } },
                    },
                ],
            });
        var samplerSet = device.CreateBindingSet(samplerLayout, new BindingSetDesc());

        Assert.True(samplerSet.IsValid);
        device.Destroy(samplerSet);
        device.Destroy(samplerLayout);
    }

    [D3D12DxcFact]
    public void D3D12Backend_ComputeExecutesPersistentAndTransientDescriptorTablesWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });

        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "descriptor execution",
                Slots =
                [
                    new BindingSlotDesc { Binding = 0, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.RawBufferRead, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 2, Type = BindingType.RawBufferReadWrite, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 3, Type = BindingType.TextureRead, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 4, Type = BindingType.Sampler, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 5, Type = BindingType.TextureReadWrite, Stages = ShaderStageFlags.Compute },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(
            new PipelineLayoutDesc
            {
                BindingLayouts = [bindingLayout],
                PushConstants = [new PushRangeDesc { Stages = ShaderStageFlags.Compute, Offset = 0, SizeInBytes = 16 }],
            });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "descriptor execution cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileDescriptorComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });

        RunDescriptorCompute(usePersistentSet: true, pushValue: 5, expectedValue: 70);
        RunDescriptorCompute(usePersistentSet: false, pushValue: 6, expectedValue: 71);

        void RunDescriptorCompute(bool usePersistentSet, uint pushValue, uint expectedValue)
        {
            var constants = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "descriptor constants",
                    SizeInBytes = 256,
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = BindFlags.ConstantBuffer,
                    InitialState = ResourceState.ConstantBuffer,
                },
                ConstantBufferBytes(37));
            var constantsView = device.CreateBufferView(constants, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
            var input = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "descriptor input",
                    SizeInBytes = 16,
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = BindFlags.ShaderResource,
                    InitialState = ResourceState.ShaderResource,
                    Raw = true,
                },
                UIntBytes(11));
            var inputView = device.CreateBufferView(input, new BufferViewDesc { Kind = ViewKind.ShaderResource, SizeInBytes = 16, Raw = true });
            var output = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "descriptor output",
                    SizeInBytes = 16,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                    InitialState = ResourceState.UnorderedAccess,
                    Raw = true,
                });
            var outputView = device.CreateBufferView(output, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
            var sourceUpload = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "descriptor texture upload",
                    SizeInBytes = TextureRowPitch,
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.CopySource,
                },
                Rgba8Upload(17, 0, 0, 255));
            var sourceTexture = device.CreateTexture(
                new TextureDesc
                {
                    Name = "descriptor source texture",
                    Width = 1,
                    Height = 1,
                    Format = Format.Rgba8Unorm,
                    BindFlags = BindFlags.CopyDestination | BindFlags.ShaderResource,
                    InitialState = ResourceState.CopyDestination,
                });
            var sourceView = device.CreateTextureView(sourceTexture, new TextureViewDesc { Kind = ViewKind.ShaderResource, Format = Format.Rgba8Unorm });
            var outputTexture = device.CreateTexture(
                new TextureDesc
                {
                    Name = "descriptor output texture",
                    Width = 1,
                    Height = 1,
                    Format = Format.Rgba8Unorm,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                    InitialState = ResourceState.UnorderedAccess,
                });
            var outputTextureView = device.CreateTextureView(outputTexture, new TextureViewDesc { Kind = ViewKind.UnorderedAccess, Format = Format.Rgba8Unorm });
            var sampler = device.CreateSampler(new SamplerDesc { MinFilter = FilterMode.Nearest, MagFilter = FilterMode.Nearest });
            var outputReadback = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "descriptor output readback",
                    SizeInBytes = 16,
                    Memory = MemoryClass.CpuReadback,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                });
            var textureReadback = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "descriptor texture readback",
                    SizeInBytes = TextureRowPitch,
                    Memory = MemoryClass.CpuReadback,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                });
            var resources = new[]
            {
                new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = constantsView },
                new BindingResourceDesc { Binding = 1, ResourceType = BindingType.RawBufferRead, BufferView = inputView },
                new BindingResourceDesc { Binding = 2, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView },
                new BindingResourceDesc { Binding = 3, ResourceType = BindingType.TextureRead, TextureView = sourceView },
                new BindingResourceDesc { Binding = 4, ResourceType = BindingType.Sampler, SamplerHandle = sampler },
                new BindingResourceDesc { Binding = 5, ResourceType = BindingType.TextureReadWrite, TextureView = outputTextureView },
            };

            BindingSetHandle bindingSet = default;
            if (usePersistentSet)
                bindingSet = device.CreateBindingSet(bindingLayout, new BindingSetDesc { Resources = resources });

            var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
            list.CopyToTexture(
                sourceUpload,
                new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch),
                sourceTexture,
                new TextureCopyRegion(0, 0, 0, 0, 0, 1, 1, 1));
            list.Barrier([new TextureBarrier(sourceTexture, ResourceState.CopyDestination, ResourceState.ShaderResource, SubresourceRange.All)], []);
            var pass = list.BeginComputePass(new ComputePassDesc());
            pass.SetPipeline(pipeline);
            if (usePersistentSet)
                pass.SetBindingSet(0, bindingSet);
            else
                pass.SetBindings(0, bindingLayout, resources);
            pass.SetPushConstants(ShaderStageFlags.Compute, 0, PushConstantBytes(pushValue));
            pass.Dispatch(1, 1, 1);
            pass.End();
            list.Barrier(
                [new TextureBarrier(outputTexture, ResourceState.UnorderedAccess, ResourceState.CopySource, SubresourceRange.All)],
                [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
            list.CopyBuffer(output, 0, outputReadback, 0, 4);
            list.CopyToBuffer(
                outputTexture,
                new TextureCopyRegion(0, 0, 0, 0, 0, 1, 1, 1),
                textureReadback,
                new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch));
            SubmitAndWait(device, QueueType.Graphics, list.Finish(), usePersistentSet ? "persistent descriptors" : "transient descriptors");

            var mappedOutput = device.MapBuffer(outputReadback, MapMode.Read, 0, 4);
            Assert.Equal(expectedValue, BitConverter.ToUInt32(mappedOutput.Span));
            device.UnmapBuffer(outputReadback);
            var mappedTexture = device.MapBuffer(textureReadback, MapMode.Read, 0, TextureRowPitch);
            AssertRgba8PixelNear(mappedTexture.Span, TextureRowPitch, 0, 0, 0, 255, 0, 255);
            device.UnmapBuffer(textureReadback);

            if (usePersistentSet)
                device.Destroy(bindingSet);
        }
    }

    [D3D12DxcFact]
    public void D3D12Backend_DynamicOffsetConstantBuffersRewritePersistentAndTransientDescriptorsWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "dynamic offset execution",
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.DynamicOffset,
                    },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.RawBufferReadWrite, Stages = ShaderStageFlags.Compute },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "dynamic offset cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileDynamicOffsetComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });

        Run(usePersistentSet: true);
        Run(usePersistentSet: false);

        void Run(bool usePersistentSet)
        {
            var constants = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "dynamic offset constants",
                    SizeInBytes = 512,
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = BindFlags.ConstantBuffer,
                    InitialState = ResourceState.ConstantBuffer,
                },
                DynamicConstantBufferBytes(11, 37));
            var constantsView = device.CreateBufferView(constants, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 512 });
            var output = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "dynamic offset output",
                    SizeInBytes = 16,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                    InitialState = ResourceState.UnorderedAccess,
                    Raw = true,
                });
            var outputView = device.CreateBufferView(output, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
            var readback = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "dynamic offset readback",
                    SizeInBytes = 16,
                    Memory = MemoryClass.CpuReadback,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                });
            var resources = new[]
            {
                new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = constantsView },
                new BindingResourceDesc { Binding = 1, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView },
            };

            BindingSetHandle bindingSet = default;
            if (usePersistentSet)
                bindingSet = device.CreateBindingSet(bindingLayout, new BindingSetDesc { Resources = resources });

            var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
            var pass = list.BeginComputePass(new ComputePassDesc());
            pass.SetPipeline(pipeline);
            if (usePersistentSet)
                pass.SetBindingSet(0, bindingSet, [new DynamicOffset(0, 0, 256)]);
            else
                pass.SetBindings(0, bindingLayout, resources, [new DynamicOffset(0, 0, 256)]);
            pass.Dispatch(1, 1, 1);
            pass.End();
            list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
            list.CopyBuffer(output, 0, readback, 0, 4);
            SubmitAndWait(device, QueueType.Compute, list.Finish(), usePersistentSet ? "persistent dynamic offsets" : "transient dynamic offsets");

            var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
            Assert.Equal(37u, BitConverter.ToUInt32(mapped.Span));
            device.UnmapBuffer(readback);

            if (usePersistentSet)
                device.Destroy(bindingSet);
        }
    }

    [D3D12DxcFact]
    public void D3D12Backend_DynamicOffsetBindingSetKeepsPartiallyBoundTablePersistentWhenAdapterAndDxcAreAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        if (!device.Features.PartiallyBoundDescriptors)
        {
            var unsupported = Assert.Throws<RhiException>(
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
                                Count = 32,
                                Flags = BindingFlags.PartiallyBound,
                                Shape = new BindShapeDesc { TextureDimension = TextureViewDimension.Texture2D, Format = Format.Rgba8Unorm },
                            },
                        ],
                    }));
            Assert.Equal(ErrorCode.UnsupportedFeature, unsupported.Code);
            return;
        }

        var dxc = RequireDxc();
        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "dynamic offset plus partially bound table",
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.DynamicOffset,
                    },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.RawBufferReadWrite, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc
                    {
                        Binding = 2,
                        Type = BindingType.TextureRead,
                        Stages = ShaderStageFlags.Compute,
                        Count = 32,
                        Flags = BindingFlags.PartiallyBound,
                        Shape = new BindShapeDesc { TextureDimension = TextureViewDimension.Texture2D, Format = Format.Rgba8Unorm },
                    },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "dynamic offset split table cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileDynamicOffsetComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var constants = device.CreateBuffer(
            new BufferDesc
            {
                Name = "dynamic split constants",
                SizeInBytes = 512,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            },
            DynamicConstantBufferBytes(11, 37));
        var constantsView = device.CreateBufferView(constants, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 512 });
        var output = device.CreateBuffer(
            new BufferDesc
            {
                Name = "dynamic split output",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            });
        var outputView = device.CreateBufferView(output, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "dynamic split readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var bindingSet = device.CreateBindingSet(
            bindingLayout,
            [
                new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = constantsView },
                new BindingResourceDesc { Binding = 1, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView },
            ]);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.SetBindingSet(0, bindingSet, [new DynamicOffset(0, 0, 256)]);
        pass.Dispatch(1, 1, 1);
        pass.End();
        list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
        list.CopyBuffer(output, 0, readback, 0, 4);
        SubmitAndWait(device, QueueType.Compute, list.Finish(), "dynamic offset split descriptor tables");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
        Assert.Equal(37u, BitConverter.ToUInt32(mapped.Span));
        device.UnmapBuffer(readback);
        device.Destroy(bindingSet);
    }

    [D3D12DxcFact]
    public void D3D12Backend_TransientDescriptorsAndCommandBuffersRetireAcrossManySubmissionsWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        const int SubmissionCount = 256;
        const int ConstantStride = 256;
        int constantBufferSize = SubmissionCount * ConstantStride;

        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "retirement soak layout",
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Type = BindingType.ConstantBuffer,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.DynamicOffset,
                    },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.RawBufferReadWrite, Stages = ShaderStageFlags.Compute },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "retirement soak cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileDynamicOffsetComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var constants = device.CreateBuffer(
            new BufferDesc
            {
                Name = "retirement soak constants",
                SizeInBytes = checked((ulong)constantBufferSize),
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            },
            DynamicConstantBufferBytes(SubmissionCount));
        var constantsView = device.CreateBufferView(constants, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = checked((ulong)constantBufferSize) });
        var output = device.CreateBuffer(
            new BufferDesc
            {
                Name = "retirement soak output",
                SizeInBytes = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                InitialState = ResourceState.UnorderedAccess,
                Raw = true,
            });
        var outputView = device.CreateBufferView(output, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "retirement soak readback",
                SizeInBytes = checked((ulong)SubmissionCount * sizeof(uint)),
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var resources = new[]
        {
            new BindingResourceDesc { Binding = 0, ResourceType = BindingType.ConstantBuffer, BufferView = constantsView },
            new BindingResourceDesc { Binding = 1, ResourceType = BindingType.RawBufferReadWrite, BufferView = outputView },
        };
        var queue = device.GetQueue(QueueType.Compute);
        var fence = device.CreateFence("retirement soak");
        var outputState = ResourceState.UnorderedAccess;

        for (int index = 0; index < SubmissionCount; index++)
        {
            var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
            if (outputState != ResourceState.UnorderedAccess)
            {
                list.Barrier([], [new BufferBarrier(output, outputState, ResourceState.UnorderedAccess)]);
                outputState = ResourceState.UnorderedAccess;
            }

            var pass = list.BeginComputePass(new ComputePassDesc());
            pass.SetPipeline(pipeline);
            pass.SetBindings(0, bindingLayout, resources, [new DynamicOffset(0, 0, checked((uint)(index * ConstantStride)))]);
            pass.Dispatch(1, 1, 1);
            pass.End();
            list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
            outputState = ResourceState.CopySource;
            list.CopyBuffer(output, 0, readback, checked((ulong)index * sizeof(uint)), sizeof(uint));
            var commandBuffer = list.Finish();

            queue.Submit([commandBuffer], signals: [new QueueSignal(fence, checked((ulong)index + 1))]);
            device.Destroy(commandBuffer);
        }

        device.WaitFence(fence, SubmissionCount);
        device.Destroy(outputView);
        device.Destroy(constantsView);
        device.Destroy(output);
        device.Destroy(constants);
        var mapped = device.MapBuffer(readback, MapMode.Read, 0, SubmissionCount * sizeof(uint));
        for (int index = 0; index < SubmissionCount; index++)
            Assert.Equal((uint)(index + 1), BitConverter.ToUInt32(mapped.Span.Slice(index * sizeof(uint), sizeof(uint))));
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_BindlessMutablePartiallyBoundTextureArrayUpdatesOnlyChangedDescriptorsWhenAdapterAndDxcAreAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        if (!device.Features.Bindless)
        {
            var unsupported = Assert.Throws<RhiException>(
                () => device.CreateBindingLayout(
                    new BindingLayoutDesc
                    {
                        Slots =
                        [
                            new BindingSlotDesc
                            {
                                Binding = 0,
                                Count = 4,
                                Type = BindingType.TextureRead,
                                Stages = ShaderStageFlags.Compute,
                                Flags = BindingFlags.Bindless,
                            },
                        ],
                    }));
            Assert.Equal(ErrorCode.UnsupportedFeature, unsupported.Code);
            return;
        }

        var dxc = RequireDxc();

        var bindingLayout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Name = "bindless finite texture array",
                Slots =
                [
                    new BindingSlotDesc
                    {
                        Binding = 0,
                        Count = 4,
                        Type = BindingType.TextureRead,
                        Stages = ShaderStageFlags.Compute,
                        Flags = BindingFlags.Bindless | BindingFlags.PartiallyBound,
                        Shape = new BindShapeDesc
                        {
                            TextureDimension = TextureViewDimension.Texture2D,
                            Format = Format.Rgba8Unorm,
                        },
                    },
                    new BindingSlotDesc { Binding = 1, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 2, Type = BindingType.RawBufferReadWrite, Stages = ShaderStageFlags.Compute },
                    new BindingSlotDesc { Binding = 3, Type = BindingType.Sampler, Stages = ShaderStageFlags.Compute },
                ],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [bindingLayout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "bindless array cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileBindlessTextureComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var firstTexture = CreateTextureView(19);
        var secondTexture = CreateTextureView(29);
        var sampler = device.CreateSampler(new SamplerDesc { MinFilter = FilterMode.Nearest, MagFilter = FilterMode.Nearest });
        var constants = device.CreateBuffer(
            new BufferDesc
            {
                Name = "bindless index constants",
                SizeInBytes = 256,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.ConstantBuffer,
                InitialState = ResourceState.ConstantBuffer,
            },
            ConstantBufferBytes(2));
        var constantsView = device.CreateBufferView(constants, new BufferViewDesc { Kind = ViewKind.ConstantBuffer, SizeInBytes = 256 });
        var firstOutput = CreateOutputBuffer();
        var firstOutputView = device.CreateBufferView(firstOutput, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
        var bindingSet = device.CreateBindingSet(
            bindingLayout,
            new BindingSetDesc
            {
                Flags = BindingSetFlags.Mutable,
                Resources =
                [
                    new BindingResourceDesc { Binding = 0, ArrayElement = 2, ResourceType = BindingType.TextureRead, TextureView = firstTexture },
                    new BindingResourceDesc { Binding = 1, ResourceType = BindingType.ConstantBuffer, BufferView = constantsView },
                    new BindingResourceDesc { Binding = 2, ResourceType = BindingType.RawBufferReadWrite, BufferView = firstOutputView },
                    BindingResourceDesc.Sampler(3, sampler),
                ],
            });

        Run(bindingSet, firstOutput, expectedValue: 19, "bindless first");

        var secondOutput = CreateOutputBuffer();
        var secondOutputView = device.CreateBufferView(secondOutput, new BufferViewDesc { Kind = ViewKind.UnorderedAccess, SizeInBytes = 16, Raw = true });
        device.UpdateBindingSet(
            bindingSet,
            [
                new BindingResourceDesc { Binding = 0, ArrayElement = 2, ResourceType = BindingType.TextureRead, TextureView = secondTexture },
                new BindingResourceDesc { Binding = 2, ResourceType = BindingType.RawBufferReadWrite, BufferView = secondOutputView },
            ]);
        Run(bindingSet, secondOutput, expectedValue: 29, "bindless update");

        TextureViewHandle CreateTextureView(byte red)
        {
            var upload = device.CreateBuffer(
                new BufferDesc
                {
                    Name = "bindless texture upload",
                    SizeInBytes = TextureRowPitch,
                    Memory = MemoryClass.CpuUpload,
                    BindFlags = BindFlags.CopySource,
                    InitialState = ResourceState.CopySource,
                },
                Rgba8Upload(red, 0, 0, 255));
            var texture = device.CreateTexture(
                new TextureDesc
                {
                    Name = "bindless texture",
                    Width = 1,
                    Height = 1,
                    Format = Format.Rgba8Unorm,
                    BindFlags = BindFlags.CopyDestination | BindFlags.ShaderResource,
                    InitialState = ResourceState.CopyDestination,
                });
            var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
            list.CopyToTexture(upload, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch), texture, new TextureCopyRegion(0, 0, 0, 0, 0, 1, 1, 1));
            list.Barrier([new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.ShaderResource, SubresourceRange.All)], []);
            SubmitAndWait(device, QueueType.Graphics, list.Finish(), "bindless texture upload");
            return device.CreateTextureView(texture, new TextureViewDesc { Kind = ViewKind.ShaderResource, Format = Format.Rgba8Unorm });
        }

        BufferHandle CreateOutputBuffer()
            => device.CreateBuffer(
                new BufferDesc
                {
                    Name = "bindless output",
                    SizeInBytes = 16,
                    BindFlags = BindFlags.UnorderedAccess | BindFlags.CopySource,
                    InitialState = ResourceState.UnorderedAccess,
                    Raw = true,
                });

        void Run(BindingSetHandle set, BufferHandle output, uint expectedValue, string label)
        {
            var readback = device.CreateBuffer(
                new BufferDesc
                {
                    Name = label + " readback",
                    SizeInBytes = 16,
                    Memory = MemoryClass.CpuReadback,
                    BindFlags = BindFlags.CopyDestination,
                    InitialState = ResourceState.CopyDestination,
                });
            var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
            var pass = list.BeginComputePass(new ComputePassDesc());
            pass.SetPipeline(pipeline);
            pass.SetBindingSet(0, set);
            pass.Dispatch(1, 1, 1);
            pass.End();
            list.Barrier([], [new BufferBarrier(output, ResourceState.UnorderedAccess, ResourceState.CopySource)]);
            list.CopyBuffer(output, 0, readback, 0, 4);
            SubmitAndWait(device, QueueType.Compute, list.Finish(), label);
            var mapped = device.MapBuffer(readback, MapMode.Read, 0, 4);
            Assert.Equal(expectedValue, BitConverter.ToUInt32(mapped.Span));
            device.UnmapBuffer(readback);
        }
    }

    [D3D12Fact]
    public void D3D12Backend_PipelineLayoutRejectsSetZeroConstantBufferRegistersReservedForPushConstantsWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var directConflict = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 100, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute }],
            });
        var spanningConflict = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 99, Count = 2, Type = BindingType.ConstantBuffer, Stages = ShaderStageFlags.Compute }],
            });

        Assert.Equal(
            ErrorCode.InvalidDescriptor,
            Assert.Throws<RhiException>(() => device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [directConflict] })).Code);
        Assert.Equal(
            ErrorCode.InvalidDescriptor,
            Assert.Throws<RhiException>(() => device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [spanningConflict] })).Code);
    }

    [D3D12Fact]
    public void D3D12Backend_PersistentSamplerBindingSetsReuseShaderVisibleDescriptorsWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 0, Type = BindingType.Sampler, Stages = ShaderStageFlags.Compute }],
            });
        var sampler = device.CreateSampler(new SamplerDesc());

        for (int index = 0; index < 2050; index++)
        {
            var set = device.CreateBindingSet(layout, new BindingSetDesc { Resources = [BindingResourceDesc.Sampler(0, sampler)] });
            device.Destroy(set);
        }
    }

    [D3D12Fact]
    public void D3D12Backend_FinishedCommandBuffersProtectReferencedBuffersUntilDestroyedWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "finished command buffer upload",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "finished command buffer readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        using var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        list.CopyBuffer(upload, 0, readback, 0, 16);
        var commandBuffer = list.Finish();

        Assert.Equal(ErrorCode.ValidationFailure, Assert.Throws<RhiException>(() => device.Destroy(upload)).Code);
        Assert.Equal(ErrorCode.ValidationFailure, Assert.Throws<RhiException>(() => device.Destroy(readback)).Code);

        device.Destroy(commandBuffer);
        device.Destroy(upload);
        device.Destroy(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_TransientSamplerDescriptorsAreReleasedWithDestroyedCommandBuffersWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var layout = device.CreateBindingLayout(
            new BindingLayoutDesc
            {
                Slots = [new BindingSlotDesc { Binding = 0, Type = BindingType.Sampler, Stages = ShaderStageFlags.Compute }],
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc { BindingLayouts = [layout] });
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "noop cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileNoopComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var sampler = device.CreateSampler(new SamplerDesc());
        var queue = device.GetQueue(QueueType.Compute);

        for (int index = 0; index < 2050; index++)
        {
            var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
            var pass = list.BeginComputePass(new ComputePassDesc());
            pass.SetPipeline(pipeline);
            pass.SetBindings(0, layout, [BindingResourceDesc.Sampler(0, sampler)]);
            pass.End();
            var commandBuffer = list.Finish();
            queue.Submit([commandBuffer]);
            queue.WaitIdle();
            device.Destroy(commandBuffer);
        }
    }

    [D3D12DxcFact]
    public void D3D12Backend_DispatchIndirectExecutesWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var shader = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "indirect noop cs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileNoopComputeShader(dxc),
            });
        var pipeline = device.CreateComputePipeline(new ComputePipelineDesc { Layout = pipelineLayout, ComputeShader = shader });
        var arguments = device.CreateBuffer(
            new BufferDesc
            {
                Name = "dispatch indirect arguments",
                SizeInBytes = IndirectArgumentSize.Dispatch * 2,
                BindFlags = BindFlags.IndirectArgument,
                InitialState = ResourceState.IndirectArgument,
            },
            UIntBytes(1, 1, 1, 1, 1, 1));

        var invalidList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var invalidPass = invalidList.BeginComputePass(new ComputePassDesc());
        invalidPass.SetPipeline(pipeline);
        var invalidStride = Assert.Throws<RhiException>(
            () => invalidPass.DispatchIndirect(
                new IndirectDispatchDesc
                {
                    Arguments = arguments,
                    StrideInBytes = IndirectArgumentSize.Dispatch + 4,
                }));
        invalidPass.End();
        device.Destroy(invalidList.Finish());
        Assert.Equal(ErrorCode.InvalidDescriptor, invalidStride.Code);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Compute });
        var pass = list.BeginComputePass(new ComputePassDesc());
        pass.SetPipeline(pipeline);
        pass.DispatchIndirect(new IndirectDispatchDesc { Arguments = arguments, DispatchCount = 2 });
        pass.End();
        var commandBuffer = list.Finish();
        var queue = device.GetQueue(QueueType.Compute);
        queue.Submit([commandBuffer]);
        queue.WaitIdle();
        device.Destroy(commandBuffer);
    }

    [D3D12Fact]
    public void D3D12Backend_DestroyRetiredCommandBufferReleasesReferencedResourceLifetimeWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "retired upload",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            [1, 2, 3, 4]);
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "retired readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        list.CopyBuffer(upload, 0, readback, 0, 4);
        var commandBuffer = list.Finish();
        var fence = device.CreateFence("retired command");

        device.GetQueue(QueueType.Copy).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);
        device.Destroy(commandBuffer);
        device.Destroy(fence);
        device.Destroy(upload);
        device.Destroy(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_MapBufferValidatesModesRangesAndCopiesSubrangeWritesWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "map upload",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "map readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        Assert.Equal(ErrorCode.ValidationFailure, Assert.Throws<RhiException>(() => device.MapBuffer(upload, MapMode.Read)).Code);
        Assert.Equal(ErrorCode.ValidationFailure, Assert.Throws<RhiException>(() => device.MapBuffer(readback, MapMode.Write)).Code);
        Assert.Equal(ErrorCode.ValidationFailure, Assert.Throws<RhiException>(() => device.FlushBufferRange(upload, 0, 4)).Code);

        var mapped = device.MapBuffer(upload, MapMode.Write, 4, 4);
        Assert.Equal(ErrorCode.ValidationFailure, Assert.Throws<RhiException>(() => device.MapBuffer(upload, MapMode.Write)).Code);
        mapped.Span[0] = 9;
        mapped.Span[1] = 8;
        mapped.Span[2] = 7;
        mapped.Span[3] = 6;
        device.FlushBufferRange(upload, 4, 4);
        device.InvalidateBufferRange(upload, 4, 4);
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => device.FlushBufferRange(upload, 3, 5)).Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => device.InvalidateBufferRange(upload, 4, 0)).Code);
        device.UnmapBuffer(upload);
        Assert.Equal(ErrorCode.ValidationFailure, Assert.Throws<RhiException>(() => device.UnmapBuffer(upload)).Code);

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Copy });
        list.CopyBuffer(upload, 0, readback, 0, 16);
        SubmitAndWait(device, QueueType.Copy, list.Finish(), "map copy");

        var copied = device.MapBuffer(readback, MapMode.Read, 0, 16);
        Assert.Equal(9, copied.Span[4]);
        Assert.Equal(8, copied.Span[5]);
        Assert.Equal(7, copied.Span[6]);
        Assert.Equal(6, copied.Span[7]);
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_QueryValidationRejectsInvalidTimestampResolveCasesWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var queryPool = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.Timestamp, Count = 2 });
        var statisticsPool = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.PipelineStatistics, Count = 1 });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "query validation readback",
                SizeInBytes = 16,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.QueryResolve,
            });

        _ = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.Occlusion, Count = 1 });

        var beginList = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => beginList.BeginQuery(queryPool, 0)).Code);
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => beginList.EndQuery(queryPool, 0)).Code);

        var zeroCount = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => zeroCount.ResolveQueryData(queryPool, 0, 0, readback, 0)).Code);

        var outsidePool = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => outsidePool.ResolveQueryData(queryPool, 2, 1, readback, 0)).Code);

        var poolOverflow = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => poolOverflow.ResolveQueryData(queryPool, 1, 2, readback, 0)).Code);

        var unalignedOffset = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => unalignedOffset.ResolveQueryData(queryPool, 0, 1, readback, 4)).Code);

        var destinationOverflow = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => destinationOverflow.ResolveQueryData(queryPool, 0, 2, readback, 8)).Code);

        var statisticsDestinationTooSmall = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        Assert.Equal(ErrorCode.InvalidDescriptor, Assert.Throws<RhiException>(() => statisticsDestinationTooSmall.ResolveQueryData(statisticsPool, 0, 1, readback, 0)).Code);
    }

    [D3D12Fact]
    public void D3D12Backend_OcclusionAndPipelineStatisticsQueriesResolveWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var occlusion = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.Occlusion, Count = 1 });
        var statistics = device.Get<IQueryDevice>()!.CreateQueryPool(new QueryPoolDesc { Type = QueryType.PipelineStatistics, Count = 1 });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "non timestamp query readback",
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
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "non timestamp queries");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, 8 + 88);
        Assert.Equal(8 + 88, mapped.Length);
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_ResolveTextureCopiesMultisampledRenderTargetToSingleSampleTextureWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        const int Width = 64;
        const int Height = 64;

        var source = device.CreateTexture(
            new TextureDesc
            {
                Name = "resolve msaa source",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                SampleCount = 4,
                BindFlags = BindFlags.RenderTarget,
                InitialState = ResourceState.RenderTarget,
                OptimizedClearValue = ClearValue.FromColor(Format.Rgba8Unorm, new Color(1.0f, 0.0f, 0.0f, 1.0f)),
            });
        var sourceView = device.CreateTextureView(
            source,
            new TextureViewDesc
            {
                Kind = ViewKind.RenderTarget,
                Dimension = TextureViewDimension.Texture2DMultisampled,
                Format = Format.Rgba8Unorm,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Name = "resolve destination",
                Width = Width,
                Height = Height,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.CopySource,
                InitialState = ResourceState.ResolveDestination,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "resolve readback",
                SizeInBytes = TextureRowPitch * Height,
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
                        View = sourceView,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = new Color(1.0f, 0.0f, 0.0f, 1.0f),
                    },
                ],
            });
        pass.End();
        list.Barrier([new TextureBarrier(source, ResourceState.RenderTarget, ResourceState.ResolveSource, SubresourceRange.All)], []);
        list.ResolveTexture(
            source,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1),
            destination,
            new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1));
        list.Barrier([new TextureBarrier(destination, ResourceState.ResolveDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(destination, new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1), readback, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * Height));
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "resolve texture");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, TextureRowPitch * Height);
        AssertRgba8PixelRed(mapped.Span, TextureRowPitch, Width / 2, Height / 2);
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_CopyTextureRoundTripsThroughSecondTextureWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        byte[] uploadBytes = Rgba8Upload(41, 53, 67, 255, height: 2);
        var upload = device.CreateBuffer(
            new BufferDesc
            {
                Name = "copy texture upload",
                SizeInBytes = (ulong)uploadBytes.Length,
                Memory = MemoryClass.CpuUpload,
                BindFlags = BindFlags.CopySource,
                InitialState = ResourceState.CopySource,
            },
            uploadBytes);
        var source = device.CreateTexture(
            new TextureDesc
            {
                Name = "copy texture source",
                Width = 2,
                Height = 2,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var destination = device.CreateTexture(
            new TextureDesc
            {
                Name = "copy texture destination",
                Width = 2,
                Height = 2,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var readback = device.CreateBuffer(
            new BufferDesc
            {
                Name = "copy texture readback",
                SizeInBytes = (ulong)uploadBytes.Length,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });

        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.CopyToTexture(upload, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * 2), source, new TextureCopyRegion(0, 0, 0, 0, 0, 2, 2, 1));
        list.Barrier([new TextureBarrier(source, ResourceState.CopyDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyTexture(source, new TextureCopyRegion(0, 0, 0, 0, 0, 2, 2, 1), destination, new TextureCopyRegion(0, 0, 0, 0, 0, 2, 2, 1));
        list.Barrier([new TextureBarrier(destination, ResourceState.CopyDestination, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(destination, new TextureCopyRegion(0, 0, 0, 0, 0, 2, 2, 1), readback, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * 2));
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "copy texture");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, uploadBytes.Length);
        AssertRgba8PixelNear(mapped.Span, TextureRowPitch, 0, 0, 41, 53, 67, 255);
        AssertRgba8PixelNear(mapped.Span, TextureRowPitch, 1, 1, 41, 53, 67, 255);
        device.UnmapBuffer(readback);
    }

    [D3D12Fact]
    public void D3D12Backend_TextureBarrierTracksSelectedSubresourcesAndUavBarrierSucceedsWhenAdapterIsAvailable()
    {
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        var texture = device.CreateTexture(
            new TextureDesc
            {
                Name = "subresource barrier",
                Width = 4,
                Height = 4,
                ArraySize = 2,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.CopySource | BindFlags.CopyDestination,
                InitialState = ResourceState.CopySource,
            });
        var uavTexture = device.CreateTexture(
            new TextureDesc
            {
                Name = "valid uav barrier",
                Width = 4,
                Height = 4,
                Format = Format.Rgba8Unorm,
                BindFlags = BindFlags.UnorderedAccess,
                InitialState = ResourceState.UnorderedAccess,
            });
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        list.Barrier([new TextureBarrier(texture, ResourceState.CopySource, ResourceState.CopySource, new SubresourceRange(0, 1, 0, 1))], []);
        list.Barrier([new TextureBarrier(texture, ResourceState.CopySource, ResourceState.CopyDestination, new SubresourceRange(0, 1, 1, 1))], []);
        list.UavBarrier(uavTexture);
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "subresource barrier");

        Assert.Equal(ResourceState.CopySource, device.GetTextureState(texture, 0, 0));
        Assert.Equal(ResourceState.CopyDestination, device.GetTextureState(texture, 0, 1));
        Assert.Equal(ResourceState.UnorderedAccess, device.GetTextureState(uavTexture));
    }

    [D3D12DxcFact]
    public void D3D12Backend_DrawIndexedUsesVertexIndexViewportAndScissorWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        const int Width = 64;
        const int Height = 64;
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "indexed scissor target",
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
                Name = "indexed scissor readback",
                SizeInBytes = TextureRowPitch * Height,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var vertices = device.CreateBuffer(
            new BufferDesc
            {
                Name = "indexed vertices",
                SizeInBytes = 4 * 2 * sizeof(float),
                BindFlags = BindFlags.VertexBuffer,
                InitialState = ResourceState.VertexBuffer,
            },
            FloatBytes(-1.0f, -1.0f, -1.0f, 1.0f, 1.0f, -1.0f, 1.0f, 1.0f));
        var indices = device.CreateBuffer(
            new BufferDesc
            {
                Name = "indexed indices",
                SizeInBytes = 6 * sizeof(ushort),
                BindFlags = BindFlags.IndexBuffer,
                InitialState = ResourceState.IndexBuffer,
            },
            UInt16Bytes(0, 1, 2, 2, 1, 3));
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "indexed vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileVertexInputShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "indexed ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileRedPixelShader(dxc),
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "indexed scissor pipeline",
                Layout = pipelineLayout,
                VertexShader = vertex,
                PixelShader = pixel,
                VertexBuffers = [new VertexLayoutDesc { Slot = 0, StrideInBytes = 2 * sizeof(float) }],
                VertexAttributes = [new VertexAttributeDesc { Location = 0, BufferSlot = 0, Format = Format.Rg32Float }],
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
        pass.SetViewport(new Viewport(0, 0, Width, Height));
        pass.SetScissor(new Rect(0, 0, Width / 2, Height));
        pass.SetPipeline(pipeline);
        pass.SetVertexBuffer(0, vertices);
        pass.SetIndexBuffer(indices, IndexFormat.UInt16);
        pass.DrawIndexed(6);
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(target, new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1), readback, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * Height));
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "indexed scissor");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, TextureRowPitch * Height);
        AssertRgba8PixelRed(mapped.Span, TextureRowPitch, Width / 4, Height / 2);
        AssertRgba8PixelBlack(mapped.Span, TextureRowPitch, (Width * 3) / 4, Height / 2);
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_DrawIndirectAndIndexedIndirectExecuteWithCountBuffersWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
        const int Width = 64;
        const int Height = 64;
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = "indirect draw target",
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
                Name = "indirect draw readback",
                SizeInBytes = TextureRowPitch * Height,
                Memory = MemoryClass.CpuReadback,
                BindFlags = BindFlags.CopyDestination,
                InitialState = ResourceState.CopyDestination,
            });
        var drawVertices = device.CreateBuffer(
            new BufferDesc
            {
                Name = "indirect draw vertices",
                SizeInBytes = 3 * 2 * sizeof(float),
                BindFlags = BindFlags.VertexBuffer,
                InitialState = ResourceState.VertexBuffer,
            },
            FloatBytes(-1.0f, -1.0f, -1.0f, 3.0f, 3.0f, -1.0f));
        var indexedVertices = device.CreateBuffer(
            new BufferDesc
            {
                Name = "indirect indexed vertices",
                SizeInBytes = 4 * 2 * sizeof(float),
                BindFlags = BindFlags.VertexBuffer,
                InitialState = ResourceState.VertexBuffer,
            },
            FloatBytes(-1.0f, -1.0f, -1.0f, 1.0f, 1.0f, -1.0f, 1.0f, 1.0f));
        var indices = device.CreateBuffer(
            new BufferDesc
            {
                Name = "indirect indexed indices",
                SizeInBytes = 6 * sizeof(ushort),
                BindFlags = BindFlags.IndexBuffer,
                InitialState = ResourceState.IndexBuffer,
            },
            UInt16Bytes(0, 1, 2, 2, 1, 3));
        var drawArguments = device.CreateBuffer(
            new BufferDesc
            {
                Name = "draw indirect arguments",
                SizeInBytes = IndirectArgumentSize.Draw * 2,
                BindFlags = BindFlags.IndirectArgument,
                InitialState = ResourceState.IndirectArgument,
            },
            UIntBytes(3, 1, 0, 0, 3, 1, 0, 0));
        var drawIndexedArguments = device.CreateBuffer(
            new BufferDesc
            {
                Name = "draw indexed indirect arguments",
                SizeInBytes = IndirectArgumentSize.DrawIndexed * 2,
                BindFlags = BindFlags.IndirectArgument,
                InitialState = ResourceState.IndirectArgument,
            },
            UIntBytes(6, 1, 0, 0, 0, 6, 1, 0, 0, 0));
        var count = device.CreateBuffer(
            new BufferDesc
            {
                Name = "indirect count",
                SizeInBytes = 16,
                BindFlags = BindFlags.IndirectArgument,
                InitialState = ResourceState.IndirectArgument,
            },
            UIntBytes(1));
        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "indirect vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileVertexInputShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "indirect ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileRedPixelShader(dxc),
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "indirect draw pipeline",
                Layout = pipelineLayout,
                VertexShader = vertex,
                PixelShader = pixel,
                VertexBuffers = [new VertexLayoutDesc { Slot = 0, StrideInBytes = 2 * sizeof(float) }],
                VertexAttributes = [new VertexAttributeDesc { Location = 0, BufferSlot = 0, Format = Format.Rg32Float }],
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
        pass.SetViewport(new Viewport(0, 0, Width, Height));
        pass.SetPipeline(pipeline);
        pass.SetScissor(new Rect(0, 0, Width / 2, Height));
        pass.SetVertexBuffer(0, drawVertices);
        pass.DrawIndirect(
            new IndirectDrawDesc
            {
                Arguments = drawArguments,
                DrawCount = 2,
                CountBuffer = count,
            });
        pass.SetScissor(new Rect(Width / 2, 0, Width / 2, Height));
        pass.SetVertexBuffer(0, indexedVertices);
        pass.SetIndexBuffer(indices, IndexFormat.UInt16);
        pass.DrawIndexedIndirect(
            new DrawIdxDesc
            {
                Arguments = drawIndexedArguments,
                DrawCount = 2,
                CountBuffer = count,
            });
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(target, new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1), readback, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * Height));
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), "draw indirect");

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, TextureRowPitch * Height);
        AssertRgba8PixelRed(mapped.Span, TextureRowPitch, Width / 4, Height / 2);
        AssertRgba8PixelRed(mapped.Span, TextureRowPitch, (Width * 3) / 4, Height / 2);
        device.UnmapBuffer(readback);
    }

    [D3D12DxcFact]
    public void D3D12Backend_GeometryShaderPipelineDrawsWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });

        Assert.True(device.Features.GeometryShader);

        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "geometry vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGeneratedTriangleVertexShader(dxc),
            });
        var geometry = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "geometry gs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Geometry,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompilePassthroughGeometryShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "geometry ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileRedPixelShader(dxc),
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "geometry shader pipeline",
                Layout = pipelineLayout,
                VertexShader = vertex,
                GeometryShader = geometry,
                PixelShader = pixel,
                Rasterizer = new RasterizerDesc { CullMode = CullMode.None },
                ColorFormats = [Format.Rgba8Unorm],
            });

        DrawPipelineAndAssertCenterRed(device, pipeline, 3, "geometry shader draw");
    }

    [D3D12DxcFact]
    public void D3D12Backend_TessellationPipelineDrawsPatchWhenAdapterAndDxcAreAvailable()
    {
        var dxc = RequireDxc();
        using var instance = CreateD3D12Instance();
        using var device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });

        Assert.True(device.Features.TessellationShader);

        var vertex = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "tessellation vs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Vertex,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileGeneratedTriangleVertexShader(dxc),
            });
        var hull = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "tessellation hs",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Hull,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileTriangleHullShader(dxc),
            });
        var domain = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "tessellation ds",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Domain,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileTriangleDomainShader(dxc),
            });
        var pixel = device.CreateShaderModule(
            new ShaderModuleDesc
            {
                Name = "tessellation ps",
                Backend = Backend.D3D12,
                Stage = ShaderStage.Pixel,
                EntryPoint = "main",
                BytecodeFormat = ShaderBytecodeFormat.Dxil,
                Bytecode = CompileRedPixelShader(dxc),
            });
        var pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc());
        var pipeline = device.CreateGraphicsPipeline(
            new GraphicsPipelineDesc
            {
                Name = "tessellation pipeline",
                Layout = pipelineLayout,
                VertexShader = vertex,
                HullShader = hull,
                DomainShader = domain,
                PixelShader = pixel,
                Topology = PrimitiveTopology.PatchList,
                PatchControlPoints = 3,
                Rasterizer = new RasterizerDesc { CullMode = CullMode.None },
                ColorFormats = [Format.Rgba8Unorm],
            });

        DrawPipelineAndAssertCenterRed(device, pipeline, 3, "tessellation draw");
    }

    private static IInstance CreateD3D12Instance()
    {
        if (D3D12TestEnvironment.BackendSkipReason is { } reason)
            throw new InvalidOperationException(reason);

        return SomeEngine.Rhi.Instance.Create(D3D12Backend.Factory);
    }

    private static string RequireDxc()
        => D3D12TestEnvironment.FindDxc() ?? throw new InvalidOperationException("D3D12 DXC test was discovered without dxc.exe.");

    private static Hdr10Metadata TestHdr10Metadata()
        => new()
        {
            RedPrimaryX = 34000,
            RedPrimaryY = 16000,
            GreenPrimaryX = 13250,
            GreenPrimaryY = 34500,
            BluePrimaryX = 7500,
            BluePrimaryY = 3000,
            WhitePointX = 15635,
            WhitePointY = 16450,
            MaxMasteringLuminance = 1000,
            MinMasteringLuminance = 1,
            MaxContentLightLevel = 1000,
            MaxFrameAverageLightLevel = 400,
        };

    private static void SubmitAndWait(IDevice device, QueueType queueType, CommandBufferHandle commandBuffer, string fenceName)
    {
        var fence = device.CreateFence(fenceName);
        device.GetQueue(queueType).Submit([commandBuffer], signals: [new QueueSignal(fence, 1)]);
        device.WaitFence(fence, 1);
        device.Destroy(commandBuffer);
        device.Destroy(fence);
    }

    private static void AssertSetPipelineValidationFailure(IDevice device, PipelineHandle pipeline, in RenderPassDesc desc)
    {
        var list = device.CreateCommandList(new CommandListDesc { QueueType = QueueType.Graphics });
        var pass = list.BeginRenderPass(desc);
        var failure = Assert.Throws<RhiException>(() => pass.SetPipeline(pipeline));
        Assert.Equal(ErrorCode.ValidationFailure, failure.Code);
        pass.End();
        device.Destroy(list.Finish());
    }

    private static void DrawPipelineAndAssertCenterRed(IDevice device, PipelineHandle pipeline, uint vertexCount, string label)
    {
        const int Width = 64;
        const int Height = 64;
        var target = device.CreateTexture(
            new TextureDesc
            {
                Name = label + " target",
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
                Name = label + " readback",
                SizeInBytes = TextureRowPitch * Height,
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
                        ClearColor = Color.Black,
                    },
                ],
            });
        pass.SetViewport(new Viewport(0, 0, Width, Height));
        pass.SetPipeline(pipeline);
        pass.Draw(vertexCount);
        pass.End();
        list.Barrier([new TextureBarrier(target, ResourceState.RenderTarget, ResourceState.CopySource, SubresourceRange.All)], []);
        list.CopyToBuffer(target, new TextureCopyRegion(0, 0, 0, 0, 0, Width, Height, 1), readback, new BufferTextureCopy(0, TextureRowPitch, TextureRowPitch * Height));
        SubmitAndWait(device, QueueType.Graphics, list.Finish(), label);

        var mapped = device.MapBuffer(readback, MapMode.Read, 0, TextureRowPitch * Height);
        AssertRgba8PixelRed(mapped.Span, TextureRowPitch, Width / 2, Height / 2);
        device.UnmapBuffer(readback);
    }

    private static byte[] ConstantBufferBytes(uint value)
    {
        var bytes = new byte[256];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), value);
        return bytes;
    }

    private static byte[] DynamicConstantBufferBytes(uint first, uint second)
    {
        var bytes = new byte[512];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), first);
        BitConverter.TryWriteBytes(bytes.AsSpan(256, 4), second);
        return bytes;
    }

    private static byte[] DynamicConstantBufferBytes(int count)
    {
        const int ConstantStride = 256;
        var bytes = new byte[checked(count * ConstantStride)];
        for (int index = 0; index < count; index++)
            BitConverter.TryWriteBytes(bytes.AsSpan(index * ConstantStride, 4), (uint)(index + 1));
        return bytes;
    }

    private static byte[] UIntBytes(uint value)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), value);
        return bytes;
    }

    private static byte[] UIntBytes(params uint[] values)
        => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static byte[] PushConstantBytes(uint value)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), value);
        return bytes;
    }

    private static byte[] Rgba8Upload(byte r, byte g, byte b, byte a, int height = 1)
    {
        var bytes = new byte[TextureRowPitch * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int offset = (y * TextureRowPitch) + (x * 4);
                bytes[offset] = r;
                bytes[offset + 1] = g;
                bytes[offset + 2] = b;
                bytes[offset + 3] = a;
            }
        }

        return bytes;
    }

    private static byte[] FloatBytes(params float[] values)
        => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static byte[] UInt16Bytes(params ushort[] values)
        => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static void AssertRgba8PixelNear(ReadOnlySpan<byte> data, int rowPitch, int x, int y, byte r, byte g, byte b, byte a)
    {
        var pixel = data.Slice((y * rowPitch) + (x * 4), 4);
        Assert.InRange(pixel[0], Math.Max(0, r - 1), Math.Min(255, r + 1));
        Assert.InRange(pixel[1], Math.Max(0, g - 1), Math.Min(255, g + 1));
        Assert.InRange(pixel[2], Math.Max(0, b - 1), Math.Min(255, b + 1));
        Assert.InRange(pixel[3], Math.Max(0, a - 1), Math.Min(255, a + 1));
    }

    private static void AssertRgba8PixelRed(ReadOnlySpan<byte> data, int rowPitch, int x, int y)
    {
        var pixel = data.Slice((y * rowPitch) + (x * 4), 4);
        Assert.InRange(pixel[0], 200, 255);
        Assert.InRange(pixel[1], 0, 32);
        Assert.InRange(pixel[2], 0, 32);
        Assert.InRange(pixel[3], 200, 255);
    }

    private static void AssertRgba8PixelBlack(ReadOnlySpan<byte> data, int rowPitch, int x, int y)
    {
        var pixel = data.Slice((y * rowPitch) + (x * 4), 4);
        Assert.InRange(pixel[0], 0, 8);
        Assert.InRange(pixel[1], 0, 8);
        Assert.InRange(pixel[2], 0, 8);
        Assert.InRange(pixel[3], 200, 255);
    }

    private static byte[] CompileDescriptorComputeShader(string dxc)
        => CompileShader(
            dxc,
            """
            cbuffer Constants : register(b0, space0)
            {
                uint ConstantValue;
            };

            ByteAddressBuffer InputData : register(t1, space0);
            RWByteAddressBuffer OutputData : register(u2, space0);
            Texture2D<float4> SourceTexture : register(t3, space0);
            SamplerState SourceSampler : register(s4, space0);
            RWTexture2D<float4> OutputTexture : register(u5, space0);

            cbuffer PushConstants : register(b100, space0)
            {
                uint PushValue;
            };

            [numthreads(1, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                uint textureValue = (uint)(SourceTexture.SampleLevel(SourceSampler, float2(0.5, 0.5), 0.0).r * 255.0 + 0.5);
                OutputData.Store(0, InputData.Load(0) + ConstantValue + PushValue + textureValue);
                OutputTexture[uint2(0, 0)] = float4(0.0, 1.0, 0.0, 1.0);
            }
            """,
            "cs_6_0",
            "descriptor_execution_cs");

    private static byte[] CompileNoopComputeShader(string dxc)
        => CompileShader(
            dxc,
            """
            [numthreads(1, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
            }
            """,
            "cs_6_0",
            "noop_cs");

    private static byte[] CompileDynamicOffsetComputeShader(string dxc)
        => CompileShader(
            dxc,
            """
            cbuffer Constants : register(b0, space0)
            {
                uint ConstantValue;
            };

            RWByteAddressBuffer OutputData : register(u1, space0);

            [numthreads(1, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                OutputData.Store(0, ConstantValue);
            }
            """,
            "cs_6_0",
            "dynamic_offset_cs");

    private static byte[] CompileBindlessTextureComputeShader(string dxc)
        => CompileShader(
            dxc,
            """
            Texture2D<float4> SourceTextures[4] : register(t0, space0);

            cbuffer Constants : register(b1, space0)
            {
                uint TextureIndex;
            };

            RWByteAddressBuffer OutputData : register(u2, space0);
            SamplerState SourceSampler : register(s3, space0);

            [numthreads(1, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                float value = SourceTextures[TextureIndex].SampleLevel(SourceSampler, float2(0.5, 0.5), 0.0).r;
                OutputData.Store(0, (uint)(value * 255.0 + 0.5));
            }
            """,
            "cs_6_0",
            "bindless_texture_cs");

    private static byte[] CompileFullscreenMeshShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct MeshVertex
            {
                float4 Position : SV_Position;
            };

            [outputtopology("triangle")]
            [numthreads(1, 1, 1)]
            void main(out vertices MeshVertex vertices[3], out indices uint3 triangles[1])
            {
                SetMeshOutputCounts(3, 1);
                vertices[0].Position = float4(-1.0, -1.0, 0.0, 1.0);
                vertices[1].Position = float4(-1.0, 3.0, 0.0, 1.0);
                vertices[2].Position = float4(3.0, -1.0, 0.0, 1.0);
                triangles[0] = uint3(0, 1, 2);
            }
            """,
            "ms_6_5",
            "fullscreen_ms");

    private static byte[] CompileRayGenerationLibrary(string dxc)
        => CompileShader(
            dxc,
            """
            [shader("raygeneration")]
            void RayGen()
            {
            }
            """,
            "lib_6_3",
            "rt_raygen",
            entryPoint: null);

    private static byte[] CompileMissLibrary(string dxc)
        => CompileShader(
            dxc,
            """
            struct Payload
            {
                float4 Color;
            };

            [shader("miss")]
            void Miss(inout Payload payload)
            {
            }
            """,
            "lib_6_3",
            "rt_miss",
            entryPoint: null);

    private static byte[] CompileClosestHitLibrary(string dxc)
        => CompileShader(
            dxc,
            """
            struct Payload
            {
                float4 Color;
            };

            [shader("closesthit")]
            void ClosestHit(inout Payload payload, in BuiltInTriangleIntersectionAttributes attributes)
            {
            }
            """,
            "lib_6_3",
            "rt_closest_hit",
            entryPoint: null);

    private static byte[] CompileTraceRayGenerationLibrary(string dxc)
        => CompileShader(
            dxc,
            """
            RaytracingAccelerationStructure Scene : register(t0, space0);
            RWByteAddressBuffer Output : register(u1, space0);

            struct Payload
            {
                uint Value;
            };

            [shader("raygeneration")]
            void RayGen()
            {
                Payload payload;
                payload.Value = 0;

                RayDesc ray;
                ray.Origin = float3(0.0, 0.0, -1.0);
                ray.Direction = float3(0.0, 0.0, 1.0);
                ray.TMin = 0.0;
                ray.TMax = 10.0;

                TraceRay(Scene, RAY_FLAG_NONE, 0xFF, 0, 1, 0, ray, payload);
                Output.Store(0, payload.Value);
            }
            """,
            "lib_6_3",
            "rt_trace_raygen",
            entryPoint: null);

    private static byte[] CompileTraceRayMissLibrary(string dxc)
        => CompileShader(
            dxc,
            """
            struct Payload
            {
                uint Value;
            };

            [shader("miss")]
            void Miss(inout Payload payload)
            {
                payload.Value = 2;
            }
            """,
            "lib_6_3",
            "rt_trace_miss",
            entryPoint: null);

    private static byte[] CompileTraceRayClosestHitLibrary(string dxc)
        => CompileShader(
            dxc,
            """
            struct Payload
            {
                uint Value;
            };

            [shader("closesthit")]
            void ClosestHit(inout Payload payload, in BuiltInTriangleIntersectionAttributes attributes)
            {
                payload.Value = 1;
            }
            """,
            "lib_6_3",
            "rt_trace_closest_hit",
            entryPoint: null);

    private static byte[] D3D12RayTracingInstanceBytes(ulong bottomLevelAddress)
    {
        var data = new byte[64];
        var transform = MemoryMarshal.Cast<byte, float>(data.AsSpan(0, 48));
        transform[0] = 1.0f;
        transform[5] = 1.0f;
        transform[10] = 1.0f;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48, 4), 0xFFu << 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52, 4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(56, 8), bottomLevelAddress);
        return data;
    }

    private static byte[] RayTracingShaderTableBytes(IDevice device, PipelineHandle pipeline)
    {
        int identifierSize = checked((int)device.Get<IRtDevice>()!.GetRtSize(pipeline));
        int tableAlignment = checked((int)device.Limits.RayTracingShaderTableAlignment);
        var bytes = new byte[tableAlignment * 3];
        CopyIdentifier("RayGenGroup", 0);
        CopyIdentifier("MissGroup", tableAlignment);
        CopyIdentifier("HitGroup", tableAlignment * 2);
        return bytes;

        void CopyIdentifier(string group, int offset)
        {
            var identifier = bytes.AsSpan(offset, identifierSize);
            device.Get<IRtDevice>()!.GetRtId(pipeline, group, identifier);
        }
    }

    private static byte[] CompileGeneratedTriangleVertexShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct VSOut
            {
                float4 Position : SV_Position;
            };

            VSOut main(uint id : SV_VertexID)
            {
                float2 positions[3] =
                {
                    float2(-1.0, -1.0),
                    float2(-1.0, 3.0),
                    float2(3.0, -1.0)
                };

                VSOut output;
                output.Position = float4(positions[id], 0.0, 1.0);
                return output;
            }
            """,
            "vs_6_0",
            "generated_triangle_vs");

    private static byte[] CompilePassthroughGeometryShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct VSOut
            {
                float4 Position : SV_Position;
            };

            [maxvertexcount(3)]
            void main(triangle VSOut input[3], inout TriangleStream<VSOut> output)
            {
                output.Append(input[0]);
                output.Append(input[1]);
                output.Append(input[2]);
                output.RestartStrip();
            }
            """,
            "gs_6_0",
            "passthrough_gs");

    private static byte[] CompileTriangleHullShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct ControlPoint
            {
                float4 Position : SV_Position;
            };

            struct PatchConstants
            {
                float Edges[3] : SV_TessFactor;
                float Inside : SV_InsideTessFactor;
            };

            PatchConstants Constants(InputPatch<ControlPoint, 3> patch, uint patchId : SV_PrimitiveID)
            {
                PatchConstants output;
                output.Edges[0] = 1.0;
                output.Edges[1] = 1.0;
                output.Edges[2] = 1.0;
                output.Inside = 1.0;
                return output;
            }

            [domain("tri")]
            [partitioning("integer")]
            [outputtopology("triangle_cw")]
            [outputcontrolpoints(3)]
            [patchconstantfunc("Constants")]
            ControlPoint main(InputPatch<ControlPoint, 3> patch, uint index : SV_OutputControlPointID)
            {
                return patch[index];
            }
            """,
            "hs_6_0",
            "triangle_hs");

    private static byte[] CompileTriangleDomainShader(string dxc)
        => CompileShader(
            dxc,
            """
            struct ControlPoint
            {
                float4 Position : SV_Position;
            };

            struct PatchConstants
            {
                float Edges[3] : SV_TessFactor;
                float Inside : SV_InsideTessFactor;
            };

            [domain("tri")]
            ControlPoint main(PatchConstants constants, const OutputPatch<ControlPoint, 3> patch, float3 barycentric : SV_DomainLocation)
            {
                ControlPoint output;
                output.Position =
                    patch[0].Position * barycentric.x +
                    patch[1].Position * barycentric.y +
                    patch[2].Position * barycentric.z;
                return output;
            }
            """,
            "ds_6_0",
            "triangle_ds");

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
            "indexed_vs");

    private static byte[] CompileRedPixelShader(string dxc)
        => CompileShader(
            dxc,
            """
            float4 main() : SV_Target0
            {
                return float4(1.0, 0.0, 0.0, 1.0);
            }
            """,
            "ps_6_0",
            "red_ps");

    private static byte[] CompileEmptyPixelShader(string dxc)
        => CompileShader(
            dxc,
            """
            void main()
            {
            }
            """,
            "ps_6_0",
            "empty_ps");

    private static byte[] CompileMipGeneratorTexture2DShader(string dxc)
        => CompileShader(
            dxc,
            """
            Texture2D<float4> SourceMip : register(t0, space0);
            RWTexture2D<float4> DestinationMip : register(u1, space0);

            [numthreads(8, 8, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                uint dstWidth;
                uint dstHeight;
                DestinationMip.GetDimensions(dstWidth, dstHeight);
                if (id.x >= dstWidth || id.y >= dstHeight)
                    return;

                uint srcWidth;
                uint srcHeight;
                SourceMip.GetDimensions(srcWidth, srcHeight);
                uint2 src = min(id.xy * 2, uint2(srcWidth - 1, srcHeight - 1));
                uint2 srcX = uint2(min(src.x + 1, srcWidth - 1), src.y);
                uint2 srcY = uint2(src.x, min(src.y + 1, srcHeight - 1));
                uint2 srcXY = uint2(min(src.x + 1, srcWidth - 1), min(src.y + 1, srcHeight - 1));
                DestinationMip[id.xy] =
                    (SourceMip.Load(int3(src, 0)) +
                     SourceMip.Load(int3(srcX, 0)) +
                     SourceMip.Load(int3(srcY, 0)) +
                     SourceMip.Load(int3(srcXY, 0))) * 0.25;
            }
            """,
            "cs_6_0",
            "mip_generator_2d");

    private static byte[] CompileMipGeneratorTexture2DArrayShader(string dxc)
        => CompileShader(
            dxc,
            """
            Texture2DArray<float4> SourceMip : register(t0, space0);
            RWTexture2DArray<float4> DestinationMip : register(u1, space0);

            [numthreads(8, 8, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                uint dstWidth;
                uint dstHeight;
                uint dstSlices;
                DestinationMip.GetDimensions(dstWidth, dstHeight, dstSlices);
                if (id.x >= dstWidth || id.y >= dstHeight || id.z >= dstSlices)
                    return;

                uint srcWidth;
                uint srcHeight;
                uint srcSlices;
                SourceMip.GetDimensions(srcWidth, srcHeight, srcSlices);
                uint2 src = min(id.xy * 2, uint2(srcWidth - 1, srcHeight - 1));
                uint2 srcX = uint2(min(src.x + 1, srcWidth - 1), src.y);
                uint2 srcY = uint2(src.x, min(src.y + 1, srcHeight - 1));
                uint2 srcXY = uint2(min(src.x + 1, srcWidth - 1), min(src.y + 1, srcHeight - 1));
                DestinationMip[uint3(id.xy, id.z)] =
                    (SourceMip.Load(int4(src, id.z, 0)) +
                     SourceMip.Load(int4(srcX, id.z, 0)) +
                     SourceMip.Load(int4(srcY, id.z, 0)) +
                     SourceMip.Load(int4(srcXY, id.z, 0))) * 0.25;
            }
            """,
            "cs_6_0",
            "mip_generator_2d_array");

    private static byte[] MipGeneratorUploadBytesRgba32Float()
    {
        const int Width = 4;
        const int Height = 4;
        const int PixelSize = 16;
        var data = new byte[TextureRowPitch * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                var pixel = data.AsSpan(y * TextureRowPitch + x * PixelSize, PixelSize);
                WriteSingle(pixel, x + y * Width);
                WriteSingle(pixel[4..], 0);
                WriteSingle(pixel[8..], 0);
                WriteSingle(pixel[12..], 1);
            }
        }

        return data;
    }

    private static byte[] MipGeneratorUploadBytesRgba32FloatArray(int sliceCount)
    {
        const int Width = 4;
        const int Height = 4;
        const int PixelSize = 16;
        int slicePitch = TextureRowPitch * Height;
        var data = new byte[slicePitch * sliceCount];
        for (int slice = 0; slice < sliceCount; slice++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var pixel = data.AsSpan(slice * slicePitch + y * TextureRowPitch + x * PixelSize, PixelSize);
                    WriteSingle(pixel, slice * 100 + x + y * Width);
                    WriteSingle(pixel[4..], 0);
                    WriteSingle(pixel[8..], 0);
                    WriteSingle(pixel[12..], 1);
                }
            }
        }

        return data;
    }

    private static void WriteSingle(Span<byte> destination, float value)
    {
        if (!BitConverter.TryWriteBytes(destination, value))
            throw new InvalidOperationException("Failed to write test float bytes.");
    }

    private static byte[] CompileShader(string dxc, string sourceText, string target, string fileName, string? entryPoint = "main")
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
            if (entryPoint != null)
            {
                process.StartInfo.ArgumentList.Add("-E");
                process.StartInfo.ArgumentList.Add(entryPoint);
            }
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

    private sealed class HiddenWindow : IDisposable
    {
        private const int ErrorClassAlreadyExists = 1410;
        private const uint WindowStyleOverlappedWindow = 0x00CF0000;
        private const string ClassName = "SomeEngineRhiD3D12TestWindow";
        private static readonly WindowProcedure Procedure = WindowProc;
        private static int Registered;

        private HiddenWindow(nint handle)
        {
            Handle = handle;
        }

        public nint Handle { get; private set; }

        public static HiddenWindow Create(int width, int height)
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("D3D12 swapchain tests require Windows.");

            EnsureClassRegistered();
            var module = GetModuleHandleW(null);
            var handle = CreateWindowExW(
                0,
                ClassName,
                "SomeEngine RHI D3D12 Test Window",
                WindowStyleOverlappedWindow,
                0,
                0,
                width,
                height,
                0,
                0,
                module,
                0);
            if (handle == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create hidden D3D12 test window.");
            return new HiddenWindow(handle);
        }

        public void Dispose()
        {
            if (Handle == 0)
                return;
            DestroyWindow(Handle);
            Handle = 0;
        }

        private static void EnsureClassRegistered()
        {
            if (Interlocked.CompareExchange(ref Registered, 1, 0) != 0)
                return;

            var module = GetModuleHandleW(null);
            var wndClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = module,
                ClassName = ClassName,
            };
            ushort atom = RegisterClassExW(ref wndClass);
            if (atom != 0)
                return;

            int error = Marshal.GetLastWin32Error();
            if (error == ErrorClassAlreadyExists)
                return;

            Registered = 0;
            throw new Win32Exception(error, "Failed to register hidden D3D12 test window class.");
        }

        private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
            => DefWindowProcW(hwnd, message, wParam, lParam);

        private delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClassEx
        {
            public uint Size;
            public uint Style;
            public nint WindowProc;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public string? MenuName;
            public string ClassName;
            public nint IconSmall;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint param);

        [DllImport("user32.dll")]
        private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(nint hwnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint GetModuleHandleW(string? moduleName);
    }
}
