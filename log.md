# Development Log

## [2026-03-15] Cluster Draw Meta Handle 修复
- 保持现有新架构与 Draw metadata 设计不变，为 `ClusterDrawConfig` 增加 `VisibleClusterMeta` 显式传递入口，避免 `ClusterDrawStage` 私有 zero-buffer 截断后续 phase/deform/shade 依赖的 draw meta 协议。
- `ClusterPipeline` 现显式把 `traverse.ZeroOffsetBuffer` 传给 Phase1 / Phase2 / Transparent Draw stage，回退路径仍保留本地 0 偏移缓冲，避免 draw request 膨胀。
- 清理 `ClusterRender` 路径中残留的 `ResourceStateTransitionMode.Transition`，统一改为 `Verify`。
- 新增 `ClusterPipelineTypesTests` 覆盖 `VisibleClusterMeta` 配置传递语义。

## [2026-03-03] HiZ Culling Pipeline Debug Mode Implementation
- Replaced `bool UseHiZ` with `HiZDebugMode` enum (`Legacy`, `Phase1Only`, `Phase1OnlyPassAll`, `Phase1ThenHiZ`, `Full2Phase`) to isolate and test the 2-phase culling behavior.
- Updated `ClusterPipeline.cs` to dynamically inject render passes based on the selected debug mode.
- Created robust bindings between legacy variables (`_useHiZ`) and standard evaluation to facilitate graceful testing degradation.
- Extended `ClusterDebugReadbackPass` inside `ClusterGraphPasses.cs` (augmented buffer sizes up to 80 bytes) to fetch rendering counts dynamically specifically targeting `HPhase2IndirectDrawArgs`.
- Implemented ImGui Enum switch UI interface (`HiZ Mode`) within `SomeEngine.Runtime/Program.cs` providing real-time `Phase2 Draw Vertex Count` and `Phase2 Draw Instance Count` metrics reflection.

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
- [2026-03-02] 完成 RenderGraph Refactoring Phase 1 & 2，将 ClusterPipeline 的瞬态 Uniform 改造为 RenderGraph 资源并新增 UploadUniformsPass；拆解了 ClusterResourceManager 的 BVH Patch 逻辑，移入 RenderGraph 的 ClusterBVHPatchPass 中执行。
- [2026-03-02] 完成 RenderGraph Refactoring Phase 3 & 4，创建 InstanceDataManager 彻底隔离 ECS 与渲染图后端的资源依赖，且全面引入 RGPooledBuffer 进行全局资源（如 PageHeap、GlobalBVHBuffer）的历史状态自动化跨帧闭环。

## [2026-03-10] RenderGraph 重构续作（4 阶段）
- **阶段 0**: 所有 Pass 从 `RenderPass` 基类迁移到 `IRenderGraphPass` 接口，`Execute` 签名统一为 `Execute(RenderGraphContext)`。API 重命名：`Reset`→`BeginFrame`, `ImportTexture`→`Import`, `WriteTexture`→`Write`, `ReadTexture`→`Read`, 移除 `MarkAsOutput`。
- **阶段 1**: `RenderGraphTests.cs` API 对齐修复。
- **阶段 2**: `RenderGraph.cs` 添加 frame-count-based deferred release queue（3 帧延迟），防止 GPU 仍在使用的资源被立即 Dispose。
- **阶段 3**: GPU 资源全面迁移到 RG：`ClusterResourceManager` 3 个 buffer 改为 desc-only + `graph.CreateBuffer`；`RenderContext` depth buffer 改为 desc-only + `graph.CreateTexture`；所有 DSV 引用更新。
- **阶段 4**: `ClusterUploadInstanceDataPass` 和 `ClusterResourceManager.ExecutePendingUploads` 的 `unsafe`/`fixed` 代码全部替换为 Span API `UpdateBuffer<T>`。

