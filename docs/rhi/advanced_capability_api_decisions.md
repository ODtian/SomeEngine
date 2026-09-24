# RHI Advanced Capability API Decisions

This document freezes the public API direction for advanced-but-core RHI capabilities that are required by modern renderer and RDG work. It supersedes the old "Deferred Non-99% Features" framing in `api_spec.md`: some features may still be implemented in phases, but their API shape must be decided before deeper D3D12 renderer integration.

The design keeps these constraints:

- Core RHI stays backend-neutral.
- Public hot paths use handles, spans, value descriptors, and fail-fast validation.
- Native descriptor heaps, descriptor rings, Vulkan pools, Metal argument buffers, and retirement queues remain backend-private.
- RHI does not compile shaders and does not reflect shader bytecode.
- Shader tooling owns bytecode, entry points, interface metadata, and generated CPU-side writers.
- Unsupported features fail fast through `DeviceFeatures` and validation.

## Decision Basis

The choices in this document are not based on one backend preference. They are scored against these criteria, in this order:

1. **Renderer/RDG necessity**: features needed by GPU-driven rendering, frame graph resource binding, material tables, temporal/deferred rendering, or swapchain output are promoted even if implementation is phased.
2. **D3D12/Vulkan/Metal portability**: public API must map to D3D12 and Vulkan first, and must not make Metal impossible. Backend-specific escape hatches stay in backend extension interfaces.
3. **Hot-path cost**: draw/dispatch/bind/present paths must avoid strings, reflection, managed collection allocation, and hidden descriptor allocation in core API. Spans, handles, and value descriptors are preferred.
4. **C# call-site safety**: API should use named value descriptors when positional arguments would be fragile. This is why dynamic offsets use `(Binding, ArrayElement, Offset)` instead of positional `uint[]`.
5. **Fail-fast validation**: illegal states must be expressible in descriptors and rejected early. Feature-disabled usage returns `UnsupportedFeature`; malformed descriptors return `InvalidDescriptor`; lifecycle misuse returns `ValidationFailure`.
6. **Backend-private allocation policy**: descriptor heaps, descriptor pools, descriptor rings, command allocator pools, retirement queues, upload rings, and null descriptor allocation are implementation details.
7. **No shader-tooling leakage**: RHI accepts bytecode and explicit layout descriptors. It does not compile, reflect, infer names, or patch bindings. Shader tooling must generate bytecode matching RHI layout conventions.
8. **Stable ABI over minimal first version**: when a feature is clearly required, choose a shape that can grow without signature churn, even if the first backend implementation supports only a subset.
9. **Separation of core, utility, renderer, and backend extension**: low-level synchronization/copy/binding belongs in core; policy commands such as mip generation belong in utilities; scene/render scheduling belongs in RDG/renderer; native handles belong in backend extensions.
10. **Testability**: every capability needs a small RHI-level test surface and later an engine-scene test. Features that cannot be exercised without a scene still need explicit validation and fast failure paths.
11. **Modern high-performance path first**: mesh/bindless/indirect/dynamic offset decisions are optimized for modern renderer architecture. Legacy stages such as geometry/tessellation are supported as optional extensions, but they do not drive the primary API shape.
12. **No Diligent compatibility layer**: mature RHIs are used for coverage ideas and failure modes, not copied as compatibility APIs. Name-based SRB/resource-variable models stay outside core RHI.

Decision sources used:

- Current `SomeEngine.Rhi` public surface and D3D12 backend implementation.
- Local DiligentCore test structure, especially `Tests/GPUTestFramework`, which separates API/offscreen GPU tests from visible samples.
- The existing design baseline that already fixed descriptor-set style binding, explicit pipeline layouts, no reflection in RHI, and backend extension boundaries.
- Known native API constraints from D3D12 and Vulkan: D3D12 `ExecuteIndirect`, D3D12 root signatures/root constants, Vulkan descriptor sets/dynamic offsets/pipeline cache, Vulkan/D3D12 ray tracing acceleration structures and SBT-style dispatch, and flip-model swapchain behavior.

## Decision Summary

| Capability | API Decision | Implementation Priority |
|---|---|---|
| Indirect draw / indexed indirect / dispatch indirect | Replace single-offset methods with descriptor-based indirect calls; keep convenience overloads only as wrappers. Support multi-indirect and optional GPU count buffer. | P0 |
| Dynamic offsets | Use explicit `DynamicOffset` descriptors keyed by binding and array element, not Vulkan-style positional `uint` arrays in public API. | Implemented |
| Bindless / partially bound descriptors | Keep finite descriptor arrays in `BindingLayoutDesc`; add mutable binding sets and `UpdateBindingSet`; `Bindless` and `PartiallyBound` have separate semantics. | Implemented |
| MSAA resolve | Keep core `ResolveTexture` and render-pass resolve. Core resolve is full-subresource for D3D12 portability. | Implemented |
| Generate mips | Do not add to core `IDevice`/`ICommandEncoder`; add `SomeEngine.Rhi.Utilities.MipGenerator` built from core commands and caller-supplied backend bytecode. | Implemented utility |
| Pipeline cache blob | Add `PipelineCacheHandle` and cache object API; pipeline descriptors reference an optional cache. | Implemented |
| Mesh / amplification shaders | Add separate mesh pipeline descriptor and render-pass `DispatchMesh` commands. | Implemented |
| Geometry / tessellation shaders | Extend classic `GraphicsPipelineDesc` with optional geometry/hull/domain shaders and patch control points. | Implemented |
| Ray tracing | Add core optional handles and pass API for BLAS/TLAS build, RT pipeline, SBT regions, and trace dispatch. | Implemented |
| Native interop / import / export | Add backend-extension lookup; native resource access/import stays in backend extension interfaces, not core descriptors. | Implemented for D3D12 borrowed access/import |
| HDR / colorspace / fullscreen | Replace `Present(bool)` target API with `PresentDesc`; expand swapchain mode/colorspace/HDR metadata. | Implemented |
| Long-running descriptor/frame retirement soak | Not a public API feature; required test harness before renderer migration is considered stable. | Implemented headless soak |

