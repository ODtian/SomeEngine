# SomeEngine RHI API Spec

> Status: v0 public API re-frozen after the BATCH-24 advanced capability completion. Future public API changes require an explicit spec update and architecture review; implementation gaps must be fixed behind this API unless a new approved batch reopens the contract.

## Design Inputs

Primary references: NRI, The Forge, O3DE Atom RHI, NVRHI, Diligent Engine, Unreal RDG/RHI descriptor allocation practice, Dawn, wgpu, Filament, Godot RenderingDevice, and SDL3 GPU.

The API favors high-performance explicit control over convenience abstractions. RenderGraph remains above RHI. RHI exposes stable cross-backend semantics and keeps backend-native allocation details private.

Core rules:

- public API uses typed handles, descriptors, value types, spans, and explicit state
- hot paths must not allocate managed arrays, look up binding names, or depend on dictionaries
- invalid descriptors, invalid handles, unsupported features, state mismatches, and device loss fail fast
- shader compilation, shader reflection, material binding names, upload helpers, RDG scheduling, and resource allocator policy are outside core RHI
- D3D12/Vulkan/Metal implementation details must not leak into core interfaces

## Names

Types live in `SomeEngine.Rhi`; avoid repeating `Rhi` in type names unless ambiguity requires it. Public interfaces keep the normal C# `I` prefix.

Examples:

- `IDevice`
- `IQueue`
- `BufferHandle`
- `TextureHandle`
- `ResourceState`
- `PipelineLayoutDesc`

The API deliberately keeps `*Handle` names. Handles are the core resource identity model. C# ergonomics come from init-only descriptors, overloads, `ReadOnlySpan<T>`, `stackalloc`-friendly value descriptors, and static factory methods for union-like value types, not from managed object wrappers.

## Handles

GPU objects are addressed by lightweight typed generational handles:

- `BufferHandle`
- `TextureHandle`
- `TextureViewHandle`
- `BufferViewHandle`
- `SamplerHandle`
- `ShaderModuleHandle`
- `BindingLayoutHandle`
- `PipelineLayoutHandle`
- `BindingSetHandle`
- `PipelineHandle`
- `PipelineCacheHandle`
- `AccelerationStructureHandle`
- `CommandBufferHandle`
- `FenceHandle`
- `QueryPoolHandle`
- `SwapchainHandle`
- `MemoryHeapHandle`

Each handle contains:

- `Id`
- `Generation`
- `IsValid`

`default` is always invalid. Backends must reject invalid, stale, cross-device, or destroyed handles.

Resources, views, pipelines, binding objects, command buffers, fences, query pools, swapchains, and heaps are not managed object wrappers. Core RHI must not expose `GpuBuffer : IDisposable`, `Texture : IDisposable`, or similar resource classes as the primary API. A future utilities layer may provide owning convenience wrappers, but those wrappers are not part of the hot-path contract.

Destroy is exposed as type-safe overloads, not object disposal:

```csharp
void Destroy(BufferHandle buffer);
void Destroy(TextureHandle texture);
void Destroy(TextureViewHandle view);
void Destroy(BufferViewHandle view);
void Destroy(SamplerHandle sampler);
void Destroy(ShaderModuleHandle shader);
void Destroy(BindingLayoutHandle layout);
void Destroy(PipelineLayoutHandle layout);
void Destroy(BindingSetHandle set);
void Destroy(PipelineHandle pipeline);
void Destroy(CommandBufferHandle commandBuffer);
void Destroy(FenceHandle fence);
void Destroy(QueryPoolHandle queryPool);
void Destroy(SwapchainHandle swapchain);
void Destroy(MemoryHeapHandle heap);
```

Destroy uses explicit ownership plus dependency validation, not reference-counted lifetime.

- callers own handles and must destroy them explicitly
- backends must reject destroying an object while live views, binding sets, pipeline layouts, pipelines, command buffers, swapchains, or placed heap allocations still depend on it
- dependency lists captured by backend records are validation data, not ownership references
- RHI must not silently keep a resource alive after `Destroy(handle)` through hidden AddRef/Release semantics
- native API reference counting, such as D3D12 COM lifetime, is a backend implementation detail and must not change public handle lifetime semantics

Descriptor inputs are copied at creation time when the backend stores them. Callers may reuse or mutate original lists and shader byte arrays after object creation without changing the created object.

Null backend storage rules:

- command operation arrays are backend-owned implementation detail, not API-visible storage
- command buffer destruction releases any rented operation storage
- hot-path state maps use internal `SomeEngine.Core.Collections.InlineFlatDictionaryCore<TKey, TValue>` storage, keeping small sets in `[InlineArray(8)]` storage and spilling to shared pooled open-addressed storage; public callers use reference-type `SomeEngine.Core.Collections.InlineFlatDictionary<TKey, TValue>` and `FlatDictionary<TKey, TValue>` standard collection wrappers
- texture copy observability uses sparse copied-region backing, not whole-texture pixel arrays
- texture writes prune stale fully covered sparse segments

## Backend Organization

`SomeEngine.Rhi` contains:

- public interfaces
- public descriptors, enums, flags, handles, and value types
- shared validation helpers
- Null backend while standalone RHI is being established

Backend implementations live in separate assemblies:

- `SomeEngine.Rhi.D3D12` depends on `Vortice.Windows`
- future `SomeEngine.Rhi.Vulkan` depends on `Silk.NET.Vulkan`
- future `SomeEngine.Rhi.Metal` depends on the selected Metal binding layer

Core RHI must not reference Vortice, Silk Vulkan, native D3D12 types, Vulkan handles, Metal handles, or backend-specific descriptor allocator types.

`Instance.Create()` must not hard-code a single backend implementation. Backend selection is driven by `DeviceDesc.Backend`, while `Instance` aggregates backend factories supplied by the host application. Core RHI registers Null by default. External backend assemblies contribute factories explicitly:

