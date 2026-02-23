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