## Decision Rationale Matrix

| Capability | Why This Shape Wins | Rejected Alternative |
|---|---|---|
| Indirect commands | Descriptor structs are the only shape that covers single indirect, multi indirect, and GPU count buffers without later signature churn. This is required for GPU-driven cluster/culling workflows. | Keeping only `(buffer, offset)` would force a breaking API change for multi-draw/count-buffer indirect. |
| Dynamic offsets | Binding-keyed offsets preserve C# readability and remain allocation-free through spans. They avoid hidden layout-order coupling at call sites. | Vulkan-style positional `uint[]` is fast but fragile and backend-shaped. |
| Bindless / partially bound | Finite descriptor arrays plus mutable binding sets keep descriptor allocation backend-private while supporting material/texture streaming. `Bindless` and `PartiallyBound` are separate because large non-uniform arrays and sparse population are different guarantees. | Exposing GPU descriptor handles leaks D3D12 heap policy; immutable-only bindless tables are not practical for streaming. |
| MSAA resolve | Keeping render-pass resolve and explicit command resolve covers both normal frame rendering and explicit resolve passes/tests. Full-subresource resolve is the portable baseline. | Render-pass-only resolve cannot cover explicit graph passes; partial resolve is not portable enough for the frozen core rule. |
| Generate mips | Mip generation is a policy operation implemented through shaders/blits. Utility placement keeps core low-level while still making it a first-class project feature. | Core `GenerateMips` would hide backend shader/barrier policy in the command encoder. |
| Pipeline cache | A cache handle maps to Vulkan pipeline cache and can also carry D3D12 cached PSO data. It keeps pipeline descriptors stable. | Blob fields on every pipeline descriptor are D3D12-biased and awkward for Vulkan. |
| Mesh/amplification | A separate mesh pipeline avoids invalid combinations with vertex input, index buffers, topology, and draw commands. | Folding mesh into classic graphics pipeline creates too many meaningless fields and validation holes. |
| Geometry/tessellation | These are optional classic graphics stages, so extending `GraphicsPipelineDesc` avoids duplicating render-state descriptors. | A separate tessellation/geometry pipeline descriptor would duplicate almost all graphics state. |
| Ray tracing | Core optional RT keeps renderer/RDG backend-neutral while still feature-gated. BLAS/TLAS/SBT are explicit because shader reflection is outside RHI. | Backend-extension-only RT would split renderer architecture by backend; D3D12-shaped raw API would hurt Vulkan portability. |
| Native interop/import/export | Backend extension lookup keeps native handles out of core descriptors while allowing explicit D3D12/Vulkan integrations. | Putting native pointers in `TextureDesc`/`BufferDesc` creates unclear ownership and backend leakage. |
| HDR/colorspace/fullscreen | `PresentDesc` and richer swapchain descriptors are needed for sync interval, tearing, HDR metadata, and mode policy. No compatibility wrapper is kept. | `Present(bool)` cannot express modern swapchain behavior without later API breakage. |
| Long-running soak | Descriptor/frame retirement bugs usually appear over thousands of frames, not in tiny functional tests. It is a mandatory validation lane, not a public API. | Relying on renderer scenes alone makes backend lifetime bugs harder to isolate. |

## 1. Indirect Commands

### Options

**Option A: Keep current methods only**

```csharp
void DrawIndirect(BufferHandle arguments, ulong offset);
void DrawIndexedIndirect(BufferHandle arguments, ulong offset);
void DispatchIndirect(BufferHandle arguments, ulong offset);
```

Pros:

- Minimal API.
- Matches the smallest Direct3D/WebGPU-style single indirect call.

Cons:

- No multi-draw.
- No GPU-generated count buffer.
- Not sufficient for GPU-driven rendering, culling, cluster draw compaction, or future RDG-driven batches.

**Option B: Add `count` and `stride` parameters**

```csharp
void DrawIndirect(BufferHandle arguments, ulong offset, uint count, uint strideInBytes);
```

Pros:

- Covers Vulkan-style multi indirect.
- Simple to implement on D3D12 with command signatures.

Cons:

- Still cannot use a GPU-generated count buffer.
- Method signatures grow unevenly across draw/indexed/dispatch.

**Option C: Use descriptor structs with optional count buffer**

```csharp
void DrawIndirect(in IndirectDrawDesc desc);
void DrawIndexedIndirect(in DrawIdxDesc desc);
void DispatchIndirect(in IndirectDispatchDesc desc);
```

Pros:

- Covers single indirect, multi indirect, and GPU-generated count.
- Maps to D3D12 `ExecuteIndirect`, Vulkan `vkCmd*Indirect` / `vkCmd*IndirectCount`, and Metal indirect command paths.
- Keeps future fields out of method-parameter churn.

Cons:

- Slightly more verbose than two-parameter calls.

### Decision

Use **Option C**. Keep two-parameter overloads only as convenience wrappers that build a descriptor with `Count = 1`.

### Final API Shape