```csharp
using IInstance instance = Instance.Create(D3D12Backend.Factory);
using IDevice device = instance.CreateDevice(new DeviceDesc { Backend = Backend.D3D12 });
```

Upper layers do not directly instantiate backend device classes. They pass backend factories into instance creation, then select devices through `DeviceDesc.Backend`. This keeps Vortice/Silk dependencies out of `SomeEngine.Rhi` and avoids static global backend registration.

## Instance, Device, Queue

`Instance` owns backend selection and adapter discovery.

`IDevice` owns resources, views, shaders, pipeline layouts, pipelines, binding sets, command encoders, command buffers, fences, query pools, swapchains, and memory heaps.

`IQueue` submits command buffers and signals timeline fences.

Queues are explicit:

- graphics
- compute
- copy

Missing queue support must fail fast. Queue families are queried through `IDevice.QueueFamilies` before `GetQueue`. Each family reports queue type, count, and graphics/compute/copy/timestamp capabilities.

Target public entry points before API freeze:

- `Instance.Create(params IBackendFactory[] backendFactories)`
- `IBackendFactory`
- `IInstance.EnumerateAdapters()`
- `IInstance.CreateDevice(DeviceDesc desc)`
- `IDevice.AdapterInfo`
- `IDevice.Features`
- `IDevice.Limits`
- `IDevice.QueueFamilies`
- `IDevice.GetQueue(QueueType type, uint index = 0)`
- `IDevice.GetBufferReqs(BufferDesc desc)`
- `IDevice.GetTextureReqs(TextureDesc desc)`
- `IDevice.CreateMemoryHeap(MemoryHeapDesc desc)`
- `IDevice.GetHeapDesc(MemoryHeapHandle heap)`
- `IDevice.GetMemoryBudget(MemoryClass memory)`
- `IDevice.CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default)`
- `IDevice.CreateTexture(TextureDesc desc)`
- `IDevice.CreatePlacedBuffer(MemoryHeapHandle heap, ulong offset, BufferDesc desc, ReadOnlySpan<byte> initialData = default)`
- `IDevice.CreatePlacedTexture(MemoryHeapHandle heap, ulong offset, TextureDesc desc)`
- `IDevice.CreateTextureView(TextureHandle texture, TextureViewDesc desc)`
- `IDevice.CreateBufferView(BufferHandle buffer, BufferViewDesc desc)`
- `IDevice.CreateSampler(SamplerDesc desc)`
- `IDevice.CreateShaderModule(ShaderModuleDesc desc)`
- `IDevice.CreateBindingLayout(BindingLayoutDesc desc)`
- `IDevice.CreateBindingLayout(ReadOnlySpan<BindingSlotDesc> slots, string name = "")`
- `IDevice.CreatePipelineLayout(PipelineLayoutDesc desc)`
- `IDevice.CreatePipelineLayout(ReadOnlySpan<BindingLayoutHandle> bindingLayouts, ReadOnlySpan<PushRangeDesc> pushConstants = default, ReadOnlySpan<StaticSamplerDesc> staticSamplers = default, string name = "")`
- `IDevice.CreateBindingSet(BindingLayoutHandle layout, BindingSetDesc desc)`
- `IDevice.CreateBindingSet(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, string name = "")`
- `IDevice.CreateComputePipeline(ComputePipelineDesc desc)`
- `IDevice.CreateGraphicsPipeline(GraphicsPipelineDesc desc)`
- `IDevice.CreateGraphicsPipeline(GraphicsPipelineDesc desc, ReadOnlySpan<VertexLayoutDesc> vertexBuffers, ReadOnlySpan<VertexAttributeDesc> vertexAttributes, ReadOnlySpan<Format> colorFormats, ReadOnlySpan<BlendTargetDesc> blendTargets)`
- `IDevice.CreateCommandEncoder(CommandEncoderDesc desc)`
- `IDevice.CreateFence(string name, ulong initialValue = 0)`
- `IDevice.CreateQueryPool(QueryPoolDesc desc)`
- `IDevice.CreateSwapchain(SwapchainDesc desc)`
- `IDevice.GetSwapchain(SwapchainHandle handle)`
- `IDevice.GetBufferDesc(BufferHandle buffer)`
- `IDevice.GetTextureDesc(TextureHandle texture)`
- `IDevice.GetBufferAlloc(BufferHandle buffer)`
- `IDevice.GetTextureAlloc(TextureHandle texture)`
- `IDevice.GetBufferState(BufferHandle buffer)`
- `IDevice.GetTextureState(TextureHandle texture, uint mipLevel = 0, uint arraySlice = 0)`
- `IDevice.GetFormatSupport(Format format)`
- `IDevice.GetFormatCapabilities(Format format)`
- `IDevice.GetFenceValue(FenceHandle fence)`
- `IDevice.WaitFence(FenceHandle fence, ulong value, ulong timeoutNanoseconds = ulong.MaxValue)`
- `IDevice.MapBuffer(BufferHandle buffer, MapMode mode, ulong offset = 0, int sizeInBytes = -1)`
- `IDevice.FlushBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)`
- `IDevice.InvalidateBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)`
- `IDevice.UnmapBuffer(BufferHandle buffer)`
- `IDevice.Destroy(...)` overloads for every created handle family
- `IDevice.WaitIdle()`
- `IQueue.Submit(ReadOnlySpan<CommandBufferHandle> commandBuffers, ReadOnlySpan<QueueWait> waits = default, ReadOnlySpan<QueueSignal> signals = default)`
- `IQueue.WaitIdle()`
- `ICommandEncoder.Barrier(ReadOnlySpan<TextureBarrier> textureBarriers, ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)`
- `ICommandEncoder.UavBarrier(TextureHandle texture)`
- `ICommandEncoder.UavBarrier(BufferHandle buffer)`
- `ICommandEncoder.BeginRenderPass(in RenderPassDesc desc)`
- `ICommandEncoder.BeginComputePass(ComputePassDesc desc)`
- `ICommandEncoder.CopyBuffer(...)`
- `ICommandEncoder.CopyToTexture(...)`
- `ICommandEncoder.CopyToBuffer(...)`
- `ICommandEncoder.CopyTexture(...)`
- `ICommandEncoder.ResolveTexture(...)`
- `ICommandEncoder.WriteTimestamp(...)`
- `ICommandEncoder.BeginQuery(...)`
- `ICommandEncoder.EndQuery(...)`
- `ICommandEncoder.ResolveQueryData(...)`
- `ICommandEncoder.PushDebugGroup(string name)`
- `ICommandEncoder.PopDebugGroup()`
- `ICommandEncoder.InsertDebugMarker(string name)`
- `ICommandEncoder.Finish()`
- `IRenderOps.SetViewport(...)`
- `IRenderOps.SetScissor(...)`
- `IRenderOps.SetPipeline(...)`
- `IRenderOps.SetBindingSet(...)`
- `IRenderOps.SetBindings(...)`
- `IRenderOps.SetPushConstants(...)`
- `IRenderOps.SetVertexBuffer(...)`
- `IRenderOps.SetIndexBuffer(...)`
- `IRenderOps.Draw(...)`
- `IRenderOps.DrawIndexed(...)`
- `IRenderOps.DrawIndirect(...)`
- `IRenderOps.DrawIndexedIndirect(...)`
- `IRenderOps.End()`
- `IComputeOps.SetPipeline(...)`
- `IComputeOps.SetBindingSet(...)`
- `IComputeOps.SetBindings(...)`
- `IComputeOps.SetPushConstants(...)`
- `IComputeOps.Dispatch(...)`
- `IComputeOps.DispatchIndirect(...)`
- `IComputeOps.End()`
- `ISwapchain.Resize(uint width, uint height)`
- `ISwapchain.Present(in PresentDesc desc)`