## [2026-03-10] RenderGraph Per-Subresource Barrier & Lifetime Aliasing
- 新增 `SubResourceRange` 结构（mip + array slice 范围），扩展 `RenderGraphBuilder.Read/Write/ReadWrite` 支持 per-mip/slice 声明。
- 重写 `BuildAutomaticBarriersAndTrackedStates`：状态追踪粒度从 whole-resource 改为 `(resourceId, mip, slice)`，支持 per-subresource barrier 生成与合并优化。
- `CompiledBarrier` 和 `Execute` barrier 发射使用 `FirstMipLevel/MipLevelCount/FirstArraySlice/ArraySliceCount`。
- `HiZBuildPass.SetupMip0` 声明写 mip 0，`SetupDownsample` 声明读 mip N-1 写 mip N。
- 修复 `RGMemoryHeap.TryAllocate` 从保守模式改为 lifetime-aware aliasing：只把 lifetime 重叠的 allocation 加入冲突集，启用 placed resource 内存复用。

## [2026-03-11] Debug Freeze Culling Camera
- 在 `ClusterRenderFeature` 中新增 `FreezeCullingCamera` 属性，勾选后将剔除相机（CullingUniforms + BVH Traverse）锁定为冻结时刻的快照，渲染相机（DrawUniforms）仍跟随自由相机。
- 在 Runtime ImGui 面板的 Rendering 分组下新增 "Freeze Culling Camera" Checkbox。

## [2026-03-11] Debug Overdraw View and Readback Buffer Fixes
- Fixed an issue where the Overdraw view failed to reflect HiZ culling and flickered. Restored `DepthEnable = true` and `DepthWriteEnable = true` through a new `Cluster Draw Depth Only PSO` pre-pass, ensuring the depth buffer is correctly populated for the next frame's HiZ pyramid generation. Then, an additive `Cluster Draw Overdraw PSO` pass with `DepthEnable = false` is run to stably accumulate the overdraw color of all submitted fragments, independent of draw order.
- Fixed the `ClusterDebugReadbackPass` returning all zeros (readback failure). Moved the `MapBuffer` (with `MapFlags.DoNotWait`) invocation to *before* the `CopyBuffer` operations. This correctly utilizes the RenderGraph's asynchronous execution pattern to map the staging buffer from the previous frame's copy, instead of incorrectly mapping immediately after issuing a new copy command.
- Marked `HDebugReadbackBuffer` as an output in `ClusterRenderFeature` to ensure the RenderGraph does not strip or optimize away the readback pass.

## [2026-03-11] HiZ Culling Accuracy Fix
- Fixed an issue in `cluster_cull.slang` where totally occluded objects were not being culled, which was especially noticeable when the camera is stationary.
- Changed the Mip Level calculation for `SampleHiZMax` from `floor(log2(pixelDiameter))` to `ceil(log2(pixelDiameter))`. This ensures the sampled bounding box footprint spans at most 2x2 texels at the selected mip level, preventing "holes" during the 4-corner maximum depth sampling lookup that caused false visibility results.
- Changed `IsOccludedByHiZ` to use `cluster.Center`/`cluster.Radius` (render bounds) instead of `cluster.LODCenter`/`cluster.LODRadius` (LOD hierarchy bounds) for tighter occlusion testing.

## [2026-03-12] HiZ Debug Frame Dump
- Added `ClusterDebugDumper.cs` — one-shot GPU data dump triggered by F5. Exports HiZ mip chain and depth buffer as binary R32_Float files with JSON metadata (ViewProj, camera, HiZ params).
- Added `tools/analyze_hiz.py` — Python analysis script (numpy/matplotlib) to load and visualize the dumped data.
- Improved HiZ occlusion rate: Phase 1 now tests against previous frame's **complete** depth buffer (Phase 1+2) instead of Phase 1-only depth. Added second HiZ build pass after Phase 2 draw, reusing same ping-pong `HiZ_A`/`HiZ_B` textures (written twice per frame).
- Fixed `nearDepth` calculation: replaced linearized `centerDepth - depthRadius` with direct projection of the sphere's nearest world-space point to clip space, fixing gross overestimation under perspective.
- Improved Culling Stats display: now shows per-stage breakdown (BVH Output → LOD Rejected → Phase1 HiZ Cull / Drawn → Phase2 HiZ Cull / Drawn → Total Drawn).
- Fixed HiZ false-visibility near silhouettes: replaced the derivative-based screen bounds approximation (`ComputeNdcDelta`) with exact 8-corner AABB projection. Under strong perspective, the linear derivative overestimated the footprint, causing clusters to sample the `1.0` background sky at high mip levels and fail culling.

