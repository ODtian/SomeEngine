using System.Runtime.InteropServices;
namespace SomeEngine.Rhi;

public sealed record DeviceDesc
{
    public Backend Backend { get; init; }
    public bool EnableValidation { get; init; } = true;
    public string AdapterName { get; init; } = string.Empty;
}

public sealed record AdapterInfo
{
    public string Name { get; init; } = string.Empty;
    public Backend Backend { get; init; }
    public ulong DedicatedVideoMemory { get; init; }
    public ulong DedicatedSystemMemory { get; init; }
    public ulong SharedSystemMemory { get; init; }
    public bool IsSoftware { get; init; }
    public bool IsIntegrated { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
}

public sealed record DeviceFeatures
{
    public bool GraphicsQueue { get; init; } = true;
    public bool ComputeQueue { get; init; }
    public bool CopyQueue { get; init; }
    public bool ParallelCommandRecording { get; init; }
    public bool Bindless { get; init; }
    public bool PartiallyBoundDescriptors { get; init; }
    public bool DynamicOffsets { get; init; }
    public bool StaticSamplers { get; init; } = true;
    public bool PlacedResources { get; init; }
    public bool MixedResourceHeaps { get; init; }
    public bool ResourceAliasing { get; init; }
    public bool MemoryBudget { get; init; }
    public bool TimestampQueries { get; init; }
    public bool OcclusionQueries { get; init; }
    public bool PipelineStatisticsQueries { get; init; }
    public bool PipelineCache { get; init; }
    public bool DrawIndirect { get; init; }
    public bool IndirectCount { get; init; }
    public bool MultiDrawIndirect { get; init; }
    public bool DispatchIndirect { get; init; }
    public bool MultiDispatchIndirect { get; init; }
    public bool TypedUavLoad { get; init; }
    public bool SamplerAnisotropy { get; init; }
    public bool TextureCubeArray { get; init; }
    public bool ConservativeRasterization { get; init; }
    public bool DepthBoundsTest { get; init; }
    public bool IndependentBlend { get; init; }
    public bool LogicOp { get; init; }
    public bool DualSourceBlend { get; init; }
    public bool GeometryShader { get; init; }
    public bool TessellationShader { get; init; }
    public bool MeshShader { get; init; }
    public bool RayTracing { get; init; }
    public bool VariableRateShading { get; init; }
}

public sealed record DeviceLimits
{
    public uint MaxColorAttachments { get; init; } = 8;
    public uint MaxBindingSets { get; init; } = 8;
    public uint MaxBindingsPerSet { get; init; } = 64;
    public uint MaxDescriptorArrayLength { get; init; } = 4096;
    public uint MaxBindlessResourceDescriptors { get; init; } = 1_000_000;
    public uint MaxBindlessSamplerDescriptors { get; init; } = 2048;
    public uint MaxSamplers { get; init; } = 4096;
    public uint MaxStaticSamplers { get; init; } = 16;
    public uint MaxTextureDimension1D { get; init; } = 16384;
    public uint MaxTextureDimension2D { get; init; } = 16384;
    public uint MaxTextureDimension3D { get; init; } = 2048;
    public uint MaxTextureDimensionCube { get; init; } = 16384;
    public uint MaxTextureArrayLayers { get; init; } = 2048;
    public uint MaxVertexBuffers { get; init; } = 32;
    public uint MaxVertexAttributes { get; init; } = 32;
    public uint MaxPatchControlPoints { get; init; } = 32;
    public uint MaxPushConstantBytes { get; init; } = 256;
    public uint MaxIndirectDrawCount { get; init; } = 1;
    public uint MaxIndirectDispatchCount { get; init; } = 1;
    public uint MaxComputeThreadGroupSizeX { get; init; } = 1024;
    public uint MaxComputeThreadGroupSizeY { get; init; } = 1024;
    public uint MaxComputeThreadGroupSizeZ { get; init; } = 64;
    public uint MaxComputeThreadGroupInvocations { get; init; } = 1024;
    public ulong MinConstantBufferOffsetAlignment { get; init; } = 256;
    public ulong MinStorageBufferOffsetAlignment { get; init; } = 16;
    public ulong TextureRowPitchAlignment { get; init; } = 256;
    public ulong BufferCopyOffsetAlignment { get; init; } = 4;
    public ulong MinMemoryHeapAlignment { get; init; } = 65536;
    public ulong MaxMemoryHeapSize { get; init; } = ulong.MaxValue;
    public ulong BufferPlacementAlignment { get; init; } = 65536;
    public ulong TexturePlacementAlignment { get; init; } = 65536;
    public ulong MsaaTexturePlacementAlignment { get; init; } = 4194304;
    public float TimestampPeriodNanoseconds { get; init; } = 1.0f;
    public uint MaxRayRecursionDepth { get; init; } = 31;
    public uint MaxRayTracingAttributeSizeInBytes { get; init; } = 32;
    public uint RayTracingShaderIdentifierSizeInBytes { get; init; } = 32;
    public uint RayTracingShaderRecordAlignment { get; init; } = 32;
    public uint RayTracingShaderTableAlignment { get; init; } = 64;
    public uint MaxRayTracingShaderRecordStride { get; init; } = 4096;
    public ulong AccelerationStructureAlignment { get; init; } = 256;
}

public readonly record struct FormatCapabilities
{
    public FormatCapabilities(
        FormatSupport support,
        SampleCountFlags sampleCounts = SampleCountFlags.Count1,
        bool typedUavLoad = false,
        bool typedUavStore = false,
        bool filterable = true,
        bool blendable = true,
        bool depthStencil = false)
    {
        Support = support;
        SampleCounts = sampleCounts;
        TypedUavLoad = typedUavLoad;
        TypedUavStore = typedUavStore;
        Filterable = filterable;
        Blendable = blendable;
        DepthStencil = depthStencil;
    }

    public FormatSupport Support { get; init; }
    public SampleCountFlags SampleCounts { get; init; } = SampleCountFlags.Count1;
    public bool TypedUavLoad { get; init; }
    public bool TypedUavStore { get; init; }
    public bool Filterable { get; init; } = true;
    public bool Blendable { get; init; } = true;
    public bool DepthStencil { get; init; }
}

public sealed record QueueFamilyInfo
{
    public QueueType Type { get; init; }
    public uint Count { get; init; }
    public bool SupportsGraphics { get; init; }
    public bool SupportsCompute { get; init; }
    public bool SupportsCopy { get; init; }
    public bool SupportsTimestamps { get; init; }
}

public sealed record BufferDesc
{
    public string Name { get; init; } = string.Empty;
    public ulong SizeInBytes { get; init; }
    public MemoryClass Memory { get; init; } = MemoryClass.DeviceLocal;
    public BindFlags BindFlags { get; init; }
    public ResourceState InitialState { get; init; } = ResourceState.Undefined;
    public uint StrideInBytes { get; init; }
    public bool Raw { get; init; }
}

public sealed record TextureDesc
{
    public string Name { get; init; } = string.Empty;
    public ResourceDimension Dimension { get; init; } = ResourceDimension.Texture2D;
    public uint Width { get; init; }
    public uint Height { get; init; } = 1;
    public uint Depth { get; init; } = 1;
    public uint MipLevels { get; init; } = 1;
    public uint ArraySize { get; init; } = 1;
    public uint SampleCount { get; init; } = 1;
    public Format Format { get; init; }
    public MemoryClass Memory { get; init; } = MemoryClass.DeviceLocal;
    public BindFlags BindFlags { get; init; }
    public ResourceState InitialState { get; init; } = ResourceState.Undefined;
    public ClearValue? OptimizedClearValue { get; init; }
}

public sealed record TextureViewDesc
{
    public string Name { get; init; } = string.Empty;
    public ViewKind Kind { get; init; }
    public TextureViewDimension Dimension { get; init; } = TextureViewDimension.Texture2D;
    public Format Format { get; init; } = Format.Unknown;
    public uint FirstMip { get; init; }
    public uint MipCount { get; init; } = 1;
    public uint FirstSlice { get; init; }
    public uint SliceCount { get; init; } = 1;
    public uint PlaneSlice { get; init; }
}

public sealed record BufferViewDesc
{
    public string Name { get; init; } = string.Empty;
    public ViewKind Kind { get; init; }
    public ulong Offset { get; init; }
    public ulong SizeInBytes { get; init; }
    public Format Format { get; init; } = Format.Unknown;
    public uint StrideInBytes { get; init; }
    public bool Raw { get; init; }
}

public sealed record SamplerDesc
{
    public string Name { get; init; } = string.Empty;
    public FilterMode MinFilter { get; init; } = FilterMode.Linear;
    public FilterMode MagFilter { get; init; } = FilterMode.Linear;
    public MipmapMode MipmapMode { get; init; } = MipmapMode.Linear;
    public AddressMode AddressU { get; init; } = AddressMode.ClampToEdge;
    public AddressMode AddressV { get; init; } = AddressMode.ClampToEdge;
    public AddressMode AddressW { get; init; } = AddressMode.ClampToEdge;
    public float MipLodBias { get; init; }
    public float MinLod { get; init; }
    public float MaxLod { get; init; } = float.MaxValue;
    public uint MaxAnisotropy { get; init; } = 1;
    public CompareOp? Compare { get; init; }
    public BorderColor BorderColor { get; init; } = BorderColor.TransparentBlack;
}

public sealed record BindingSlotDesc
{
    public uint Binding { get; init; }
    public BindingType Type { get; init; }
    public ShaderStageFlags Stages { get; init; }
    public uint Count { get; init; } = 1;
    public BindingFlags Flags { get; init; }
    public BindShapeDesc Shape { get; init; } = new();
}

public sealed record BindShapeDesc
{
    public TextureViewDimension TextureDimension { get; init; } = TextureViewDimension.Texture2D;
    public Format Format { get; init; } = Format.Unknown;
    public uint StrideInBytes { get; init; }
    public SamplerDesc Sampler { get; init; } = new();
}

public sealed record BindingLayoutDesc
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<BindingSlotDesc> Slots { get; init; } = Array.Empty<BindingSlotDesc>();
}

