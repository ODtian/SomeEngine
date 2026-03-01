# Development Log

## [2026-02-20] Slot-Based Binding Implementation
- Updated `ShaderResourceVariableDesc` to include `ResourceType` for D3D12 disambiguation.
- Added `GetShaderResourceRegisterClass` to group resource types into register classes (SRV, UAV, CBV, Sampler).
- Updated `PipelineResourceSignatureBase::FindResource` and `GetResourceAttribution` to support slot-based matching with `ResourceType` filtering.
- Implemented platform-specific logic:
    - **Vulkan/D3D12/WebGPU**: Match `Binding` and `Set` (and `ResourceType` for D3D12).
    - **D3D11/OpenGL**: Ignore `Set` (pass `~0u`) and match by `Binding` and `ResourceType` class.
- Updated `IShaderResourceBinding::GetVariableByBinding` to support the new matching logic.
- Updated `IPipelineState::GetStaticVariableByBinding` and `IPipelineResourceSignature::GetStaticVariableByBinding` for consistency.
- Updated `Archiver` module (`SerializedPipelineStateImpl` and `SerializedResourceSignatureImpl`) to match the new interface signatures.
- Updated `Mapping.xml` to provide C# default values for `ResourceType` parameters.
- Refactored `ShaderAsset` schema and importer to support generic per-backend reflection data instead of hardcoded fields.
- Updated C# `ShaderExtensions` and `TriangleRenderPass` to pass `ResourceType` during binding.
- Re-aligned `ShaderResourceVariableDesc` members to maintain 24-byte size and updated serialization/hashing logic.
- Updated all backend `ShaderVariableManager` implementations.

## [2026-02-20] Logging and PSO Ambiguity Fix
- Set `DebugMessageCallback` in `RenderContext.InitializeD3D12` to ensure Diligent logs are routed to C# console.
- Updated `FindPipelineResourceLayoutVariable` calls in D3D, WebGPU, and OpenGL backends to pass the explicit `ResourceType`. This fixes the "Ambiguous slot-based match" error when multiple resources of different types (e.g., CBV and SRV) share the same binding slot.
- Identified that `LOG_ERROR_MESSAGE` missing is due to Diligent's multi-module architecture on Windows; different DLLs (like `GraphicsEngine.dll`) may have their own `DebugMessageCallback` pointer which remains uninitialized unless `SetDebugMessageCallback` is called within that module.
## [2026-02-20] Instance Culling Implementation
- Updated `ClusterRenderPass.cs` to pass `InstanceCount` to culling shader and dispatch compute shader with Y-dimension corresponding to instance count.
- Updated `cluster_cull.slang` to support instance culling:
    - Added `InstanceCount` and `InstanceData` (StructuredBuffer) to shader resources.
    - Implemented logic to transform cluster bounds (Center, Radius) using instance transform matrix.
    - Updated `IsVisible` (Frustum Culling) and `IsLodSelected` to operate on world-space bounds.
    - Correctly populated `DrawRequest` with global instance ID for the draw pass.

