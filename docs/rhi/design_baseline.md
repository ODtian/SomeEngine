# RHI Design Baseline

> Status: planning baseline. This document records agreed RHI design conclusions before any public API freeze.

## Scope

- Build a standalone `SomeEngine.Rhi` module.
- Do not migrate the existing Diligent rendering path yet.
- Do not change `SomeEngine.Render`, `SomeEngine.Runtime`, or `SomeEngine.Editor` behavior as part of the RHI planning baseline.
- Treat the API as unfrozen until the freeze criteria below are met.

## Corpus

Only recently maintained, high-performance-oriented projects are valid primary references.

### Primary References

| Project | Role |
|---|---|
| [NRI](https://github.com/NVIDIA-RTX/NRI) | Main low-level explicit RHI reference. |
| [NVRHI](https://github.com/NVIDIA-RTX/NVRHI) | Reference for state tracking, binding, delayed destruction, and multi-queue support. |
| [The Forge](https://github.com/ConfettiFX/The-Forge) | Reference for production cross-platform command, shader, and backend design. |
| [O3DE Atom RHI](https://github.com/o3de/o3de) | Reference for engine-scale resource pools, frame scheduling, and backend boundaries. |
| [Diligent Engine](https://github.com/DiligentGraphics/DiligentEngine) | Current baseline and cautionary reference for convenience abstractions leaking into engine code. |

### Auxiliary References

| Project | Role |
|---|---|
| [Dawn](https://github.com/google/dawn) | Validation, surface, bind group, and feature/limit reference. |
| [wgpu](https://github.com/gfx-rs/wgpu) | Validation, bind group, feature/limit, and portability reference. |
| [Filament](https://github.com/google/filament) | Driver/backend split and command stream reference. |
| Godot `RenderingDevice` | Engine-internal rendering device boundary reference. |
| SDL3 GPU | Shader format flags, surface, and modern common-denominator reference. |

### Non-Primary References

- bgfx, sokol_gfx, LLGL, MethaneKit, and orhi may be observed for specific ideas, but they do not drive the API shape.
- gfx-hal/gfx-rs/gfx, Rafx, Veldrid, teaching projects, and small experiments are excluded from the freeze basis.

## Architecture Conclusions

- The core model is an explicit low-level RHI.
- The core direction follows NRI and The Forge more than Diligent or WebGPU.
- SomeEngine must not become a WebGPU clone.
- SomeEngine must not become a bgfx/sokol-style high-level submission library.
- SomeEngine must not copy O3DE's full frame scheduler, because SomeEngine already has a RenderGraph.
- SomeEngine must not put Diligent/NVRHI-style full automatic state management in core.
- RenderGraph stays above RHI and remains responsible for:
  - pass DAG construction
  - barrier derivation
  - transient resource lifetime
  - history and extraction
- RHI is responsible for:
  - instance, adapter, device, queue
  - surface and swapchain
  - resource, memory, view
  - shader module, pipeline layout, pipeline
  - descriptor layout and binding set
  - command encoder, render pass encoder, compute pass encoder
  - explicit barrier primitives
  - queue submit, timeline fence, synchronization
  - upload, readback, and copy primitives
  - validation, debug, and native interop

## API Shape Conclusions

- Public API should prefer lightweight handles over object interfaces.
- Handles need generation/version validation.
- `default` handles represent invalid/null.
- The API should avoid a noisy `Rhi` prefix inside the `SomeEngine.Rhi` namespace. Public interfaces keep the normal C# `I` prefix (`IDevice`, `IQueue`); descriptors, handles, enums, and value types stay short (`BufferHandle`, `TextureDesc`, `ResourceState`).
- Core hot paths must not use strings for resource binding.
- Debug and tooling names are metadata only.

## Resource Conclusions

- Core first implementation may use committed resources.
- The API must leave room for memory requirements, heap allocation, and placed resource extensions.
- Resources must distinguish ownership:
  - committed: created and destroyed by RHI
  - placed: bound to RHI-managed heap memory; heap owner controls lifetime
  - external: imported native resource; RHI does not destroy the native object
  - swapchain: owned by swapchain; not destroyed through normal resource destroy
  - transient: owned by the caller such as RenderGraph, with caller-defined release strategy
- Views are independent handles.
- A texture is not implicitly an SRV, RTV, DSV, or UAV.
- View creation must validate format, mip range, array slice range, bind flags, and resource ownership.

## Binding And Pipeline Layout Conclusions

- Binding uses a descriptor set / bind group hybrid model.
- Pipeline layout is an explicit first-class object.
- Binding hot paths use set, binding, slot, and handles.
- String binding names are allowed only for debug and tooling.
- Binding sets are immutable after creation unless an explicit update API is designed.
- Descriptor arrays are required.
- Bindless, partially bound arrays, and dynamic offsets are feature-gated capabilities.
- Backend pass encoders share one descriptor binding owner per backend. Persistent binding sets, transient resources, dynamic offsets, descriptor allocation, root table binding, tracking, and rebind cache rules must not be duplicated across render, compute, and ray tracing pass encoders.
- Pipeline layout must explicitly contain:
  - binding layouts
  - push/root constants
  - static samplers
  - shader stage visibility
  - compatibility hash or cache key
- RHI does not perform shader reflection. Pipeline creation validates only RHI-owned descriptors and handles; shader bytecode/layout compatibility is delegated to the native backend and validation layer.

## Shader Conclusions

- RHI does not compile shaders.
- RHI receives backend-native bytecode:
  - D3D12: DXIL
  - Vulkan: SPIR-V
  - Metal: metallib or equivalent backend-ready payload
- Shader modules do not carry reflection metadata.
- Shader interface metadata belongs to shader tooling or the asset pipeline when needed for editor validation, authoring, or generated CPU-side writers.

## Command Conclusions

- Commands use `ICommandEncoder`, `IRenderOps`, and `IComputeOps`.
- This is not a WebGPU clone: queue submit, barriers, and synchronization remain explicit.
- Render passes must explicitly express:
  - color attachments
  - depth/stencil attachment
  - load op
  - store op
  - clear value
  - resolve target
  - render area
  - depth/stencil read/write semantics
- Command encoder and command buffer are one-shot by default.
- Reusable command buffers are a future feature-gated capability.
- Queue submit and render pass begin are hot paths and must not require managed descriptor/list allocation. Public entry points use spans or stack-only descriptors for command buffers, waits/signals, and render pass attachments.
- Null command recording must not grow command operation storage through managed array resize on the command path. The backend may rent/cache operation storage internally, but ownership must transfer to the command buffer and be released on command buffer destruction or device disposal.
- Null recording and submit state tracking must avoid quadratic behavior when state maps spill beyond their small inline capacity. The hot path uses the shared Core internal `InlineFlatDictionaryCore<TKey, TValue>` storage with `[InlineArray(8)]` small storage and a shared pooled open-addressed spill core. Public standard collection wrappers are reference types: `InlineFlatDictionary<TKey, TValue>` uses the inline front end, and `FlatDictionary<TKey, TValue>` uses the same open-addressed core directly.

## Barrier And Sync Conclusions

- Core exposes explicit barrier primitives only.
- Automatic state tracking may exist as a helper or debug layer, but not as core behavior.
- Texture barriers must support subresource ranges.
- Buffer barriers may be whole-buffer in the first core API, with range support left open.
- Sync uses timeline fences and queue submit waits/signals.
- Queues are explicit:
  - graphics
  - compute
  - copy
- Missing queue support must fail fast; no silent fallback.
- Queue family count and capabilities are queried before queue retrieval.

## Upload, Readback, And Copy Conclusions

- Core exposes low-level map and copy primitives.
- Upload rings, staging allocators, and convenience `UpdateTexture`/`UpdateBuffer` helpers live outside core.
- Copy primitives must express:
  - buffer-to-buffer
  - buffer-to-texture
  - texture-to-buffer
  - texture-to-texture
  - row pitch
  - slice pitch
  - mip level
  - array slice/layer
  - required alignment
- Null texture data backing must stay sparse. A small copied region in a large texture must not allocate full texture or full subresource pixel storage just to preserve copy/readback observability. Repeated covered writes must prune stale covered segments so retained backing and readback work follow current sparse contents rather than full write history.

## Feature And Format Conclusions

- Feature and limit queries are mandatory, not optional.
- Required query domains include:
  - format support
  - sample count support
  - typed UAV support
  - bindless limits
  - descriptor limits
  - max color attachments
  - timestamp support
  - queue family support
- Unsupported capabilities must fail fast with a clear error.

## Coordinate And Convention Conclusions

The RHI spec must fix these conventions before API freeze:

- clip-space depth range
- viewport Y direction
- texture origin
- front-face winding
- NDC convention
- half-pixel convention, if any

## Error And Validation Conclusions

- Invalid API usage fails fast.
- Debug validation provides detailed diagnostics.
- Release builds retain checks for unrecoverable errors such as invalid handles, unsupported features, and device loss.
- Error categories must distinguish:
  - invalid descriptor
  - invalid handle
  - unsupported feature
  - backend failure
  - device lost
  - validation failure
- Null backend is a strict validation backend, not a no-op success backend.

## Native Interop Conclusions

- Native interop uses backend extension interfaces.
- Native handles must not pollute core interfaces.
- Native pointer lifetime is bound to the corresponding RHI object lifetime.
- External resource import must explicitly state ownership and initial state.

## Freeze Criteria

The public API cannot be frozen until all of these exist:

- corpus comparison document
- SomeEngine RHI requirements document
- API spec
- compile-only usage tests
- strict Null backend validation tests
- D3D12 vertical slice

The D3D12 vertical slice must cover:

- swapchain clear and present
- graphics pipeline triangle
- compute UAV write
- buffer upload
- texture upload
- readback validation
- resize
- timestamp query
- debug marker

Null backend alone is not sufficient to freeze the API.

## Highest-Risk Domains

These domains must be designed first because freezing them incorrectly will force breaking API changes:

1. Binding / PipelineLayout
2. Shader bytecode and tooling boundary
3. ICommandEncoder / RenderPass / Barrier