The advanced capability freeze extends several provisional entries above with descriptor structs and richer presentation descriptors. Compatibility wrappers are intentionally not part of the public RHI because the project is still in construction.

## Adapter, Features, Limits

`AdapterInfo` must expose enough data for deterministic adapter selection and budget decisions:

```csharp
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
```

`DeviceFeatures` must include feature gates even when v0 implementations return `false`:

- graphics, compute, and copy queues
- bindless descriptors
- partially bound descriptors
- dynamic offsets
- static samplers
- placed resources
- mixed resource heaps
- resource aliasing
- memory budget
- timestamp queries
- occlusion queries
- pipeline statistics queries
- draw indirect
- multi-draw indirect
- dispatch indirect
- multi-dispatch indirect
- typed UAV load
- sampler anisotropy
- texture cube arrays
- conservative rasterization
- depth bounds test
- independent blend
- logic op
- dual-source blend
- mesh shader
- ray tracing
- variable rate shading

`DeviceLimits` must cover binding counts, descriptor counts, push constant size, vertex input limits, texture dimensions, compute group limits, memory heap/placement alignment, copy alignment, and timestamp period. Actual resource placement must still use `Get*MemoryRequirements`; limits are coarse capability data.

## Memory And Allocation

Core RHI supports committed resources and placed resources before API freeze.

`CreateBuffer` and `CreateTexture` always create committed resources. `CreatePlacedBuffer` and `CreatePlacedTexture` create resources from a caller-supplied heap and offset. Do not encode allocation mode as `BufferDesc.Ownership = Placed` or `TextureDesc.Ownership = Placed`; ownership is not resource shape, it is allocation source.

Required public types:

```csharp
public enum MemoryClass
{
    DeviceLocal,
    CpuUpload,
    CpuReadback,
}

public enum MemoryHeapKind
{
    Buffer,
    Texture,
    RenderTargetOrDepthStencil,
    Mixed,
}

[Flags]
public enum MemoryHeapFlags
{
    None = 0,
    AllowAliasing = 1 << 0,
}

public sealed record MemoryHeapDesc
{
    public string Name { get; init; } = string.Empty;
    public ulong SizeInBytes { get; init; }
    public MemoryClass Memory { get; init; } = MemoryClass.DeviceLocal;
    public MemoryHeapKind Kind { get; init; } = MemoryHeapKind.Buffer;
    public MemoryHeapFlags Flags { get; init; }
}

public readonly record struct ResourceMemoryRequirements(
    ulong SizeInBytes,
    ulong Alignment,
    MemoryHeapKind HeapKind,
    bool RequiresDedicatedAllocation,
    bool PrefersDedicatedAllocation);

public enum ResourceOwnership
{
    Committed,
    Placed,
    Swapchain,
    External,
}

public readonly record struct ResourceAllocationInfo(
    ResourceOwnership Ownership,
    MemoryClass Memory,
    MemoryHeapHandle Heap,
    ulong HeapOffset,
    ulong SizeInBytes);

public readonly record struct MemoryBudget(
    MemoryClass Memory,
    ulong BudgetInBytes,
    ulong CurrentUsageInBytes,
    bool IsExact);
```

Rules:

- `MemoryHeapKind` is part of heap compatibility; D3D12 resource heap tier restrictions must be represented through requirements and fail-fast validation
- `Mixed` is only valid when `DeviceFeatures.MixedResourceHeaps` is true
- `RequiresDedicatedAllocation` means `CreatePlaced*` must reject the descriptor
- heap offsets must satisfy `ResourceMemoryRequirements.Alignment`
- heap kind must match `ResourceMemoryRequirements.HeapKind`, except for supported compatible mixed heaps
- placed resources sharing overlapping heap ranges require explicit aliasing barriers when lifetime/use switches
- heap destruction fails if live placed resources still reference it
- committed resources return `ResourceOwnership.Committed` and `MemoryHeapHandle.Invalid` in allocation info
- swapchain textures return `ResourceOwnership.Swapchain` and `MemoryHeapHandle.Invalid`