```csharp
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
    public uint DrawCount { get; init; }
    public uint StrideInBytes { get; init; }
    public BufferHandle CountBuffer { get; init; }
    public ulong CountBufferOffset { get; init; }
}

public readonly record struct DrawIdxDesc
{
    public BufferHandle Arguments { get; init; }
    public ulong ArgumentOffset { get; init; }
    public uint DrawCount { get; init; }
    public uint StrideInBytes { get; init; }
    public BufferHandle CountBuffer { get; init; }
    public ulong CountBufferOffset { get; init; }
}

public readonly record struct IndirectDispatchDesc
{
    public BufferHandle Arguments { get; init; }
    public ulong ArgumentOffset { get; init; }
    public uint DispatchCount { get; init; }
    public uint StrideInBytes { get; init; }
    public BufferHandle CountBuffer { get; init; }
    public ulong CountBufferOffset { get; init; }
}
```

Pass APIs:

```csharp
public interface IRenderOps
{
    void DrawIndirect(in IndirectDrawDesc desc);
    void DrawIndexedIndirect(in DrawIdxDesc desc);
}

public interface IComputeOps
{
    void DispatchIndirect(in IndirectDispatchDesc desc);
}
```

Rules:

- `Arguments` must have `BindFlags.IndirectArgument`.
- `Arguments` must be in `ResourceState.IndirectArgument`.
- `DrawCount == 0` / `DispatchCount == 0` is invalid.
- `StrideInBytes` defaults to the tight command argument size for the command kind.
- D3D12/Null v0 require `StrideInBytes` to be exactly the tight command argument size. `0` is not a special runtime value and is rejected if explicitly supplied.
- `CountBuffer` is optional. If valid, `DrawCount` / `DispatchCount` is the maximum command count.
- `CountBuffer` must have `BindFlags.IndirectArgument`.
- `CountBuffer` must be in `ResourceState.IndirectArgument`.
- `CountBufferOffset` must be 4-byte aligned.
- Multi-draw indirect requires `DeviceFeatures.MultiDrawIndirect`; multi-dispatch indirect requires `DeviceFeatures.MultiDispatchIndirect`.
- Count-buffer indirect requires `DeviceFeatures.IndirectCount`.

D3D12 v0 implements only tight command signatures, so it currently rejects padded strides and requires `StrideInBytes == IndirectArgumentSize.*`. A later backend can cache command signatures per stride and accept padded strides only after the public contract is deliberately changed.

`DeviceFeatures` adds:

```csharp
public bool IndirectCount { get; init; }
public bool MultiDrawIndirect { get; init; }
public bool MultiDispatchIndirect { get; init; }
```

`DeviceLimits` adds:

```csharp
public uint MaxIndirectDrawCount { get; init; }
public uint MaxIndirectDispatchCount { get; init; }
```

## 2. Dynamic Offsets

### Options

**Option A: Vulkan-style positional `ReadOnlySpan<uint>`**

```csharp
void SetBindingSet(uint setIndex, BindingSetHandle set, ReadOnlySpan<uint> dynamicOffsets);
```

Pros:

- Fast.
- Mirrors Vulkan.
- Already present in the current provisional interface.

Cons:

- Public API is brittle: the caller must know hidden layout ordering.
- Easy to bind offsets to the wrong binding after a layout change.
- Feels like leaking Vulkan mechanics into the engine API.

**Option B: Put offset directly in `BindingResourceDesc`**

Pros:

- Clear for transient bindings.

Cons:

- Does not work cleanly for persistent binding sets.
- Forces per-draw descriptor packets for a common constant-buffer path.

**Option C: Explicit binding-keyed offset descriptors**

```csharp
public readonly record struct DynamicOffset(uint Binding, uint ArrayElement, uint OffsetInBytes);
```

Pros:

- Still allocation-free through `ReadOnlySpan<DynamicOffset>`.
- Safer and more C#-native than positional arrays.
- Works for persistent and transient binding APIs.

Cons:

- Backend must map binding keys to dynamic slots.
- Slightly more validation work.

### Decision

Use **Option C**. Replace public `ReadOnlySpan<uint>` dynamic offset parameters with `ReadOnlySpan<DynamicOffset>`.

### Final API Shape

```csharp
public readonly record struct DynamicOffset
{
    public uint Binding { get; init; }
    public uint ArrayElement { get; init; }
    public uint OffsetInBytes { get; init; }
}

public interface IRenderOps
{
    void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
}

public interface IComputeOps
{
    void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
}
```

Rules:

- Dynamic offsets apply only to buffer descriptor slots.
- Initial supported slot types:
  - `ConstantBuffer`
  - `StorageBufferRead`
  - `StorageBufferReadWrite`
  - `RawBufferRead`
  - `RawBufferReadWrite`
- Dynamic offsets do not apply to textures, samplers, acceleration structures, or static samplers.
- Dynamic offset is added to the bound `BufferViewDesc.Offset`.
- The resulting range must stay inside the buffer view.
- Constant-buffer dynamic offsets must respect `MinConstantBufferOffsetAlignment`.
- Storage-buffer dynamic offsets must respect `MinStorageBufferOffsetAlignment`.
- A dynamic offset entry is required for every descriptor element in a slot flagged `DynamicOffset`.
- Duplicate `(Binding, ArrayElement)` offsets are invalid.
- `DynamicOffset` cannot be combined with `Bindless` or `PartiallyBound`. Dynamic buffers are small, fully populated ranges; sparse descriptor arrays belong in persistent binding sets without dynamic offset rewriting.

## 3. Bindless And Partially Bound Descriptors

### Options

**Option A: Bindless is just a giant immutable binding set**

Pros:

- No update API.
- Simple validation.

Cons:

- Not useful for renderer material/texture streaming.
- Requires rebuilding binding sets for descriptor changes.

**Option B: Expose native descriptor indices or heaps**

Pros:

- Maximum D3D12 performance control.

Cons:

- Breaks backend abstraction.
- Does not map cleanly to Vulkan/Metal.
- Violates the existing descriptor-allocation boundary.

**Option C: Finite descriptor arrays plus explicit mutable binding-set updates**

Pros:

- Backend-neutral.
- Keeps descriptor allocation backend-private.
- Works for material bindless tables and RDG-generated tables.
- Lets validation enforce lifetime and in-flight update rules.

Cons:

- Requires binding set usage/lifetime tracking.

### Decision

Use **Option C**.

`BindingSlotDesc.Count` remains a finite declared descriptor capacity. `BindingFlags.Bindless` means the shader may non-uniformly index a large descriptor array. `BindingFlags.PartiallyBound` means not every element must be populated. They are related but separate semantics.

### Final API Shape

```csharp
[Flags]
public enum BindingSetFlags
{
    None = 0,
    Mutable = 1 << 0,
}

public sealed record BindingSetDesc
{
    public string Name { get; init; } = string.Empty;
    public BindingSetFlags Flags { get; init; }
    public IReadOnlyList<BindingResourceDesc> Resources { get; init; } = Array.Empty<BindingResourceDesc>();
}

public interface IDevice
{
    BindingSetHandle CreateBindingSet(BindingLayoutHandle layout, BindingSetDesc desc);
    BindingSetHandle CreateBindingSet(BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, string name = "");
    void UpdateBindingSet(BindingSetHandle bindingSet, ReadOnlySpan<BindingResourceDesc> resources);
}
```

Rules:

- Non-partially-bound slots require every descriptor element at create time.
- `PartiallyBound` slots may omit elements.
- Omitted partially-bound elements are backed by backend null descriptors where supported.
- Reading an omitted descriptor is not a portable way to produce a value; shader code should guard indices.
- `Bindless` slots still have finite `Count`.
- `Bindless` does not imply `PartiallyBound`; sparse bindless tables should set both flags.
- Partially-bound texture slots, non-raw storage-buffer slots, and sampler slots must declare `BindingSlotDesc.Shape` so D3D12/Vulkan/Metal can create a native null descriptor with the shader-visible descriptor shape. Texture slots declare `TextureDimension` plus concrete non-depth `Format`; structured storage-buffer slots declare `StrideInBytes`; typed storage-buffer slots declare `Format`; sampler slots declare `Sampler`, including `Sampler.Compare` for comparison samplers.
- `Mutable` binding sets can be updated only when not referenced by any live command buffer or submitted in-flight work.
- Updating an in-use binding set fails fast.
- `UpdateBindingSet` only updates specified elements; unspecified elements keep their previous descriptor/null state.
- `BindingResourceDesc.Clear(binding, arrayElement)` is the only public way to clear an existing descriptor element. It is valid only for `UpdateBindingSet` on `PartiallyBound` slots and is rejected for strict slots, bindless-only slots, `CreateBindingSet`, and transient `SetBindings`.
- Transient `SetBindings` remains the RDG path and does not create public binding-set objects.
- Transient `SetBindings` rejects `PartiallyBound` and `Bindless` slots; partially bound and bindless arrays must use persistent `BindingSet` objects so null descriptor materialization, sparse updates, and large descriptor tables are not repeated on the per-draw/per-dispatch hot path.
- D3D12 root signatures split resource descriptors into persistent resource tables and dynamic-resource tables per set. Calling `SetBindingSet` with dynamic offsets rewrites only the dynamic-resource table, so a large partially-bound/bindless texture table in the same set stays persistent.

`DeviceLimits` adds:

```csharp
public uint MaxBindlessResourceDescriptors { get; init; }
public uint MaxBindlessSamplerDescriptors { get; init; }
```

## 4. MSAA Resolve

### Options

**Option A: Render-pass resolve only**

Pros:

- Common and ergonomic for color render targets.

Cons:

- Not enough for explicit resolve passes, texture workflows, or tests.

**Option B: Command resolve only**

Pros:

- Simple command model.

Cons:

- Less ergonomic for normal MSAA render-pass usage.

**Option C: Keep both render-pass resolve target and explicit command resolve**

Pros:

- Matches renderer needs.
- Already present in current API.

Cons:

- Needs strict compatibility validation.

### Decision

Use **Option C**.

Core resolve stays full-subresource for D3D12 portability. Partial resolve remains unsupported until a backend matrix proves it is needed.

Rules:

- Source must be multisampled.
- Destination must be single-sampled.
- Source/destination format must match.
- Source/destination extent must match for the target subresource.
- Source must be in `ResolveSource`.
- Destination must be in `ResolveDestination`.
- Render-pass resolve transitions internal native state as needed but must preserve RHI-visible state semantics.

## 5. Generate Mips

### Options

**Option A: Add `ICommandEncoder.GenerateMips(...)` to core**

Pros:

- Very convenient.

Cons:

- D3D12/Vulkan do not have a single universal native generate-mips command.
- Requires hidden backend shaders/pipelines or blit policies.
- Pulls utility policy into low-level RHI.

**Option B: Renderer/RDG utility built on core commands**

Pros:

- Keeps core low-level.
- Can choose compute or render implementation per format/backend.
- Can own barrier policy explicitly.

Cons:

- Not a one-call core command.

### Decision

Use **Option B**. Generate mips is important and must be implemented early, but it belongs in `SomeEngine.Rhi.Utilities`, not `IDevice` or `ICommandEncoder`.

