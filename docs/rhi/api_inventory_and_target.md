# RHI API Inventory And Target

> Status: design deliverable. This file contains the two requested lists: the current public RHI API surface with semantic method notes, and the target architecture API surface.

## Reference Basis

The target design uses three external references:

- UE5 RHI for engine-scale naming around resources, command recording, render passes, and explicit graphics concepts: <https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RHI>.
- Diligent for the current renderer migration context and for what not to keep in core: name-based shader variables and convenience context behavior should not leak into renderer code: <https://github.com/DiligentGraphics/DiligentCore>.
- NVIDIA NRI as the third primary RHI because it is closest to modern explicit D3D12/Vulkan semantics: explicit device, queue, command buffer, memory, descriptors, barriers, and no hidden render graph: <https://github.com/NVIDIA-RTX/NRI>.

Local context:

- Current API files: `src/SomeEngine.Rhi`, `src/SomeEngine.Rhi.D3D12`, and `src/SomeEngine.Rhi/Utilities`.
- Existing design docs: `docs/rhi/api_spec.md`, `docs/rhi/design_baseline.md`, and `docs/rhi/conformance_sources.md`.
- Generated record methods such as `Equals`, `GetHashCode`, `ToString`, deconstructors, and compiler-created constructors are not listed as RHI API behavior.

## Current Public API

### Current Behavior Types

#### `Instance`

| Method | Semantic note |
|---|---|
| `Create(params IBackendFactory[] backendFactories)` | Builds an aggregate RHI instance with Null always available and host-provided native backends added explicitly. |

#### `IInstance`

| Method | Semantic note |
|---|---|
| `EnumerateAdapters()` | Reports adapters across every registered backend so the host can make a deterministic device choice. |
| `CreateDevice(DeviceDesc desc)` | Creates one logical device from the backend named in `DeviceDesc` rather than relying on static global backend registration. |
| `Dispose()` | Ends adapter/device creation through this instance and makes subsequent instance calls invalid. |

#### `IBackendFactory`

| Method | Semantic note |
|---|---|
| `EnumerateAdapters()` | Lets a backend expose its available adapters before a device exists. |
| `CreateDevice(DeviceDesc desc)` | Constructs the backend-specific `IDevice` when the requested backend matches the factory. |

#### `IExtensible`

| Method | Semantic note |
|---|---|
| `Get<T>()` | Retrieves a capability or native-extension interface without adding backend-specific members to the core interface. |

#### `IDevice`

| Method | Semantic note |
|---|---|
| `GetQueue(QueueType type, uint index = 0)` | Returns an explicit queue of the requested type and index, failing instead of silently falling back to another queue class. |
| `CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default)` | Creates a committed buffer whose shape, memory class, allowed use, and initial state are fixed at creation. |
| `CreateTexture(TextureDesc desc)` | Creates a committed texture whose dimensions, format, usage, sample count, and optimized clear metadata are fixed at creation. |
| `CreateTextureView(TextureHandle texture, TextureViewDesc desc)` | Creates a shader, render-target, or depth view over a typed texture subresource range. |
| `CreateBufferView(BufferHandle buffer, BufferViewDesc desc)` | Creates a shader-visible, UAV, or constant-buffer view over a byte range of a buffer. |
| `CreateSampler(SamplerDesc desc)` | Creates immutable sampler state validated against device feature support. |
| `CreateShaderModule(ShaderModuleDesc desc)` | Stores backend-ready shader bytecode without compiling or reflecting it in core RHI. |
| `CreateBindingLayout(BindingLayoutDesc desc)` | Defines the descriptor slots a resource set must satisfy for a pipeline layout. |
| `CreateBindingLayout(ReadOnlySpan<BindingSlotDesc> slots, string name = "")` | Provides a stack-friendly overload for creating the same binding layout without allocating a descriptor list at the call site. |
| `CreatePipelineLayout(PipelineLayoutDesc desc)` | Creates the cross-backend root signature or pipeline layout from set layouts, push ranges, and static samplers. |
| `CreatePipelineLayout(ReadOnlySpan<BindingLayoutHandle> bindingLayouts, ReadOnlySpan<PushRangeDesc> pushConstants = default, ReadOnlySpan<StaticSamplerDesc> staticSamplers = default, string name = "")` | Provides a stack-friendly pipeline layout path for hot creation code and tests. |
| `CreateBindingSet(BindingLayoutHandle layout, BindingSetDesc desc)` | Materializes a persistent resource set that must match one binding layout. |
| `CreateBindingSet(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, string name = "")` | Creates a persistent resource set from span data without forcing the caller to allocate a descriptor object. |
| `UpdateBindingSet(BindingSetHandle bindingSet, ReadOnlySpan<BindingResourceDesc> resources)` | Rewrites selected descriptors in a mutable binding set while preserving its declared layout contract. |
| `CreateComputePipeline(ComputePipelineDesc desc)` | Creates a compute pipeline from explicit shader and layout handles. |
| `CreateGraphicsPipeline(GraphicsPipelineDesc desc)` | Creates a graphics pipeline from shader stages, render-state descriptors, vertex input, and attachment formats. |
| `CreateGraphicsPipeline(GraphicsPipelineDesc desc, ReadOnlySpan<VertexLayoutDesc> vertexBuffers, ReadOnlySpan<VertexAttributeDesc> vertexAttributes, ReadOnlySpan<Format> colorFormats, ReadOnlySpan<BlendTargetDesc> blendTargets)` | Creates the same graphics pipeline with span-fed vertex and target state to avoid per-call list allocations. |
| `CreateMeshPipeline(MeshPipelineDesc desc)` | Creates a mesh/amplification graphics pipeline when the backend exposes mesh shader support. |
| `CreateCommandList(CommandListDesc desc)` | Opens a one-shot command recorder; despite the name, the object is an encoder until `Finish` returns a command buffer handle. |
| `CreateFence(string name, ulong initialValue = 0)` | Creates a timeline fence used by queue submit and CPU waits. |
| `CreateSwapchain(SwapchainDesc desc)` | Creates a presentation chain for a native window and returns a handle to it. |
| `GetSwapchain(SwapchainHandle handle)` | Resolves a swapchain handle to the object that exposes resize, present, and current back-buffer handles. |
| `GetBufferDesc(BufferHandle buffer)` | Returns the creation shape of a live buffer for validation, tools, and utility code. |
| `GetTextureDesc(TextureHandle texture)` | Returns the creation shape of a live texture for view creation, copies, and utility code. |
| `GetBufferAlloc(BufferHandle buffer)` | Reports whether a buffer is committed, placed, swapchain-owned, or imported and which memory it occupies. |
| `GetTextureAlloc(TextureHandle texture)` | Reports the allocation source and memory range behind a texture. |
| `GetBufferState(BufferHandle buffer)` | Reports the device-global state last committed for a buffer. |
| `GetTextureState(TextureHandle texture, uint mipLevel = 0, uint arraySlice = 0)` | Reports the device-global state last committed for one texture subresource. |
| `GetFormatSupport(Format format)` | Returns a compact usage mask for quick format capability checks. |
| `GetFormatCapabilities(Format format)` | Returns the richer format support record including sample counts, typed UAV support, filtering, blending, and depth/stencil classification. |
| `GetFenceValue(FenceHandle fence)` | Reads the last completed or signaled timeline value known for a fence. |
| `WaitFence(FenceHandle fence, ulong value, ulong timeoutNanoseconds = ulong.MaxValue)` | Blocks the CPU until a fence reaches a value or validation determines the wait cannot complete. |
| `MapBuffer(BufferHandle buffer, MapMode mode, ulong offset = 0, int sizeInBytes = -1)` | Exposes a CPU-visible buffer range according to its memory class and map mode. |
| `FlushBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)` | Makes CPU writes visible to backends that need explicit host-memory flushing. |
| `InvalidateBufferRange(BufferHandle buffer, ulong offset, ulong sizeInBytes)` | Makes GPU writes visible to the CPU on backends with non-coherent host memory. |
| `UnmapBuffer(BufferHandle buffer)` | Ends the active CPU mapping and releases the mapped range contract. |
| `Destroy(BufferHandle handle)` | Destroys a buffer only when no live views, sets, commands, or placed-heap constraints depend on it. |
| `Destroy(TextureHandle handle)` | Destroys a texture only when no live views, sets, commands, or swapchain ownership rules block it. |
| `Destroy(TextureViewHandle handle)` | Destroys a texture view after dependency validation. |
| `Destroy(BufferViewHandle handle)` | Destroys a buffer view after dependency validation. |
| `Destroy(SamplerHandle handle)` | Destroys a sampler after dependency validation. |
| `Destroy(ShaderModuleHandle handle)` | Destroys shader bytecode metadata after dependent pipelines are gone. |
| `Destroy(BindingLayoutHandle handle)` | Destroys a binding layout after dependent sets and pipeline layouts are gone. |
| `Destroy(PipelineLayoutHandle handle)` | Destroys a pipeline layout after dependent pipelines and command references are gone. |
| `Destroy(BindingSetHandle handle)` | Destroys a resource set after command-buffer retirement and active references allow it. |
| `Destroy(PipelineHandle handle)` | Destroys a compute, graphics, mesh, or ray tracing pipeline after active command references are gone. |
| `Destroy(PipelineCacheHandle handle)` | Releases a pipeline cache object and any backend cache state it owns. |
| `Destroy(AccelerationStructureHandle handle)` | Releases a ray-tracing acceleration structure after build, binding, and command references are clear. |
| `Destroy(CommandBufferHandle handle)` | Invalidates a finished command buffer handle and retires backend storage when the GPU is done with it. |
| `Destroy(FenceHandle handle)` | Destroys a timeline fence after queues and waits no longer reference it. |
| `Destroy(QueryPoolHandle handle)` | Destroys a query pool after recorded and resolved query users are gone. |
| `Destroy(SwapchainHandle handle)` | Destroys a presentation chain and invalidates its owned back-buffer handles. |
| `Destroy(MemoryHeapHandle handle)` | Destroys a heap only when no placed resource still lives inside it. |
| `WaitIdle()` | Drains device work for shutdown, tests, resize, and exceptional synchronization. |
| `Dispose()` | Releases the device and all backend state that belongs to it. |