public sealed record PushRangeDesc
{
    public ShaderStageFlags Stages { get; init; }
    public uint Offset { get; init; }
    public uint SizeInBytes { get; init; }
}

public sealed record StaticSamplerDesc
{
    public uint Set { get; init; }
    public uint Binding { get; init; }
    public SamplerDesc Sampler { get; init; } = new();
    public ShaderStageFlags Stages { get; init; }
}

public sealed record PipelineLayoutDesc
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<BindingLayoutHandle> BindingLayouts { get; init; } = Array.Empty<BindingLayoutHandle>();
    public IReadOnlyList<PushRangeDesc> PushConstants { get; init; } = Array.Empty<PushRangeDesc>();
    public IReadOnlyList<StaticSamplerDesc> StaticSamplers { get; init; } = Array.Empty<StaticSamplerDesc>();
}

public readonly record struct BindingResourceDesc
{
    public uint Binding { get; init; }
    public uint ArrayElement { get; init; }
    public BindingType ResourceType { get; init; }
    public BufferViewHandle BufferView { get; init; }
    public TextureViewHandle TextureView { get; init; }
    public SamplerHandle SamplerHandle { get; init; }
    public AccelerationStructureHandle AccelerationStructure { get; init; }

    public static BindingResourceDesc Clear(uint binding, uint arrayElement = 0)
        => new() { Binding = binding, ArrayElement = arrayElement, ResourceType = BindingType.None };

    public static BindingResourceDesc Buffer(uint binding, BufferViewHandle view, uint arrayElement = 0)
        => new() { Binding = binding, ArrayElement = arrayElement, ResourceType = BindingType.StorageBufferRead, BufferView = view };

    public static BindingResourceDesc Texture(uint binding, TextureViewHandle view, uint arrayElement = 0)
        => new() { Binding = binding, ArrayElement = arrayElement, ResourceType = BindingType.TextureRead, TextureView = view };

    public static BindingResourceDesc Sampler(uint binding, SamplerHandle sampler, uint arrayElement = 0)
        => new() { Binding = binding, ArrayElement = arrayElement, ResourceType = BindingType.Sampler, SamplerHandle = sampler };

    public static BindingResourceDesc AccelerationStructureBinding(uint binding, AccelerationStructureHandle accelerationStructure, uint arrayElement = 0)
        => new() { Binding = binding, ArrayElement = arrayElement, ResourceType = BindingType.AccelerationStructure, AccelerationStructure = accelerationStructure };
}