## [2026-03-12] Exact Sphere Projection for HiZ Culling
- Replaced the 8-corner AABB projection in `cluster_cull.slang`'s `BuildScreenBoundsAndNearDepth` with mathematical exact bounding sphere projection, eliminating AABB-induced whitespace padding and minimizing projected footprint bounds.
- Added `View`, `P00`, and `P11` matrix parameters to `CullingUniforms` in C# and slang to feed view-space coordinates directly to the exact projection formula.

## [2026-03-12] View 内存泄漏修复
- `CachedTexture`/`CachedBuffer` 实现 `IDisposable`，增加 `Views` 字典缓存所有通过 `CreateView` 创建的视图，Dispose 时统一释放。
- 删除 `GetPhysicalTextureMipView` 和 `GetMipView`，新增通用 `GetOrCreateTextureView`/`GetOrCreateView`，按 `viewDesc.Name` 在 `CachedTexture.Views` 中查找或创建。
- `HiZBuildPass` 改用 `GetOrCreateView` 构造 `TextureViewDesc`。
- `ClusterDebugAABBPass` 改用 `GetDefaultView` 替代每帧 `CreateView`。
- RenderGraph 的资源替换、空闲淘汰和 Dispose 统一走 `CachedTexture.Dispose()`。
- `ClusterRenderFeature.Dispose` 补充遗漏的 `_debugAABBPass?.Dispose()`。
- `ClusterBVHTraversePass.Dispose` 补充遗漏的 `_clearArgsSRB_A/B` 和 `_clearArgsPSO` 释放。
- `ImGuiRenderer` 中 `CreateDefaultShaderSourceStreamFactory` 改为 `using var` 确保释放。

## [2026-03-12] 深入排查与修复 HiZ Culling 剔除精度漏洞
- 修复 `hiz_build.slang` 中 `DownsampleMip` 的边缘奇数截断问题：通过在渲染最后一行/列时增加 `+ 1` texel 采样跨度，完美保证奇数源维度下降采样时的全覆盖，杜绝屏幕右下边缘深度的丢失（解决边缘 False Occlusion），且避免对齐 2 的幂次带来的 4096 极高内存开销。
- 解决 `cluster_cull.slang` 投影跨越 Texel 引发背景露缝、集群规律低频闪烁的问题（Center Flashing Bug）：
  - 移除了包围盒 Mip 计算公式中的 `- 1.0` 以达成严格的 `pixelDiameter <= divisor` 数学条件。通过确保包围盒**最多只横跨 2 个格子**，使得 4 角 (`SampleHiZMax`) 能够毫无死角地闭环包围所有空间缝隙。
  - 将 `mipSize` `float` 乘法带来的非对齐映射舍弃，更换为等价于 GPU 内部硬件 `floor(pxMin / divisor)`（`>> L` 整数截断）的逻辑，解决 texel 采样的边界滑动问题。 
- 重构并清理调试阶段添加的高内存开销功能，移除硬编码 `Pad4` 并在 C# 构建了基于 `DumpHiZData` 标志位的条件资源分配流程（正常态分配 `16 byte` Buffer）。将 F5 Dump 数据脚本作为 Python 分析工程 (`analyze_bounds.py`) 保留于 `tools/`。