#### `IMemoryDevice`

| Method | Semantic note |
|---|---|
| `GetBufferReqs(BufferDesc desc)` | Computes the heap kind, size, alignment, and dedicated-allocation rule for a buffer shape. |
| `GetTextureReqs(TextureDesc desc)` | Computes the heap kind, size, alignment, and dedicated-allocation rule for a texture shape. |
| `CreateMemoryHeap(MemoryHeapDesc desc)` | Allocates an explicit heap that placed resources can occupy. |
| `GetHeapDesc(MemoryHeapHandle heap)` | Returns the declared memory class, size, kind, and aliasing rule for a heap. |
| `GetMemoryBudget(MemoryClass memory)` | Reports current and available memory for one memory class when the backend can expose it. |
| `CreatePlacedBuffer(MemoryHeapHandle heap, ulong offset, BufferDesc desc, ReadOnlySpan<byte> initialData = default)` | Creates a buffer at a caller-selected heap offset after compatibility and aliasing validation. |
| `CreatePlacedTexture(MemoryHeapHandle heap, ulong offset, TextureDesc desc)` | Creates a texture at a caller-selected heap offset after compatibility and aliasing validation. |

#### `ICacheDevice`

| Method | Semantic note |
|---|---|
| `CreatePipelineCache(PipelineCacheDesc desc)` | Creates a backend pipeline cache from optional previously saved data. |
| `GetPipelineData(PipelineCacheHandle cache)` | Extracts serialized backend cache data that can warm a subsequent run. |

#### `IRtDevice`

| Method | Semantic note |
|---|---|
| `CreateRtPipeline(RtPipelineDesc desc)` | Creates a ray-tracing pipeline with explicit shader groups and recursion/payload limits. |
| `GetRtSize(PipelineHandle pipeline)` | Returns the backend shader identifier byte width for shader table records. |
| `GetRtId(PipelineHandle pipeline, string shaderGroupName, Span<byte> destination)` | Writes the backend shader identifier for a named ray-tracing shader group. |
| `GetAccelSizes(AccelBuildDesc desc)` | Computes destination and scratch memory sizes for an acceleration-structure build. |
| `CreateAccelerationStructure(AccelerationStructureDesc desc)` | Allocates the resource that stores a BLAS or TLAS. |

#### `IQueryDevice`

| Method | Semantic note |
|---|---|
| `CreateQueryPool(QueryPoolDesc desc)` | Creates timestamp, occlusion, or pipeline-statistics query storage. |

#### `IQueue`

| Method | Semantic note |
|---|---|
| `Submit(ReadOnlySpan<CommandBufferHandle> commandBuffers, ReadOnlySpan<QueueWait> waits = default, ReadOnlySpan<QueueSignal> signals = default)` | Submits one-shot command buffers to their matching queue and applies timeline waits/signals. |
| `WaitIdle()` | Blocks until this queue has retired submitted work. |
| `Get<T>()` | Retrieves backend-native queue extensions through `IExtensible`. |

#### `ICommandList`