D3D12 v0 must implement committed resources, placed resources, memory requirements, heap creation, allocation info, aliasing barrier support, budget query, and ranged mapping. The allocator strategy can remain thin; the public API must not be deferred.

## Resources

Core resources are buffers and textures.

`BufferDesc` describes only buffer shape and usage:

```csharp
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
```

`TextureDesc` describes only texture shape and usage:

```csharp
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
```

`OptimizedClearValue` is part of the freeze target because D3D12 render-target/depth-stencil optimized clear values and equivalent backend allocation hints are resource creation state.

Views are independent handles. A texture is not implicitly an SRV, RTV, DSV, or UAV. A buffer is not implicitly a CBV, SRV, UAV, vertex buffer, or index buffer view.

`TextureViewDesc` must include:

- `Name`
- `Kind`
- `Dimension`
- `Format`
- `FirstMip`
- `MipCount`
- `FirstSlice`
- `SliceCount`
- `PlaneSlice`

`BufferViewDesc` must include:

- `Name`
- `Kind`
- `Offset`
- `SizeInBytes`
- `Format`
- `StrideInBytes`
- `Raw`

View creation validates format compatibility, mip ranges, array/depth slices, plane slice, bind flags, resource ownership, and view dimensions. Typeless/reinterpretation families are deferred until explicit compatibility rules exist.

## Formats And Capabilities

The format enum must cover renderer-normal usage before renderer migration:

- R/RG/RGBA 8-bit, 16-bit, and 32-bit integer, unsigned integer, normalized, and float formats
- sRGB variants for common color formats
- common depth/stencil formats
- BC1 through BC7 compressed texture formats
- common swapchain formats

`FormatCapabilities` must report:

- support flags
- sample counts
- typed UAV load
- typed UAV store
- filterability
- blendability
- depth/stencil classification

D3D12 format capabilities must come from real feature queries such as `CheckFeatureSupport`, not hard-coded optimistic tables. Unsupported format/resource/view/pipeline combinations fail fast.

## Vertex Input

RHI vertex input is location based:

- `VertexAttributeDesc.Location = n` means vertex input location `n`.
- D3D12 maps that location to `SemanticName = "ATTRIB"` and `SemanticIndex = n`.
- HLSL or Slang-generated D3D12 vertex shader inputs must therefore use `ATTRIBn` for IA-fed vertex attributes.
- `POSITION`, `COLOR`, `TEXCOORD`, and similar semantics are allowed for shader-stage outputs and pixel-stage inputs, but they are not RHI vertex input locations.

Example:

```hlsl
struct VertexInput
{
    float4 InstanceColor : ATTRIB0;
};

struct PixelInput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
};
```

The matching RHI attribute is:

```csharp
new VertexAttributeDesc
{
    Location = 0,
    BufferSlot = 0,
    Format = Format.Rgba32Float,
    OffsetInBytes = 0,
}
```

RHI does not compile shaders, reflect bytecode, or infer vertex input semantics from shader source. Asset/tooling layers must generate shader bytecode whose vertex input signature matches the explicit `GraphicsPipelineDesc.VertexAttributes` contract. A D3D12 shader input declared as `COLOR0` will not match `Location = 0`; use `ATTRIB0`.

## Binding And Pipeline Layout

Bindings use descriptor set / bind group semantics.

Core hot path uses:

- set index
- binding index
- array index
- typed handles

Core hot path does not use strings.

`PipelineLayout` explicitly contains:

- binding layouts
- push/root constants
- static samplers
- shader stage visibility
- compatibility signature

`BindingLayoutDesc` describes exactly one descriptor set layout. Slots do not carry a set index. `PipelineLayoutDesc.BindingLayouts[index]` defines the set index.

`BindingSlotDesc` must express:

- set-local binding index
- resource type
- shader stages
- descriptor array count
- slot flags for dynamic offset, partially bound, and bindless behavior
- optional resource shape used for partially-bound null descriptors and for explicit layout/resource validation

Descriptor arrays are required. Bindless, partially bound descriptors, and dynamic offsets are feature-gated. Feature-disabled use must fail fast. Static samplers are declared in `PipelineLayoutDesc.StaticSamplers`, are pipeline layout state, must point at an existing set, and must not conflict with descriptor slots.

```csharp
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
```

`Shape` is mandatory in practice for partially-bound texture slots, non-raw storage-buffer slots, and sampler slots because a backend must create native null descriptors with the same descriptor class the shader expects. Texture partial slots declare `TextureDimension` and concrete non-depth `Format`. Structured storage-buffer partial slots declare `StrideInBytes`; typed storage-buffer partial slots declare `Format`; exactly one of those two fields is set. Sampler partial slots declare the sampler shape through `Sampler`; comparison sampler slots must set `Sampler.Compare`. Constant-buffer and raw-buffer null descriptors do not need extra shape.

Binding sets are created from `BindingLayoutHandle` and bind against pipeline layouts by layout compatibility signature rather than pipeline layout object identity. They are immutable by default. `BindingSetFlags.Mutable` opts into explicit `UpdateBindingSet` for descriptor elements that must change without recreating the whole set.

RHI exposes binding semantics, not native descriptor allocation. Public API must never expose descriptor rings, descriptor heaps, heap slots, GPU descriptor handles, Vulkan descriptor pools, Metal argument-buffer allocation, or fence-retirement queues.

The binding model has two required paths:

- persistent binding sets for material, global, and other long-lived bindings
- transient binding packets for RDG pass-local and frame-local graph resources