## [2026-03-13] Visibility Buffer + Compute Resolve 管线
- 在 `cluster_draw.slang` 中新增 `VSVisBuffer` / `PSVisBuffer` 入口点，输出 `R32_UINT` 编码 `(VisibleClusterIndex << 7) | TriangleID`。
- 新建 `cluster_resolve.slang`：全屏 Compute Shader (`CSResolve`, 8×8 线程组)，读取 VisBuffer 反查 PageHeap 重建三角形，计算面法线，支持 ClusterID/LOD/Normal 三种 Debug 可视化。
- 在 `ClusterDrawPass.cs` 中新增 VisBuffer PSO（`R32_UINT` RT + `D32_Float` DS），通过 `_useVisBuffer` 标志在 VisBuffer 和 Forward 路径间切换。
- 新建 `ClusterResolvePass.cs`：Compute Pass 读取 VisBuffer + VisibleClusters + PageHeap，写入 ColorTarget UAV。
- `ClusterRenderFeature.cs`：`DrawUniforms` 增加 `ScreenWidth`/`ScreenHeight`；新增 `UseVisBuffer` 属性（默认 true）；创建 VisBuffer 纹理和 Clear pass；在 Legacy 和 2-Phase 分支末尾均挂载 Resolve pass。

## [2026-03-13] 修复 LOD 选择闪烁 Bug
- 修复 `ClusterBuilder.cs` 中 BVH 叶节点 `LODError`（float）与 GPUCluster `LODError`（f16）精度不一致：量化重构将 cluster 的 LODError 从 float 压缩为 f16，但 BVH 节点仍用 float 值。当 f16 四舍五入略大于 float 原值时，LOD 切换边界会出现父级被拒绝同时子级被剔除的间隙（一帧闪烁）。修复：`LODError = (float)(Half)m.ParentError` 确保 BVH 与 cluster 使用相同 f16 精度。
- 需要重建 .mesh 资产使改动生效。

## [2026-03-13] 合并 Phase 1/Phase 2 Visible Clusters
- `ClusterRenderFeature.cs`：Phase 2 cull/draw 共享 `hVisibleClusters` 和 `hIndirectDrawArgs`，删除独立的 `Phase2VisibleClusters`/`Phase2IndirectDrawArgs` 资源。Phase 2 draw 重画全部 N1+N2 instances（early-Z 拒绝 Phase 1 重叠），Resolve Pass 自动看到所有 cluster。零 shader 改动。

## [2026-03-14] SoA 顶点属性打包重构
- 将 `ClusterBuilder.cs` 中顶点属性打包方式从 AoS（interleaved）改为 SoA（per-stream 顺序排列）。
- `VertexAttributeDescriptor.Offset` 重命名为 `StreamIndex`，反映 SoA 语义。
- Page Header 结构不变，`AttributesOffset` 指向首个 stream 起始。
- 材质 shader 可通过 `attrBase + sum(precedingStreamSizes) + (vStart + vi) * elementSize` 直接寻址。
- 新增 `TestSoAStreamLayout` 测试验证回读 Normal/UV 数据正确性和 stream 物理连续性。

## [2026-03-14] Shader 公共 Helper 提取 + 顶点属性 Fetch 工具
- 新建 `cluster_common.slang`：提取 `LoadClusterInfo`、`FetchVertexPosition`、`FetchVertexIndex`、`ProjectToScreen`、`ComputeBarycentric`、`PCGHash`、`ColorFromHash`、`DrawRequest` 等共享代码。
- 添加 SoA 属性解码工具：`DecodeSnorm8x4`、`DecodeFloat16x2`、`DecodeUnorm8x4`、`FetchNormal`、`FetchTangent`、`FetchUV`、`FetchVertexColor`。
- 重构 `cluster_resolve.slang`（227→107 行）和 `cluster_shade_material.slang`（205→113 行），删除重复代码，统一使用 `#include "cluster_common.slang"`。
- 所有 helper 函数改为显式参数传递（不依赖全局资源声明），提高复用性。