| Method | Semantic note |
|---|---|
| `Barrier(ReadOnlySpan<TextureBarrier> textureBarriers, ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)` | Records explicit resource and aliasing transitions, with aliasing ordered before state transitions. |
| `UavBarrier(TextureHandle texture)` | Records an unordered-access dependency for writes to one texture. |
| `UavBarrier(BufferHandle buffer)` | Records an unordered-access dependency for writes to one buffer. |
| `BeginRenderPass(in RenderPassDesc desc)` | Opens render-pass recording over declared attachments and render area. |
| `BeginComputePass(ComputePassDesc desc)` | Opens compute-pass recording on the command list. |
| `BeginRtPass(RtPassDesc desc)` | Opens ray-tracing dispatch recording on the command list. |
| `CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong byteCount)` | Records a byte-range buffer copy that requires explicit source and destination states. |
| `CopyToTexture(BufferHandle source, BufferTextureCopy sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)` | Records a buffer-to-texture copy using an explicit row and slice footprint. |
| `CopyToBuffer(TextureHandle source, TextureCopyRegion sourceRegion, BufferHandle destination, BufferTextureCopy destinationRegion)` | Records a texture-to-buffer copy for readback or format-neutral transfer. |
| `CopyTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)` | Records a texture subresource copy between matching texture shapes and formats. |
| `ResolveTexture(TextureHandle source, TextureCopyRegion sourceRegion, TextureHandle destination, TextureCopyRegion destinationRegion)` | Records an MSAA resolve between compatible full subresources. |
| `BuildAccelerationStructure(AccelBuildDesc desc)` | Records a BLAS or TLAS build using explicit destination, geometry, and scratch buffers. |
| `CopyAccelerationStructure(AccelerationStructureHandle source, AccelerationStructureHandle destination, AccelCopyMode mode)` | Records acceleration-structure clone or compaction work. |
| `WriteTimestamp(QueryPoolHandle queryPool, uint queryIndex)` | Records a timestamp write into a query pool slot. |
| `BeginQuery(QueryPoolHandle queryPool, uint queryIndex)` | Begins a non-timestamp query interval. |
| `EndQuery(QueryPoolHandle queryPool, uint queryIndex)` | Ends a non-timestamp query interval and marks it resolvable. |
| `ResolveQueryData(QueryPoolHandle queryPool, uint firstQuery, uint queryCount, BufferHandle destination, ulong destinationOffset)` | Copies query results into a buffer with the RHI-defined packed result layout. |
| `PushDebugGroup(string name)` | Starts a nested debug marker scope that must be balanced before finish. |
| `PopDebugGroup()` | Ends the current debug marker scope. |
| `InsertDebugMarker(string name)` | Inserts a single marker without changing debug scope depth. |
| `Finish()` | Closes recording and produces a one-shot command buffer handle, or aborts the recorder on terminal validation failure. |
| `Dispose()` | Aborts an unfinished recorder or releases encoder-side resources not owned by a finished command buffer. |

#### `IRenderPass`

| Method | Semantic note |
|---|---|
| `SetViewport(Viewport viewport)` | Sets the viewport transform for following draw calls. |
| `SetScissor(Rect rect)` | Sets the scissor rectangle for following draw calls. |
| `SetPipeline(PipelineHandle pipeline)` | Binds a graphics or mesh pipeline compatible with the open render pass. |
| `SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)` | Binds a persistent resource set at a pipeline layout set index. |
| `SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)` | Binds a transient resource packet without creating a persistent binding set. |
| `SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)` | Writes a small byte range into pipeline layout push/root constants. |
| `SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset = 0)` | Binds one vertex buffer stream with a byte offset. |
| `SetIndexBuffer(BufferHandle buffer, IndexFormat format, ulong offset = 0)` | Binds the index buffer and index width used by indexed draws. |
| `Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)` | Emits a non-indexed draw using the current graphics state. |
| `DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)` | Emits an indexed draw using the current index buffer and graphics state. |
| `DrawIndirect(in IndirectDrawDesc desc)` | Emits one or more non-indexed draws from an argument buffer. |
| `DrawIndexedIndirect(in DrawIdxDesc desc)` | Emits one or more indexed draws from an argument buffer. |
| `DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ)` | Emits a direct mesh shader dispatch inside a render pass. |
| `DispatchMeshIndirect(in IndirectDispatchDesc desc)` | Emits a mesh shader dispatch using argument buffer data. |
| `End()` | Closes the render pass and records attachment store/resolve behavior. |

#### `IComputePass`

| Method | Semantic note |
|---|---|
| `SetPipeline(PipelineHandle pipeline)` | Binds a compute pipeline compatible with the current pipeline layout state. |
| `SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)` | Binds a persistent compute resource set at a pipeline layout set index. |
| `SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)` | Binds a transient compute resource packet without creating a persistent binding set. |
| `Barrier(ReadOnlySpan<TextureBarrier> textureBarriers, ReadOnlySpan<BufferBarrier> bufferBarriers, ReadOnlySpan<AliasingBarrier> aliasingBarriers = default)` | Records explicit transitions inside an open compute pass. |
| `SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)` | Writes compute-visible push/root constants. |
| `Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)` | Emits a direct compute dispatch. |
| `DispatchIndirect(in IndirectDispatchDesc desc)` | Emits one or more compute dispatches from an argument buffer. |
| `End()` | Closes compute pass recording. |

#### `IRtPass`

| Method | Semantic note |
|---|---|
| `SetPipeline(PipelineHandle pipeline)` | Binds a ray-tracing pipeline for following trace commands. |
| `SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)` | Binds a persistent ray-tracing resource set at a pipeline layout set index. |
| `SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default)` | Binds a transient ray-tracing resource packet without creating a persistent binding set. |
| `SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data)` | Writes ray-stage push/root constants. |
| `TraceRays(in ShaderTableDesc shaderBindingTable, uint width, uint height, uint depth)` | Emits ray dispatch work using explicit shader table regions. |
| `End()` | Closes ray-tracing pass recording. |

#### `ISwapchain`

| Method | Semantic note |
|---|---|
| `Resize(uint width, uint height)` | Recreates back buffers and invalidates old swapchain-owned texture/view handles. |
| `Present(in PresentDesc desc)` | Presents the current back buffer with explicit sync interval and tearing intent. |
| `Get<T>()` | Retrieves backend-native swapchain extensions through `IExtensible`. |

#### `ClearValue`

| Method | Semantic note |
|---|---|
| `FromColor(Format format, Color color)` | Creates an optimized clear value for color attachments while leaving depth/stencil unused. |
| `FromDepthStencil(Format format, ClearDepthStencil depthStencil)` | Creates an optimized clear value for depth/stencil attachments while leaving color unused. |

#### `BindingResourceDesc`