public sealed record BindingSetDesc
{
    public string Name { get; init; } = string.Empty;
    public BindingSetFlags Flags { get; init; }
    public BindingResourceDesc[] Resources { get; init; } = Array.Empty<BindingResourceDesc>();
}

public readonly record struct DynamicOffset
{
    public uint Binding { get; init; }
    public uint ArrayElement { get; init; }
    public uint OffsetInBytes { get; init; }

    public DynamicOffset(uint binding, uint arrayElement, uint offsetInBytes)
    {
        Binding = binding;
        ArrayElement = arrayElement;
        OffsetInBytes = offsetInBytes;
    }
}

public sealed record ShaderModuleDesc
{
    public string Name { get; init; } = string.Empty;
    public Backend Backend { get; init; }
    public ShaderStage Stage { get; init; }
    public string EntryPoint { get; init; } = string.Empty;
    public ShaderBytecodeFormat BytecodeFormat { get; init; }
    public ReadOnlyMemory<byte> Bytecode { get; init; }
}

public sealed record RasterizerDesc
{
    public CullMode CullMode { get; init; } = CullMode.Back;
    public FillMode FillMode { get; init; } = FillMode.Solid;
    public bool FrontCounterClockwise { get; init; } = true;
    public int DepthBias { get; init; }
    public float DepthBiasClamp { get; init; }
    public float SlopeScaledDepthBias { get; init; }
}