## [2026-03-14] Shade Material Shader — 属性插值着色
- `cluster_shade_binning.slang`：删除内联 `DrawRequest`，改用 `#include "cluster_common.slang"`。
- `cluster_shade_material.slang`：从面法线着色改造为重心坐标插值着色管线（`ProjectToScreen` + `ComputeBarycentric` + `FetchNormal3` + 四元数旋转 + 方向光）。
- `cluster_common.slang`：新增 `LoadStreamBytes`（字节级非对齐加载）、`DecodeSnorm8x3`、`FetchNormal3`（3B/vertex 法线读取）。
- `ClusterBuilder.cs`：属性排序为确定性顺序（NORMAL→TANGENT→TEXCOORD→COLOR→...），不做 padding。

## [2026-03-14] BSDF PBR 材质系统 MVP
- 新建 `brdf.slang`：Cook-Torrance 微表面 BRDF 库（GGX NDF、Smith-Schlick 几何遮蔽、Schlick Fresnel、`EvaluateDirectionalLight` 完整直接光计算）。
- 新建 `material_interfaces.slang`：定义 `PixelContext`（几何数据 + 输出）和 `ISurfaceEvaluate` 接口。PixelContext 仅包含几何信息，光照/Decal/IBL 等系统通过独立 Provider 接口按需注入（MVP 阶段不实现）。
- 新建 `standard_pbr.slang`：`StandardPBRMaterial : ISurfaceEvaluate`，固定 PBR 参数（baseColor=0.8, metallic=0, roughness=0.5），通过 `DirectionalLightParams` 读取光照 Uniform。
- 重构 `cluster_shade_material.slang`：提取 `buildPixelContext()` 封装 VisBuffer 解码 + 三角形重建 + 重心坐标插值 + 属性 fetch；`CSMaterialShade` 入口改为构建 PixelContext → 创建 StandardPBRMaterial → 调用 `evaluateSurface()`。所有 Debug 模式保持不变。
- `ShadeUniforms` 增加 `CameraPos`（`Vector3` + padding），`ClusterRenderFeature.cs` 传入 `_cameraPos`。

## [2026-03-14] 材质系统核心类型（Phase 1）
- 新建 `Materials/` 目录，添加 6 个核心文件：`ShaderResourceAttribute.cs`、`MaterialSlots.cs`（TextureSlot/BufferSlot/SamplerSlot）、`MaterialBase.cs`（抽象基类）、`MaterialShaderType.cs`（PSO 封装）、`MaterialRegistry.cs`（泛型注册表）、`StandardPBRMaterial.cs`。
- 设计：材质类型 = C# 类定义，材质实例 = C# 对象。`[ShaderResource]` 特性标记字段 → Phase 2 源生成器自动生成 SRB 绑定代码。
- 重写 `ClusterMaterialShadePass.cs`：移除自有 PSO/SRB，改为 `MaterialRegistry` 驱动的双层循环（外层 ShaderType 切 PSO，内层 Material 切 SRB + DispatchIndirect）。
- `ClusterRenderFeature.cs` 新增 `RegisterDefaultMaterials()` 方法，PSO 创建搬入，`activeMaterialCount` 从 registry 读取。
- `Program.cs` DI 注册 `MaterialRegistry`。
- 9 个单元测试通过。