| Method | Semantic note |
|---|---|
| `Clear(uint binding, uint arrayElement = 0)` | Describes removal of one descriptor from a partially bound mutable set. |
| `Buffer(uint binding, BufferViewHandle view, uint arrayElement = 0)` | Describes a storage-buffer read binding, which makes the helper too narrow for other buffer binding types. |
| `Texture(uint binding, TextureViewHandle view, uint arrayElement = 0)` | Describes a sampled texture binding, which makes the helper too narrow for UAV textures. |
| `Sampler(uint binding, SamplerHandle sampler, uint arrayElement = 0)` | Describes a sampler binding at one array element. |
| `AccelerationStructureBinding(uint binding, AccelerationStructureHandle accelerationStructure, uint arrayElement = 0)` | Describes a ray-tracing acceleration-structure descriptor at one array element. |

#### `AliasingBarrier`

| Method | Semantic note |
|---|---|
| `Between(TextureHandle before, TextureHandle after)` | Describes heap aliasing ownership moving from one texture to another. |
| `Between(BufferHandle before, BufferHandle after)` | Describes heap aliasing ownership moving from one buffer to another. |
| `Between(BufferHandle before, TextureHandle after)` | Describes heap aliasing ownership moving from a buffer allocation to a texture allocation. |
| `Between(TextureHandle before, BufferHandle after)` | Describes heap aliasing ownership moving from a texture allocation to a buffer allocation. |
| `FromUnknown(TextureHandle after)` | Activates a texture allocation when previous heap contents are not represented by a live RHI object. |
| `FromUnknown(BufferHandle after)` | Activates a buffer allocation when previous heap contents are not represented by a live RHI object. |
| `Release(TextureHandle before)` | Marks a texture allocation as no longer the active occupant of an aliasing heap range. |
| `Release(BufferHandle before)` | Marks a buffer allocation as no longer the active occupant of an aliasing heap range. |

#### `AliasingResource`

| Method | Semantic note |
|---|---|
| `BufferResource(BufferHandle buffer)` | Wraps a buffer as an aliasing-barrier endpoint. |
| `TextureResource(TextureHandle texture)` | Wraps a texture as an aliasing-barrier endpoint. |

#### Data constructors with RHI meaning

| Type | Method | Semantic note |
|---|---|---|
| `FormatCapabilities` | `FormatCapabilities(...)` | Creates the compact capability record for one format from usage support, sample counts, UAV behavior, filtering, blending, and depth/stencil classification. |
| `DynamicOffset` | `DynamicOffset(uint binding, uint arrayElement, uint offsetInBytes)` | Addresses one dynamic-offset descriptor element by binding number and array element. |
| `IndirectDrawDesc` | `IndirectDrawDesc()` | Gives indirect draws a valid single-draw default and tight draw-argument stride. |
| `DrawIdxDesc` | `DrawIdxDesc()` | Gives indexed indirect draws a valid single-draw default and tight indexed-argument stride. |
| `IndirectDispatchDesc` | `IndirectDispatchDesc()` | Gives indirect dispatches a valid single-dispatch default and tight dispatch-argument stride. |
| `ColorAttachmentDesc` | `ColorAttachmentDesc()` | Preserves render-pass defaults where color attachments load and store unless the caller chooses otherwise. |
| `DepthAttachDesc` | `DepthAttachDesc()` | Preserves depth attachment defaults where depth loads, stores, and clears to one when requested. |
| `RenderPassDesc` | `RenderPassDesc()` | Allows stack-created render pass descriptors with empty attachment spans and explicit caller-filled fields. |
| `PresentDesc` | `PresentDesc()` | Makes presentation default to immediate/no-tearing unless the caller asks for sync or tearing. |

#### `RhiException`

| Method | Semantic note |
|---|---|
| `RhiException(ErrorCode code, string message)` | Carries an RHI error category with the failure message so callers can distinguish usage, support, backend, and device-loss failures. |

#### `D3D12Backend`

| Member | Semantic note |
|---|---|
| `Factory` | Exposes the D3D12 backend factory that hosts pass into `Instance.Create`. |

#### `ID3D12DeviceInterop`

| Method | Semantic note |
|---|---|
| `ImportBuffer(ExternalBufferDesc desc)` | Wraps an external `ID3D12Resource` as an RHI buffer with explicit metadata and lifetime rules. |
| `ImportTexture(ExternalTextureDesc desc)` | Wraps an external `ID3D12Resource` as an RHI texture with explicit metadata and lifetime rules. |
| `GetNativeBuffer(BufferHandle buffer)` | Borrows the native D3D12 resource behind a buffer handle. |
| `GetNativeTexture(TextureHandle texture)` | Borrows the native D3D12 resource behind a texture handle. |
| `GetNativeAcceleration(AccelerationStructureHandle accelerationStructure)` | Borrows the native D3D12 resource behind an acceleration-structure handle. |

#### `ID3D12QueueInterop`

This is a native extension property carrier; it exposes `NativeQueue` and has no public methods.

#### `ID3D12SwapchainInterop`

This is a native extension property carrier; it exposes `NativeSwapchain` and has no public methods.

#### `MipGenerator`

| Method | Semantic note |
|---|---|
| `MipGenerator(IDevice device, MipGeneratorDesc desc)` | Builds the fixed compute pipelines and cached views needed by the utility from caller-supplied backend shader bytecode. |
| `GenerateMips(ICommandList list, GenerateMipsDesc desc)` | Records barriers, transient bindings, and compute dispatches that generate mip levels for a supported texture range. |
| `Dispose()` | Destroys the utility-owned shader, layout, pipeline, and cached view handles. |

### Current Data Types

These public types carry API data and have no handwritten RHI behavior beyond properties, constants, or positional record construction.