Persistent binding sets use `CreateBindingSet` and `SetBindingSet`. They are explicit objects with caller-managed lifetime and are appropriate when the bound resources are stable across passes or frames.

Transient binding packets are stack/frame-local semantic packets built from a `BindingLayoutHandle` plus `BindingResourceDesc` entries. They are consumed by a pass encoder during command recording and do not create public handle objects:

```csharp
void SetBindings(
    uint setIndex,
    BindingLayoutHandle layout,
    ReadOnlySpan<BindingResourceDesc> resources,
    ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
```

`DynamicOffset` descriptors are binding-keyed value structs. The positional `ReadOnlySpan<uint>` model is rejected for the final advanced API because it makes call sites depend on hidden layout ordering. See `advanced_capability_api_decisions.md`.

Slots flagged `DynamicOffset` must be fully populated buffer slots. `DynamicOffset` cannot be combined with `Bindless` or `PartiallyBound`; sparse or large descriptor arrays must use persistent mutable binding sets without dynamic offset rewriting.

`BindingResourceDesc` is the binding-specific union. Do not use a generic resource union for binding.

```csharp
public readonly record struct BindingResourceDesc
{
    public uint Binding { get; init; }
    public uint ArrayElement { get; init; }
    public BindingType ResourceType { get; init; }
    public BufferViewHandle BufferView { get; init; }
    public TextureViewHandle TextureView { get; init; }
    public SamplerHandle SamplerHandle { get; init; }
    public AccelerationStructureHandle AccelerationStructure { get; init; }

    public static BindingResourceDesc Clear(uint binding, uint arrayElement = 0);
    public static BindingResourceDesc Buffer(uint binding, BufferViewHandle view, uint arrayElement = 0);
    public static BindingResourceDesc Texture(uint binding, TextureViewHandle view, uint arrayElement = 0);
    public static BindingResourceDesc Sampler(uint binding, SamplerHandle sampler, uint arrayElement = 0);
    public static BindingResourceDesc AccelerationStructureBinding(uint binding, AccelerationStructureHandle accelerationStructure, uint arrayElement = 0);
}
```

The convenience factories select the common read-only resource type for buffers/textures and sampler type for samplers. Callers use object initializers when the layout slot requires a specific non-default binding type, such as `ConstantBuffer`, `StorageBufferReadWrite`, `RawBufferRead`, or `TextureReadWrite`.

`BindingResourceDesc.Clear` is valid only for `UpdateBindingSet` on layout slots flagged `PartiallyBound`. `Bindless` does not imply clear/null semantics; sparse bindless tables must set both `Bindless` and `PartiallyBound`. `Clear` removes the specified element from a mutable binding set and causes the backend to materialize the slot as a null descriptor where the native API requires a descriptor table entry. It is not a resource type for layout slots and is not accepted in `CreateBindingSet` or transient `SetBindings`.

Transient `SetBindings` requires fully populated, non-`PartiallyBound`, non-`Bindless` layouts. Use `CreateBindingSet` plus `UpdateBindingSet` for partially bound or bindless arrays so null descriptor materialization, sparse updates, and large descriptor tables stay off the per-draw/per-dispatch transient binding path.

D3D12 splits a set's resource descriptors into a persistent resource table and a dynamic-resource table. `SetBindingSet(..., dynamicOffsets)` binds the persistent table unchanged and rewrites only dynamic buffer descriptors, keeping large partially-bound/bindless tables out of the per-draw/per-dispatch descriptor path.

`SetBindings` is the RDG path. RDG resolves logical graph resources to current RHI resource/view handles, builds the transient packet, and calls the encoder. RDG does not allocate shader-visible descriptors and does not own backend descriptor heap policy.

Backend responsibilities differ by API:

- D3D12 backend owns descriptor cache, shader-visible online descriptor heap/ring, CPU/offline descriptor allocators, descriptor-table dirty tracking, heap rollover, and fence/sync-point retirement
- Vulkan backend owns descriptor pool/set allocation or a future descriptor-buffer allocator
- Metal backend owns direct resource binding or argument-buffer allocation
- Null backend validates layout/resource/state compatibility without allocating native descriptors

Core RHI may share layout compatibility signatures, binding packet validation, resource type checks, and binding hashes. Native allocator state stays backend-private.

## Shaders And Pipelines

RHI does not compile shaders and does not perform shader reflection. Shader modules receive backend-native bytecode only.

`ShaderModuleDesc` contains:

- name
- backend
- shader stage
- entry point
- bytecode format
- bytecode

Bytecode formats:

- D3D12: DXIL only
- Vulkan: SPIR-V only
- Metal: Metal library/payload only

Shader interface metadata belongs to shader tooling or the asset pipeline, not RHI core. Slang, DXC, SPIR-V reflection, or an offline cooker may generate metadata for editor validation and asset authoring, but `SomeEngine.Rhi` does not accept or require that metadata.

D3D12 pipeline state uses the explicit `PipelineLayoutHandle` root signature generated from RHI pipeline layout descriptors. RHI does not rely on shader-embedded root signatures.

D3D12 register mapping is fixed by RHI descriptors:

- descriptor set `n` maps to register space `n`
- `BindingSlotDesc.Binding` maps to the native shader register index inside that space
- `ConstantBuffer` maps to `b#`
- `RawBufferRead`, `StorageBufferRead`, and `TextureRead` map to `t#`
- `RawBufferReadWrite`, `StorageBufferReadWrite`, and `TextureReadWrite` map to `u#`
- `Sampler` maps to `s#`
- push constant range index `i` maps to root constants at `b100+i, space0`

Shader tooling must reserve `b100+` in `space0` for RHI push constants. Push constants are root constants, not descriptors, and must not be declared as regular binding layout slots.