## [2026-03-14] 多材质验证 + UV Fetch（Phase 2A）
- `PrimitiveMeshGenerator.cs`：ico 球新增 TANGENT 属性生成（cross(up, normal)），SoA 流顺序改为 NORMAL(3B) → TANGENT(4B) → UV(4B)。
- `cluster_shade_material.slang`：`buildPixelContext()` 添加 `cursor.advance(4)` 跳过 TANGENT、`FetchUV` + 重心坐标插值 UV；`CSMaterialShade` 用 `PCGHash(MaterialID)` 算 baseColor 实现每材质颜色区分。
- 新增 UV debug 可视化模式（mode 10，棋盘格 + UV 色彩映射）。
- `ClusterDebugMode` 枚举扩展：MaterialID=7, Barycentric=8, Normal=9, UV=10。
- `Program.cs`：`SpawnEntity` 新增 `materialId` 参数；DI 初始化后创建第 2 个 `StandardPBRMaterial`(ID=1)。
- ⚠️ 需要重新生成 .mesh 资产（旧资产无 TANGENT 流）。
- **Bug fixes**:
  - `cluster_common.slang`: `FetchUV` 改用 `LoadStreamBytes`（非对齐读取），修复 3B normal 导致 UV streamBase 非 4 字节对齐问题。
  - `cluster_resolve.slang`: mode≥3 时 `return` 不写颜色，避免覆盖 shade pass debug 输出。
  - `ClusterRenderFeature.cs`: resolve-only 提前返回只对 mode 1/2 生效，mode 7-10 走完整 shade pipeline。
  - `Program.cs`: Debug Cluster ID checkbox → Shade Debug Combo 下拉框；Add Entity 交替分配 MaterialID。
  - `IcoSphereTest.cs`: 新增 3 属性（NORMAL/TANGENT/UV）断言验证。

## [2026-03-14] SRB 优化 + Source Generator + 纹理（Phase 2B）
- **SRB 优化 (S0→S1)**:
  - Shade PSO `DefaultVariableType` → `Mutable`，仅 `Uniforms` 保留 `Dynamic`。
  - `BindPipelineResources` 所有 `Set()` 改用 `AllowOverwrite`（Mutable + RG placed resource 每帧重绑）。
- **Source Generator**:
  - 新建 `SomeEngine.Generators` 项目（netstandard2.0 ISG）。
  - `MaterialBindingGenerator`：扫描 `[ShaderResource]` 字段，按类型（TextureSlot/BufferSlot/SamplerSlot）生成 `CommitBindings()`。
- **纹理支持**:
  - `StandardPBRMaterial`：添加 `AlbedoMap`/`NormalMap`/`ARMMap`/`MaterialSampler` 字段。
  - `ClusterRenderFeature`：`RegisterDefaultMaterials()` 创建 1×1 默认纹理（白色 albedo、平坦法线、默认 ARM）+ linear sampler。
  - `cluster_shade_material.slang`：声明 `Texture2D AlbedoMap/NormalMap/ARMMap` + `SamplerState MaterialSampler`；baseColor 改用 `AlbedoMap.Sample()`。
  - 新增 `SetupMaterialWithDefaults()` 公共 API 供外部配置新材质。

## [2026-03-14] 材质系统重新设计 v4.2
- **IShaderParams 统一体系**：新建 `IShaderParams` 接口 + `ShaderParamAttribute`（含 `Dynamic` / `Stage` 属性，默认 Mutable）；`MaterialBase` 实现 `IShaderParams`。
- **Params 组合**：材质通过持有 `IShaderParams` 字段组合可复用参数块（`PBRParams`、`NoiseParams` 等），源生成器自动在 `ApplyToSRB()` 中调用 `field.ApplyToSRB(srb)`；材质自身也可直接放 `[ShaderParam]` 字段。
- **语义标签**：`IMaterialTag` + `MaterialTagSet`（Dictionary-based），核心标签 `OpaqueTag`/`MaskedTag`/`TwoSidedTag`/`StencilRefTag` 等。
- **多 Pass PSO**：`IPassKey` + `MaterialShaderType`，每个 ShaderType 可注册多个 Pass（如 ShadowCaster）。
- **源生成器重写**：`MaterialBindingGenerator` → `ShaderParamsGenerator`。统一扫描 `IShaderParams` 实现类和 `MaterialBase` 子类，处理资源字段（SRB Set）、IShaderParams 组合字段（委托调用）、base 链。
- **管线集成**：`ClusterShadePipelineParams : IShaderParams`（全部 Dynamic）替代手写 `BindPipelineResources()`；`ClusterMaterialShadePass` 改用 `pipelineParams.ApplyToSRB()` + `material.CommitBindings()`。
- **BufferSlot 增强**：增加 `IBuffer?` 字段支持 ConstantBuffer 直接绑定。
- 删除旧 `ShaderResourceAttribute.cs`。9 个测试通过。

