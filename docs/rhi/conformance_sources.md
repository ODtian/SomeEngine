# RHI Conformance Source Matrix

This document records the mature RHI test suites used as coverage input for `SomeEngine.Rhi` conformance tests.

The test implementation in `tests/SomeEngine.Rhi.Tests` is a port to the SomeEngine RHI contract, not a verbatim import of third-party C++/Rust test bodies. Third-party test projects use their own device/context abstractions, shader tooling, resource binding models, and build systems; copying them directly would not produce runnable .NET tests and would risk importing incompatible API assumptions such as Diligent SRB/name-variable behavior.

## Source Policy

- Keep third-party source paths and licenses documented.
- Do not copy large third-party test bodies into the main C# test project.
- Port test intent into backend-neutral RHI contract tests.
- Preserve explicit SomeEngine boundaries: no shader compilation/reflection in core RHI, no public descriptor allocator API, no auto-transition compatibility layer, no managed object wrapper resource identity.
- Prefer tests that exercise 99% RHI behavior: resource creation, binding layouts, descriptor arrays, copy/resolve rules, explicit barriers, memory placement, command-buffer lifecycle, debug markers, queries, fences, swapchain lifecycle, and validation failure ordering.

## Imported Coverage Sources

| Source | Local / Upstream Location | License To Preserve | Imported Coverage |
|---|---|---|---|
| DiligentCore API tests | `external/DiligentCore/Tests/DiligentCoreAPITest/src`; upstream: <https://github.com/DiligentGraphics/DiligentCore> | `external/DiligentCore/License.txt` | Buffer creation/access, copy texture, state transitions, pipeline resource signatures, render pass compatibility, queries/fences, debug groups, object creation failure. |
| DiligentCore engine/tool tests | `external/DiligentCore/Tests/DiligentCoreTest/src`; upstream: <https://github.com/DiligentGraphics/DiligentCore> | `external/DiligentCore/License.txt` | Format/type validation, texture view validation, graphics type invariants, utility edge cases. |
| NVIDIA NRI | upstream: <https://github.com/NVIDIA-RTX/NRI> | upstream project license | Explicit resource state, memory allocation, descriptor set/pipeline layout, queue synchronization, upload/readback patterns. |
| NVIDIA NVRHI | upstream: <https://github.com/NVIDIA-RTX/NVRHI> | upstream project license | Binding set/layout compatibility, command list lifecycle, validation paths, framebuffer/render target behavior, D3D12/Vulkan parity expectations. |
| wgpu / Dawn / WebGPU CTS-style validation | upstream: <https://github.com/gfx-rs/wgpu>, <https://github.com/google/dawn> | upstream project licenses | Copy footprint rules, view dimension/range validation, usage-state validation, shader-visible binding layout validation, error-on-invalid-command ordering. |
| The Forge | upstream: <https://github.com/ConfettiFX/The-Forge> | upstream project license | Explicit renderer API smoke paths, command-buffer/fence lifecycle, resource loader/update constraints, platform swapchain behavior. |
| bgfx | upstream: <https://github.com/bkaradzic/bgfx> | upstream project license | Cross-backend format/caps sanity, transient resource pressure patterns, simple graphics/compute smoke expectations. |

## Local Diligent Corpus Snapshot

The local Diligent checkout is much larger than the first imported slice. Current snapshot:

- `external/DiligentCore/Tests`: 566 C/C++/C#/header source files.
- `external/DiligentCore/Tests/DiligentCoreAPITest/src`: 101 API-test source/header files.
- `external/DiligentCore/Tests/DiligentCoreTest/src`: 48 engine/tool-test source/header files.
- `external/DiligentCore/Tests/IncludeTest`: 364 include-coverage files.
- Rough `TEST`, `TEST_F`, `TEST_P`, `TYPED_TEST`, `Fact`, `Theory`, and `NUnit` macro/name hits: 1452.

The 1452 count is a raw grep-style hit count, not a unique runnable-test count. It includes parameterized tests, helper macros, include-only checks, backend reference tests, archive/shader tooling tests, and tests for Diligent API concepts that SomeEngine RHI intentionally does not expose.

## Corpus Disposition