Pipeline creation validates only RHI-owned state: layout handles, shader handles, shader stages, pipeline formats, vertex input descriptors, render-state descriptors, and other explicit descriptors. Shader bytecode compatibility with the supplied pipeline layout is delegated to the native backend and its validation layer.

Graphics pipeline descriptors must cover:

- shader stages
- pipeline layout
- vertex buffer layouts
- vertex attributes
- primitive topology
- rasterizer state
- depth/stencil state
- multisample state
- sample mask
- color target formats
- depth/stencil format
- per-target blend state
- debug name

Compute pipelines contain shader module and pipeline layout.

Pipeline cache blobs, shader compilation, shader reflection, and material name binding are outside core RHI.

## Samplers

`SamplerDesc` must cover:

- minification, magnification, and mip filters
- address modes
- LOD bias and LOD clamp
- anisotropy
- optional compare operation
- border color

Sampler anisotropy is feature-gated. Unsupported sampler state fails fast.

## Commands

Commands use:

- `ICommandEncoder`
- `IRenderOps`
- `IComputeOps`

Encoders are one-shot. A command buffer produced by `Finish()` can be submitted once. Re-submitting a submitted command buffer is invalid. `CommandEncoderDesc.QueueType` fixes the queue class for the finished command buffer. Submitting a command buffer to another queue is invalid. If `Finish()` fails because the encoder is in a terminal invalid state, such as an open pass or unbalanced debug marker, the encoder is aborted: active resource references and transient backend storage are released, and the encoder cannot be used again.

D3D12 backend maintains per-queue command allocator/list pools and recycles them through fence-deferred retirement. `WaitIdle()` is valid for tests, resize, shutdown, and exceptional synchronization, not as a normal frame path.

Render pass attachments explicitly carry:

- view
- load op
- store op
- clear value
- resolve target
- render area
- depth/stencil read/write semantics

`RenderPassDesc` is stack-only and carries color attachments as `ReadOnlySpan<ColorAttachmentDesc>`. `ColorAttachmentDesc` and `DepthAttachDesc` are value types, so per-frame render pass setup can use stack or pooled memory instead of descriptor lists.

The core API keeps lightweight dynamic render pass begin. It does not introduce persistent `RenderPassHandle` or `FramebufferHandle` before Vulkan proves a need. Vulkan should use dynamic rendering if that remains the better fit for the final backend matrix; otherwise a later persistent render pass proposal must prove the compatibility cost.

Render pass encoder must cover:

- pipeline bind
- viewport
- scissor
- vertex buffer bind
- index buffer bind
- persistent binding set bind
- transient binding packet bind
- push constants
- draw
- indexed draw
- draw indirect
- indexed draw indirect
- end pass

Compute pass encoder must cover:

- pipeline bind
- persistent binding set bind
- transient binding packet bind
- push constants
- direct dispatch
- indirect dispatch
- end pass

Queue submit and resource barriers remain explicit. This is not a WebGPU clone.

## Barriers, Aliasing, And Sync

Core exposes explicit barrier primitives.

Texture barriers support subresource ranges. Null validation tracks texture state per mip/slice subresource.

Buffer barriers are whole-buffer in the initial API.

Resource state is explicit:

- within one command encoder, `Before` must match that encoder's local recorded state or fail fast
- first use of a resource in a command encoder records the required input state as a submit-time precondition
- queue submit validates those preconditions against device global state plus earlier command buffers in the same submit
- successful queue submit commits final command-buffer states to device global state
- render pass begin validates attachment state
- binding validates resource state
- copy validates copy source/destination state
- RHI does not perform Diligent-style automatic transitions
- RenderGraph is expected to generate most barriers in higher-level renderer paths

Required resource states:

- `Undefined`
- `Common`
- `Present`
- `VertexBuffer`
- `IndexBuffer`
- `ConstantBuffer`
- `ShaderResource`
- `UnorderedAccess`
- `RenderTarget`
- `DepthWrite`
- `DepthRead`
- `CopySource`
- `CopyDestination`
- `ResolveSource`
- `ResolveDestination`
- `IndirectArgument`
- `QueryResolve`

Placed resources require aliasing barriers when overlapping heap memory switches from one resource use to another. Do not expose a generic public `ResourceRef`. The only union-like resource endpoint in core RHI is aliasing-specific:

```csharp
public enum AliasingResourceKind
{
    None,
    Buffer,
    Texture,
}

public readonly record struct AliasingResource
{
    public AliasingResourceKind Kind { get; init; }
    public BufferHandle Buffer { get; init; }
    public TextureHandle Texture { get; init; }

    public static AliasingResource None => default;
    public static AliasingResource BufferResource(BufferHandle buffer);
    public static AliasingResource TextureResource(TextureHandle texture);
}

public readonly record struct AliasingBarrier
{
    public AliasingResource Before { get; init; }
    public AliasingResource After { get; init; }

    public static AliasingBarrier Between(TextureHandle before, TextureHandle after);
    public static AliasingBarrier Between(BufferHandle before, BufferHandle after);
    public static AliasingBarrier Between(BufferHandle before, TextureHandle after);
    public static AliasingBarrier Between(TextureHandle before, BufferHandle after);
    public static AliasingBarrier FromUnknown(TextureHandle after);
    public static AliasingBarrier FromUnknown(BufferHandle after);
    public static AliasingBarrier Release(TextureHandle before);
    public static AliasingBarrier Release(BufferHandle before);
}
```

Most user code should call `AliasingBarrier.Between(...)`, `FromUnknown(...)`, or `Release(...)` overloads and never manually construct `AliasingResource`.

When `ICommandEncoder.Barrier(...)` receives aliasing barriers together with texture or buffer barriers, aliasing barriers are ordered first. This lets a single call activate a placed resource and then transition it. Releasing an aliased resource after its final use must be a later barrier call, not mixed with that final use in the same call.