### Final API Shape

```csharp
namespace SomeEngine.Rhi.Utilities;

public sealed record MipGeneratorDesc
{
    public ShaderModuleDesc Texture2DShader { get; init; }
    public ShaderModuleDesc Texture2DArrayShader { get; init; }
    public uint ThreadGroupSizeX { get; init; } = 8;
    public uint ThreadGroupSizeY { get; init; } = 8;
    public uint ThreadGroupSizeZ { get; init; } = 1;
}

public sealed record GenerateMipsDesc
{
    public TextureHandle Texture { get; init; }
    public uint FirstSlice { get; init; }
    public uint SliceCount { get; init; } = uint.MaxValue;
    public uint MostDetailedMip { get; init; }
    public uint MipCount { get; init; } = uint.MaxValue;
    public ResourceState? InitialState { get; init; }
    public IReadOnlyList<ResourceState> InitialStates { get; init; } = Array.Empty<ResourceState>();
    public ResourceState FinalState { get; init; } = ResourceState.ShaderResource;
}

public sealed class MipGenerator : IDisposable
{
    public MipGenerator(IDevice device, MipGeneratorDesc desc);
    public void GenerateMips(ICommandEncoder encoder, GenerateMipsDesc desc);
}
```

Rules:

- Utility may insert required barriers.
- The current utility requires `ShaderResource` and `UnorderedAccess` bind flags.
- Unsupported formats fail fast.
- Core RHI still exposes only the primitive commands required to implement this.
- RHI still does not own shader compilation or reflection. `MipGeneratorDesc` receives backend bytecode matching a documented utility shader ABI: binding `t0, space0` is the source mip, binding `u1, space0` is the destination mip, and the shader stage is compute.
- The implemented utility uses compute UAV writes. 2D textures require `Texture2DShader`. 2D array textures require `Texture2DArrayShader`.
- Cube, cube-array, and 3D texture mip generation are not exposed by this utility yet because their shader ABI and view contract are different; they fail fast with `UnsupportedFeature` instead of sharing the 2D-array path implicitly.
- `InitialState == null` and empty `InitialStates` means the utility seeds its local barriers from `IDevice.GetTextureState`.
- `InitialState` supplies one prior state for all selected subresources. `InitialStates` supplies per-subresource prior states in mip-major, then slice-major order. They are mutually exclusive.
- Set `InitialState` or `InitialStates` when the caller has already changed selected subresources earlier in the same command encoder.

## 6. Pipeline Cache Blob

### Options

**Option A: Put byte blobs directly on every pipeline descriptor**

Pros:

- Simple for D3D12 cached PSO blobs.

Cons:

- Does not model Vulkan pipeline cache well.
- Bloats pipeline descriptors.

**Option B: Device-level pipeline cache object**

Pros:

- Maps to Vulkan pipeline cache, D3D12 cached blobs, and future Metal binary archives.
- Keeps pipeline descriptors small.
- Lets asset/runtime code serialize cache data explicitly.

Cons:

- Adds a handle type.

### Decision

Use **Option B**.

### Final API Shape

```csharp
public readonly record struct PipelineCacheHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly PipelineCacheHandle Invalid = default;
}

public sealed record PipelineCacheDesc
{
    public string Name { get; init; } = string.Empty;
    public ReadOnlyMemory<byte> InitialData { get; init; }
}

public interface IDevice
{
    PipelineCacheHandle CreatePipelineCache(PipelineCacheDesc desc);
    byte[] GetPipelineData(PipelineCacheHandle cache);
    void Destroy(PipelineCacheHandle cache);
    void DestroyPipelineCache(PipelineCacheHandle cache);
}
```

Pipeline descriptors add:

```csharp
public PipelineCacheHandle PipelineCache { get; init; }
```

Rules:

- Invalid cache handle means no cache.
- Invalid/stale cache data must not be silently accepted as a fake cache artifact. A backend either implements a real native cache path or fails `CreatePipelineCache` with `UnsupportedFeature`.
- `GetPipelineData` is not a hot path; returning `byte[]` is acceptable.

Implementation status:

- Null implements a strict validation cache object.
- D3D12 implements native pipeline libraries, validates stale/random cache bytes at creation, and stores/loads compute and graphics PSOs with deterministic cache keys.

## 7. Mesh And Amplification Shaders

### Options

**Option A: Fold mesh shaders into `GraphicsPipelineDesc`**

Pros:

- One graphics pipeline descriptor.

Cons:

- Many invalid field combinations.
- Vertex input and primitive topology become meaningless for mesh pipelines.

**Option B: Add a separate mesh pipeline descriptor**

Pros:

- Clear legal state.
- Better validation.
- Maps directly to D3D12 mesh shader pipeline state and Vulkan mesh/task shader pipeline state.

Cons:

- Adds one create method and render-pass dispatch commands.

### Decision

Use **Option B**.

### Final API Shape

Enums extend:

```csharp
public enum ShaderStage
{
    Vertex,
    Hull,
    Domain,
    Geometry,
    Pixel,
    Compute,
    Amplification,
    Mesh,
    RayGeneration,
    AnyHit,
    ClosestHit,
    Miss,
    Intersection,
    Callable,
}
```

`ShaderStageFlags` gains matching flags.

Mesh pipeline:

```csharp
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

public interface IDevice
{
    PipelineHandle CreateMeshPipeline(MeshPipelineDesc desc);
}

public interface IRenderOps
{
    void DispatchMesh(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1);
    void DispatchMeshIndirect(in IndirectDispatchDesc desc);
}
```

Rules:

- `MeshShader` is required.
- `AmplificationShader` is optional.
- `PixelShader` is optional. Pipelines with color attachments normally provide it; depth-only or side-effect-only mesh workloads may omit it if the backend accepts the shader state.
- Mesh pipelines do not use vertex buffers, vertex attributes, index buffers, primitive topology, or draw commands.
- Mesh pipelines still declare non-patch primitive topology for rasterization compatibility. `PatchList` is invalid.
- Calling draw commands with a mesh pipeline is invalid.
- Calling `DispatchMesh*` with a classic graphics pipeline is invalid.

Implementation status:

- The public API shape is frozen.
- Null implements strict mesh-pipeline and mesh-dispatch validation.
- D3D12 creates real mesh/amplification pipeline-state streams, supports direct and indirect mesh dispatch, and stores/loads mesh PSOs through `ID3D12PipelineLibrary1` when a pipeline cache is supplied. Hardware without mesh-shader support reports `DeviceFeatures.MeshShader == false` and fails fast.

## 8. Geometry And Tessellation Shaders

### Options

**Option A: Separate tessellation/geometry pipeline descriptor**

Pros:

- Very explicit.

Cons:

- Duplicates almost all classic graphics pipeline state.

**Option B: Extend classic `GraphicsPipelineDesc` with optional stages**

Pros:

- Matches how D3D12/Vulkan treat these as optional stages in a graphics pipeline.
- Keeps existing draw path.

Cons:

- Requires validation for legal stage combinations.

### Decision

Use **Option B**.

### Final API Shape

`GraphicsPipelineDesc` adds:

```csharp
public ShaderModuleHandle HullShader { get; init; }
public ShaderModuleHandle DomainShader { get; init; }
public ShaderModuleHandle GeometryShader { get; init; }
public uint PatchControlPoints { get; init; }
public PipelineCacheHandle PipelineCache { get; init; }
```

`PrimitiveTopology` adds:

```csharp
PatchList,
```

Rules:

- Hull/domain shaders must appear together.
- Tessellation requires `Topology == PrimitiveTopology.PatchList`.
- `PatchControlPoints` must be in backend-supported range when tessellation is used.
- Geometry shader is optional and independent.
- These stages are feature-gated.

Implementation status:

- Null accepts geometry and tessellation pipeline descriptors for validation coverage.
- D3D12 creates real geometry, hull, and domain shader PSOs and maps patch topology to D3D12 patch primitive topology during draw recording.

`DeviceFeatures` adds:

```csharp
public bool GeometryShader { get; init; }
public bool TessellationShader { get; init; }
```

`DeviceLimits` adds:

```csharp
public uint MaxPatchControlPoints { get; init; }
```

## 9. Ray Tracing

### Options

**Option A: Backend extension only**

Pros:

- Avoids large core API surface.

Cons:

- Renderer/RDG cannot remain backend-neutral for ray tracing.
- Binding model already has `AccelerationStructure`; leaving RT entirely outside core creates a split.

**Option B: D3D12-like raw API in core**

Pros:

- Direct mapping to D3D12.

Cons:

- Leaks D3D12 concepts too strongly.
- Harder to map cleanly to Vulkan.

**Option C: Core optional RT model with backend-neutral BLAS/TLAS, RT pipeline, SBT regions, and trace pass**

Pros:

- Keeps renderer/RDG backend-neutral.
- Still low-level enough for high performance.
- Maps to D3D12 and Vulkan ray tracing.

Cons:

- Adds several descriptors and handles.

### Decision

Use **Option C**.

### Final API Shape

Handle:

```csharp
public readonly record struct AccelerationStructureHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0 && Generation != 0;
    public static readonly AccelerationStructureHandle Invalid = default;
}
```

Binding resource extends:

```csharp
public readonly record struct BindingResourceDesc
{
    public AccelerationStructureHandle AccelerationStructure { get; init; }

    public static BindingResourceDesc AccelerationStructureBinding(uint binding, AccelerationStructureHandle accelerationStructure, uint arrayElement = 0);
}
```

Acceleration structure API:

```csharp
public enum AccelerationStructureKind
{
    BottomLevel,
    TopLevel,
}

[Flags]
public enum AccelBuildFlags
{
    None = 0,
    PreferFastTrace = 1 << 0,
    PreferFastBuild = 1 << 1,
    AllowUpdate = 1 << 2,
    AllowCompaction = 1 << 3,
}

public sealed record AccelerationStructureDesc
{
    public string Name { get; init; } = string.Empty;
    public AccelerationStructureKind Kind { get; init; }
    public ulong SizeInBytes { get; init; }
}

public readonly record struct AccelBuildSizes
{
    public ulong AccelerationStructureSizeInBytes { get; init; }
    public ulong BuildScratchSizeInBytes { get; init; }
    public ulong UpdateScratchSizeInBytes { get; init; }
}

public interface IDevice
{
    AccelBuildSizes GetAccelSizes(in AccelBuildDesc desc);
    AccelerationStructureHandle CreateAccelerationStructure(AccelerationStructureDesc desc);
    void Destroy(AccelerationStructureHandle handle);
    void DestroyAccelerationStructure(AccelerationStructureHandle handle);
}

public interface ICommandEncoder
{
    void BuildAccelerationStructure(in AccelBuildDesc desc);
    void CopyAccelerationStructure(AccelerationStructureHandle source, AccelerationStructureHandle destination, AccelCopyMode mode);
}
```

Geometry and instance descriptor details are intentionally in `AccelBuildDesc`, not in shader reflection metadata. That descriptor must be fully explicit and use buffer handles plus offsets for vertex, index, transform, AABB, and instance data.