## [2026-03-15] 修复源生成器重复生成 ApplyToSRB Bug
- 移除 `PBRParams` 和 `ClusterShadePipelineParams` 中手动编写的 `ApplyToSRB` 空方法，避免与源生成器发生 CS0111 冲突。
- 修复 `MaterialBindingGenerator.cs` 中未过滤隐式字段（如 auto-property 的 `<Field>k__BackingField`），导致其生成非法 C# 语法的漏洞：在 `GetMembers()` 遍历时前置 `IsImplicitlyDeclared` 检查。
- 修复 `PBRParams` 中 ShaderParam 资源绑定名称不匹配导致的 `No resource is bound to variable` 错误：移除与属性名完全一致的冗余字符串参数（改为 `[ShaderParam]`），仅在绑定名与属性名不同时（如 `MaterialSampler`）使用字符串指定名称，通过源生成器的默认规则保持精准匹配。

## [2026-03-15] 光栅化 Binning 管线（Step 1-2）
- **Step 1 回退 + RasterBinKey**：回退 `cluster_cull.slang`、`ClusterCullPass.cs`、`ClusterRenderFeature.cs`、`ClusterGraphPasses.cs` 中错误的 DeformFlags/DeformedBuffer 代码；`GpuInstanceHeader`（C# + Slang）字段改为 `RasterBinKey`（uint16 = VertexEvalProgram:8 | RasterFlags:8）+ padding；`InstanceSyncSystem.cs` 同步更新。
- **Step 2 Binning CS**：新建 `cluster_binning.slang`（`CSBinningInit` 初始化 per-bin metadata + DrawArgs，`CSBinning` 按 InstanceHeaders.RasterBinKey 将 VisibleClusters 原子散射到 BinnedClusterBuffer 对应 bin 区域，count 从 DrawArgs GPU 读取）。新建 `ClusterBinningPass.cs`（双 PSO/SRB 分 Init + Scatter 内核，绑定 7 资源）。`ClusterRenderFeature.cs` 集成：4 新 buffer（RasterBinMeta、BinnedClusterBuffer、BinnedDrawArgs、BinningUniforms），uniform upload lambda 增加 BinningUniforms，legacy path cull→binning→draw 路由（draw 改读 BinnedClusterBuffer + BinnedDrawArgs）。

- [Render] Cluster Render Pass Pipeline: Refactored PSOs and SRB Pools to 100% Static caching, making all Passes stateless and thread-safe.

## [2026-03-16] 材质架构文档更新 (ShaderAsset + PSO + Pull 模型)
- **ShaderAsset**：复用已有的预编译 `ShaderAsset` 类（FlatBuffers: name + variants + reflections）。统一三种生成方式：1) 编辑器下拉组装；2) `[ClusterShade]` 等特性驱动 Importer 自动模板实例化（推荐）；3) 手写入口函数 + Stage Wrapper。
- **PSO 所有权**：PSO 归 Stage 缓存。同一 ShaderAsset 可被多个管线使用（Cluster Compute vs Forward Graphics），不同管线产出不同 PSO。
- **PSO 缓存策略**：bin key = PSO 索引。Stage 维护扁平数组 `_psoByBin[binKey]`，Dictionary 仅在低频 `RebuildDispatchTable` 中做 ShaderAsset 去重，热路径零 hash 开销。
- **Pull 模型**：去除 `OnMaterialRegistered` 回调。兼容性 Tag 由 `MaterialRegistry.Register()` 根据 ShaderAsset 元数据自动打标；bin key 和 PSO 由 Stage 在 Setup 阶段 Pull 查询后按需构建。
- **底部对照表修正**：`ShaderAsset` 标记为已有类，去除过时的 `ModulePath + StructName` 描述。
