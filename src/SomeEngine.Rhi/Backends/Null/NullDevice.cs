using SomeEngine.Core.Collections;

namespace SomeEngine.Rhi.Backends.Null;

internal sealed partial class NullDevice : IDevice, IMemoryDevice, ICacheDevice, IRtDevice, IQueryDevice
{
    private const ulong NullDeviceLocalBudget = 16UL * 1024 * 1024 * 1024;
    private const ulong NullHostVisibleBudget = 16UL * 1024 * 1024 * 1024;
    private static readonly FormatCapabilities UnknownFormatCapabilities = new(FormatSupport.None);
    private static readonly FormatCapabilities DepthFormatCapabilities = new(
        FormatSupport.DepthStencil | FormatSupport.CopySource | FormatSupport.CopyDestination,
        SampleCountFlags.Count1 | SampleCountFlags.Count2 | SampleCountFlags.Count4);
    private static readonly FormatCapabilities ColorFormatCapabilities = new(
        FormatSupport.ShaderSample
            | FormatSupport.RenderTarget
            | FormatSupport.UnorderedAccess
            | FormatSupport.CopySource
            | FormatSupport.CopyDestination
            | FormatSupport.VertexAttribute,
        SampleCountFlags.Count1 | SampleCountFlags.Count2 | SampleCountFlags.Count4 | SampleCountFlags.Count8,
        typedUavLoad: true,
        typedUavStore: true);
    private static readonly FormatCapabilities CompressedFormatCapabilities = new(
        FormatSupport.ShaderSample,
        SampleCountFlags.Count1,
        filterable: true,
        blendable: false);

    private readonly NullQueue _graphicsQueue;
    private readonly NullQueue _computeQueue;
    private readonly NullQueue _copyQueue;
    private NullCommandOperation[]? _cachedCommandOperations;
    private BindingResourceDesc[]? _cachedBindingResources;
    private readonly FlatDictionary<BufferHandle, int> _activeBuffers = new();
    private readonly FlatDictionary<TextureHandle, int> _activeTextures = new();
    private readonly FlatDictionary<QueryPoolHandle, int> _activeQueryPools = new();
    private readonly FlatDictionary<TextureViewHandle, int> _activeTextureViews = new();
    private readonly FlatDictionary<BufferViewHandle, int> _activeBufferViews = new();
    private readonly FlatDictionary<SamplerHandle, int> _activeSamplers = new();
    private readonly FlatDictionary<AccelerationStructureHandle, int> _activeAccelerationStructures = new();
    private readonly FlatDictionary<BindingLayoutHandle, int> _activeBindingLayouts = new();
    private readonly FlatDictionary<BindingSetHandle, int> _activeBindingSets = new();
    private readonly FlatDictionary<PipelineLayoutHandle, int> _activePipelineLayouts = new();
    private readonly FlatDictionary<PipelineHandle, int> _activePipelines = new();
    private bool _disposed;