Ray tracing pipeline and pass:

```csharp
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

public sealed record RtGroupDesc
{
    public string Name { get; init; } = string.Empty;
    public ShaderModuleHandle GeneralShader { get; init; }
    public ShaderModuleHandle ClosestHitShader { get; init; }
    public ShaderModuleHandle AnyHitShader { get; init; }
    public ShaderModuleHandle IntersectionShader { get; init; }
}

public readonly record struct ShaderTableRegion
{
    public BufferHandle Buffer { get; init; }
    public ulong Offset { get; init; }
    public ulong SizeInBytes { get; init; }
    public ulong StrideInBytes { get; init; }
}

public readonly record struct ShaderTableDesc
{
    public ShaderTableRegion RayGeneration { get; init; }
    public ShaderTableRegion Miss { get; init; }
    public ShaderTableRegion HitGroup { get; init; }
    public ShaderTableRegion Callable { get; init; }
}

public interface IDevice
{
    PipelineHandle CreateRtPipeline(RtPipelineDesc desc);
    int GetRtSize(PipelineHandle pipeline);
    void GetRtId(PipelineHandle pipeline, string shaderGroupName, Span<byte> destination);
}

public interface ICommandEncoder
{
    IRtOps BeginRtPass(RtPassDesc desc);
}

public interface IRtOps
{
    void SetPipeline(PipelineHandle pipeline);
    void SetBindingSet(uint setIndex, BindingSetHandle bindingSet, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetBindings(uint setIndex, BindingLayoutHandle layout, ReadOnlySpan<BindingResourceDesc> resources, ReadOnlySpan<DynamicOffset> dynamicOffsets = default);
    void SetPushConstants(ShaderStageFlags stages, uint offset, ReadOnlySpan<byte> data);
    void TraceRays(in ShaderTableDesc shaderBindingTable, uint width, uint height, uint depth = 1);
    void End();
}
```

Rules:

- Ray tracing is feature-gated by `DeviceFeatures.RayTracing`.
- AS build scratch buffers must have `UnorderedAccess`.
- `AccelBuildSizes.AccelerationStructureSizeInBytes`, `BuildScratchSizeInBytes`, and `UpdateScratchSizeInBytes` are aligned to `DeviceLimits.AccelerationStructureAlignment`.
- `AccelerationStructureDesc.SizeInBytes` must be aligned to `DeviceLimits.AccelerationStructureAlignment`.
- `AccelBuildDesc.ScratchOffset` must be aligned to `DeviceLimits.AccelerationStructureAlignment`.
- The destination AS size must be at least `AccelBuildSizes.AccelerationStructureSizeInBytes` for the submitted build descriptor.
- AS result buffers/resources are represented by `AccelerationStructureHandle`, not generic `BufferHandle`.
- SBT storage is a normal `BufferHandle`; shader identifier writing belongs to tooling/utility code.
- Strings are allowed in RT pipeline creation and shader identifier lookup because they are not hot-path draw/dispatch calls.

Implementation status:

- The public API shape is frozen.
- Null implements strict validation for acceleration-structure descriptors/builds/copies, ray tracing pipelines, shader identifiers, SBT regions, and trace dispatch.
- D3D12 implements BLAS/TLAS prebuild-size queries, acceleration-structure resources and AS SRV descriptors, build/copy commands, DXR state-object creation, shader identifier retrieval, and `DispatchRays` through a ray tracing pass. Hardware without DXR reports `DeviceFeatures.RayTracing == false` and fails fast.
- D3D12 `ID3D12PipelineLibrary` PSO caches do not support DXR state objects. `RtPipelineDesc.PipelineCache` is therefore rejected on D3D12 with `UnsupportedFeature`; leaving it invalid is the supported path. This is a native D3D12 cache-model constraint, not a silent no-op.

## 10. Native Interop, Import, And Export

### Options

**Option A: Add native pointers to core descriptors**

Pros:

- Easy to wire quickly.

Cons:

- Pollutes backend-neutral API.
- Creates unclear ownership/lifetime rules.

**Option B: Backend extension interfaces**

Pros:

- Keeps core clean.
- Lets each backend expose correct native concepts.
- Matches previous design baseline.

Cons:

- Requires callers to opt into backend-specific code.

### Decision

Use **Option B**.

### Final API Shape

Core:

```csharp
public interface IBackendExtensible
{
    T? GetBackendInterface<T>() where T : class;
}

public interface IDevice : IDisposable, IBackendExtensible
{
}

public interface IQueue : IBackendExtensible
{
}

public interface ISwapchain : IBackendExtensible
{
}
```

D3D12 backend assembly may expose:

```csharp
public interface ID3D12DeviceInterop
{
    ID3D12Device NativeDevice { get; }
    IDXGIFactory6 Factory { get; }
    IDXGIAdapter1 Adapter { get; }
    BufferHandle ImportBuffer(ExternalBufferDesc desc);
    TextureHandle ImportTexture(ExternalTextureDesc desc);
    ID3D12Resource GetNativeBuffer(BufferHandle buffer);
    ID3D12Resource GetNativeTexture(TextureHandle texture);
    ID3D12Resource GetNativeAcceleration(AccelerationStructureHandle accelerationStructure);
}

public interface ID3D12QueueInterop
{
    ID3D12CommandQueue NativeQueue { get; }
}

public interface ID3D12SwapchainInterop
{
    IDXGISwapChain3 NativeSwapchain { get; }
}
```

Rules:

- Native pointers are borrowed; their lifetime is tied to the RHI object unless an import/export descriptor says otherwise.
- Imported resources must explicitly state ownership, initial state, bind flags, format/size, and queue ownership when applicable.
- Shared-handle export/import is backend extension API, not core API.
- Core `ResourceOwnership.External` is used internally/for allocation info; callers do not smuggle external pointers through `TextureDesc` or `BufferDesc`.

Implementation status:

- D3D12 exposes native device/queue/swapchain/resource access, borrowed native buffer/texture/acceleration-structure resources, and explicit external `ID3D12Resource` import for buffers and textures.
- Shared handle export/import, cross-adapter resources, and explicit residency policy remain separate backend-extension work.

## 11. HDR, Colorspace, And Fullscreen

### Options

**Option A: Keep `Present(bool vsync)` and current `ColorSpace`**

Pros:

- Minimal.

Cons:

- Cannot express sync interval, tearing policy per present, HDR metadata, fullscreen mode, or scRGB.

**Option B: Add richer swapchain and present descriptors**

Pros:

- Covers modern flip-model swapchains.
- Keeps future HDR/fullscreen work from breaking API.

Cons:

- More fields before every backend implements all modes.

### Decision

Use **Option B**. Unsupported swapchain modes/color spaces fail fast.

### Final API Shape

```csharp
public enum ColorSpace
{
    Sdr,
    ScRgbLinear,
    Hdr10,
}

public enum SwapchainMode
{
    Windowed,
    BorderlessFullscreen,
    ExclusiveFullscreen,
}

public readonly record struct Rational
{
    public uint Numerator { get; init; }
    public uint Denominator { get; init; }
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
    public Hdr10Metadata? Hdr10Metadata { get; init; }
    public SwapchainMode Mode { get; init; } = SwapchainMode.Windowed;
    public Rational RefreshRate { get; init; }
}

public readonly record struct PresentDesc
{
    public uint SyncInterval { get; init; }
    public bool AllowTearing { get; init; }
}

public interface ISwapchain
{
    void Present(in PresentDesc desc);
}
```

Rules:

- There is no `Present(bool)` compatibility wrapper.
- `SyncInterval == 0` means immediate present.
- Tearing is only allowed when `SyncInterval == 0`, swapchain `AllowTearing` is true, and backend support exists.
- `PresentDesc.AllowTearing` defaults to false, so `new PresentDesc { SyncInterval = 1 }` is the valid common vsync case.
- HDR10 requires `ColorSpace.Hdr10` and valid `Hdr10Metadata`.
- `ExclusiveFullscreen` may fail fast on platforms/backends that do not support it.
- Borderless fullscreen is a windowing concern plus swapchain resize; backend does not fake exclusive semantics.

Implementation status:

- D3D12 configures DXGI color spaces for SDR, scRGB, and HDR10, applies HDR10 metadata through `IDXGISwapChain4`, supports borderless fullscreen as windowed flip-model presentation, and attempts exclusive fullscreen through DXGI when requested.
- Null intentionally accepts only SDR windowed swapchains as a validation backend.

## 12. Command Buffer Retirement

Command encoders and command buffers remain one-shot. Successful `Finish()` creates a command buffer, submit accepts it once, and a submitted command buffer cannot be submitted again. If `Finish()` fails because an encoder is terminally invalid, for example due to an open pass or unbalanced debug marker, the encoder is aborted and backend active references/transient storage are released without creating a command buffer.

Backend storage retirement is not exposed to callers. `DestroyCommandBuffer` invalidates the public handle. If the command buffer was never submitted, the backend can immediately recycle/free its command allocator, command list, and transient descriptor allocations. If it was submitted and is still in flight, the backend keeps an internal pending-retirement record and releases storage only after the queue fence proves completion. Resource/view/sampler/pipeline destroy checks still see those pending records as live until retired.

This keeps command allocator pools and descriptor retirement queues backend-private while allowing renderer/RDG code to destroy command buffer handles promptly without manually mirroring backend fence state.

## 13. Long-Running Descriptor And Frame Retirement Soak

This is not a public API capability, but it is required before renderer migration is considered robust.

Required soak tests:

- 5k-50k frames of transient `SetBindings`.
- Persistent mutable bindless table updates while previous frames are in flight.
- Descriptor heap rollover and reuse.
- Command allocator/list retirement under graphics, compute, and copy queues.
- Swapchain resize during in-flight frames.
- Placed resource aliasing reuse across frames.
- Upload/readback ring pressure once utilities exist.
- `WaitIdle + Dispose` leaves no live backend objects.

These tests should not all run in the default fast xUnit suite. The suite should have:

- fast D3D12 coverage tests
- explicit soak category
- optional debug-layer/GPU-validation run
- visible sample/scene run

## 14. Testing Strategy

Mature RHI pattern from local Diligent:

- API tests use a testing swapchain / offscreen render target with readback snapshots.
- Platform/window creation exists in the GPU test environment.
- Real tutorials/samples validate visible window behavior.
- Engine scenes validate cross-system integration.

SomeEngine should follow the same separation:

1. Keep fast headless RHI tests for API contract and backend behavior.
2. Add a D3D12 visible-window sample for present/resize/manual inspection.
3. Add test scenes after renderer integration:
   - clear/resize
   - triangle
   - textured quad with generated mips
   - depth + MSAA + resolve
   - compute-generated indirect args + indirect draw
   - bindless material table
   - descriptor/frame retirement soak
   - HDR output when display support exists

Engine integration is broader than RHI tests, but it must not replace low-level RHI tests. The RHI suite catches backend contract bugs earlier and with smaller repros; engine scenes catch cross-system mistakes.