Synchronization uses timeline fences and queue submit waits/signals. CPU waiting must support fence-value waits:

```csharp
void WaitFence(FenceHandle fence, ulong value, ulong timeoutNanoseconds = ulong.MaxValue);
```

Debug markers are explicit push/pop/insert commands and pushed groups must be balanced before `Finish`.

Command buffer lifetime is backend-managed after creation. A command buffer is one-shot and can be submitted once. Destroying an unsubmitted command buffer releases its backend storage immediately. Destroying a submitted D3D12 command buffer before the GPU has retired it invalidates the public handle immediately and moves backend storage to a fence-deferred retirement queue; transient descriptors and command allocator/list storage are released only after the retirement fence completes. Resource destroy checks must still treat pending retired command buffers as live until the fence is complete.

## Copy, Upload, Readback

Core exposes low-level copy primitives:

- buffer-to-buffer
- buffer-to-texture
- texture-to-buffer
- texture-to-texture
- MSAA resolve

Copy regions explicitly carry row pitch, slice pitch, mip level, array slice, and alignment-sensitive fields. Null backend executes buffer-to-texture, texture-to-buffer, and texture-to-texture copies into observable backing storage so upload/readback tests can validate data movement.

Upload rings, staging allocators, direct texture initial data, `UpdateTexture`, `UpdateBuffer`, and convenience readback helpers are outside core. D3D12 backend may internally use an upload allocator for `initialData`, but the public API does not expose staging details.

Project-level convenience upload/readback helpers belong in `SomeEngine.Rhi.Utilities`, RDG, or renderer-specific uploader code and must be built from core primitives: map, copy, barrier, submit, and fence.

## Mapping

Only buffers are mappable. Textures are uploaded/read back through buffers and copy commands.

Target API:

```csharp
Memory<byte> MapBuffer(BufferHandle buffer, MapMode mode, ulong offset = 0, int sizeInBytes = -1);
void FlushBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes);
void InvalidateBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes);
void UnmapBuffer(BufferHandle buffer);
```

Rules:

- `CpuUpload` buffers can be mapped for write
- `CpuReadback` buffers can be mapped for read
- `DeviceLocal` buffers cannot be mapped
- range must be inside the buffer
- D3D12 may implement flush/invalidate as no-op
- Vulkan needs flush/invalidate for non-coherent host-visible memory, so the API must exist before Vulkan integration

## Queries

Query pools are explicit:

```csharp
public enum QueryType
{
    Timestamp,
    Occlusion,
    PipelineStatistics,
}
```

```csharp
public sealed record QueryPoolDesc
{
    public string Name { get; init; } = string.Empty;
    public QueryType Type { get; init; }
    public uint Count { get; init; }
}
```

Command API must include:

- `WriteTimestamp(QueryPoolHandle pool, uint queryIndex)`
- `BeginQuery(QueryPoolHandle pool, uint queryIndex)`
- `EndQuery(QueryPoolHandle pool, uint queryIndex)`
- `ResolveQueryData(QueryPoolHandle pool, uint firstQuery, uint queryCount, BufferHandle destination, ulong destinationOffset)`

D3D12 and Null must implement timestamp, occlusion, and pipeline statistics query pools. `WriteTimestamp` is valid only for timestamp pools. `BeginQuery`/`EndQuery` are valid only for non-timestamp pools. `ResolveQueryData` writes tightly packed results: 8 bytes for timestamp/occlusion and 88 bytes for pipeline statistics.

`TimestampPeriodNanoseconds` comes from real queue timestamp frequency.

## Swapchain

D3D12 uses DXGI flip discard. Default buffer count is three. `SwapchainDesc.NativeWindowHandle` is HWND on D3D12.

`SwapchainDesc` target shape:

```csharp
public sealed record SwapchainDesc
{
    public string Name { get; init; } = string.Empty;
    public nint NativeWindowHandle { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public Format Format { get; init; }
    public uint BufferCount { get; init; } = 3;
    public bool AllowTearing { get; init; } = true;
    public ColorSpace ColorSpace { get; init; } = ColorSpace.Sdr;
}

public enum ColorSpace
{
    Sdr,
    Hdr10,
}
```

`ISwapchain` target shape:

```csharp
public interface ISwapchain
{
    uint Width { get; }
    uint Height { get; }
    Format Format { get; }
    uint CurrentBackBufferIndex { get; }
    TextureHandle CurrentTexture { get; }
    TextureViewHandle CurrentRenderTargetView { get; }

    void Present(in PresentDesc desc);
    void Resize(uint width, uint height);
}
```

The final swapchain API uses `PresentDesc` plus expanded swapchain mode/colorspace/HDR metadata; see `advanced_capability_api_decisions.md`. There is no `Present(bool)` compatibility path.

Rules:

- `Present(new PresentDesc { SyncInterval = 0, AllowTearing = true })` uses tearing when the swapchain and backend support it
- `Present(new PresentDesc { SyncInterval = 1 })` uses vsync interval 1
- `PresentDesc.AllowTearing` defaults to false; tearing must be requested explicitly per present call
- resize waits/retires frame resources and recreates backbuffer texture/view handles
- old backbuffer handles become invalid after resize
- D3D12 implements SDR, scRGB, HDR10 color-space configuration, HDR10 metadata, borderless fullscreen, and best-effort exclusive fullscreen; unsupported DXGI color-space/fullscreen cases fail fast

## Native Extension Boundary

Native handles do not pollute core interfaces.

If native access is needed, use backend extension interfaces:

```csharp
T GetBackendInterface<T>() where T : class;
```

or an equivalent backend-extension lookup. Extensions may expose D3D12/Vulkan/Metal native handles in backend assemblies only. Core API remains backend-neutral. The D3D12 backend exposes `ID3D12DeviceInterop`, `ID3D12QueueInterop`, and `ID3D12SwapchainInterop`, and may also return the native Vortice interface when requested directly.