| Type group | Types |
|---|---|
| Device and adapter data | `DeviceDesc`, `AdapterInfo`, `DeviceFeatures`, `DeviceLimits`, `QueueFamilyInfo` |
| Resource data | `BufferDesc`, `TextureDesc`, `TextureViewDesc`, `BufferViewDesc`, `ResourceMemoryRequirements`, `ResourceAllocationInfo`, `MemoryBudget`, `MemoryHeapDesc` |
| Binding and layout data | `BindingSlotDesc`, `BindShapeDesc`, `BindingLayoutDesc`, `PushRangeDesc`, `StaticSamplerDesc`, `PipelineLayoutDesc`, `BindingSetDesc`, `DynamicOffset` |
| Shader and pipeline data | `ShaderModuleDesc`, `RasterizerDesc`, `DepthStencilDesc`, `BlendDesc`, `BlendTargetDesc`, `VertexLayoutDesc`, `VertexAttributeDesc`, `MultisampleDesc`, `PipelineCacheDesc`, `ComputePipelineDesc`, `GraphicsPipelineDesc`, `MeshPipelineDesc` |
| Indirect draw data | `IndirectArgumentSize`, `DrawIndirectArguments`, `DrawIdxArgs`, `DispatchIndirectArguments`, `IndirectDrawDesc`, `DrawIdxDesc`, `IndirectDispatchDesc` |
| Pass and attachment data | `ColorAttachmentDesc`, `DepthAttachDesc`, `RenderPassDesc`, `ComputePassDesc`, `RtPassDesc`, `Viewport`, `Rect`, `Color`, `ClearDepthStencil`, `SubresourceRange` |
| Copy and barrier data | `TextureCopyRegion`, `BufferTextureCopy`, `TextureBarrier`, `BufferBarrier`, `QueueWait`, `QueueSignal` |
| Ray tracing data | `AccelerationStructureDesc`, `AccelBuildSizes`, `AccelGeomDesc`, `AccelBuildDesc`, `RtGroupDesc`, `RtPipelineDesc`, `ShaderTableRegion`, `ShaderTableDesc` |
| Query and present data | `QueryPoolDesc`, `SwapchainDesc`, `Rational`, `Hdr10Metadata`, `PresentDesc`, `FormatCapabilities` |
| Native extension data | `ExternalBufferDesc`, `ExternalTextureDesc` |
| Utility data | `MipGeneratorDesc`, `GenerateMipsDesc` |
| Handles | `BufferHandle`, `TextureHandle`, `TextureViewHandle`, `BufferViewHandle`, `SamplerHandle`, `ShaderModuleHandle`, `BindingLayoutHandle`, `PipelineLayoutHandle`, `BindingSetHandle`, `PipelineHandle`, `PipelineCacheHandle`, `AccelerationStructureHandle`, `CommandBufferHandle`, `FenceHandle`, `QueryPoolHandle`, `SwapchainHandle`, `MemoryHeapHandle` |
| Enums and flags | `Backend`, `QueueType`, `Format`, `ResourceDimension`, `TextureViewDimension`, `MemoryClass`, `ResourceOwnership`, `MemoryHeapKind`, `MemoryHeapFlags`, `BindFlags`, `ResourceState`, `ViewKind`, `ShaderStageFlags`, `ShaderStage`, `ShaderBytecodeFormat`, `BindingType`, `BindingFlags`, `PrimitiveTopology`, `CullMode`, `FillMode`, `CompareOp`, `IndexFormat`, `LoadOp`, `StoreOp`, `MapMode`, `FilterMode`, `MipmapMode`, `AddressMode`, `BorderColor`, `VertexInputRate`, `ColorWriteMask`, `BlendFactor`, `BlendOp`, `FormatSupport`, `SampleCountFlags`, `QueryType`, `ColorSpace`, `SwapchainMode`, `BindingSetFlags`, `AccelerationStructureKind`, `AccelBuildFlags`, `AccelCopyMode`, `AccelGeomKind`, `AccelGeomFlags`, `RtGroupKind`, `AliasingResourceKind`, `ErrorCode` |

### Current Semantic Friction

- `ICommandList` is an encoder until `Finish`, while `CommandBufferHandle` is the finished submission object; this mixes D3D12 list vocabulary with a WebGPU-style recording lifecycle.
- `BindingLayout`, `BindingSet`, `BindingResourceDesc`, `SetBindingSet`, and `SetBindings` mix layout identity, descriptor writes, persistent sets, and transient sets under near-identical names.
- `IMemoryDevice`, `ICacheDevice`, `IRtDevice`, and `IQueryDevice` are shallow capability splits; they force callers to ask for small sub-interfaces even though the implementation is still the same device.
- `Rt`, `Accel`, `AccelerationStructure`, `ShaderTable`, and `GetRtId` use multiple names for one ray-tracing domain.
- `DrawIdxDesc` is an abbreviation while `DrawIndexed` is spelled out elsewhere.
- `BindFlags`, `BindingFlags`, and `BindingSetFlags` are three different flag domains with names that are easy to confuse during review.
- `ViewKind` names view purpose while `TextureViewDimension` names view shape; both are currently needed, but the word `Kind` hides that difference.
- Static helpers on `BindingResourceDesc` only cover common resource types and can imply the wrong binding type for UAV buffers or UAV textures.

## Target Architecture API

### Target Rules

- Core RHI exposes one deep `IDevice`; optional hardware support is represented by `FeatureSet`, `LimitSet`, validation, and clear errors, not by shallow public capability interfaces.
- Backend-native access stays behind `GetNative<T>()` and backend extension interfaces.
- The recording object is `IEncoder`; the finished submit object is `CommandId`.
- Shader resources use `SetLayout`, `SetId`, and `SetWrite`; this aligns with descriptor set / bind group semantics without Diligent's name-variable model.
- Ray tracing uses `Ray` for pipelines and passes, and `Accel` for BLAS/TLAS storage.
- Handles use `Id` suffix in the target API because these values are identities, not owning managed wrappers.
- Target method names stay within three domain words.

### Target Behavior Types

#### `Rhi`

| Method | Semantic note |
|---|---|
| `Create(params IBackend[] backends)` | Creates an instance from explicitly supplied backend adapters while keeping Null available for validation. |

#### `IBackend`

| Method | Semantic note |
|---|---|
| `ListAdapters()` | Reports the adapters owned by this backend. |
| `CreateDevice(DeviceDesc desc)` | Creates a device for this backend after adapter and feature validation. |

#### `IInstance`

| Method | Semantic note |
|---|---|
| `ListAdapters()` | Lists adapters across registered backends without creating a device. |
| `CreateDevice(DeviceDesc desc)` | Creates one device from the requested backend and adapter selection. |
| `Dispose()` | Closes the instance and rejects subsequent device creation through it. |

#### `INative`

| Method | Semantic note |
|---|---|
| `GetNative<T>()` | Returns a backend-native extension when the object supports it and `null` otherwise. |

#### `IDevice`