public sealed record DepthStencilDesc
{
    public bool DepthEnable { get; init; }
    public bool DepthWriteEnable { get; init; }
    public CompareOp DepthCompare { get; init; } = CompareOp.LessOrEqual;
}

public sealed record BlendDesc
{
    public bool AlphaToCoverageEnable { get; init; }
    public IReadOnlyList<BlendTargetDesc> Targets { get; init; } = Array.Empty<BlendTargetDesc>();
}

public sealed record BlendTargetDesc
{
    public bool Enable { get; init; }
    public BlendFactor SourceColor { get; init; } = BlendFactor.One;
    public BlendFactor DestinationColor { get; init; } = BlendFactor.Zero;
    public BlendOp ColorOp { get; init; } = BlendOp.Add;
    public BlendFactor SourceAlpha { get; init; } = BlendFactor.One;
    public BlendFactor DestinationAlpha { get; init; } = BlendFactor.Zero;
    public BlendOp AlphaOp { get; init; } = BlendOp.Add;
    public ColorWriteMask WriteMask { get; init; } = ColorWriteMask.All;
}

public sealed record VertexLayoutDesc
{
    public uint Slot { get; init; }
    public uint StrideInBytes { get; init; }
    public VertexInputRate InputRate { get; init; } = VertexInputRate.Vertex;
    public uint InstanceStepRate { get; init; } = 1;
}

public sealed record VertexAttributeDesc
{
    public uint Location { get; init; }
    public uint BufferSlot { get; init; }
    public Format Format { get; init; }
    public uint OffsetInBytes { get; init; }
}

public sealed record MultisampleDesc
{
    public uint SampleCount { get; init; } = 1;
    public uint SampleMask { get; init; } = uint.MaxValue;
}