D3D12 native interop supports borrowed native device/queue/swapchain/resource access, borrowed native buffer/texture/acceleration-structure resources, and explicit external `ID3D12Resource` import through backend-only descriptors. Imported resources must state ownership, initial state, bind flags, format/size, and lifetime rules.

Shared-handle export/import, cross-adapter resources, and explicit residency control are still outside core RHI. If added later, they must remain backend extension APIs with explicit import/export descriptors; they must not be smuggled through `TextureDesc.Ownership` or native pointer fields.

## Utilities Boundary

Do not add these to core `IDevice`:

- `UpdateBuffer`
- `UpdateTexture`
- `SomeEngine.Rhi.Utilities.MipGenerator`
- global resource allocator policy
- upload manager
- readback helper
- transient RDG allocator
- shader compiler
- shader reflection
- material binding name mapper

They belong in `SomeEngine.Rhi.Utilities`, RDG, renderer code, or asset/shader tooling. Core RHI must provide the low-level primitives those utilities need: placed memory, memory requirements, mapping, copy, barriers, query resolve, queues, and fences.

## Renderer Migration

Engine renderer migration is intentionally outside the current RHI-only batch. Do not migrate `SomeEngine.Render` wholesale as part of backend bring-up. The D3D12 backend must first pass standalone RHI tests/sample coverage, then renderer entry points should be replaced incrementally through a Diligent usage coverage matrix.

The standalone RHI coverage gate is:

- device creation
- swapchain clear and present
- graphics pipeline triangle
- compute UAV write
- buffer upload
- texture upload
- readback validation
- resize
- timestamp query
- debug marker
- placed resource creation
- aliasing barrier validation
- transient binding packet path backed by a D3D12 descriptor cache / online descriptor heap
- indirect draw/indexed draw/dispatch
- bindless and partially-bound binding sets
- dynamic offsets
- MSAA resolve and utility mip generation
- mesh/amplification, geometry, and tessellation shader paths
- ray tracing BLAS/TLAS/SBT and trace dispatch
- pipeline cache object validation
- native D3D12 borrowed access/import extension paths
- descriptor/frame retirement through fence wait
- HDR/colorspace/fullscreen swapchain descriptor validation

## Validation

Validation is strict in debug and Null backend.

Errors are categorized:

- invalid descriptor
- invalid handle
- unsupported feature
- backend failure
- device lost
- validation failure

Invalid API usage fails fast. Unsupported capabilities fail fast with a clear error. Null backend is a strict validation backend, not a no-op success backend.

## Advanced Capability API Freeze

The old "Deferred Non-99% Features" bucket is no longer precise enough for renderer migration. Several advanced capabilities are important to the final RHI even if their D3D12 implementation lands in phases.

The API shape and decision record for these capabilities lives in `docs/rhi/advanced_capability_api_decisions.md`:

- indirect draw / indexed indirect / dispatch indirect
- bindless and partially bound descriptors
- dynamic offsets
- ray tracing BLAS/TLAS/SBT and trace dispatch
- mesh shader / amplification shader
- geometry and tessellation shaders
- MSAA resolve and generate-mips utility boundary
- pipeline cache blob API
- native interop/import/export boundary
- descriptor/frame retirement soak tests
- HDR, colorspace, and fullscreen swapchain policy

Unsupported features still fail fast through `DeviceFeatures`, descriptor validation, and backend-specific capability checks. Shader compilation/reflection, Diligent SRB/name variable compatibility, automatic render graph behavior, public native descriptor allocator APIs, explicit residency APIs, protected memory, and multi-GPU/cross-adapter policy remain outside core RHI until a separate design proves they are required.

`SomeEngine.Rhi.Utilities.MipGenerator` is implemented as a utility, not as a core command. It builds compute dispatches from core RHI primitives and requires caller-supplied backend shader bytecode for the fixed utility ABI. This preserves the core rule that RHI accepts bytecode and explicit layouts but does not compile, reflect, or infer shader resources.

The current `MipGenerator` utility supports only 2D textures and 2D texture arrays. Cube, cube-array, and 3D textures fail fast with `UnsupportedFeature` until their shader ABI and view contract are designed separately.

## Freeze Gate

Completed in BATCH-20 and re-frozen in BATCH-24. The API was not allowed to freeze until a real D3D12 vertical slice and the 99% advanced-capability surface covered:

- backend factory selection through `DeviceDesc.Backend`
- committed and placed resource creation
- memory requirements, memory heaps, allocation info, and budget query
- aliasing barriers
- swapchain clear and present
- graphics pipeline triangle
- compute UAV write
- buffer upload
- texture upload
- readback validation
- ranged map/flush/invalidate/unmap API
- resize
- timestamp query
- debug marker
- transient binding packet path backed by a D3D12 backend descriptor cache / online descriptor heap
- strict Null backend validation for the same public contract
- indirect draw/indexed draw/dispatch
- bindless and partially-bound binding sets
- dynamic offsets
- MSAA resolve and utility mip generation
- mesh/amplification, geometry, and tessellation shader paths
- ray tracing BLAS/TLAS/SBT and trace dispatch
- pipeline cache object validation
- native D3D12 borrowed access/import extension paths
- descriptor/frame retirement through fence wait
- HDR/colorspace/fullscreen swapchain descriptor validation

Null backend alone was insufficient for API freeze. BATCH-20 added the D3D12 backend vertical slice and strict Null validation; BATCH-24 completes the current non-Engine advanced surface and keeps shader compilation/reflection, RDG scheduling, renderer migration, residency, protected memory, sparse/tiled resources, shared-handle export/import, and multi-GPU/cross-adapter policy outside core RHI. This v0 public API is now frozen again at the RHI layer.