| Bucket | Diligent / mature-suite examples | SomeEngine RHI disposition |
|---|---|---|
| Resource creation and views | `BufferCreationTest`, `TextureCreationTest`, texture/buffer view validation, format/type tests | Port directly to xUnit descriptor/view validation matrices. |
| Memory and placement | NRI memory requirements/allocation tests, D3D12/Vulkan placed resource expectations | Port directly for committed/placed heaps, heap kind, alignment, overlap, aliasing, and allocation info. |
| Binding layout and resource sets | Diligent `PipelineResourceSignatureTest`, shader resource array tests, null-resource binding tests; NVRHI binding-set compatibility | Port to explicit set/binding/array-index tests. Do not import Diligent SRB/name-variable API. |
| Command lifecycle and state | Diligent state transition/copy tests, NVRHI command-list lifecycle, The Forge queue/fence patterns | Port to command-buffer one-shot, explicit barriers, copy/resolve/query/fence validation, and submit-time state preconditions. |
| Render pass and pipeline compatibility | Diligent render pass and graphics PSO validation, draw command validation | Port backend-neutral compatibility checks first; shader execution/triangle samples remain backend sample tests. |
| Swapchain lifecycle | Diligent platform tests, The Forge swapchain resize/present paths | Port Null lifecycle/ownership tests now; D3D12 visible-swapchain RHI validation runs through `tools/SomeEngine.Rhi.WindowTests` with Silk only as the HWND fixture, plus Slang-backed triangle draw, compute UAV/readback, and present/resize pressure scenarios. |
| Shader compiler/reflection/tooling | Diligent shader source factory/compiler/converter/archive tests, Slang/DXC reflection-related suites | Out of core RHI. The RHI accepts bytecode and explicit layouts only. Asset/tooling tests belong outside `SomeEngine.Rhi`. |
| Include/C API/object-refcount tests | Diligent include coverage, C API bridge, COM/refcount conventions | Not applicable to the C# handle API. Some header/include coverage can become compile-only API smoke if a separate package task needs it. |
| Advanced features | ray tracing, mesh shader, tessellation, sparse/tiled resources, pipeline cache blobs, native interop/import/export | Tessellation, pipeline cache, native D3D12 resource import, bindless/partially-bound descriptors, dynamic offsets, indirect count, MSAA resolve, generate-mips utility, mesh shader execution, ray tracing pipeline/AS paths, HDR/colorspace/fullscreen, timestamp/occlusion/pipeline-statistics queries, and descriptor/frame retirement soak are now covered by the RHI API/tests. Sparse/tiled resources and shared-handle export/import remain outside the current core RHI and must stay backend-extension work if added. |

## First Imported Test Slice

The first executable slice is `tests/SomeEngine.Rhi.Tests/MatureRhiConformanceTests.cs`.

It covers:

- Diligent-style buffer creation, initial data, mapping, and access validation.
- Diligent-style descriptor array and static sampler layout validation.
- NRI-style memory requirements, heap compatibility, placed allocation info, and heap lifetime validation.
- WebGPU/Dawn-style texture view dimension/range validation.
- WebGPU/Dawn-style copy footprint validation and source region observability.
- NVRHI/The Forge-style debug marker stack and one-shot command-buffer/fence validation.

Future slices should add:

- longer D3D12 visible-swapchain soak on top of `tools/SomeEngine.Rhi.WindowTests`
- D3D12 descriptor heap rollover pressure tests
- D3D12 timestamp frequency sanity under repeated submissions
- additional graphics triangle and compute UAV shader smoke variants extracted into reusable fixtures
- Vulkan onscreen presentation/HDR/fullscreen tests once the Vulkan backend exists
- longer descriptor/frame retirement soak beyond the headless unit-test duration
- Vulkan-specific queue-family ownership and dynamic-rendering compatibility tests once Vulkan backend work starts

## Corpus Matrix Expansion

The second executable slice is `tests/SomeEngine.Rhi.Tests/MatureRhiCorpusMatrixTests.cs`.

It adds matrix-style rows for the highest-frequency mature RHI validation categories:

- buffer descriptor, initial-data, mapping, and buffer-view rules
- texture descriptor, texture-view dimension/range, MSAA, cube/cube-array, and format-support rules
- sampler, binding layout, descriptor array, binding-set, static sampler, shader module, pipeline layout, and pipeline validation
- memory heap, placed resource, overlap, heap-kind, and aliasing-barrier validation
- render pass, command encoder, copy, resolve, query, swapchain, and submit-time state-precondition validation
- valid resource/view/binding/memory/command/swapchain shape smoke cases

The current executable suite also includes the advanced capability completion slice:

- descriptor arrays with mutable, partially-bound, and bindless binding sets
- keyed dynamic offsets across constant, storage, and raw-buffer bindings
- indirect draw/indexed draw/dispatch validation and D3D12 execution paths
- mesh/amplification, geometry, and tessellation pipeline validation and smoke coverage
- MSAA resolve validation for explicit resolves and render-pass resolves
- `SomeEngine.Rhi.Utilities.MipGenerator` Null recording and D3D12 compute/readback coverage for 2D and 2D-array textures
- pipeline cache object validation
- acceleration-structure build/copy size and alignment validation, plus D3D12 BLAS/TLAS trace-ray execution
- native D3D12 borrowed access/import tests through backend extension interfaces
- swapchain buffer-count, tearing, colorspace, HDR metadata, and fullscreen-mode validation
- descriptor/frame retirement soak through fence wait
- D3D12 split resource/dynamic-resource descriptor tables so dynamic offsets do not rewrite large persistent partially-bound/bindless tables
- transient binding validation rejects bindless layouts as well as partially-bound layouts

Current scoped executable RHI suite after this expansion:

- `dotnet test tests\SomeEngine.Rhi.Tests\SomeEngine.Rhi.Tests.csproj --no-restore --verbosity minimal -p:UseSharedCompilation=false`
- Passed: 351 / 351.

This still is not a claim that every upstream Diligent/NRI/NVRHI/wgpu/Dawn/The Forge test has been ported. It is a corpus-driven conformance matrix for the SomeEngine v0 API. The raw upstream backlog remains several hundred to over one thousand test intents after excluding out-of-bound tooling/API-model coverage.