public static class IndirectArgumentSize
{
    public const uint Draw = 16;
    public const uint DrawIndexed = 20;
    public const uint Dispatch = 12;
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DrawIndirectArguments(
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstInstance);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DrawIdxArgs(
    uint IndexCount,
    uint InstanceCount,
    uint FirstIndex,
    int VertexOffset,
    uint FirstInstance);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DispatchIndirectArguments(
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ);

public readonly record struct IndirectDrawDesc
{
    public BufferHandle Arguments { get; init; }
    public ulong ArgumentOffset { get; init; }
    public uint DrawCount { get; init; } = 1;
    public uint StrideInBytes { get; init; } = IndirectArgumentSize.Draw;
    public BufferHandle CountBuffer { get; init; }
    public ulong CountBufferOffset { get; init; }

    public IndirectDrawDesc()
    {
    }
}

public readonly record struct DrawIdxDesc
{
    public BufferHandle Arguments { get; init; }
    public ulong ArgumentOffset { get; init; }
    public uint DrawCount { get; init; } = 1;
    public uint StrideInBytes { get; init; } = IndirectArgumentSize.DrawIndexed;
    public BufferHandle CountBuffer { get; init; }
    public ulong CountBufferOffset { get; init; }

    public DrawIdxDesc()
    {
    }
}

public readonly record struct IndirectDispatchDesc
{
    public BufferHandle Arguments { get; init; }
    public ulong ArgumentOffset { get; init; }
    public uint DispatchCount { get; init; } = 1;
    public uint StrideInBytes { get; init; } = IndirectArgumentSize.Dispatch;
    public BufferHandle CountBuffer { get; init; }
    public ulong CountBufferOffset { get; init; }

    public IndirectDispatchDesc()
    {
    }
}

public sealed record PipelineCacheDesc
{
    public string Name { get; init; } = string.Empty;
    public ReadOnlyMemory<byte> InitialData { get; init; }
}

public sealed record ComputePipelineDesc
{
    public string Name { get; init; } = string.Empty;
    public PipelineLayoutHandle Layout { get; init; }
    public ShaderModuleHandle ComputeShader { get; init; }
    public PipelineCacheHandle PipelineCache { get; init; }
}

public sealed record GraphicsPipelineDesc
{
    public string Name { get; init; } = string.Empty;
    public PipelineLayoutHandle Layout { get; init; }
    public ShaderModuleHandle VertexShader { get; init; }
    public ShaderModuleHandle PixelShader { get; init; }
    public ShaderModuleHandle HullShader { get; init; }
    public ShaderModuleHandle DomainShader { get; init; }
    public ShaderModuleHandle GeometryShader { get; init; }
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public uint PatchControlPoints { get; init; }
    public IReadOnlyList<VertexLayoutDesc> VertexBuffers { get; init; } = Array.Empty<VertexLayoutDesc>();
    public IReadOnlyList<VertexAttributeDesc> VertexAttributes { get; init; } = Array.Empty<VertexAttributeDesc>();
    public IReadOnlyList<Format> ColorFormats { get; init; } = Array.Empty<Format>();
    public Format DepthStencilFormat { get; init; } = Format.Unknown;
    public uint SampleCount { get; init; } = 1;
    public MultisampleDesc Multisample { get; init; } = new();
    public RasterizerDesc Rasterizer { get; init; } = new();
    public DepthStencilDesc DepthStencil { get; init; } = new();
    public BlendDesc Blend { get; init; } = new();
    public PipelineCacheHandle PipelineCache { get; init; }
}

public sealed record MeshPipelineDesc
{
    public string Name { get; init; } = string.Empty;
    public PipelineLayoutHandle Layout { get; init; }
    public ShaderModuleHandle AmplificationShader { get; init; }
    public ShaderModuleHandle MeshShader { get; init; }
    public ShaderModuleHandle PixelShader { get; init; }
    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public IReadOnlyList<Format> ColorFormats { get; init; } = Array.Empty<Format>();
    public Format DepthStencilFormat { get; init; } = Format.Unknown;
    public uint SampleCount { get; init; } = 1;
    public MultisampleDesc Multisample { get; init; } = new();
    public RasterizerDesc Rasterizer { get; init; } = new();
    public DepthStencilDesc DepthStencil { get; init; } = new();
    public BlendDesc Blend { get; init; } = new();
    public PipelineCacheHandle PipelineCache { get; init; }
}

public readonly record struct ColorAttachmentDesc
{
    public ColorAttachmentDesc()
    {
    }

    public TextureViewHandle View { get; init; }
    public TextureViewHandle ResolveTarget { get; init; }
    public LoadOp LoadOp { get; init; } = LoadOp.Load;
    public StoreOp StoreOp { get; init; } = StoreOp.Store;
    public Color ClearColor { get; init; } = Color.Transparent;
}

public readonly record struct DepthAttachDesc
{
    public DepthAttachDesc()
    {
    }

    public TextureViewHandle View { get; init; }
    public LoadOp DepthLoadOp { get; init; } = LoadOp.Load;
    public StoreOp DepthStoreOp { get; init; } = StoreOp.Store;
    public ClearDepthStencil ClearValue { get; init; } = new(1.0f);
    public bool DepthReadOnly { get; init; }
}

public readonly ref struct RenderPassDesc
{
    public RenderPassDesc()
    {
    }

    public string Name { get; init; } = string.Empty;
    public ReadOnlySpan<ColorAttachmentDesc> ColorAttachments { get; init; } = default;
    public DepthAttachDesc? DepthStencilAttachment { get; init; }
    public Rect RenderArea { get; init; }
}

public sealed record ComputePassDesc
{
    public string Name { get; init; } = string.Empty;
}

public sealed record AccelerationStructureDesc
{
    public string Name { get; init; } = string.Empty;
    public AccelerationStructureKind Kind { get; init; } = AccelerationStructureKind.BottomLevel;
    public ulong SizeInBytes { get; init; }
}

public readonly record struct AccelBuildSizes(
    ulong AccelerationStructureSizeInBytes,
    ulong BuildScratchSizeInBytes,
    ulong UpdateScratchSizeInBytes);

public sealed record AccelGeomDesc
{
    public AccelGeomKind Kind { get; init; } = AccelGeomKind.Triangles;
    public AccelGeomFlags Flags { get; init; } = AccelGeomFlags.Opaque;
    public Format VertexFormat { get; init; } = Format.Rgba32Float;
    public BufferHandle VertexBuffer { get; init; }
    public ulong VertexOffset { get; init; }
    public uint VertexStrideInBytes { get; init; }
    public uint VertexCount { get; init; }
    public BufferHandle IndexBuffer { get; init; }
    public ulong IndexOffset { get; init; }
    public IndexFormat IndexFormat { get; init; } = IndexFormat.UInt32;
    public uint IndexCount { get; init; }
    public BufferHandle TransformBuffer { get; init; }
    public ulong TransformOffset { get; init; }
    public BufferHandle AabbBuffer { get; init; }
    public ulong AabbOffset { get; init; }
    public uint AabbStrideInBytes { get; init; }
    public uint AabbCount { get; init; }
    public BufferHandle InstanceBuffer { get; init; }
    public ulong InstanceOffset { get; init; }
    public uint InstanceCount { get; init; }
}

public sealed record AccelBuildDesc
{
    public string Name { get; init; } = string.Empty;
    public AccelerationStructureKind Kind { get; init; } = AccelerationStructureKind.BottomLevel;
    public AccelerationStructureHandle Destination { get; init; }
    public AccelerationStructureHandle Source { get; init; }
    public AccelBuildFlags Flags { get; init; }
    public IReadOnlyList<AccelGeomDesc> Geometries { get; init; } = Array.Empty<AccelGeomDesc>();
    public BufferHandle ScratchBuffer { get; init; }
    public ulong ScratchOffset { get; init; }
}

public sealed record RtGroupDesc
{
    public string Name { get; init; } = string.Empty;
    public RtGroupKind Kind { get; init; }
    public ShaderModuleHandle GeneralShader { get; init; }
    public ShaderModuleHandle AnyHitShader { get; init; }
    public ShaderModuleHandle ClosestHitShader { get; init; }
    public ShaderModuleHandle IntersectionShader { get; init; }
}

public sealed record RtPipelineDesc
{
    public string Name { get; init; } = string.Empty;
    public PipelineLayoutHandle Layout { get; init; }
    public IReadOnlyList<ShaderModuleHandle> Shaders { get; init; } = Array.Empty<ShaderModuleHandle>();
    public IReadOnlyList<RtGroupDesc> ShaderGroups { get; init; } = Array.Empty<RtGroupDesc>();
    public uint MaxRayRecursionDepth { get; init; } = 1;
    public uint MaxPayloadSizeInBytes { get; init; }
    public uint MaxAttributeSizeInBytes { get; init; } = 8;
    public PipelineCacheHandle PipelineCache { get; init; }
}

public readonly record struct ShaderTableRegion(
    BufferHandle Buffer,
    ulong Offset,
    ulong SizeInBytes,
    ulong StrideInBytes);

public readonly record struct ShaderTableDesc
{
    public ShaderTableRegion RayGeneration { get; init; }
    public ShaderTableRegion Miss { get; init; }
    public ShaderTableRegion HitGroup { get; init; }
    public ShaderTableRegion Callable { get; init; }
}

public sealed record RtPassDesc
{
    public string Name { get; init; } = string.Empty;
}

public sealed record CommandListDesc
{
    public string Name { get; init; } = string.Empty;
    public QueueType QueueType { get; init; } = QueueType.Graphics;
}

public sealed record QueryPoolDesc
{
    public string Name { get; init; } = string.Empty;
    public QueryType Type { get; init; } = QueryType.Timestamp;
    public uint Count { get; init; }
}

public sealed record SwapchainDesc
{
    public string Name { get; init; } = string.Empty;
    public nint NativeWindowHandle { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public Format Format { get; init; } = Format.Bgra8Unorm;
    public uint BufferCount { get; init; } = 3;
    public bool AllowTearing { get; init; } = true;
    public ColorSpace ColorSpace { get; init; } = ColorSpace.Sdr;
    public SwapchainMode Mode { get; init; } = SwapchainMode.Windowed;
    public Rational RefreshRate { get; init; }
    public Hdr10Metadata? Hdr10Metadata { get; init; }
}

public readonly record struct Rational(uint Numerator, uint Denominator)
{
    public static readonly Rational Default = new(0, 0);
}

public readonly record struct Hdr10Metadata
{
    public ushort RedPrimaryX { get; init; }
    public ushort RedPrimaryY { get; init; }
    public ushort GreenPrimaryX { get; init; }
    public ushort GreenPrimaryY { get; init; }
    public ushort BluePrimaryX { get; init; }
    public ushort BluePrimaryY { get; init; }
    public ushort WhitePointX { get; init; }
    public ushort WhitePointY { get; init; }
    public uint MaxMasteringLuminance { get; init; }
    public uint MinMasteringLuminance { get; init; }
    public ushort MaxContentLightLevel { get; init; }
    public ushort MaxFrameAverageLightLevel { get; init; }
}

public readonly record struct PresentDesc
{
    public uint SyncInterval { get; init; }
    public bool AllowTearing { get; init; }

    public PresentDesc()
    {
    }
}

public readonly record struct TextureBarrier(
    TextureHandle Texture,
    ResourceState Before,
    ResourceState After,
    SubresourceRange Range);

public readonly record struct BufferBarrier(
    BufferHandle Buffer,
    ResourceState Before,
    ResourceState After);

public readonly record struct AliasingBarrier
{
    public AliasingResource Before { get; init; }
    public AliasingResource After { get; init; }

    public static AliasingBarrier Between(TextureHandle before, TextureHandle after)
        => Between(AliasingResource.TextureResource(before), AliasingResource.TextureResource(after));

    public static AliasingBarrier Between(BufferHandle before, BufferHandle after)
        => Between(AliasingResource.BufferResource(before), AliasingResource.BufferResource(after));

    public static AliasingBarrier Between(BufferHandle before, TextureHandle after)
        => Between(AliasingResource.BufferResource(before), AliasingResource.TextureResource(after));

    public static AliasingBarrier Between(TextureHandle before, BufferHandle after)
        => Between(AliasingResource.TextureResource(before), AliasingResource.BufferResource(after));

    public static AliasingBarrier FromUnknown(TextureHandle after)
        => Between(AliasingResource.None, AliasingResource.TextureResource(after));

    public static AliasingBarrier FromUnknown(BufferHandle after)
        => Between(AliasingResource.None, AliasingResource.BufferResource(after));

    public static AliasingBarrier Release(TextureHandle before)
        => Between(AliasingResource.TextureResource(before), AliasingResource.None);

    public static AliasingBarrier Release(BufferHandle before)
        => Between(AliasingResource.BufferResource(before), AliasingResource.None);

    private static AliasingBarrier Between(AliasingResource before, AliasingResource after)
        => new() { Before = before, After = after };
}

public sealed record MemoryHeapDesc
{
    public string Name { get; init; } = string.Empty;
    public ulong SizeInBytes { get; init; }
    public MemoryClass Memory { get; init; } = MemoryClass.DeviceLocal;
    public MemoryHeapKind Kind { get; init; } = MemoryHeapKind.Buffer;
    public MemoryHeapFlags Flags { get; init; }
}

public readonly record struct QueueWait(FenceHandle Fence, ulong Value);

public readonly record struct QueueSignal(FenceHandle Fence, ulong Value);