| Method | Semantic note |
|---|---|
| `GetQueue(QueueKind kind, uint index = 0)` | Returns the exact queue requested by kind and index. |
| `GetBufferMemory(BufferDesc desc)` | Computes memory requirements for a buffer before committed or placed creation. |
| `GetTextureMemory(TextureDesc desc)` | Computes memory requirements for a texture before committed or placed creation. |
| `CreateHeap(HeapDesc desc)` | Allocates an explicit memory heap for placed resources. |
| `GetHeap(HeapId heap)` | Returns the declared shape and policy of a heap. |
| `GetBudget(MemoryKind memory)` | Reports budget and current usage for a memory class. |
| `CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> data = default)` | Creates a committed buffer with optional initial contents. |
| `CreateTexture(TextureDesc desc)` | Creates a committed texture with explicit shape, format, usage, and initial access. |
| `PlaceBuffer(HeapId heap, ulong offset, BufferDesc desc, ReadOnlySpan<byte> data = default)` | Creates a buffer in a caller-managed heap range. |
| `PlaceTexture(HeapId heap, ulong offset, TextureDesc desc)` | Creates a texture in a caller-managed heap range. |
| `CreateView(TextureId texture, ViewDesc desc)` | Creates a texture view over a subresource range. |
| `CreateView(BufferId buffer, ViewDesc desc)` | Creates a buffer view over a byte range. |
| `CreateSampler(SamplerDesc desc)` | Creates immutable sampler state. |
| `CreateShader(ShaderDesc desc)` | Stores backend-ready shader bytecode and stage identity. |
| `CreateSetLayout(SetLayoutDesc desc)` | Defines the resource slots for one descriptor set. |
| `CreatePipelineLayout(PipelineLayoutDesc desc)` | Defines set layouts, push constants, and static samplers for pipelines. |
| `CreateSet(SetLayoutId layout, SetDesc desc)` | Creates a persistent shader resource set. |
| `UpdateSet(SetId set, ReadOnlySpan<SetWrite> writes)` | Mutates descriptors in a set that was created as mutable. |
| `CreatePipeline(ComputeDesc desc)` | Creates a compute pipeline. |
| `CreatePipeline(GraphicsDesc desc)` | Creates a graphics pipeline. |
| `CreatePipeline(MeshDesc desc)` | Creates a mesh pipeline. |
| `CreatePipeline(RayDesc desc)` | Creates a ray-tracing pipeline. |
| `CreateCache(CacheDesc desc)` | Creates a pipeline cache object from optional saved data. |
| `GetCacheData(CacheId cache)` | Exports pipeline cache bytes for persistence. |
| `GetShaderSize(PipelineId pipeline)` | Returns the shader identifier size required by ray shader tables. |
| `GetShaderId(PipelineId pipeline, string group, Span<byte> dst)` | Writes the identifier for one ray shader group. |
| `GetAccelSize(AccelBuildDesc desc)` | Computes result and scratch sizes for a BLAS or TLAS build. |
| `CreateAccel(AccelDesc desc)` | Allocates a BLAS or TLAS storage object. |
| `CreateEncoder(EncoderDesc desc)` | Opens a one-shot command encoder for one queue kind. |
| `CreateFence(string name, ulong value = 0)` | Creates a timeline fence with an initial value. |
| `CreateQuery(QueryDesc desc)` | Creates timestamp, occlusion, or pipeline-statistics query storage. |
| `CreateSwapchain(SwapchainDesc desc)` | Creates a swapchain for a native surface. |
| `GetSwapchain(SwapchainId swapchain)` | Resolves a swapchain id to its presentation object. |
| `GetDesc(BufferId buffer)` | Returns the immutable descriptor used to create a buffer. |
| `GetDesc(TextureId texture)` | Returns the immutable descriptor used to create a texture. |
| `GetAlloc(BufferId buffer)` | Returns allocation ownership and heap placement for a buffer. |
| `GetAlloc(TextureId texture)` | Returns allocation ownership and heap placement for a texture. |
| `GetState(BufferId buffer)` | Returns the last committed access state for a buffer. |
| `GetState(TextureId texture, uint mip = 0, uint slice = 0)` | Returns the last committed access state for one texture subresource. |
| `GetFormat(Format format)` | Returns format usage, sample count, filtering, blending, and typed UAV support. |
| `GetFence(FenceId fence)` | Reads the current timeline value of a fence. |
| `WaitFence(FenceId fence, ulong value, ulong timeoutNs = ulong.MaxValue)` | Waits on the CPU for a fence value. |
| `Map(BufferId buffer, MapKind kind, ulong offset = 0, int size = -1)` | Maps a CPU-visible buffer range for read or write. |
| `Flush(BufferId buffer, ulong offset, ulong size)` | Flushes a written host-visible range when the backend requires it. |
| `Invalidate(BufferId buffer, ulong offset, ulong size)` | Invalidates a host-visible range before CPU reads when the backend requires it. |
| `Unmap(BufferId buffer)` | Ends a buffer mapping. |
| `Destroy(BufferId id)` | Destroys a buffer after dependency validation. |
| `Destroy(TextureId id)` | Destroys a texture after dependency validation. |
| `Destroy(ViewId id)` | Destroys a buffer or texture view after dependency validation. |
| `Destroy(SamplerId id)` | Destroys a sampler after dependency validation. |
| `Destroy(ShaderId id)` | Destroys a shader module after dependent pipelines are gone. |
| `Destroy(SetLayoutId id)` | Destroys a set layout after dependent sets and pipeline layouts are gone. |
| `Destroy(PipelineLayoutId id)` | Destroys a pipeline layout after dependent pipelines are gone. |
| `Destroy(SetId id)` | Destroys a persistent resource set after command references retire. |
| `Destroy(PipelineId id)` | Destroys any pipeline kind after command references retire. |
| `Destroy(CacheId id)` | Destroys a pipeline cache object. |
| `Destroy(AccelerationStructureHandle handle)` | Destroys a ray-tracing acceleration structure. |
| `Destroy(CommandId id)` | Destroys or retires a finished command buffer. |
| `Destroy(FenceId id)` | Destroys a timeline fence. |
| `Destroy(QueryId id)` | Destroys a query pool. |
| `Destroy(SwapchainId id)` | Destroys a swapchain and its owned images. |
| `Destroy(HeapId id)` | Destroys a heap after placed resources are gone. |
| `WaitIdle()` | Drains all queues owned by the device. |
| `GetNative<T>()` | Returns a backend-native device extension when available. |
| `Dispose()` | Releases device-owned backend state. |

#### `IQueue`

| Method | Semantic note |
|---|---|
| `Submit(ReadOnlySpan<CommandId> commands, ReadOnlySpan<QueueWait> waits = default, ReadOnlySpan<QueueSignal> signals = default)` | Submits finished one-shot commands to this queue with explicit timeline sync. |
| `WaitIdle()` | Blocks until this queue retires all submitted work. |
| `GetNative<T>()` | Returns a backend-native queue extension when available. |

#### `IEncoder`

| Method | Semantic note |
|---|---|
| `Barrier(ReadOnlySpan<TextureBarrier> textures, ReadOnlySpan<BufferBarrier> buffers, ReadOnlySpan<AliasBarrier> aliases = default)` | Records explicit access and aliasing transitions. |
| `UavBarrier(TextureId texture)` | Records an unordered-access dependency for one texture. |
| `UavBarrier(BufferId buffer)` | Records an unordered-access dependency for one buffer. |
| `BeginPass(in RenderPassDesc desc)` | Opens a render pass over explicit attachments. |
| `BeginPass(ComputePassDesc desc)` | Opens a compute pass. |
| `BeginPass(RayPassDesc desc)` | Opens a ray-tracing pass. |
| `Copy(in BufferCopy copy)` | Records a buffer-to-buffer copy. |
| `Copy(in TextureUpload copy)` | Records a buffer-to-texture copy. |
| `Copy(in TextureRead copy)` | Records a texture-to-buffer copy. |
| `Copy(in TextureCopy copy)` | Records a texture-to-texture copy. |
| `Resolve(in ResolveDesc desc)` | Records a multisample resolve. |
| `Build(in AccelBuildDesc desc)` | Records a BLAS or TLAS build. |
| `Copy(in AccelCopy copy)` | Records acceleration-structure clone or compaction work. |
| `WriteTime(QueryId query, uint index)` | Records a timestamp query write. |
| `BeginQuery(QueryId query, uint index)` | Begins a non-timestamp query range. |
| `EndQuery(QueryId query, uint index)` | Ends a non-timestamp query range. |
| `ResolveQuery(QueryId query, uint first, uint count, BufferId dst, ulong offset)` | Writes packed query results into a buffer. |
| `PushDebug(string name)` | Starts a debug marker scope. |
| `PopDebug()` | Ends a debug marker scope. |
| `MarkDebug(string name)` | Emits a one-shot debug marker. |
| `Finish()` | Closes the encoder and returns a submit-ready command id. |
| `Dispose()` | Aborts unfinished recording and releases encoder-owned storage. |