    internal readonly HandleStore<BufferHandle, BufferRecord> Buffers = new((id, gen) => new BufferHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<TextureHandle, TextureRecord> Textures = new((id, gen) => new TextureHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<TextureViewHandle, TextureViewRecord> TextureViews = new((id, gen) => new TextureViewHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<BufferViewHandle, BufferViewRecord> BufferViews = new((id, gen) => new BufferViewHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<SamplerHandle, SamplerRecord> Samplers = new((id, gen) => new SamplerHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<ShaderModuleHandle, ShaderModuleRecord> ShaderModules = new((id, gen) => new ShaderModuleHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<BindingLayoutHandle, BindingLayoutRecord> BindingLayouts = new((id, gen) => new BindingLayoutHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<PipelineLayoutHandle, PipelineLayoutRecord> PipelineLayouts = new((id, gen) => new PipelineLayoutHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<BindingSetHandle, BindingSetRecord> BindingSets = new((id, gen) => new BindingSetHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<PipelineHandle, PipelineRecord> Pipelines = new((id, gen) => new PipelineHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<PipelineCacheHandle, PipelineCacheRecord> PipelineCaches = new((id, gen) => new PipelineCacheHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<AccelerationStructureHandle, AccelerationStructureRecord> AccelerationStructures = new((id, gen) => new AccelerationStructureHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<CommandBufferHandle, CommandBufferRecord> CommandBuffers = new((id, gen) => new CommandBufferHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<FenceHandle, FenceRecord> Fences = new((id, gen) => new FenceHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<QueryPoolHandle, QueryPoolRecord> QueryPools = new((id, gen) => new QueryPoolHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<SwapchainHandle, SwapchainRecord> Swapchains = new((id, gen) => new SwapchainHandle(id, gen), h => h.Id, h => h.Generation);
    internal readonly HandleStore<MemoryHeapHandle, MemoryHeapRecord> MemoryHeaps = new((id, gen) => new MemoryHeapHandle(id, gen), h => h.Id, h => h.Generation);
    private ulong _timestamp;

    public NullDevice()
    {
        Features = new DeviceFeatures
        {
            GraphicsQueue = true,
            ComputeQueue = true,
            CopyQueue = true,
            ParallelCommandRecording = true,
            StaticSamplers = true,
            PlacedResources = true,
            ResourceAliasing = true,
            MemoryBudget = true,
            TimestampQueries = true,
            OcclusionQueries = true,
            PipelineStatisticsQueries = true,
            PipelineCache = true,
            DrawIndirect = true,
            DispatchIndirect = true,
            MultiDrawIndirect = true,
            MultiDispatchIndirect = true,
            IndirectCount = true,
            Bindless = true,
            PartiallyBoundDescriptors = true,
            DynamicOffsets = true,
            TypedUavLoad = true,
            SamplerAnisotropy = true,
            TextureCubeArray = true,
            GeometryShader = true,
            TessellationShader = true,
            MeshShader = true,
            RayTracing = true,
        };
        Limits = new DeviceLimits
        {
            MaxIndirectDrawCount = 65_535,
            MaxIndirectDispatchCount = 65_535,
        };
        AdapterInfo = new AdapterInfo
        {
            Name = "Null Adapter",
            Backend = Backend.Null,
            DedicatedVideoMemory = NullDeviceLocalBudget,
            SharedSystemMemory = NullHostVisibleBudget,
        };
        QueueFamilies =
        [
            new QueueFamilyInfo
            {
                Type = QueueType.Graphics,
                Count = 1,
                SupportsGraphics = true,
                SupportsCompute = true,
                SupportsCopy = true,
                SupportsTimestamps = true,
            },
            new QueueFamilyInfo
            {
                Type = QueueType.Compute,
                Count = 1,
                SupportsCompute = true,
                SupportsCopy = true,
                SupportsTimestamps = true,
            },
            new QueueFamilyInfo
            {
                Type = QueueType.Copy,
                Count = 1,
                SupportsCopy = true,
                SupportsTimestamps = true,
            },
        ];
        _graphicsQueue = new NullQueue(this, QueueType.Graphics);
        _computeQueue = new NullQueue(this, QueueType.Compute);
        _copyQueue = new NullQueue(this, QueueType.Copy);
    }

    public AdapterInfo AdapterInfo { get; }
    public DeviceFeatures Features { get; }
    public DeviceLimits Limits { get; }
    public IReadOnlyList<QueueFamilyInfo> QueueFamilies { get; }

    public T? Get<T>() where T : class
        => this is T self ? self : null;

    public IQueue GetQueue(QueueType type, uint index = 0)
    {
        ThrowIfDisposed();
        if (index != 0)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Null backend exposes exactly one queue per queue type.");
        return type switch
        {
            QueueType.Graphics => _graphicsQueue,
            QueueType.Compute => _computeQueue,
            QueueType.Copy => _copyQueue,
            _ => throw new RhiException(ErrorCode.UnsupportedFeature, $"Queue type '{type}' is not supported."),
        };
    }

    public ResourceMemoryRequirements GetBufferReqs(BufferDesc desc)
    {
        ThrowIfDisposed();
        Validation.BufferDesc(desc);
        return new ResourceMemoryRequirements(
            AlignUp(desc.SizeInBytes, Limits.BufferPlacementAlignment),
            Limits.BufferPlacementAlignment,
            MemoryHeapKind.Buffer,
            RequiresDedicatedAllocation: false,
            PrefersDedicatedAllocation: false);
    }

    public ResourceMemoryRequirements GetTextureReqs(TextureDesc desc)
    {
        ThrowIfDisposed();
        Validation.TextureDesc(desc);
        ValidateTexFormat(desc);
        ValidateTextureLimits(desc);
        MemoryHeapKind kind = desc.BindFlags.HasFlag(BindFlags.RenderTarget) || desc.BindFlags.HasFlag(BindFlags.DepthStencil)
            ? MemoryHeapKind.RenderTargetOrDepthStencil
            : MemoryHeapKind.Texture;
        ulong size = EstimateTextureSize(desc);
        ulong alignment = desc.SampleCount > 1 ? Limits.MsaaTexturePlacementAlignment : Limits.TexturePlacementAlignment;
        return new ResourceMemoryRequirements(
            AlignUp(size, alignment),
            alignment,
            kind,
            RequiresDedicatedAllocation: false,
            PrefersDedicatedAllocation: false);
    }

    public MemoryHeapHandle CreateMemoryHeap(MemoryHeapDesc desc)
    {
        ThrowIfDisposed();
        Validation.MemoryHeapDesc(desc, Features);
        return MemoryHeaps.Add(new MemoryHeapRecord(Snapshot(desc)));
    }

    public MemoryHeapDesc GetHeapDesc(MemoryHeapHandle heap)
    {
        ThrowIfDisposed();
        return MemoryHeaps.Get(heap, "MemoryHeap").Desc;
    }

    public MemoryBudget GetMemoryBudget(MemoryClass memory)
    {
        ThrowIfDisposed();
        ulong budget = memory == MemoryClass.DeviceLocal
            ? NullDeviceLocalBudget
            : NullHostVisibleBudget;
        return new MemoryBudget(memory, budget, CurrentMemoryUsage(memory), IsExact: false);
    }

    public BufferHandle CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        ThrowIfDisposed();
        Validation.BufferDesc(desc);
        if (!initialData.IsEmpty && (ulong)initialData.Length > desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Initial buffer data exceeds buffer size.");
        if (desc.SizeInBytes > int.MaxValue)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Null backend buffers are limited to 2GB.");
        byte[] data = new byte[(int)desc.SizeInBytes];
        initialData.CopyTo(data);
        var requirements = GetBufferReqs(desc);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Committed, desc.Memory, MemoryHeapHandle.Invalid, 0, requirements.SizeInBytes);
        return Buffers.Add(new BufferRecord(Snapshot(desc), data, allocation));
    }

    public TextureHandle CreateTexture(TextureDesc desc)
    {
        ThrowIfDisposed();
        Validation.TextureDesc(desc);
        ValidateTexFormat(desc);
        ValidateTextureLimits(desc);
        var requirements = GetTextureReqs(desc);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Committed, desc.Memory, MemoryHeapHandle.Invalid, 0, requirements.SizeInBytes);
        return Textures.Add(new TextureRecord(Snapshot(desc), allocation));
    }

    public BufferHandle CreatePlacedBuffer(MemoryHeapHandle heap, ulong offset, BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        ThrowIfDisposed();
        Validation.BufferDesc(desc);
        if (!initialData.IsEmpty && (ulong)initialData.Length > desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Initial buffer data exceeds buffer size.");
        if (desc.SizeInBytes > int.MaxValue)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Null backend buffers are limited to 2GB.");
        var requirements = GetBufferReqs(desc);
        ValidatePlacement(heap, offset, requirements, desc.Memory);
        byte[] data = new byte[(int)desc.SizeInBytes];
        initialData.CopyTo(data);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Placed, desc.Memory, heap, offset, requirements.SizeInBytes);
        var handle = Buffers.Add(new BufferRecord(Snapshot(desc), data, allocation));
        RegisterPlacedAllocation(AliasingResource.BufferResource(handle), allocation);
        return handle;
    }

    public TextureHandle CreatePlacedTexture(MemoryHeapHandle heap, ulong offset, TextureDesc desc)
    {
        ThrowIfDisposed();
        Validation.TextureDesc(desc);
        ValidateTexFormat(desc);
        ValidateTextureLimits(desc);
        var requirements = GetTextureReqs(desc);
        ValidatePlacement(heap, offset, requirements, desc.Memory);
        var allocation = new ResourceAllocationInfo(ResourceOwnership.Placed, desc.Memory, heap, offset, requirements.SizeInBytes);
        var handle = Textures.Add(new TextureRecord(Snapshot(desc), allocation));
        RegisterPlacedAllocation(AliasingResource.TextureResource(handle), allocation);
        return handle;
    }

    public TextureViewHandle CreateTextureView(TextureHandle texture, TextureViewDesc desc)
    {
        ThrowIfDisposed();
        var textureRecord = Textures.Get(texture, "Texture");
        Validation.TextureViewDesc(textureRecord.Desc, desc);
        var storedDesc = Snapshot(desc);
        if (storedDesc.Format == Format.Unknown)
            storedDesc = storedDesc with { Format = textureRecord.Desc.Format };
        ValidateViewFormat(storedDesc);
        return TextureViews.Add(new TextureViewRecord(texture, storedDesc));
    }

    public BufferViewHandle CreateBufferView(BufferHandle buffer, BufferViewDesc desc)
    {
        ThrowIfDisposed();
        var bufferRecord = Buffers.Get(buffer, "Buffer");
        Validation.BufferViewDesc(bufferRecord.Desc, desc);
        return BufferViews.Add(new BufferViewRecord(buffer, Snapshot(desc)));
    }

    public SamplerHandle CreateSampler(SamplerDesc desc)
    {
        ThrowIfDisposed();
        Validation.SamplerDesc(desc);
        return Samplers.Add(new SamplerRecord(Snapshot(desc)));
    }

    public ShaderModuleHandle CreateShaderModule(ShaderModuleDesc desc)
    {
        ThrowIfDisposed();
        var storedDesc = Snapshot(desc);
        Validation.ShaderModuleDesc(storedDesc);
        if (storedDesc.Backend != Backend.Null)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Null backend only accepts shader modules tagged Backend.Null, got {storedDesc.Backend}.");
        return ShaderModules.Add(new ShaderModuleRecord(storedDesc));
    }

    public BindingLayoutHandle CreateBindingLayout(BindingLayoutDesc desc)
    {
        ThrowIfDisposed();
        var storedDesc = Snapshot(desc);
        Validation.BindingLayoutDesc(storedDesc, Limits, Features);
        return BindingLayouts.Add(new BindingLayoutRecord(storedDesc, CreateLayoutSignature(storedDesc)));
    }

    public BindingLayoutHandle CreateBindingLayout(ReadOnlySpan<BindingSlotDesc> slots, string name = "")
        => CreateBindingLayout(new BindingLayoutDesc { Name = name, Slots = slots.ToArray() });

    public PipelineLayoutHandle CreatePipelineLayout(PipelineLayoutDesc desc)
    {
        ThrowIfDisposed();
        var storedDesc = Snapshot(desc);
        Validation.PipelineLayoutDesc(storedDesc, Limits);
        foreach (var layout in storedDesc.BindingLayouts)
            BindingLayouts.Get(layout, "BindingLayout");
        ValidateStaticSamplers(storedDesc);
        return PipelineLayouts.Add(new PipelineLayoutRecord(storedDesc));
    }

    public PipelineLayoutHandle CreatePipelineLayout(
        ReadOnlySpan<BindingLayoutHandle> bindingLayouts,
        ReadOnlySpan<PushRangeDesc> pushConstants = default,
        ReadOnlySpan<StaticSamplerDesc> staticSamplers = default,
        string name = "")
        => CreatePipelineLayout(
            new PipelineLayoutDesc
            {
                Name = name,
                BindingLayouts = bindingLayouts.ToArray(),
                PushConstants = pushConstants.ToArray(),
                StaticSamplers = staticSamplers.ToArray(),
            });

    public BindingSetHandle CreateBindingSet(BindingLayoutHandle layout, BindingSetDesc desc)
        => CreateBindingSet(
            layout,
            desc.Resources,
            desc.Name,
            desc.Flags);

    public BindingSetHandle CreateBindingSet(
        BindingLayoutHandle layout,
        BindingResourceDesc[] resources,
        string name = "",
        BindingSetFlags flags = BindingSetFlags.None)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resources);
        var bindingLayout = BindingLayouts.Get(layout, "BindingLayout");
        var storedDesc = new BindingSetDesc
        {
            Name = name,
            Flags = flags,
            Resources = resources,
        };
        Validation.BindingSetDesc(storedDesc);
        ValidateBindingSet(bindingLayout.Desc, storedDesc);
        var bindStates = BuildBindStates(storedDesc.Resources);
        return BindingSets.Add(new BindingSetRecord(layout, bindingLayout.Signature, storedDesc, bindStates));
    }

    public void UpdateBindingSet(BindingSetHandle bindingSet, ReadOnlySpan<BindingResourceDesc> resources)
    {
        ThrowIfDisposed();
        var record = BindingSets.Get(bindingSet, "BindingSet");
        if (!record.Desc.Flags.HasFlag(BindingSetFlags.Mutable))
            throw new RhiException(ErrorCode.ValidationFailure, "UpdateBindingSet requires the binding set to be created with BindingSetFlags.Mutable.");
        CheckSetFree(bindingSet);
        var layout = BindingLayouts.Get(record.Layout, "BindingLayout");
        RhiBindingValidation.ValidateUpdates(layout.Desc, resources);
        var updated = record.Desc with { Resources = RhiBindingValidation.MergeResources(record.Desc.Resources, resources) };
        ValidateBindingSet(layout.Desc, updated);
        BindingSets.Set(
            bindingSet,
            new BindingSetRecord(
                record.Layout,
                record.LayoutSignature,
                updated,
                BuildBindStates(updated.Resources)),
            "BindingSet");
    }

    public PipelineCacheHandle CreatePipelineCache(PipelineCacheDesc desc)
    {
        ThrowIfDisposed();
        Validation.PipelineCacheDesc(desc);
        byte[] initialData = desc.InitialData.ToArray();
        return PipelineCaches.Add(new PipelineCacheRecord(desc with { InitialData = initialData }, initialData));
    }

    public byte[] GetPipelineData(PipelineCacheHandle cache)
    {
        ThrowIfDisposed();
        return PipelineCaches.Get(cache, "PipelineCache").Data.ToArray();
    }

    public PipelineHandle CreateComputePipeline(ComputePipelineDesc desc)
    {
        ThrowIfDisposed();
        PipelineLayouts.Get(desc.Layout, "PipelineLayout");
        if (desc.PipelineCache.IsValid)
            PipelineCaches.Get(desc.PipelineCache, "PipelineCache");
        var shader = ShaderModules.Get(desc.ComputeShader, "ShaderModule").Desc;
        if (shader.Stage != ShaderStage.Compute)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Compute pipeline requires a compute shader.");
        return Pipelines.Add(new PipelineRecord(PipelineKind.Compute, desc.Layout, desc.Name));
    }

    public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDesc desc)
    {
        ThrowIfDisposed();
        var storedDesc = Snapshot(desc);
        ValidatePipelineEnums(storedDesc);
        if (storedDesc.ColorFormats.Count == 0 && storedDesc.DepthStencilFormat == Format.Unknown)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline requires at least one color or depth format.");
        if (storedDesc.SampleCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline sample count must be greater than zero.");
        if (storedDesc.Multisample.SampleCount == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline multisample count must be greater than zero.");
        if (storedDesc.SampleCount != storedDesc.Multisample.SampleCount)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline SampleCount must match Multisample.SampleCount.");
        if (storedDesc.Blend.Targets.Count != 0 && storedDesc.Blend.Targets.Count != storedDesc.ColorFormats.Count)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Blend target count must match color attachment count.");
        if (storedDesc.Blend.Targets.Count > 1 && !Features.IndependentBlend)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Independent blend is not supported by this device.");
        if (storedDesc.PipelineCache.IsValid)
            PipelineCaches.Get(storedDesc.PipelineCache, "PipelineCache");
        ValidateShaderStages(storedDesc);
        ValidateVertexInput(storedDesc);
        PipelineLayouts.Get(storedDesc.Layout, "PipelineLayout");
        var vertex = ShaderModules.Get(storedDesc.VertexShader, "Vertex Shader").Desc;
        var pixel = ShaderModules.Get(storedDesc.PixelShader, "Pixel Shader").Desc;
        if (vertex.Stage != ShaderStage.Vertex)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline vertex shader handle does not reference a vertex shader.");
        if (pixel.Stage != ShaderStage.Pixel)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline pixel shader handle does not reference a pixel shader.");
        foreach (var format in storedDesc.ColorFormats)
        {
            if (format == Format.Unknown || Validation.IsDepthFormat(format))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Invalid graphics color format {format}.");
            if (!GetFormatCapabilities(format).Support.HasFlag(FormatSupport.RenderTarget))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Graphics color format {format} does not support render targets.");
        }
        if (storedDesc.DepthStencilFormat != Format.Unknown && !Validation.IsDepthFormat(storedDesc.DepthStencilFormat))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Depth/stencil format must be a depth format, got {storedDesc.DepthStencilFormat}.");
        if (storedDesc.DepthStencilFormat != Format.Unknown
            && !GetFormatCapabilities(storedDesc.DepthStencilFormat).Support.HasFlag(FormatSupport.DepthStencil))
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Depth/stencil format {storedDesc.DepthStencilFormat} does not support depth/stencil usage.");
        }
        return Pipelines.Add(new PipelineRecord(PipelineKind.Graphics, storedDesc.Layout, storedDesc.Name, storedDesc));
    }

    public PipelineHandle CreateGraphicsPipeline(
        GraphicsPipelineDesc desc,
        ReadOnlySpan<VertexLayoutDesc> vertexBuffers,
        ReadOnlySpan<VertexAttributeDesc> vertexAttributes,
        ReadOnlySpan<Format> colorFormats,
        ReadOnlySpan<BlendTargetDesc> blendTargets)
        => CreateGraphicsPipeline(
            desc with
            {
                VertexBuffers = vertexBuffers.ToArray(),
                VertexAttributes = vertexAttributes.ToArray(),
                ColorFormats = colorFormats.ToArray(),
                Blend = desc.Blend with { Targets = blendTargets.ToArray() },
            });

    public PipelineHandle CreateMeshPipeline(MeshPipelineDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.MeshShader)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Mesh pipelines require mesh shader support.");
        Validation.MeshPipelineDesc(desc);
        var storedDesc = desc with
        {
            ColorFormats = desc.ColorFormats.ToArray(),
            Blend = desc.Blend with { Targets = desc.Blend.Targets.ToArray() },
        };
        PipelineLayouts.Get(storedDesc.Layout, "PipelineLayout");
        EnsureShaderStage(storedDesc.MeshShader, ShaderStage.Mesh, "Mesh pipeline mesh shader");
        if (storedDesc.AmplificationShader.IsValid)
            EnsureShaderStage(storedDesc.AmplificationShader, ShaderStage.Amplification, "Mesh pipeline amplification shader");
        if (storedDesc.PixelShader.IsValid)
            EnsureShaderStage(storedDesc.PixelShader, ShaderStage.Pixel, "Mesh pipeline pixel shader");
        ValidatePipelineFormats(storedDesc.ColorFormats, storedDesc.DepthStencilFormat);
        return Pipelines.Add(new PipelineRecord(PipelineKind.Mesh, storedDesc.Layout, storedDesc.Name, MeshDesc: storedDesc));
    }

    public PipelineHandle CreateRtPipeline(RtPipelineDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing pipelines require ray tracing support.");
        Validation.RtPipelineDesc(desc, Limits);
        var storedDesc = desc with
        {
            Shaders = desc.Shaders.ToArray(),
            ShaderGroups = desc.ShaderGroups.Select(group => group with { }).ToArray(),
        };
        PipelineLayouts.Get(storedDesc.Layout, "PipelineLayout");
        ValidateClusterShaders(storedDesc);
        return Pipelines.Add(new PipelineRecord(PipelineKind.RayTracing, storedDesc.Layout, storedDesc.Name, RayTracingDesc: storedDesc));
    }

    public int GetRtSize(PipelineHandle pipeline)
    {
        ThrowIfDisposed();
        var record = Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.RayTracing)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader identifiers require a ray tracing pipeline.");
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing shader identifiers require ray tracing support.");
        return checked((int)Limits.RayTracingShaderIdentifierSizeInBytes);
    }

    public void GetRtId(PipelineHandle pipeline, string shaderGroupName, Span<byte> destination)
    {
        ThrowIfDisposed();
        var record = Pipelines.Get(pipeline, "Pipeline");
        if (record.Kind != PipelineKind.RayTracing || record.RayTracingDesc == null)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader identifiers require a ray tracing pipeline.");
        if (string.IsNullOrWhiteSpace(shaderGroupName))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader group name must be specified.");
        int size = checked((int)Limits.RayTracingShaderIdentifierSizeInBytes);
        if (destination.Length < size)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader identifier destination must be at least {size} bytes.");
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Ray tracing shader identifiers require ray tracing support.");
        bool found = false;
        foreach (var group in record.RayTracingDesc.ShaderGroups)
        {
            if (group.Name == shaderGroupName)
            {
                found = true;
                break;
            }
        }
        if (!found)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group '{shaderGroupName}' does not exist in the pipeline.");

        Span<byte> identifier = destination[..size];
        identifier.Clear();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{record.Name}:{shaderGroupName}"));
        hash.AsSpan(0, Math.Min(hash.Length, identifier.Length)).CopyTo(identifier);
    }

    public AccelBuildSizes GetAccelSizes(AccelBuildDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structure builds require ray tracing support.");
        Validation.AccelBuildDesc(desc, requireResourceHandles: false);
        ulong primitiveCount = 0;
        foreach (var geometry in desc.Geometries)
        {
            primitiveCount += geometry.Kind switch
            {
                AccelGeomKind.Triangles => geometry.IndexCount == 0 ? geometry.VertexCount / 3 : geometry.IndexCount / 3,
                AccelGeomKind.Aabbs => geometry.AabbCount,
                AccelGeomKind.Instances => geometry.InstanceCount,
                _ => 0,
            };
        }
        ulong resultSize = AlignUp(checked(4096 + primitiveCount * 128 + (ulong)desc.Geometries.Count * 256), Limits.AccelerationStructureAlignment);
        ulong buildScratch = AlignUp(Math.Max(4096, resultSize / 2), Limits.AccelerationStructureAlignment);
        ulong updateScratch = desc.Flags.HasFlag(AccelBuildFlags.AllowUpdate)
            ? AlignUp(Math.Max(2048, resultSize / 4), Limits.AccelerationStructureAlignment)
            : 0;
        return new AccelBuildSizes(resultSize, buildScratch, updateScratch);
    }

    public AccelerationStructureHandle CreateAccelerationStructure(AccelerationStructureDesc desc)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structures require ray tracing support.");
        Validation.AccelerationStructureDesc(desc);
        if (desc.SizeInBytes % Limits.AccelerationStructureAlignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Acceleration structure size must be aligned to {Limits.AccelerationStructureAlignment} bytes.");
        return AccelerationStructures.Add(new AccelerationStructureRecord(desc with { }));
    }

    public ICommandList CreateCommandList(CommandListDesc desc)
    {
        ThrowIfDisposed();
        Validation.CommandListDesc(desc);
        return new NullCommandList(this, desc.Name, desc.QueueType);
    }

    public FenceHandle CreateFence(string name, ulong initialValue = 0)
    {
        ThrowIfDisposed();
        return Fences.Add(new FenceRecord(name, initialValue));
    }

    public QueryPoolHandle CreateQueryPool(QueryPoolDesc desc)
    {
        ThrowIfDisposed();
        Validation.QueryPoolDesc(desc);
        if (desc.Count > 4096)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Null backend query pools are limited to 4096 queries.");
        if (desc.Type == QueryType.Occlusion && !Features.OcclusionQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Occlusion queries are not supported by this device.");
        if (desc.Type == QueryType.PipelineStatistics && !Features.PipelineStatisticsQueries)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Pipeline statistics queries are not supported by this device.");
        return QueryPools.Add(new QueryPoolRecord(desc));
    }

    public SwapchainHandle CreateSwapchain(SwapchainDesc desc)
    {
        ThrowIfDisposed();
        Validation.SwapchainDesc(desc);
        if (desc.ColorSpace != ColorSpace.Sdr)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Null swapchain color space {desc.ColorSpace} is not supported.");
        if (desc.Mode != SwapchainMode.Windowed)
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Null swapchain mode {desc.Mode} is not supported.");
        if (desc.RefreshRate.Numerator != 0 || desc.RefreshRate.Denominator != 0)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Null swapchain explicit refresh rates are not supported.");
        var textureDesc = SwapchainTextureDesc(desc);
        ValidateTexFormat(textureDesc);
        ValidateTextureLimits(textureDesc);
        var textures = new TextureHandle[desc.BufferCount];
        var views = new TextureViewHandle[desc.BufferCount];
        for (uint index = 0; index < desc.BufferCount; index++)
        {
            textures[index] = Textures.Add(new TextureRecord(
                textureDesc,
                new ResourceAllocationInfo(ResourceOwnership.Swapchain, textureDesc.Memory, MemoryHeapHandle.Invalid, 0, EstimateTextureSize(textureDesc))));
            views[index] = TextureViews.Add(new TextureViewRecord(
                textures[index],
                new TextureViewDesc
                {
                    Name = $"{desc.Name} RTV {index}",
                    Kind = ViewKind.RenderTarget,
                    Format = desc.Format,
                },
                ResourceOwnership.Swapchain));
        }

        return Swapchains.Add(new SwapchainRecord(desc with { }, textures, views, textureDesc));
    }

    public ISwapchain GetSwapchain(SwapchainHandle handle)
    {
        ThrowIfDisposed();
        Swapchains.Get(handle, "Swapchain");
        return new NullSwapchain(this, handle);
    }

    public BufferDesc GetBufferDesc(BufferHandle buffer)
    {
        ThrowIfDisposed();
        return Buffers.Get(buffer, "Buffer").Desc;
    }

    public TextureDesc GetTextureDesc(TextureHandle texture)
    {
        ThrowIfDisposed();
        return Textures.Get(texture, "Texture").Desc;
    }

    public TextureViewDesc GetTextureViewDesc(TextureViewHandle view)
    {
        ThrowIfDisposed();
        return TextureViews.Get(view, "TextureView").Desc;
    }

    public BufferViewDesc GetBufferViewDesc(BufferViewHandle view)
    {
        ThrowIfDisposed();
        return BufferViews.Get(view, "BufferView").Desc;
    }

    public bool TryGetTextureViewOwner(TextureViewHandle view, out TextureHandle texture, out TextureViewDesc desc)
    {
        ThrowIfDisposed();
        if (!TextureViews.IsAlive(view))
        {
            texture = default;
            desc = default!;
            return false;
        }

        TextureViewRecord record = TextureViews.Get(view, "TextureView");
        texture = record.Texture;
        desc = record.Desc;
        return true;
    }

    public bool TryGetBufferViewOwner(BufferViewHandle view, out BufferHandle buffer, out BufferViewDesc desc)
    {
        ThrowIfDisposed();
        if (!BufferViews.IsAlive(view))
        {
            buffer = default;
            desc = default!;
            return false;
        }

        BufferViewRecord record = BufferViews.Get(view, "BufferView");
        buffer = record.Buffer;
        desc = record.Desc;
        return true;
    }

    public ResourceAllocationInfo GetBufferAlloc(BufferHandle buffer)
    {
        ThrowIfDisposed();
        return Buffers.Get(buffer, "Buffer").Allocation;
    }

    public ResourceAllocationInfo GetTextureAlloc(TextureHandle texture)
    {
        ThrowIfDisposed();
        return Textures.Get(texture, "Texture").Allocation;
    }

    public ResourceState GetBufferState(BufferHandle buffer)
    {
        ThrowIfDisposed();
        return Buffers.Get(buffer, "Buffer").State;
    }

    public ResourceState GetTextureState(TextureHandle texture, uint mipLevel = 0, uint arraySlice = 0)
    {
        ThrowIfDisposed();
        return Textures.Get(texture, "Texture").GetState(mipLevel, arraySlice);
    }

    public FormatSupport GetFormatSupport(Format format)
    {
        ThrowIfDisposed();
        return GetFormatCapabilities(format).Support;
    }

    public FormatCapabilities GetFormatCapabilities(Format format)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(format))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {format} is not defined.");
        return format switch
        {
            Format.Unknown => UnknownFormatCapabilities,
            Format.D32Float or Format.D24UnormS8UInt => DepthFormatCapabilities,
            Format.Rgba8Unorm
            or Format.Rgba8UnormSrgb
            or Format.Bgra8Unorm
            or Format.Bgra8UnormSrgb
            or Format.Rgb10A2Unorm
            or Format.R8Unorm
            or Format.R8UInt
            or Format.R16UInt
            or Format.R16Float
            or Format.Rg8Unorm
            or Format.Rg16UInt
            or Format.Rgba16Float
            or Format.R32UInt
            or Format.R32Float
            or Format.Rg16Float
            or Format.Rgb32Float
            or Format.Rg32Float
            or Format.Rgba32Float => ColorFormatCapabilities,
            Format.Bc1RgbaUnorm
            or Format.Bc1RgbaUnormSrgb
            or Format.Bc2RgbaUnorm
            or Format.Bc2RgbaUnormSrgb
            or Format.Bc3RgbaUnorm
            or Format.Bc3RgbaUnormSrgb
            or Format.Bc4RUnorm
            or Format.Bc5RgUnorm
            or Format.Bc6HUFloat
            or Format.Bc7RgbaUnorm
            or Format.Bc7RgbaUnormSrgb => CompressedFormatCapabilities,
            _ => UnknownFormatCapabilities,
        };
    }

    public ulong GetFenceValue(FenceHandle fence)
    {
        ThrowIfDisposed();
        return Fences.Get(fence, "Fence").CompletedValue;
    }

    public void WaitFence(FenceHandle fence, ulong value, ulong timeoutNanoseconds = ulong.MaxValue)
    {
        ThrowIfDisposed();
        var record = Fences.Get(fence, "Fence");
        if (record.CompletedValue < value)
            throw new RhiException(ErrorCode.ValidationFailure, $"Fence has completed {record.CompletedValue}, which is below requested value {value}.");
    }

    public Memory<byte> MapBuffer(BufferHandle buffer, MapMode mode, ulong offset = 0, int sizeInBytes = -1)
    {
        ThrowIfDisposed();
        var record = Buffers.Get(buffer, "Buffer");
        if (record.Mapped)
            throw new RhiException(ErrorCode.ValidationFailure, "Buffer is already mapped.");
        if (mode != MapMode.Write && mode != MapMode.Read)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Map mode value {mode} is not defined.");
        if (mode == MapMode.Write && record.Desc.Memory != MemoryClass.CpuUpload)
            throw new RhiException(ErrorCode.ValidationFailure, "Write mapping requires CpuUpload memory.");
        if (mode == MapMode.Read && record.Desc.Memory != MemoryClass.CpuReadback)
            throw new RhiException(ErrorCode.ValidationFailure, "Read mapping requires CpuReadback memory.");
        if (offset > record.Desc.SizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Map offset is outside the buffer.");
        ulong mappedSize = sizeInBytes < 0
            ? record.Desc.SizeInBytes - offset
            : checked((ulong)sizeInBytes);
        if (mappedSize == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mapped range size must be greater than zero.");
        if (mappedSize > record.Desc.SizeInBytes - offset)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Mapped range is outside the buffer.");
        if (mappedSize > int.MaxValue)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Null backend mapped ranges are limited to 2GB.");
        record.Mapped = true;
        record.MappedOffset = offset;
        record.MappedSize = mappedSize;
        return record.Data.AsMemory(checked((int)offset), checked((int)mappedSize));
    }

    public void FlushBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)
    {
        ThrowIfDisposed();
        ValidateMappedRange(buffer, offset, sizeInBytes, "FlushBufferRange");
    }

    public void InvalidateBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)
    {
        ThrowIfDisposed();
        ValidateMappedRange(buffer, offset, sizeInBytes, "InvalidateBufferRange");
    }

    public void UnmapBuffer(BufferHandle buffer)
    {
        ThrowIfDisposed();
        var record = Buffers.Get(buffer, "Buffer");
        if (!record.Mapped)
            throw new RhiException(ErrorCode.ValidationFailure, "Buffer is not mapped.");
        record.Mapped = false;
        record.MappedOffset = 0;
        record.MappedSize = 0;
    }

    public void Destroy(BufferHandle handle) => DestroyBuffer(handle);
    public void Destroy(TextureHandle handle) => DestroyTexture(handle);
    public void Destroy(TextureViewHandle handle) => DestroyTextureView(handle);
    public void Destroy(BufferViewHandle handle) => DestroyBufferView(handle);
    public void Destroy(SamplerHandle handle) => DestroySampler(handle);
    public void Destroy(ShaderModuleHandle handle) => DestroyShaderModule(handle);
    public void Destroy(BindingLayoutHandle handle) => DestroyBindingLayout(handle);
    public void Destroy(PipelineLayoutHandle handle) => DestroyPipelineLayout(handle);
    public void Destroy(BindingSetHandle handle) => DestroyBindingSet(handle);
    public void Destroy(PipelineHandle handle) => DestroyPipeline(handle);
    public void Destroy(PipelineCacheHandle handle) => DestroyPipelineCache(handle);
    public void Destroy(AccelerationStructureHandle handle) => DestroyAccelerationStructure(handle);
    public void Destroy(CommandBufferHandle handle) => DestroyCommandBuffer(handle);
    public void Destroy(FenceHandle handle) => DestroyFence(handle);
    public void Destroy(QueryPoolHandle handle) => DestroyQueryPool(handle);
    public void Destroy(SwapchainHandle handle) => DestroySwapchain(handle);
    public void Destroy(MemoryHeapHandle handle) => DestroyMemoryHeap(handle);

    private void DestroyBuffer(BufferHandle handle)
    {
        ThrowIfDisposed();
        var record = Buffers.Get(handle, "Buffer");
        CheckBufferViews(handle);
        CheckBufferFree(handle);
        Buffers.Destroy(handle, "Buffer");
        ReleaseAllocation(record.Allocation, AliasingResource.BufferResource(handle));
    }

    private void DestroyTexture(TextureHandle handle)
    {
        ThrowIfDisposed();
        var texture = Textures.Get(handle, "Texture");
        if (texture.Allocation.Ownership == ResourceOwnership.Swapchain)
            throw new RhiException(ErrorCode.ValidationFailure, "Swapchain textures cannot be destroyed through DestroyTexture.");
        CheckTextureViews(handle);
        CheckTextureFree(handle);
        Textures.Destroy(handle, "Texture");
        ReleaseAllocation(texture.Allocation, AliasingResource.TextureResource(handle));
    }

    private void DestroyTextureView(TextureViewHandle handle)
    {
        ThrowIfDisposed();
        var view = TextureViews.Get(handle, "TextureView");
        if (view.Ownership == ResourceOwnership.Swapchain)
            throw new RhiException(ErrorCode.ValidationFailure, "Swapchain render-target views cannot be destroyed through DestroyTextureView.");
        CheckTvRefs(handle);
        CheckTvFree(handle);
        TextureViews.Destroy(handle, "TextureView");
    }

    private void DestroyBufferView(BufferViewHandle handle)
    {
        ThrowIfDisposed();
        BufferViews.Get(handle, "BufferView");
        CheckBvRefs(handle);
        CheckBvFree(handle);
        BufferViews.Destroy(handle, "BufferView");
    }

    private void DestroySampler(SamplerHandle handle)
    {
        ThrowIfDisposed();
        Samplers.Get(handle, "Sampler");
        CheckSamplerRefs(handle);
        CheckSamplerFree(handle);
        Samplers.Destroy(handle, "Sampler");
    }

    private void DestroyShaderModule(ShaderModuleHandle handle)
    {
        ThrowIfDisposed();
        ShaderModules.Destroy(handle, "ShaderModule");
    }

    private void DestroyBindingLayout(BindingLayoutHandle handle)
    {
        ThrowIfDisposed();
        BindingLayouts.Get(handle, "BindingLayout");
        CheckLayoutFree(handle);
        BindingLayouts.Destroy(handle, "BindingLayout");
    }

    private void DestroyPipelineLayout(PipelineLayoutHandle handle)
    {
        ThrowIfDisposed();
        PipelineLayouts.Get(handle, "PipelineLayout");
        CheckLayoutFree(handle);
        PipelineLayouts.Destroy(handle, "PipelineLayout");
    }

    private void DestroyBindingSet(BindingSetHandle handle)
    {
        ThrowIfDisposed();
        BindingSets.Get(handle, "BindingSet");
        CheckSetFree(handle);
        BindingSets.Destroy(handle, "BindingSet");
    }

    private void DestroyPipeline(PipelineHandle handle)
    {
        ThrowIfDisposed();
        Pipelines.Get(handle, "Pipeline");
        CheckPipelineFree(handle);
        Pipelines.Destroy(handle, "Pipeline");
    }

    private void DestroyPipelineCache(PipelineCacheHandle handle)
    {
        ThrowIfDisposed();
        PipelineCaches.Destroy(handle, "PipelineCache");
    }

    private void DestroyAccelerationStructure(AccelerationStructureHandle handle)
    {
        ThrowIfDisposed();
        if (!Features.RayTracing)
            throw new RhiException(ErrorCode.UnsupportedFeature, "Acceleration structures require ray tracing support.");
        AccelerationStructures.Get(handle, "AccelerationStructure");
        CheckAccelerationReferences(handle);
        CheckAccelerationFree(handle);
        AccelerationStructures.Destroy(handle, "AccelerationStructure");
    }

    private void DestroyCommandBuffer(CommandBufferHandle handle)
    {
        ThrowIfDisposed();
        var commandBuffer = CommandBuffers.Get(handle, "CommandBuffer");
        CommandBuffers.Destroy(handle, "CommandBuffer");
        commandBuffer.Dispose();
    }

    private void DestroyFence(FenceHandle handle)
    {
        ThrowIfDisposed();
        Fences.Destroy(handle, "Fence");
    }

    private void DestroyQueryPool(QueryPoolHandle handle)
    {
        ThrowIfDisposed();
        QueryPools.Get(handle, "QueryPool");
        CheckQueryFree(handle);
        QueryPools.Destroy(handle, "QueryPool");
    }

    private void DestroySwapchain(SwapchainHandle handle)
    {
        ThrowIfDisposed();
        var swapchain = Swapchains.Get(handle, "Swapchain");
        for (int index = 0; index < swapchain.RenderTargetViews.Length; index++)
        {
            CheckTvFree(swapchain.RenderTargetViews[index]);
            CheckTextureFree(swapchain.Textures[index]);
            TextureViews.Destroy(swapchain.RenderTargetViews[index], "TextureView");
            Textures.Destroy(swapchain.Textures[index], "Texture");
        }

        Swapchains.Destroy(handle, "Swapchain");
    }

    private void DestroyMemoryHeap(MemoryHeapHandle handle)
    {
        ThrowIfDisposed();
        var record = MemoryHeaps.Get(handle, "MemoryHeap");
        if (record.LiveResourceCount != 0)
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a memory heap while placed resources are still alive.");
        MemoryHeaps.Destroy(handle, "MemoryHeap");
    }

    public void WaitIdle()
    {
        ThrowIfDisposed();
    }

    internal void ValidateSwapchain(SwapchainRecord swapchain)
    {
        for (int index = 0; index < swapchain.RenderTargetViews.Length; index++)
        {
            CheckTvFree(swapchain.RenderTargetViews[index]);
            CheckTextureFree(swapchain.Textures[index]);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        CommandBuffers.Clear(static commandBuffer => commandBuffer.Dispose());
        if (_cachedCommandOperations != null)
        {
            System.Buffers.ArrayPool<NullCommandOperation>.Shared.Return(_cachedCommandOperations);
            _cachedCommandOperations = null;
        }
        if (_cachedBindingResources != null)
        {
            System.Buffers.ArrayPool<BindingResourceDesc>.Shared.Return(_cachedBindingResources);
            _cachedBindingResources = null;
        }
        _disposed = true;
    }

    internal void SetTextureState(TextureHandle handle, ResourceState state)
        => Textures.Get(handle, "Texture").SetState(SubresourceRange.All, state);

    internal void SetTextureState(TextureHandle handle, SubresourceRange range, ResourceState state)
        => Textures.Get(handle, "Texture").SetState(range, state);

    internal void SetBufferState(BufferHandle handle, ResourceState state)
        => Buffers.Get(handle, "Buffer").State = state;

    internal ulong NextTimestamp() => ++_timestamp;

    internal CommandBufferHandle AddCommandBuffer(
        string name,
        QueueType queueType,
        NullCommandOperation[] operations,
        int operationCount,
        bool operationsArePooled,
        BindingResourceDesc[] bindingResources,
        int bindingResourceCount,
        bool bindingResourcesArePooled)
    {
        CollectCmdRefs(
            operations.AsSpan(0, operationCount),
            bindingResources.AsSpan(0, bindingResourceCount),
            out var buffers,
            out var textures,
            out var queryPools,
            out var textureViews,
            out var bufferViews,
            out var samplers,
            out var accelerationStructures,
            out var bindingSets,
            out var bindingLayouts,
            out var pipelineLayouts,
            out var pipelines);
        return CommandBuffers.Add(new CommandBufferRecord(
            this,
            name,
            queueType,
            operations,
            operationCount,
            operationsArePooled,
            bindingResources,
            bindingResourceCount,
            bindingResourcesArePooled,
            buffers,
            textures,
            queryPools,
            textureViews,
            bufferViews,
            samplers,
            accelerationStructures,
            bindingSets,
            bindingLayouts,
            pipelineLayouts,
            pipelines));
    }

    internal void Pin(BufferHandle buffer) => ActiveHandles.Retain(_activeBuffers, buffer);

    internal void Pin(TextureHandle texture) => ActiveHandles.Retain(_activeTextures, texture);

    internal void Pin(QueryPoolHandle queryPool) => ActiveHandles.Retain(_activeQueryPools, queryPool);

    internal void Pin(TextureViewHandle view) => ActiveHandles.Retain(_activeTextureViews, view);

    internal void Pin(BufferViewHandle view) => ActiveHandles.Retain(_activeBufferViews, view);

    internal void Pin(SamplerHandle sampler) => ActiveHandles.Retain(_activeSamplers, sampler);

    internal void Pin(AccelerationStructureHandle accelerationStructure)
        => ActiveHandles.Retain(_activeAccelerationStructures, accelerationStructure);

    internal void Pin(BindingLayoutHandle bindingLayout)
        => ActiveHandles.Retain(_activeBindingLayouts, bindingLayout);

    internal void Pin(BindingSetHandle bindingSet)
        => ActiveHandles.Retain(_activeBindingSets, bindingSet);

    internal void Pin(PipelineLayoutHandle pipelineLayout)
        => ActiveHandles.Retain(_activePipelineLayouts, pipelineLayout);

    internal void Pin(PipelineHandle pipeline) => ActiveHandles.Retain(_activePipelines, pipeline);

    internal void Unpin(IEnumerable<BufferHandle> buffers)
        => ActiveHandles.Release(_activeBuffers, buffers, "Null", "buffer");

    internal void Unpin(IEnumerable<TextureHandle> textures)
        => ActiveHandles.Release(_activeTextures, textures, "Null", "texture");

    internal void Unpin(IEnumerable<QueryPoolHandle> queryPools)
        => ActiveHandles.Release(_activeQueryPools, queryPools, "Null", "query-pool");

    internal void Unpin(IEnumerable<TextureViewHandle> views)
        => ActiveHandles.Release(_activeTextureViews, views, "Null", "texture-view");

    internal void Unpin(IEnumerable<BufferViewHandle> views)
        => ActiveHandles.Release(_activeBufferViews, views, "Null", "buffer-view");

    internal void Unpin(IEnumerable<SamplerHandle> samplers)
        => ActiveHandles.Release(_activeSamplers, samplers, "Null", "sampler");

    internal void Unpin(IEnumerable<AccelerationStructureHandle> accelerationStructures)
        => ActiveHandles.Release(_activeAccelerationStructures, accelerationStructures, "Null", "acceleration-structure");

    internal void Unpin(IEnumerable<BindingLayoutHandle> bindingLayouts)
        => ActiveHandles.Release(_activeBindingLayouts, bindingLayouts, "Null", "binding-layout");

    internal void Unpin(IEnumerable<BindingSetHandle> bindingSets)
        => ActiveHandles.Release(_activeBindingSets, bindingSets, "Null", "binding-set");

    internal void Unpin(IEnumerable<PipelineLayoutHandle> pipelineLayouts)
        => ActiveHandles.Release(_activePipelineLayouts, pipelineLayouts, "Null", "pipeline-layout");

    internal void Unpin(IEnumerable<PipelineHandle> pipelines)
        => ActiveHandles.Release(_activePipelines, pipelines, "Null", "pipeline");

    private void CollectCmdRefs(
        ReadOnlySpan<NullCommandOperation> operations,
        ReadOnlySpan<BindingResourceDesc> bindingResources,
        out BufferHandle[] buffers,
        out TextureHandle[] textures,
        out QueryPoolHandle[] queryPools,
        out TextureViewHandle[] textureViews,
        out BufferViewHandle[] bufferViews,
        out SamplerHandle[] samplers,
        out AccelerationStructureHandle[] accelerationStructures,
        out BindingSetHandle[] bindingSets,
        out BindingLayoutHandle[] bindingLayouts,
        out PipelineLayoutHandle[] pipelineLayouts,
        out PipelineHandle[] pipelines)
    {
        List<BufferHandle> bufferRefs = [];
        List<TextureHandle> textureRefs = [];
        List<QueryPoolHandle> queryPoolRefs = [];
        List<TextureViewHandle> textureViewRefs = [];
        List<BufferViewHandle> bufferViewRefs = [];
        List<SamplerHandle> samplerRefs = [];
        List<AccelerationStructureHandle> accelerationStructureRefs = [];
        List<BindingSetHandle> bindingSetRefs = [];
        List<BindingLayoutHandle> bindingLayoutRefs = [];
        List<PipelineLayoutHandle> pipelineLayoutRefs = [];
        List<PipelineHandle> pipelineRefs = [];

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case NullOperationKind.TextureBarrier:
                    AddUnique(textureRefs, operation.TextureBarrierValue.Texture);
                    break;
                case NullOperationKind.BufferBarrier:
                    AddUnique(bufferRefs, operation.BufferBarrierValue.Buffer);
                    break;
                case NullOperationKind.CopyBuffer:
                    AddUnique(bufferRefs, operation.SourceBuffer);
                    AddUnique(bufferRefs, operation.DestinationBuffer);
                    break;
                case NullOperationKind.CopyToTexture:
                    AddUnique(bufferRefs, operation.SourceBuffer);
                    AddUnique(textureRefs, operation.DestinationTexture);
                    break;
                case NullOperationKind.CopyToBuffer:
                    AddUnique(textureRefs, operation.SourceTexture);
                    AddUnique(bufferRefs, operation.DestinationBuffer);
                    break;
                case NullOperationKind.CopyTexture:
                case NullOperationKind.ResolveTexture:
                    AddUnique(textureRefs, operation.SourceTexture);
                    AddUnique(textureRefs, operation.DestinationTexture);
                    break;
                case NullOperationKind.WriteTimestamp:
                case NullOperationKind.BeginQuery:
                case NullOperationKind.EndQuery:
                    AddUnique(queryPoolRefs, operation.QueryPool);
                    break;
                case NullOperationKind.ResolveQueryData:
                    AddUnique(queryPoolRefs, operation.QueryPool);
                    AddUnique(bufferRefs, operation.DestinationBuffer);
                    break;
                case NullOperationKind.AliasingBarrier:
                    AddAliasRef(operation.AliasingBarrierValue.Before, bufferRefs, textureRefs);
                    AddAliasRef(operation.AliasingBarrierValue.After, bufferRefs, textureRefs);
                    break;
                case NullOperationKind.PipelineDependency:
                case NullOperationKind.RenderPipelineDependency:
                    AddUnique(pipelineRefs, operation.Pipeline);
                    AddUnique(pipelineLayoutRefs, Pipelines.Get(operation.Pipeline, "Pipeline").Layout);
                    break;
                case NullOperationKind.TextureDependency:
                    AddUnique(textureRefs, operation.Texture);
                    break;
                case NullOperationKind.TextureViewDependency:
                {
                    var view = TextureViews.Get(operation.TextureView, "TextureView");
                    AddUnique(textureViewRefs, operation.TextureView);
                    AddUnique(textureRefs, view.Texture);
                    break;
                }
                case NullOperationKind.BufferDependency:
                    AddUnique(bufferRefs, operation.Buffer);
                    break;
                case NullOperationKind.AccelerationStructureDependency:
                    if (operation.SourceAccelerationStructure.IsValid)
                    {
                        AccelerationStructures.Get(operation.SourceAccelerationStructure, "SourceAccelerationStructure");
                        AddUnique(accelerationStructureRefs, operation.SourceAccelerationStructure);
                    }
                    if (operation.DestinationAccelerationStructure.IsValid)
                    {
                        AccelerationStructures.Get(operation.DestinationAccelerationStructure, "DestinationAccelerationStructure");
                        AddUnique(accelerationStructureRefs, operation.DestinationAccelerationStructure);
                    }
                    break;
                case NullOperationKind.BindingSetDependency:
                {
                    var set = BindingSets.Get(operation.BindingSet, "BindingSet");
                    AddUnique(bindingSetRefs, operation.BindingSet);
                    AddUnique(bindingLayoutRefs, set.Layout);
                    CollectBindRefs(set.Desc.Resources, bufferRefs, textureRefs, textureViewRefs, bufferViewRefs, samplerRefs, accelerationStructureRefs);
                    break;
                }
                case NullOperationKind.TransientBindingDependency:
                {
                    AddUnique(bindingLayoutRefs, operation.BindingLayout);
                    if (operation.BindingResourceOffset < 0
                        || operation.BindingResourceCount < 0
                        || operation.BindingResourceOffset > bindingResources.Length
                        || operation.BindingResourceCount > bindingResources.Length - operation.BindingResourceOffset)
                    {
                        throw new RhiException(ErrorCode.ValidationFailure, "Transient binding resource range is corrupt.");
                    }

                    CollectBindRefs(
                        bindingResources.Slice(operation.BindingResourceOffset, operation.BindingResourceCount),
                        bufferRefs,
                        textureRefs,
                        textureViewRefs,
                        bufferViewRefs,
                        samplerRefs,
                        accelerationStructureRefs);
                    break;
                }
                default:
                    throw new RhiException(ErrorCode.ValidationFailure, $"Command operation kind {operation.Kind} is not defined.");
            }
        }

        buffers = bufferRefs.ToArray();
        textures = textureRefs.ToArray();
        queryPools = queryPoolRefs.ToArray();
        textureViews = textureViewRefs.ToArray();
        bufferViews = bufferViewRefs.ToArray();
        samplers = samplerRefs.ToArray();
        accelerationStructures = accelerationStructureRefs.ToArray();
        bindingSets = bindingSetRefs.ToArray();
        bindingLayouts = bindingLayoutRefs.ToArray();
        pipelineLayouts = pipelineLayoutRefs.ToArray();
        pipelines = pipelineRefs.ToArray();
    }

    private void CollectBindRefs(
        ReadOnlySpan<BindingResourceDesc> resources,
        List<BufferHandle> buffers,
        List<TextureHandle> textures,
        List<TextureViewHandle> textureViews,
        List<BufferViewHandle> bufferViews,
        List<SamplerHandle> samplers,
        List<AccelerationStructureHandle> accelerationStructures)
    {
        foreach (var resource in resources)
            CollectBindRef(resource, buffers, textures, textureViews, bufferViews, samplers, accelerationStructures);
    }

    private void CollectBindRef(
        BindingResourceDesc resource,
        List<BufferHandle> buffers,
        List<TextureHandle> textures,
        List<TextureViewHandle> textureViews,
        List<BufferViewHandle> bufferViews,
        List<SamplerHandle> samplers,
        List<AccelerationStructureHandle> accelerationStructures)
    {
        switch (resource.ResourceType)
        {
            case BindingType.ConstantBuffer:
            case BindingType.StorageBufferRead:
            case BindingType.StorageBufferReadWrite:
            case BindingType.RawBufferRead:
            case BindingType.RawBufferReadWrite:
            {
                var view = BufferViews.Get(resource.BufferView, "BindingResourceBufferView");
                AddUnique(bufferViews, resource.BufferView);
                AddUnique(buffers, view.Buffer);
                break;
            }
            case BindingType.TextureRead:
            case BindingType.TextureReadWrite:
            {
                var view = TextureViews.Get(resource.TextureView, "BindingResourceTextureView");
                AddUnique(textureViews, resource.TextureView);
                AddUnique(textures, view.Texture);
                break;
            }
            case BindingType.Sampler:
                Samplers.Get(resource.SamplerHandle, "BindingResourceSampler");
                AddUnique(samplers, resource.SamplerHandle);
                break;
            case BindingType.AccelerationStructure:
                AccelerationStructures.Get(resource.AccelerationStructure, "BindingResourceAccelerationStructure");
                AddUnique(accelerationStructures, resource.AccelerationStructure);
                break;
            default:
                throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {resource.ResourceType} is not supported by Null backend.");
        }
    }

    private void AddAliasRef(AliasingResource resource, List<BufferHandle> buffers, List<TextureHandle> textures)
    {
        switch (resource.Kind)
        {
            case AliasingResourceKind.None:
                break;
            case AliasingResourceKind.Buffer:
                Buffers.Get(resource.Buffer, "AliasingBuffer");
                AddUnique(buffers, resource.Buffer);
                break;
            case AliasingResourceKind.Texture:
                Textures.Get(resource.Texture, "AliasingTexture");
                AddUnique(textures, resource.Texture);
                break;
            default:
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined.");
        }
    }

    private static void AddUnique<T>(List<T> values, T value)
    {
        if (!values.Contains(value))
            values.Add(value);
    }

    internal NullCommandOperation[] RentCommandOperations(int minimumLength)
    {
        var cached = _cachedCommandOperations;
        if (cached != null)
        {
            _cachedCommandOperations = null;
            if (cached.Length >= minimumLength)
                return cached;
            System.Buffers.ArrayPool<NullCommandOperation>.Shared.Return(cached);
        }

        return System.Buffers.ArrayPool<NullCommandOperation>.Shared.Rent(minimumLength);
    }

    internal void ReturnCommandOperations(NullCommandOperation[] operations)
    {
        Array.Clear(operations);
        if (_cachedCommandOperations == null)
        {
            _cachedCommandOperations = operations;
            return;
        }

        if (operations.Length > _cachedCommandOperations.Length)
        {
            System.Buffers.ArrayPool<NullCommandOperation>.Shared.Return(_cachedCommandOperations);
            _cachedCommandOperations = operations;
            return;
        }

        System.Buffers.ArrayPool<NullCommandOperation>.Shared.Return(operations);
    }

    internal BindingResourceDesc[] RentBindingResources(int minimumLength)
    {
        var cached = _cachedBindingResources;
        if (cached != null)
        {
            _cachedBindingResources = null;
            if (cached.Length >= minimumLength)
                return cached;
            System.Buffers.ArrayPool<BindingResourceDesc>.Shared.Return(cached);
        }

        return System.Buffers.ArrayPool<BindingResourceDesc>.Shared.Rent(minimumLength);
    }

    internal void ReturnBindingResources(BindingResourceDesc[] resources)
    {
        Array.Clear(resources);
        if (_cachedBindingResources == null)
        {
            _cachedBindingResources = resources;
            return;
        }

        if (resources.Length > _cachedBindingResources.Length)
        {
            System.Buffers.ArrayPool<BindingResourceDesc>.Shared.Return(_cachedBindingResources);
            _cachedBindingResources = resources;
            return;
        }

        System.Buffers.ArrayPool<BindingResourceDesc>.Shared.Return(resources);
    }

    private void CheckBufferFree(BufferHandle buffer)
    {
        if (ActiveHandles.Contains(_activeBuffers, buffer))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedBuffers, buffer))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer referenced by a live Null command buffer.");
        }
    }

    private void CheckBufferViews(BufferHandle buffer)
    {
        foreach (var view in BufferViews.Values)
        {
            if (view.Buffer == buffer)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer while buffer views still reference it.");
        }
    }

    private void CheckTextureFree(TextureHandle texture)
    {
        if (ActiveHandles.Contains(_activeTextures, texture))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedTextures, texture))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture referenced by a live Null command buffer.");
        }
    }

    private void CheckTextureViews(TextureHandle texture)
    {
        foreach (var view in TextureViews.Values)
        {
            if (view.Texture == texture)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture while texture views still reference it.");
        }
    }

    private void CheckQueryFree(QueryPoolHandle queryPool)
    {
        if (ActiveHandles.Contains(_activeQueryPools, queryPool))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a query pool referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedQueryPools, queryPool))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a query pool referenced by a live Null command buffer.");
        }
    }

    private void CheckTvFree(TextureViewHandle view)
    {
        if (ActiveHandles.Contains(_activeTextureViews, view))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture view referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedTextureViews, view))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture view referenced by a live Null command buffer.");
        }
    }

    private void CheckTvRefs(TextureViewHandle view)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Desc.Resources)
            {
                if ((resource.ResourceType == BindingType.TextureRead || resource.ResourceType == BindingType.TextureReadWrite)
                    && resource.TextureView == view)
                {
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a texture view referenced by a live Null binding set.");
                }
            }
        }
    }

    private void CheckBvFree(BufferViewHandle view)
    {
        if (ActiveHandles.Contains(_activeBufferViews, view))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer view referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedBufferViews, view))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer view referenced by a live Null command buffer.");
        }
    }

    private void CheckBvRefs(BufferViewHandle view)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Desc.Resources)
            {
                if (BindRules.HasBufferView(resource.ResourceType) && resource.BufferView == view)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a buffer view referenced by a live Null binding set.");
            }
        }
    }

    private void CheckSamplerFree(SamplerHandle sampler)
    {
        if (ActiveHandles.Contains(_activeSamplers, sampler))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a sampler referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedSamplers, sampler))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a sampler referenced by a live Null command buffer.");
        }
    }

    private void CheckSamplerRefs(SamplerHandle sampler)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Desc.Resources)
            {
                if (resource.ResourceType == BindingType.Sampler && resource.SamplerHandle == sampler)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a sampler referenced by a live Null binding set.");
            }
        }
    }

    private void CheckLayoutFree(BindingLayoutHandle bindingLayout)
    {
        if (ActiveHandles.Contains(_activeBindingLayouts, bindingLayout))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by an active Null command list.");
        foreach (var set in BindingSets.Values)
        {
            if (set.Layout == bindingLayout)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by a live Null binding set.");
        }

        foreach (var layout in PipelineLayouts.Values)
        {
            foreach (var referenced in layout.Desc.BindingLayouts)
            {
                if (referenced == bindingLayout)
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by a live Null pipeline layout.");
            }
        }

        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedBindingLayouts, bindingLayout))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding layout referenced by a live Null command buffer.");
        }
    }

    private void CheckLayoutFree(PipelineLayoutHandle pipelineLayout)
    {
        if (ActiveHandles.Contains(_activePipelineLayouts, pipelineLayout))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline layout referenced by an active Null command list.");
        foreach (var pipeline in Pipelines.Values)
        {
            if (pipeline.Layout == pipelineLayout)
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline layout referenced by a live Null pipeline.");
        }

        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedPipelineLayouts, pipelineLayout))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline layout referenced by a live Null command buffer.");
        }
    }

    private void CheckSetFree(BindingSetHandle bindingSet)
    {
        if (ActiveHandles.Contains(_activeBindingSets, bindingSet))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot update or destroy a binding set referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedBindingSets, bindingSet))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a binding set referenced by a live Null command buffer.");
        }
    }

    private void CheckPipelineFree(PipelineHandle pipeline)
    {
        if (ActiveHandles.Contains(_activePipelines, pipeline))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer) && Contains(commandBuffer.ReferencedPipelines, pipeline))
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy a pipeline referenced by a live Null command buffer.");
        }
    }

    private void CheckAccelerationFree(AccelerationStructureHandle accelerationStructure)
    {
        if (ActiveHandles.Contains(_activeAccelerationStructures, accelerationStructure))
            throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy an acceleration structure referenced by an active Null command list.");
        foreach (var commandBuffer in CommandBuffers.Values)
        {
            if (MayUseResources(commandBuffer)
                && Contains(commandBuffer.ReferencedAccelerationStructures, accelerationStructure))
            {
                throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy an acceleration structure referenced by a live Null command buffer.");
            }
        }
    }

    private void CheckAccelerationReferences(AccelerationStructureHandle accelerationStructure)
    {
        foreach (var set in BindingSets.Values)
        {
            foreach (var resource in set.Desc.Resources)
            {
                if (resource.ResourceType == BindingType.AccelerationStructure
                    && resource.AccelerationStructure == accelerationStructure)
                {
                    throw new RhiException(ErrorCode.ValidationFailure, "Cannot destroy an acceleration structure referenced by a live Null binding set.");
                }
            }
        }
    }

    private static bool MayUseResources(CommandBufferRecord commandBuffer)
        => !commandBuffer.Submitted;

    private static bool Contains<T>(ReadOnlySpan<T> values, T value)
    {
        var comparer = System.Collections.Generic.EqualityComparer<T>.Default;
        for (int index = 0; index < values.Length; index++)
        {
            if (comparer.Equals(values[index], value))
                return true;
        }

        return false;
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new RhiException(ErrorCode.InvalidHandle, "Device is disposed.");
    }

    internal void ValidateTransientBindings(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources)
    {
        var bindingLayout = BindingLayouts.Get(layout, "BindingLayout");
        RhiBindingValidation.ValidateTransient(bindingLayout.Desc);
        ValidateBindingSet(bindingLayout.Desc, resources);
    }

    internal void ValidateDynamicOffsets(
        BindingLayoutRecord layout,
        ReadOnlySpan<BindingResourceDesc> resources,
        ReadOnlySpan<DynamicOffset> dynamicOffsets)
    {
        RhiBindingValidation.ValidateOffsets(layout.Desc, resources, dynamicOffsets);
        foreach (var dynamicOffset in dynamicOffsets)
        {
            int resourceIndex = RhiBindingValidation.FindResourceIndex(resources, dynamicOffset.Binding, dynamicOffset.ArrayElement);
            if (resourceIndex < 0)
                throw new RhiException(ErrorCode.ValidationFailure, "Validated dynamic offset lost its binding resource.");
            var resource = resources[resourceIndex];
            var view = BufferViews.Get(resource.BufferView, "DynamicOffsetBufferView");
            var buffer = Buffers.Get(view.Buffer, "DynamicOffsetBuffer");
            ulong alignment = resource.ResourceType == BindingType.ConstantBuffer
                ? Limits.MinConstantBufferOffsetAlignment
                : Limits.MinStorageBufferOffsetAlignment;
            if (dynamicOffset.OffsetInBytes % alignment != 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset for binding {dynamicOffset.Binding} must be aligned to {alignment} bytes.");
            ulong size = view.Desc.SizeInBytes == 0 ? buffer.Desc.SizeInBytes - view.Desc.Offset : view.Desc.SizeInBytes;
            if (dynamicOffset.OffsetInBytes >= size)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Dynamic offset for binding {dynamicOffset.Binding} leaves an empty buffer view range.");
        }
    }

    private BindState[] BuildBindStates(ReadOnlySpan<BindingResourceDesc> resources)
    {
        if (resources.Length == 0)
            return [];

        var states = new List<BindState>(resources.Length);
        for (int i = 0; i < resources.Length; i++)
            AddBindState(resources[i], states);
        return states.ToArray();
    }

    private void AddBindState(
        BindingResourceDesc resource,
        List<BindState> states)
    {
        var state = BindRules.Resolve(resource.ResourceType);
        switch (state.Target)
        {
            case BindTarget.BufferView:
                AddBufferState(resource.BufferView, state.State, state.Label, states);
                break;
            case BindTarget.TextureView:
                AddTextureState(resource.TextureView, state.State, state.Label, states);
                break;
            case BindTarget.AccelerationStructure:
                AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                break;
            case BindTarget.Sampler:
            case BindTarget.None:
                break;
            default:
                throw new RhiException(
                    ErrorCode.UnsupportedFeature,
                    $"Binding state target {state.Target} is not supported by Null backend.");
        }
    }

    private void AddBufferState(
        BufferViewHandle view,
        ResourceState requiredState,
        string label,
        List<BindState> states)
    {
        var record = BufferViews.Get(view, "BufferView");
        states.Add(BindState.ForBuffer(record.Buffer, requiredState, label));
    }

    private void AddTextureState(
        TextureViewHandle view,
        ResourceState requiredState,
        string label,
        List<BindState> states)
    {
        var viewRecord = TextureViews.Get(view, "TextureView");
        var texture = Textures.Get(viewRecord.Texture, "Texture");
        states.Add(BindState.ForTexture(
            viewRecord.Texture,
            ActualViewRange(texture.Desc, viewRecord.Desc),
            requiredState,
            label));
    }

    private static SubresourceRange ActualViewRange(TextureDesc texture, TextureViewDesc view)
    {
        uint mipCount = view.MipCount == uint.MaxValue ? texture.MipLevels - view.FirstMip : view.MipCount;
        uint sliceCount = view.SliceCount == uint.MaxValue ? texture.ArraySize - view.FirstSlice : view.SliceCount;
        var range = new SubresourceRange(view.FirstMip, mipCount, view.FirstSlice, sliceCount);
        RhiCommandValidation.ValidateSubresource(texture, range);
        return range;
    }

    private void ValidateBindingSet(BindingLayoutDesc layout, BindingSetDesc desc)
        => ValidateBindingSet(layout, desc.Resources);

    private void ValidateBindingSet(BindingLayoutDesc layout, ReadOnlySpan<BindingResourceDesc> resources)
    {
        RhiBindingValidation.ValidateResourceLayout(layout, resources);
        foreach (var resource in resources)
        {
            var slot = RhiBindingValidation.FindSlot(layout.Slots, resource.Binding, resource.ResourceType)
                ?? throw new RhiException(ErrorCode.ValidationFailure, "Validated binding resource lost its slot.");
            ValidateBindingResource(resource);
            ValidateResourceShape(slot, resource);
        }
    }

    private void ValidateTexFormat(TextureDesc desc)
    {
        var capabilities = GetFormatCapabilities(desc.Format);
        if (desc.BindFlags.HasFlag(BindFlags.ShaderResource) && !capabilities.Support.HasFlag(FormatSupport.ShaderSample))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {desc.Format} does not support shader-resource textures.");
        if (desc.BindFlags.HasFlag(BindFlags.RenderTarget) && !capabilities.Support.HasFlag(FormatSupport.RenderTarget))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {desc.Format} does not support render targets.");
        if (desc.BindFlags.HasFlag(BindFlags.DepthStencil) && !capabilities.Support.HasFlag(FormatSupport.DepthStencil))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {desc.Format} does not support depth/stencil textures.");
        if (desc.BindFlags.HasFlag(BindFlags.UnorderedAccess) && !capabilities.Support.HasFlag(FormatSupport.UnorderedAccess))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {desc.Format} does not support unordered-access textures.");
        if (desc.BindFlags.HasFlag(BindFlags.CopySource) && !capabilities.Support.HasFlag(FormatSupport.CopySource))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {desc.Format} does not support copy source.");
        if (desc.BindFlags.HasFlag(BindFlags.CopyDestination) && !capabilities.Support.HasFlag(FormatSupport.CopyDestination))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {desc.Format} does not support copy destination.");
    }

    private void ValidateViewFormat(TextureViewDesc desc)
    {
        var capabilities = GetFormatCapabilities(desc.Format);
        FormatSupport required = desc.Kind switch
        {
            ViewKind.ShaderResource => FormatSupport.ShaderSample,
            ViewKind.UnorderedAccess => FormatSupport.UnorderedAccess,
            ViewKind.RenderTarget => FormatSupport.RenderTarget,
            ViewKind.DepthStencil => FormatSupport.DepthStencil,
            _ => FormatSupport.None,
        };
        if (required != FormatSupport.None && !capabilities.Support.HasFlag(required))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Format {desc.Format} does not support {desc.Kind} views.");
    }

    private void ValidatePipelineFormats(IReadOnlyList<Format> colorFormats, Format depthStencilFormat)
    {
        foreach (var format in colorFormats)
        {
            if (format == Format.Unknown || Validation.IsDepthFormat(format))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Invalid pipeline color format {format}.");
            if (!GetFormatCapabilities(format).Support.HasFlag(FormatSupport.RenderTarget))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline color format {format} does not support render targets.");
        }

        if (depthStencilFormat == Format.Unknown)
            return;
        if (!Validation.IsDepthFormat(depthStencilFormat))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline depth/stencil format must be a depth format, got {depthStencilFormat}.");
        if (!GetFormatCapabilities(depthStencilFormat).Support.HasFlag(FormatSupport.DepthStencil))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Pipeline depth/stencil format {depthStencilFormat} does not support depth/stencil usage.");
    }

    private void EnsureShaderStage(ShaderModuleHandle shader, ShaderStage expected, string label)
    {
        var record = ShaderModules.Get(shader, "ShaderModule");
        if (record.Desc.Stage != expected)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} must use stage {expected}, got {record.Desc.Stage}.");
    }

    private void ValidateClusterShaders(RtPipelineDesc desc)
    {
        var declared = new HashSet<ShaderModuleHandle>();
        foreach (var shader in desc.Shaders)
        {
            var record = ShaderModules.Get(shader, "RayTracingShader");
            if (!IsRtStage(record.Desc.Stage))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing pipeline shader {record.Desc.Name} has invalid stage {record.Desc.Stage}.");
            if (!declared.Add(shader))
                throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing pipeline shader list contains duplicate handles.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in desc.ShaderGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
                throw new RhiException(ErrorCode.InvalidDescriptor, "Ray tracing shader group name must be specified.");
            if (!names.Add(group.Name))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group '{group.Name}' is duplicated.");
            ValidateShaderGroup(group, declared);
        }
    }

    private void ValidateShaderGroup(RtGroupDesc group, HashSet<ShaderModuleHandle> declaredShaders)
    {
        if (!Enum.IsDefined(group.Kind))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group kind value {group.Kind} is not defined.");

        switch (group.Kind)
        {
            case RtGroupKind.General:
                RequireGroupShader(group.GeneralShader, declaredShaders, "general shader");
                EnsureRtStage(group.GeneralShader, ShaderStage.RayGeneration, ShaderStage.Miss, ShaderStage.Callable);
                if (group.AnyHitShader.IsValid || group.ClosestHitShader.IsValid || group.IntersectionShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "General ray tracing shader groups cannot declare hit shaders.");
                break;
            case RtGroupKind.TrianglesHitGroup:
                if (group.GeneralShader.IsValid || group.IntersectionShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Triangle hit groups cannot declare general or intersection shaders.");
                if (!group.AnyHitShader.IsValid && !group.ClosestHitShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Triangle hit groups require at least one any-hit or closest-hit shader.");
                if (group.AnyHitShader.IsValid)
                {
                    RequireGroupShader(group.AnyHitShader, declaredShaders, "any-hit shader");
                    EnsureRtStage(group.AnyHitShader, ShaderStage.AnyHit);
                }
                if (group.ClosestHitShader.IsValid)
                {
                    RequireGroupShader(group.ClosestHitShader, declaredShaders, "closest-hit shader");
                    EnsureRtStage(group.ClosestHitShader, ShaderStage.ClosestHit);
                }
                break;
            case RtGroupKind.ProceduralHitGroup:
                if (group.GeneralShader.IsValid)
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Procedural hit groups cannot declare a general shader.");
                RequireGroupShader(group.IntersectionShader, declaredShaders, "intersection shader");
                EnsureRtStage(group.IntersectionShader, ShaderStage.Intersection);
                if (group.AnyHitShader.IsValid)
                {
                    RequireGroupShader(group.AnyHitShader, declaredShaders, "any-hit shader");
                    EnsureRtStage(group.AnyHitShader, ShaderStage.AnyHit);
                }
                if (group.ClosestHitShader.IsValid)
                {
                    RequireGroupShader(group.ClosestHitShader, declaredShaders, "closest-hit shader");
                    EnsureRtStage(group.ClosestHitShader, ShaderStage.ClosestHit);
                }
                break;
        }
    }

    private void RequireGroupShader(ShaderModuleHandle shader, HashSet<ShaderModuleHandle> declaredShaders, string label)
    {
        if (!shader.IsValid)
            throw new RhiException(ErrorCode.InvalidHandle, $"Ray tracing shader group {label} handle is invalid.");
        if (!declaredShaders.Contains(shader))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader group {label} is not present in the pipeline shader list.");
    }

    private void EnsureRtStage(ShaderModuleHandle shader, params ShaderStage[] allowed)
    {
        var stage = ShaderModules.Get(shader, "RayTracingShader").Desc.Stage;
        for (int index = 0; index < allowed.Length; index++)
        {
            if (stage == allowed[index])
                return;
        }

        throw new RhiException(ErrorCode.InvalidDescriptor, $"Ray tracing shader stage {stage} is not valid for this shader group slot.");
    }

    private static bool IsRtStage(ShaderStage stage)
        => stage is ShaderStage.RayGeneration
            or ShaderStage.AnyHit
            or ShaderStage.ClosestHit
            or ShaderStage.Miss
            or ShaderStage.Intersection
            or ShaderStage.Callable;

    private void ValidateTextureLimits(TextureDesc desc)
    {
        if (desc.ArraySize > Limits.MaxTextureArrayLayers)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture array size {desc.ArraySize} exceeds limit {Limits.MaxTextureArrayLayers}.");
        switch (desc.Dimension)
        {
            case ResourceDimension.Texture1D:
                if (desc.Width > Limits.MaxTextureDimension1D)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture1D width {desc.Width} exceeds limit {Limits.MaxTextureDimension1D}.");
                break;
            case ResourceDimension.Texture2D:
                if (desc.Width > Limits.MaxTextureDimension2D || desc.Height > Limits.MaxTextureDimension2D)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture2D dimensions {desc.Width}x{desc.Height} exceed limit {Limits.MaxTextureDimension2D}.");
                break;
            case ResourceDimension.Texture3D:
                if (desc.Width > Limits.MaxTextureDimension3D || desc.Height > Limits.MaxTextureDimension3D || desc.Depth > Limits.MaxTextureDimension3D)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture3D dimensions {desc.Width}x{desc.Height}x{desc.Depth} exceed limit {Limits.MaxTextureDimension3D}.");
                break;
            case ResourceDimension.TextureCube:
                if (desc.Width > Limits.MaxTextureDimensionCube || desc.Height > Limits.MaxTextureDimensionCube)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"TextureCube face size {desc.Width} exceeds limit {Limits.MaxTextureDimensionCube}.");
                break;
        }

        if (!SampleCounts(desc.Format).HasFlag(SampleCountFlag(desc.SampleCount)))
            throw new RhiException(ErrorCode.UnsupportedFeature, $"Format {desc.Format} does not support sample count {desc.SampleCount}.");
    }

    private void ValidateStaticSamplers(PipelineLayoutDesc desc)
    {
        foreach (var sampler in desc.StaticSamplers)
        {
            if (sampler.Set >= desc.BindingLayouts.Count)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Static sampler set {sampler.Set} is outside the pipeline layout.");
            var layout = BindingLayouts.Get(desc.BindingLayouts[(int)sampler.Set], "BindingLayout");
            foreach (var slot in layout.Desc.Slots)
            {
                if (slot.Binding != sampler.Binding)
                    continue;
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Static sampler set={sampler.Set} binding={sampler.Binding} conflicts with a descriptor binding.");
            }
        }
    }

    private void ValidateBindingResource(BindingResourceDesc resource)
    {
        switch (resource.ResourceType)
        {
            case BindingType.ConstantBuffer:
                EnsureBufKind(resource.BufferView, ViewKind.ConstantBuffer);
                break;
            case BindingType.StorageBufferRead:
                EnsureBufKind(resource.BufferView, ViewKind.ShaderResource);
                EnsureBufRaw(resource.BufferView, expectedRaw: false, resource.ResourceType);
                break;
            case BindingType.RawBufferRead:
                EnsureBufKind(resource.BufferView, ViewKind.ShaderResource);
                EnsureBufRaw(resource.BufferView, expectedRaw: true, resource.ResourceType);
                break;
            case BindingType.StorageBufferReadWrite:
                EnsureBufKind(resource.BufferView, ViewKind.UnorderedAccess);
                EnsureBufRaw(resource.BufferView, expectedRaw: false, resource.ResourceType);
                break;
            case BindingType.RawBufferReadWrite:
                EnsureBufKind(resource.BufferView, ViewKind.UnorderedAccess);
                EnsureBufRaw(resource.BufferView, expectedRaw: true, resource.ResourceType);
                break;
            case BindingType.TextureRead:
                EnsureTexKind(resource.TextureView, ViewKind.ShaderResource);
                break;
            case BindingType.TextureReadWrite:
                EnsureTexKind(resource.TextureView, ViewKind.UnorderedAccess);
                break;
            case BindingType.Sampler:
                Samplers.Get(resource.SamplerHandle, "Sampler");
                break;
            case BindingType.AccelerationStructure:
                AccelerationStructures.Get(resource.AccelerationStructure, "AccelerationStructure");
                break;
            default:
                throw new RhiException(ErrorCode.UnsupportedFeature, $"Binding type {resource.ResourceType} is not supported by Null backend.");
        }
    }

    private void ValidateResourceShape(BindingSlotDesc slot, BindingResourceDesc resource)
    {
        if (!RhiBindingValidation.HasShapeRule(slot))
            return;

        switch (resource.ResourceType)
        {
            case BindingType.StorageBufferRead:
            case BindingType.StorageBufferReadWrite:
                ValidateBufferShape(slot, resource.BufferView);
                break;
            case BindingType.TextureRead:
            case BindingType.TextureReadWrite:
                ValidateTextureShape(slot, resource.TextureView);
                break;
        }
    }

    private void ValidateBufferShape(BindingSlotDesc slot, BufferViewHandle view)
    {
        var viewRecord = BufferViews.Get(view, "BufferView");
        BufferDesc? buffer = slot.Shape.StrideInBytes != 0 && viewRecord.Desc.StrideInBytes == 0
            ? Buffers.Get(viewRecord.Buffer, "BufferViewBuffer").Desc
            : null;
        RhiBindingValidation.ValidateBufferShape(slot, viewRecord.Desc, buffer);
    }

    private void ValidateTextureShape(BindingSlotDesc slot, TextureViewHandle view)
    {
        var viewRecord = TextureViews.Get(view, "TextureView");
        RhiBindingValidation.ValidateTextureShape(slot, viewRecord.Desc);
    }

    private void EnsureBufKind(BufferViewHandle handle, ViewKind kind)
    {
        var record = BufferViews.Get(handle, "BufferView");
        if (record.Desc.Kind != kind)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Buffer view kind {record.Desc.Kind} does not match required kind {kind}.");
    }

    private void EnsureBufRaw(BufferViewHandle handle, bool expectedRaw, BindingType bindingType)
    {
        var record = BufferViews.Get(handle, "BufferView");
        if (record.Desc.Raw != expectedRaw)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Binding type {bindingType} requires a {(expectedRaw ? "raw" : "non-raw")} buffer view.");
    }

    private void EnsureTexKind(TextureViewHandle handle, ViewKind kind)
    {
        var record = TextureViews.Get(handle, "TextureView");
        if (record.Desc.Kind != kind)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Texture view kind {record.Desc.Kind} does not match required kind {kind}.");
    }

    private static void ValidateVertexInput(GraphicsPipelineDesc desc)
    {
        for (int layoutIndex = 0; layoutIndex < desc.VertexBuffers.Count; layoutIndex++)
        {
            var layout = desc.VertexBuffers[layoutIndex];
            if (!Enum.IsDefined(layout.InputRate))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex input rate value {layout.InputRate} is not defined.");
            if (layout.StrideInBytes == 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Vertex buffer stride must be greater than zero.");
            if (layout.InputRate == VertexInputRate.Instance && layout.InstanceStepRate == 0)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Instance-rate vertex buffers require InstanceStepRate greater than zero.");
            for (int previousIndex = 0; previousIndex < layoutIndex; previousIndex++)
            {
                if (desc.VertexBuffers[previousIndex].Slot == layout.Slot)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Duplicate vertex buffer slot {layout.Slot}.");
            }
        }

        for (int attributeIndex = 0; attributeIndex < desc.VertexAttributes.Count; attributeIndex++)
        {
            var attribute = desc.VertexAttributes[attributeIndex];
            if (attribute.Format == Format.Unknown || Validation.IsDepthFormat(attribute.Format))
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Invalid vertex attribute format {attribute.Format}.");
            bool slotDeclared = false;
            foreach (var layout in desc.VertexBuffers)
            {
                if (layout.Slot == attribute.BufferSlot)
                {
                    slotDeclared = true;
                    break;
                }
            }

            if (!slotDeclared)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"Vertex attribute location {attribute.Location} references undeclared buffer slot {attribute.BufferSlot}.");
            for (int previousIndex = 0; previousIndex < attributeIndex; previousIndex++)
            {
                if (desc.VertexAttributes[previousIndex].Location == attribute.Location)
                    throw new RhiException(ErrorCode.InvalidDescriptor, $"Duplicate vertex attribute location {attribute.Location}.");
            }
        }
    }

    private static void ValidatePipelineEnums(GraphicsPipelineDesc desc)
    {
        if (!Enum.IsDefined(desc.Topology))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Primitive topology value {desc.Topology} is not defined.");
        if (desc.Topology == PrimitiveTopology.PatchList && desc.PatchControlPoints == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, "PatchList topology requires PatchControlPoints greater than zero.");
        if (!Enum.IsDefined(desc.Rasterizer.CullMode))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Cull mode value {desc.Rasterizer.CullMode} is not defined.");
        if (!Enum.IsDefined(desc.Rasterizer.FillMode))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Fill mode value {desc.Rasterizer.FillMode} is not defined.");
        if (!float.IsFinite(desc.Rasterizer.DepthBiasClamp) || !float.IsFinite(desc.Rasterizer.SlopeScaledDepthBias))
            throw new RhiException(ErrorCode.InvalidDescriptor, "Rasterizer depth bias values must be finite.");
        if (!Enum.IsDefined(desc.DepthStencil.DepthCompare))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Depth compare value {desc.DepthStencil.DepthCompare} is not defined.");
        for (int index = 0; index < desc.Blend.Targets.Count; index++)
            ValidateBlendTarget(desc.Blend.Targets[index], index);
    }

    private void ValidateShaderStages(GraphicsPipelineDesc desc)
    {
        if (desc.GeometryShader.IsValid)
        {
            if (!Features.GeometryShader)
                throw new RhiException(ErrorCode.UnsupportedFeature, "Geometry shaders are not supported by this device.");
            var geometry = ShaderModules.Get(desc.GeometryShader, "Geometry Shader").Desc;
            if (geometry.Stage != ShaderStage.Geometry)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline geometry shader handle does not reference a geometry shader.");
        }

        bool hasHull = desc.HullShader.IsValid;
        bool hasDomain = desc.DomainShader.IsValid;
        if (hasHull != hasDomain)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Tessellation requires both hull and domain shaders.");
        if (hasHull)
        {
            if (!Features.TessellationShader)
                throw new RhiException(ErrorCode.UnsupportedFeature, "Tessellation shaders are not supported by this device.");
            if (desc.Topology != PrimitiveTopology.PatchList)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Tessellation shaders require PatchList topology.");
            if (desc.PatchControlPoints == 0 || desc.PatchControlPoints > Limits.MaxPatchControlPoints)
                throw new RhiException(ErrorCode.InvalidDescriptor, $"PatchControlPoints must be in [1, {Limits.MaxPatchControlPoints}].");
            var hull = ShaderModules.Get(desc.HullShader, "Hull Shader").Desc;
            var domain = ShaderModules.Get(desc.DomainShader, "Domain Shader").Desc;
            if (hull.Stage != ShaderStage.Hull)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline hull shader handle does not reference a hull shader.");
            if (domain.Stage != ShaderStage.Domain)
                throw new RhiException(ErrorCode.InvalidDescriptor, "Graphics pipeline domain shader handle does not reference a domain shader.");
        }
        else if (desc.Topology == PrimitiveTopology.PatchList)
        {
            throw new RhiException(ErrorCode.InvalidDescriptor, "PatchList topology requires tessellation shaders.");
        }
    }

    private static void ValidateBlendTarget(BlendTargetDesc target, int index)
    {
        const ColorWriteMask knownColorMask = ColorWriteMask.All;
        if (!Enum.IsDefined(target.SourceColor))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend target {index} source color factor value {target.SourceColor} is not defined.");
        if (!Enum.IsDefined(target.DestinationColor))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend target {index} destination color factor value {target.DestinationColor} is not defined.");
        if (!Enum.IsDefined(target.ColorOp))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend target {index} color op value {target.ColorOp} is not defined.");
        if (!Enum.IsDefined(target.SourceAlpha))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend target {index} source alpha factor value {target.SourceAlpha} is not defined.");
        if (!Enum.IsDefined(target.DestinationAlpha))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend target {index} destination alpha factor value {target.DestinationAlpha} is not defined.");
        if (!Enum.IsDefined(target.AlphaOp))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend target {index} alpha op value {target.AlphaOp} is not defined.");
        if ((target.WriteMask & ~knownColorMask) != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Blend target {index} color write mask contains unsupported bits: {target.WriteMask}.");
    }

    private static TextureViewDesc Snapshot(TextureViewDesc desc) => desc with { };

    private static BufferViewDesc Snapshot(BufferViewDesc desc) => desc with { };

    private static SamplerDesc Snapshot(SamplerDesc desc) => desc with { };

    private static ShaderModuleDesc Snapshot(ShaderModuleDesc desc)
        => desc with
        {
            Bytecode = desc.Bytecode.ToArray(),
        };

    private static BindingLayoutDesc Snapshot(BindingLayoutDesc desc)
        => desc with { Slots = desc.Slots.ToArray() };

    private static PipelineLayoutDesc Snapshot(PipelineLayoutDesc desc)
    {
        var staticSamplers = new StaticSamplerDesc[desc.StaticSamplers.Count];
        for (int index = 0; index < staticSamplers.Length; index++)
        {
            var sampler = desc.StaticSamplers[index];
            staticSamplers[index] = sampler with { Sampler = Snapshot(sampler.Sampler) };
        }

        return desc with
        {
            BindingLayouts = desc.BindingLayouts.ToArray(),
            PushConstants = desc.PushConstants.ToArray(),
            StaticSamplers = staticSamplers,
        };
    }

    private static BindingSetDesc Snapshot(BindingSetDesc desc)
        => desc with { Resources = [.. desc.Resources] };

    private static GraphicsPipelineDesc Snapshot(GraphicsPipelineDesc desc)
        => desc with
        {
            VertexBuffers = desc.VertexBuffers.ToArray(),
            VertexAttributes = desc.VertexAttributes.ToArray(),
            ColorFormats = desc.ColorFormats.ToArray(),
            Blend = desc.Blend with { Targets = desc.Blend.Targets.ToArray() },
        };

    private static BufferDesc Snapshot(BufferDesc desc) => desc with { };

    private static TextureDesc Snapshot(TextureDesc desc) => desc with { };

    private static MemoryHeapDesc Snapshot(MemoryHeapDesc desc) => desc with { };

    private void ValidatePlacement(MemoryHeapHandle heapHandle, ulong offset, ResourceMemoryRequirements requirements, MemoryClass memory)
    {
        var heap = MemoryHeaps.Get(heapHandle, "MemoryHeap");
        if (requirements.RequiresDedicatedAllocation)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Resource requires a dedicated allocation and cannot be placed.");
        if (heap.Desc.Memory != memory)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Resource memory {memory} is incompatible with heap memory {heap.Desc.Memory}.");
        if (!HeapKindMatches(heap.Desc.Kind, requirements.HeapKind))
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Resource heap kind {requirements.HeapKind} is incompatible with heap kind {heap.Desc.Kind}.");
        if (offset % requirements.Alignment != 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"Placed resource offset {offset} must be aligned to {requirements.Alignment}.");
        if (offset > heap.Desc.SizeInBytes || requirements.SizeInBytes > heap.Desc.SizeInBytes - offset)
            throw new RhiException(ErrorCode.InvalidDescriptor, "Placed resource range is outside the memory heap.");
        if (!heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
        {
            foreach (var allocation in heap.PlacedAllocations)
            {
                if (RangesOverlap(offset, requirements.SizeInBytes, allocation.Allocation.HeapOffset, allocation.Allocation.SizeInBytes))
                    throw new RhiException(ErrorCode.InvalidDescriptor, "Overlapping placed resources require a memory heap created with AllowAliasing.");
            }
        }
    }

    private bool HeapKindMatches(MemoryHeapKind heapKind, MemoryHeapKind requiredKind)
        => heapKind == requiredKind || (heapKind == MemoryHeapKind.Mixed && Features.MixedResourceHeaps);

    private void ValidateMappedRange(BufferHandle buffer, ulong offset, ulong sizeInBytes, string label)
    {
        var record = Buffers.Get(buffer, "Buffer");
        if (!record.Mapped)
            throw new RhiException(ErrorCode.ValidationFailure, $"{label} requires the buffer to be mapped.");
        if (sizeInBytes == 0)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} range size must be greater than zero.");
        if (offset < record.MappedOffset || sizeInBytes > record.MappedSize || offset - record.MappedOffset > record.MappedSize - sizeInBytes)
            throw new RhiException(ErrorCode.InvalidDescriptor, $"{label} range must be inside the mapped range.");
    }

    private void RegisterPlacedAllocation(AliasingResource resource, ResourceAllocationInfo allocation)
    {
        if (allocation.Ownership != ResourceOwnership.Placed)
            return;
        var heap = MemoryHeaps.Get(allocation.Heap, "MemoryHeap");
        bool active = true;
        if (heap.Desc.Flags.HasFlag(MemoryHeapFlags.AllowAliasing))
        {
            foreach (var placed in heap.PlacedAllocations)
            {
                if (RangesOverlap(allocation.HeapOffset, allocation.SizeInBytes, placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes)
                    && placed.Active)
                {
                    active = false;
                    break;
                }
            }
        }

        heap.PlacedAllocations.Add(new PlacedAllocationRecord(resource, allocation) { Active = active });
        heap.LiveResourceCount++;
    }

    private void ReleaseAllocation(ResourceAllocationInfo allocation, AliasingResource resource)
    {
        if (allocation.Ownership != ResourceOwnership.Placed)
            return;
        var heap = MemoryHeaps.Get(allocation.Heap, "MemoryHeap");
        for (int index = 0; index < heap.PlacedAllocations.Count; index++)
        {
            if (heap.PlacedAllocations[index].Resource == resource)
            {
                heap.PlacedAllocations.RemoveAt(index);
                break;
            }
        }

        heap.LiveResourceCount--;
    }

    internal PlacedAllocationRecord? FindPlacedAllocation(AliasingResource resource)
    {
        ResourceAllocationInfo allocation = resource.Kind switch
        {
            AliasingResourceKind.Buffer => Buffers.Get(resource.Buffer, "AliasingBuffer").Allocation,
            AliasingResourceKind.Texture => Textures.Get(resource.Texture, "AliasingTexture").Allocation,
            _ => throw new RhiException(ErrorCode.InvalidDescriptor, $"Aliasing resource kind {resource.Kind} is not defined."),
        };
        if (allocation.Ownership != ResourceOwnership.Placed)
            return null;
        var heap = MemoryHeaps.Get(allocation.Heap, "AliasingMemoryHeap");
        foreach (var placed in heap.PlacedAllocations)
        {
            if (placed.Resource == resource)
                return placed;
        }

        throw new RhiException(ErrorCode.InvalidHandle, "Placed allocation record is missing.");
    }

    internal void ApplyAliasingBarrier(AliasingBarrier barrier)
    {
        var before = barrier.Before.Kind == AliasingResourceKind.None ? null : FindPlacedAllocation(barrier.Before);
        var after = barrier.After.Kind == AliasingResourceKind.None ? null : FindPlacedAllocation(barrier.After);
        if (before != null)
            before.Active = false;
        if (after != null)
        {
            var heap = MemoryHeaps.Get(after.Allocation.Heap, "AliasingMemoryHeap");
            foreach (var placed in heap.PlacedAllocations)
            {
                if (placed != after && RangesOverlap(after.Allocation.HeapOffset, after.Allocation.SizeInBytes, placed.Allocation.HeapOffset, placed.Allocation.SizeInBytes))
                    placed.Active = false;
            }
            after.Active = true;
        }
    }

    private ulong CurrentMemoryUsage(MemoryClass memory)
    {
        ulong usage = 0;
        foreach (var buffer in Buffers.Values)
        {
            if (buffer.Allocation.Memory == memory && buffer.Allocation.Ownership != ResourceOwnership.Placed)
                usage += buffer.Allocation.SizeInBytes;
        }

        foreach (var texture in Textures.Values)
        {
            if (texture.Allocation.Memory == memory && texture.Allocation.Ownership != ResourceOwnership.Placed)
                usage += texture.Allocation.SizeInBytes;
        }

        foreach (var heap in MemoryHeaps.Values)
        {
            if (heap.Desc.Memory == memory)
                usage += heap.Desc.SizeInBytes;
        }

        return usage;
    }

    internal static ulong EstimateSwapchain(TextureDesc desc) => EstimateTextureSize(desc);

    private static ulong EstimateTextureSize(TextureDesc desc)
    {
        ulong total = 0;
        uint width = desc.Width;
        uint height = desc.Height;
        uint depth = desc.Dimension == ResourceDimension.Texture3D ? desc.Depth : 1;
        ulong bytesPerPixel = FormatByteSize(desc.Format);
        for (uint mip = 0; mip < desc.MipLevels; mip++)
        {
            total += checked((ulong)Math.Max(1u, width >> (int)mip)
                * Math.Max(1u, height >> (int)mip)
                * Math.Max(1u, depth >> (int)mip)
                * desc.ArraySize
                * desc.SampleCount
                * bytesPerPixel);
        }

        return Math.Max(1, total);
    }

    private static ulong FormatByteSize(Format format)
        => format switch
        {
            Format.R8Unorm or Format.R8UInt => 1,
            Format.R16UInt or Format.R16Float or Format.Rg8Unorm => 2,
            Format.Rgba8Unorm
                or Format.Rgba8UnormSrgb
                or Format.Bgra8Unorm
                or Format.Bgra8UnormSrgb
                or Format.Rgb10A2Unorm
                or Format.R32UInt
                or Format.R32Float
                or Format.Rg16Float
                or Format.D32Float
                or Format.D24UnormS8UInt => 4,
            Format.Rgba16Float or Format.Rg16UInt or Format.Rg32Float => 8,
            Format.Rgb32Float => 12,
            Format.Rgba32Float => 16,
            _ => 4,
        };

    private static ulong AlignUp(ulong value, ulong alignment)
        => checked((value + alignment - 1) / alignment * alignment);

    internal static bool RangesOverlap(ulong firstOffset, ulong firstSize, ulong secondOffset, ulong secondSize)
        => firstOffset < secondOffset + secondSize && secondOffset < firstOffset + firstSize;

    private SampleCountFlags SampleCounts(Format format)
        => GetFormatCapabilities(format).SampleCounts;

    private static SampleCountFlags SampleCountFlag(uint sampleCount)
        => sampleCount switch
        {
            1 => SampleCountFlags.Count1,
            2 => SampleCountFlags.Count2,
            4 => SampleCountFlags.Count4,
            8 => SampleCountFlags.Count8,
            16 => SampleCountFlags.Count16,
            _ => SampleCountFlags.None,
        };

    private static BindingLayoutSignature CreateLayoutSignature(BindingLayoutDesc desc)
        => new(RhiBindingValidation.LayoutHash(desc), desc.Slots.Count);

    private static TextureDesc SwapchainTextureDesc(SwapchainDesc desc)
        => new()
        {
            Name = desc.Name,
            Width = desc.Width,
            Height = desc.Height,
            Format = desc.Format,
            BindFlags = BindFlags.RenderTarget | BindFlags.CopySource,
            InitialState = ResourceState.Present,
        };
}