## [2026-02-20] ImGui Debug UI Implementation
- Integrated `ImGui.NET` into `SomeEngine.Render` and `SomeEngine.Runtime`.
- Implemented `ImGuiRenderer` for Diligent (C#):
    - Handled font atlas texture creation and uploading.
    - Implemented PSO with alpha blending and dynamic vertex/index buffers.
    - Added support for Slang shaders (`imgui.slang`).
- Implemented `ImGuiInputHandler` using Silk.NET Input to handle mouse, keyboard, and scroll events.
- Added a debug UI in `SomeEngine.Runtime` providing:
    - Rendering toggles (Wireframe, Overdraw, Debug Spheres, Cluster ID).
    - Manual LOD selection slider.
    - Entity Inspector: View and edit `TransformQvvs` (Position, Scale) for all entities in the `GameWorld`.
    - "Add Entity" button for quick scene population.
## [2026-02-21] Compilation Fixes
- Fixed `ShaderAsset` reflection access in `SlangIntegrationTests.cs` (switched to `Reflections` array).
- Fixed `TestContext.WriteLine` analyzer warnings in several test files by switching to `TestContext.Out.WriteLine`.
- Resolved multiple nullability warnings (`CS8602`, `CS8600`, etc.) in `RenderContext.cs`, `SimpleMeshRenderPass.cs`, and `Program.cs`.
- Initialized `ParallelJob.Data` in `JobSystemTests.cs` to fix uninitialized field warning.
- Added explicit null checks and safe access for `MeshAsset.Payload` in `ClusterLodLevelTests.cs`.
- Fixed broken braces in `ClusterLodAutoCutTests.cs`.
## [2026-02-23] BVH Debug View Enhancements
- Added DebugBVHDepth to CullingUniforms and ClusterRenderPass to allow filtering BVH visualization by depth.
- Updated ExecuteBVH loop to track and pass CurrentDepth to the traversal shader.
- Enhanced cluster_bvh_traverse.slang to color-code BVH nodes based on culling status:
    - Green: Accepted/Traversed.
    - Blue: Culled by LOD.
- Enabled Alpha Blending and disabled Depth Write for BVH debug PSO to improve visibility of overlapping nodes.
- Added 'BVH Depth' slider to the ImGui debug panel.
- Fixed a crash caused by using UpdateBuffer on a Usage.Dynamic buffer for CullingUniforms.

## [2026-02-23] RenderGraph Refactoring & 3A Features
- Implemented **Lambda-based AddPass API**: Supports generic data passing between Setup and Execute phases, improving code modularity and clarity.
- Implemented **Topological Sort (Kahn's Algorithm)**: Automatically determines the correct execution order of render passes based on resource dependencies.
- Implemented **Dead Pass Stripping**: Automatically culls render passes that do not contribute to any output (imported resources or marked as output).
- Implemented **Automatic Resource State Barriers**: Automatically inserts `TransitionResourceStates` before each pass based on declared read/write requirements.
- Implemented **Transient Resource Allocation (Memory Aliasing)**: Introduced `RGResourcePool` to reuse physical textures and buffers between non-overlapping resource lifetimes, reducing VRAM footprint.
- Added comprehensive unit tests for RenderGraph features.
- Integrated `LambdaRenderPass` and updated `RenderGraph` to use a more robust compilation process.

## [2026-02-23] DiligentCore Review Fixes
- Fixed `GetResourceAttribution` slot matching: changed `Binding != ~0u && Set != ~0u` to `Binding != ~0u`, enabling D3D11/GL backends to use slot-based matching.
- Fixed placed resource lifetime: added `IDeviceMemory` reference holding (`AddRef`/`Release`) in D3D12 and Vulkan Buffer/Texture placed constructors/destructors.
- Removed incorrect hardcoded `m_MemoryProperties = MEMORY_PROPERTY_HOST_COHERENT` from D3D12 placed buffer constructor.
- Removed dead `InitSparseProperties()` call from Vulkan placed texture constructor.
- Added slot-based binding tests: ResourceType disambiguation, Set wildcard, GetStaticVariableByBinding, Binding=0/Set=0 distinction.- Refactored `GetResourceAttribution` multi-signature binding priority: changed from per-signature interleaved to two-pass global priority (Pass 1: slot-based across all signatures, Pass 2: name-based fallback).
- Renamed `MemoryRequirements.MemoryTypeIndex` to `MemoryTypeBits` to accurately reflect Vulkan bitmask semantics.
- Renamed `DEVICE_MEMORY_TYPE_DEFAULT` to `DEVICE_MEMORY_TYPE_PLACED` for clarity.
- Added null-pointer input validation (`DEV_CHECK_ERR`) to `CreatePlacedBuffer`/`CreatePlacedTexture` in D3D12 and Vulkan backends.

## [2026-02-24] Slang NoMangle HLSL Export
- Implemented `SlangNoMangleTests.cs` to demonstrate Slang compilation with the `NoMangle` option.
- Verified HLSL export via `GetEntryPointCode`, ensuring that entry point and resource names are preserved without standard Slang mangling.
- Enabled `AllowUnsafeBlocks` in `SomeEngine.Tests.csproj` to support `SlangShaderSharp`'s pointers.

## [2026-02-25] Dynamic BVH Patching and PageTable Removal
- Removed the implicit dependency on the `PageTable` Buffer across all Render pipeline phases (`ClusterCullPass`, `ClusterDrawPass`, `ClusterBVHTraversePass`).
- Designed a direct bit-packing scheme for `ClusterBVHNode` to store local page offsets directly in leaf nodes, removing the indirection gap.
- Added Compute Shader (`bvh_patch.slang`) based asynchronous patching using indirect CPU-tracking mappings in `ClusterResourceManager` upon mesh allocations.
- Re-architected multi-instance culling and BVH distribution: 
  - Adjusted traversal queued buffers (`_queueA`, `_queueB`) element stride from `uint` to `uint2` to pack `InstanceID`.
  - Upgraded Culling inputs in `cluster_cull.slang` and candidate representations from `uint2` to `uint3` (`pageOffset, clusterID, instanceID`).
  - Patched bounding spheres and transformations using corresponding `Instances` transformations inside the occlusion culling pass logic.
  - Linked `TransformSyncSystem` to correctly distribute world matrices arrays to BVH shaders iteratively covering all generated meshes.

## [2026-02-25] Instance Data Re-Architecture (Phase 1)
- Resolved the `roots[0]` hard-code issue in `ClusterBVHTraversePass.cs` causing identical mesh rendering across all instances.
- Introduced `GpuInstanceHeader` struct (16 bytes, holds `BVHRootIndex` and reserved `MaterialID`) in C# and Slang.
- Introduced `MeshInstance` ECS component.
- Refactored `TransformSyncSystem` into `InstanceSyncSystem` using robust multiple component query (`TransformQvvs` and `MeshInstance`). Concurrently uploads `GlobalTransformBuffer` and `GlobalInstanceHeaderBuffer`.
- Removed CPU-side queue initialization in traverse pass. Implemented `InitQueue` GPU compute kernel dispatching parallel root fetching per instance, eliminating host-side array allocations and buffer uploading overhead.
- Updated `ClusterCullPass`, `ClusterDrawPass`, `ClusterPipeline`, `TriangleRenderPass` and dependent test environments.

## [2026-02-28] Render Pass Fine-Grained Refactoring
- Refactored `HiZBuildPass` and `ClusterDebugPass` into multiple fine-grained passes to eliminate manual resource state transitions.
- Implemented `HiZMip0Pass` and `HiZDownsamplePass` for iterative HiZ pyramid construction.
- Implemented `ClusterDebugBVHPass`, `ClusterDebugSphereCopyPass`, and `ClusterDebugSphereDrawPass`.
- Updated `ClusterBVHTraversePass` to support granular setup and execute methods for different traversal stages.
- Moved `ClusterBVHReadbackPass` to the end of the BVH traversal sequence to correctly handle transient readback buffers.
- Replaced all occurrences of `ResourceStateTransitionMode.Transition` and `ResourceStateTransitionMode.None` with `Verify` in all Pass Execute methods, delegating all barrier management to the `RenderGraph`.
- Temporarily disabled HiZ logic in `ClusterPipeline` to address rendering issues (triangles missing).
- Fixed `ImGui Font Texture` and `SimpleMesh` buffer initialization states by adding explicit transitions in `Init` methods.
- Refactored `ClusterClearBuffersPass` and `ClusterBVHClearArgsPass` for discrete clear operations.
- Split BVH traversal loop into separate depth passes in `ClusterPipeline.AddToRenderGraph`.

## [2026-02-27] RenderGraph Compilation and History Resource Tracking
- Re-architected `RenderGraph` `Compile` and `Execute` phases to generate structured `_compiledPasses` and explicit execution order.
- Implemented topological sorting (Kahn's algorithm) ensuring deterministic execution via original index tie-breakers.
- Implemented **Dead Pass Stripping** by collecting sink resources (`MarkAsOutput`, `QueueTextureExtraction`) and analyzing backward producer reachability.
- Upgraded **Automatic Barrier System**: computes `PreBarriers` per pass and tracks dynamic `ResourceState`, automatically injecting `TransitionResourceStates`.
- Implemented safe extraction pipelines using `QueueTextureExtraction` and `QueueBufferExtraction` to establish definitive lifecycle ends and external ownership.
- Refactored `ClusterPipeline` HiZ history loop:
  - Registers `_prevHiZTexture` with `RegisterExternalTexture` when resolution and format validity passes (`IsHiZHistoryCompatible`).
  - Correctly configures extraction queue for `CurrHiZ` to safely promote history variables across frames.
  - Linked correct `_hasPrevHistory` uniform states and propagated it across cull components.

## [2026-02-26] Winding Order Fix
- Set `FrontCounterClockwise = true` in `RasterizerStateDesc` under `ClusterDrawPass.cs` and `SimpleMeshRenderPass.cs` to correctly handle standard CCW models like the monkey head.
- Reversed the index generation order in `PrimitiveMeshGenerator.CreateIcoSphere` so procedurally generated IcoSpheres conform to the CCW standard.

## [2026-02-26] Cluster BVH Buffer Capacity and Bounds Checking
- Fixed a major memory corruption issue (grid flickering) when rendering a high number of instances (~3600 monkey heads, exceeding former 100K cluster limits).
- Increased `_maxDraws` from 100K to 2.5M in `ClusterPipeline.cs` and updated Traverse queue buffers from 262K to 4M capacity in `ClusterBVHTraversePass.cs`.
- Introduced `MaxQueueNodes` and `MaxCandidates` limits in `CullingUniforms`.
- Added strict bounds checking across async compute kernel writes in `cluster_bvh_traverse.slang` and `cluster_cull.slang` guaranteeing memory safety during extreme clustering limits.

## [2026-02-28] RenderGraph Auto Barrier Fix
- Fixed an issue in `RenderGraph` where multiple reads/writes to the same resource in a pass would only track the last defined state. Combined required states using bitwise OR (e.g., `DepthRead | DepthWrite`) to properly support multiple usage scenarios.
- Fixed Diligent Engine debug assertion error by using `ResourceState.Unknown` instead of the tracked old state for `OldState` in `StateTransitionDesc` when automatically injecting `TransitionResourceStates` via `RenderGraph.Compile()`.
- Fixed a bug where UnorderedAccess (UAV) to UnorderedAccess transitions were missing. Updated `RenderGraph` to explicitly emit a barrier with `StateTransitionFlags.None` when `oldState == newState == ResourceState.UnorderedAccess` to ensure correct execution order between compute passes (e.g., `ClusterCullPass` to `ClusterDrawPass` args sync).
- Temporarily removed `SimpleMeshRenderPass` from `SomeEngine.Runtime/Program.cs` as requested.