#### `IRenderPass`

| Method | Semantic note |
|---|---|
| `SetViewport(Viewport viewport)` | Sets viewport state for following draws. |
| `SetScissor(Rect rect)` | Sets scissor state for following draws. |
| `SetPipeline(PipelineId pipeline)` | Binds a render-compatible graphics or mesh pipeline. |
| `SetResources(uint index, SetId set, ReadOnlySpan<Offset> offsets = default)` | Binds a persistent resource set at a pipeline layout slot. |
| `SetResources(uint index, SetLayoutId layout, ReadOnlySpan<SetWrite> writes, ReadOnlySpan<Offset> offsets = default)` | Binds transient resources without allocating a persistent set. |
| `PushConstants(ShaderStages stages, uint offset, ReadOnlySpan<byte> data)` | Writes push/root constant bytes for visible shader stages. |
| `SetVertexBuffer(uint slot, BufferId buffer, ulong offset = 0)` | Binds one vertex stream. |
| `SetIndexBuffer(BufferId buffer, IndexKind kind, ulong offset = 0)` | Binds the index stream and element width. |
| `Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0)` | Emits a direct non-indexed draw. |
| `DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexBase = 0, uint firstInstance = 0)` | Emits a direct indexed draw. |
| `DrawIndirect(in DrawDesc desc)` | Emits indirect non-indexed draw work. |
| `DrawIndexedIndirect(in DrawIndexedDesc desc)` | Emits indirect indexed draw work. |
| `DispatchMesh(uint x, uint y, uint z)` | Emits direct mesh shader work. |
| `DispatchMeshIndirect(in DispatchDesc desc)` | Emits indirect mesh shader work. |
| `End()` | Closes the render pass. |

#### `IComputePass`

| Method | Semantic note |
|---|---|
| `SetPipeline(PipelineId pipeline)` | Binds a compute pipeline. |
| `SetResources(uint index, SetId set, ReadOnlySpan<Offset> offsets = default)` | Binds a persistent resource set at a pipeline layout slot. |
| `SetResources(uint index, SetLayoutId layout, ReadOnlySpan<SetWrite> writes, ReadOnlySpan<Offset> offsets = default)` | Binds transient resources without allocating a persistent set. |
| `Barrier(ReadOnlySpan<TextureBarrier> textures, ReadOnlySpan<BufferBarrier> buffers, ReadOnlySpan<AliasBarrier> aliases = default)` | Records compute-pass-local explicit transitions. |
| `PushConstants(ShaderStages stages, uint offset, ReadOnlySpan<byte> data)` | Writes compute-visible push/root constant bytes. |
| `Dispatch(uint x, uint y, uint z)` | Emits a direct compute dispatch. |
| `DispatchIndirect(in DispatchDesc desc)` | Emits indirect compute dispatch work. |
| `End()` | Closes the compute pass. |

#### `IRayPass`

| Method | Semantic note |
|---|---|
| `SetPipeline(PipelineId pipeline)` | Binds a ray-tracing pipeline. |
| `SetResources(uint index, SetId set, ReadOnlySpan<Offset> offsets = default)` | Binds a persistent ray resource set. |
| `SetResources(uint index, SetLayoutId layout, ReadOnlySpan<SetWrite> writes, ReadOnlySpan<Offset> offsets = default)` | Binds transient ray resources without allocating a persistent set. |
| `PushConstants(ShaderStages stages, uint offset, ReadOnlySpan<byte> data)` | Writes ray-stage push/root constant bytes. |
| `Trace(in ShaderTable table, uint width, uint height, uint depth)` | Emits ray dispatch work over explicit shader table regions. |
| `End()` | Closes the ray pass. |

#### `ISwapchain`

| Method | Semantic note |
|---|---|
| `Resize(uint width, uint height)` | Recreates swapchain images and invalidates old image/view ids. |
| `Present(in PresentDesc desc)` | Presents the current image with explicit sync and tearing policy. |
| `GetNative<T>()` | Returns a backend-native swapchain extension when available. |

#### `ID3D12DeviceInterop`

| Method | Semantic note |
|---|---|
| `ImportBuffer(ExternalBufferDesc desc)` | Imports an external D3D12 resource as an RHI buffer. |
| `ImportTexture(ExternalTextureDesc desc)` | Imports an external D3D12 resource as an RHI texture. |
| `GetNativeBuffer(BufferHandle buffer)` | Borrows the native D3D12 resource for a buffer. |
| `GetNativeTexture(TextureHandle texture)` | Borrows the native D3D12 resource for a texture. |
| `GetNativeAcceleration(AccelerationStructureHandle accelerationStructure)` | Borrows the native D3D12 resource for an acceleration structure. |

#### `ID3D12QueueInterop`

This is a native extension property carrier for the D3D12 command queue.

#### `ID3D12SwapchainInterop`

This is a native extension property carrier for the DXGI swapchain.

#### `MipGenerator`

| Method | Semantic note |
|---|---|
| `MipGenerator(IDevice device, MipGenDesc desc)` | Creates utility-owned compute resources from caller-supplied backend shader bytecode. |
| `Generate(IEncoder encoder, MipDesc desc)` | Records mip-generation work into an existing encoder. |
| `Dispose()` | Destroys utility-owned pipeline, shader, layout, and cached view ids. |

### Target Data Types

These are the target public data carriers and identities. Names follow the current public Handle model and keep backend-only native import descriptors outside the core API.

| Type group | Types |
|---|---|
| Identity handles | `BufferHandle`, `TextureHandle`, `TextureViewHandle`, `BufferViewHandle`, `SamplerHandle`, `ShaderModuleHandle`, `BindingLayoutHandle`, `PipelineLayoutHandle`, `BindingSetHandle`, `PipelineHandle`, `PipelineCacheHandle`, `AccelerationStructureHandle`, `CommandBufferHandle`, `FenceHandle`, `QueryPoolHandle`, `SwapchainHandle`, `MemoryHeapHandle` |
| Device and adapter data | `DeviceDesc`, `AdapterInfo`, `FeatureSet`, `LimitSet`, `QueueInfo`, `FormatInfo` |
| Resource data | `BufferDesc`, `TextureDesc`, `ViewDesc`, `SamplerDesc`, `HeapDesc`, `MemoryReqs`, `AllocInfo`, `BudgetInfo` |
| Resource set data | `SetLayoutDesc`, `SlotDesc`, `SetDesc`, `SetWrite`, `Offset`, `PushDesc`, `StaticSamplerDesc`, `PipelineLayoutDesc` |
| Shader and pipeline data | `ShaderDesc`, `RasterDesc`, `DepthDesc`, `BlendDesc`, `ColorBlend`, `VertexBufferDesc`, `VertexAttrDesc`, `SampleDesc`, `CacheDesc`, `ComputeDesc`, `GraphicsDesc`, `MeshDesc`, `RayDesc`, `RayGroupDesc` |
| Pass data | `RenderPassDesc`, `ColorTarget`, `DepthTarget`, `ComputePassDesc`, `RayPassDesc`, `Viewport`, `Rect`, `Color`, `ClearValue`, `DepthClear` |
| Copy and sync data | `BufferCopy`, `TextureUpload`, `TextureRead`, `TextureCopy`, `ResolveDesc`, `TextureBarrier`, `BufferBarrier`, `AliasTarget`, `AliasBarrier`, `QueueWait`, `QueueSignal` |
| Draw and dispatch data | `DrawArgs`, `DrawIndexedArgs`, `DispatchArgs`, `DrawDesc`, `DrawIndexedDesc`, `DispatchDesc` |
| Ray data | `AccelDesc`, `AccelGeomDesc`, `AccelBuildDesc`, `AccelSize`, `AccelCopy`, `ShaderRegion`, `ShaderTable` |
| Query and present data | `QueryDesc`, `SwapchainDesc`, `PresentDesc`, `Rational`, `HdrMeta` |
| Native data | `ExternalBufferDesc`, `ExternalTextureDesc` |
| Utility data | `MipGenDesc`, `MipDesc` |
| Enums and flags | `BackendKind`, `QueueKind`, `Format`, `TextureKind`, `ViewShape`, `MemoryKind`, `OwnerKind`, `HeapKind`, `HeapFlags`, `UsageFlags`, `AccessState`, `ViewUsage`, `ShaderStages`, `ShaderStage`, `ShaderFormat`, `SlotKind`, `SlotFlags`, `Topology`, `CullMode`, `FillMode`, `CompareOp`, `IndexKind`, `LoadOp`, `StoreOp`, `MapKind`, `FilterKind`, `MipFilter`, `AddressMode`, `BorderColor`, `InputRate`, `WriteMask`, `BlendFactor`, `BlendOp`, `FormatUsage`, `SampleFlags`, `QueryKind`, `ColorSpace`, `SwapMode`, `SetFlags`, `AccelKind`, `AccelFlags`, `AccelCopyKind`, `GeomKind`, `GeomFlags`, `RayGroupKind`, `ErrorCode` |

### Target Factory Methods On Data Types

| Type | Method | Semantic note |
|---|---|---|
| `ClearValue` | `Color(Format format, Color value)` | Creates a color optimized-clear payload. |
| `ClearValue` | `Depth(Format format, DepthClear value)` | Creates a depth/stencil optimized-clear payload. |
| `SetWrite` | `Clear(uint slot, uint element = 0)` | Clears one descriptor in a partially bound set. |
| `SetWrite` | `Buffer(uint slot, SlotKind kind, BufferViewHandle view, uint element = 0)` | Writes any buffer descriptor kind without relying on a narrow helper default. |
| `SetWrite` | `Texture(uint slot, SlotKind kind, TextureViewHandle view, uint element = 0)` | Writes any texture descriptor kind without relying on a narrow helper default. |
| `SetWrite` | `Sampler(uint slot, SamplerHandle sampler, uint element = 0)` | Writes one sampler descriptor. |
| `SetWrite` | `Acceleration(uint slot, AccelerationStructureHandle accelerationStructure, uint element = 0)` | Writes one ray acceleration-structure descriptor. |
| `AliasTarget` | `Buffer(BufferHandle buffer)` | Creates a buffer aliasing endpoint. |
| `AliasTarget` | `Texture(TextureHandle texture)` | Creates a texture aliasing endpoint. |
| `AliasBarrier` | `Between(AliasTarget before, AliasTarget after)` | Moves active ownership from one aliasing endpoint to another. |
| `AliasBarrier` | `Unknown(AliasTarget after)` | Activates a heap range whose previous contents are not represented by a live id. |
| `AliasBarrier` | `Release(AliasTarget before)` | Marks a heap range as no longer actively occupied by an id. |

### Target Rename Map

| Current | Target | Reason |
|---|---|---|
| `IBackendFactory` | `IBackend` | The public object is the backend adapter, not just a factory function. |
| `ICommandList` | `IEncoder` | The object records commands and only becomes submit work after `Finish`. |
| `CommandBufferHandle` | `CommandId` | The finished work identity should not share the recorder name. |
| `BindingLayout` | `SetLayout` | The concept is a descriptor set layout, not a vague binding bucket. |
| `BindingSet` | `Set` | Persistent shader resources are a set; `Binding` is a slot-level term. |
| `BindingResourceDesc` | `SetWrite` | The value writes one descriptor, it is not the resource itself. |
| `SetBindingSet` | `SetResources` | The pass call binds resources; whether they are persistent or transient is an overload detail. |
| `IRtPass` | `IRayPass` | `Ray` is readable at the pass/pipeline level; `Rt` remains only where native APIs require it. |
| `RtPipelineDesc` | `RayDesc` | The descriptor creates a ray pipeline, not a generic realtime object. |
| `AccelerationStructureHandle` | keep | The name is explicit, already follows the Handle identity model, and avoids short forms in public APIs. |
| `DrawIdxDesc` | `DrawIndexedDesc` | The public API should spell the command the same way the pass method does. |
| `BindFlags` | `UsageFlags` | The flags describe legal resource usage, not binding actions. |
| `ResourceState` | `AccessState` | The state is about access/layout expectations, not ownership or lifetime. |
| `ViewKind` | `ViewUsage` | The value describes how the view is used. |
| `TextureViewDimension` | `ViewShape` | The value describes dimensional shape, not usage. |
| `DeviceFeatures` | `FeatureSet` | Features are capability bits, not device behavior. |
| `DeviceLimits` | `LimitSet` | Limits are capability values, not device behavior. |
| `IMemoryDevice`, `ICacheDevice`, `IRtDevice`, `IQueryDevice` | `IDevice` methods | Capability splits are shallow here; feature bits and validation already decide support. |
