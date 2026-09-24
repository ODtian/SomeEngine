# TASK-TRACKER

## Phase 0: Baseline & Documentation

| ID | Task | Status | Detail |
|---|---|---|---|
| TASK-001 | 同步 cluster_pipeline.md（GPUCluster/Stage） | DONE | [→](../../docs/TASK-DETAIL.md#task-001-同步-cluster_pipelinemd) |
| TASK-002 | 修正 materials/architecture.md MaterialSlot | DONE | [→](../../docs/TASK-DETAIL.md#task-002-修正-materialsarchitecturemd-materialslot-残留) |
| TASK-003 | 标注 material_pipeline_full_chain.md 待实现 | DONE | [→](../../docs/TASK-DETAIL.md#task-003-归档-material_pipeline_full_chainmd) |
| TASK-004 | 同步 shading_pipeline.md 到当前 ClusterShade 实现 | DONE | [→](../../docs/TASK-DETAIL.md#task-004-同步-shading_pipelinemd-到当前-clustershade-实现) |
| TASK-005 | 同步 rasterization.md 的 DeformCache 命名 | DONE | [→](../../docs/TASK-DETAIL.md#task-005-同步-rasterizationmd-的-deformcache-命名) |
| TASK-006 | 修正 overview.md 遍历模型 | DONE | [→](../../docs/TASK-DETAIL.md#task-006-修正-overviewmd-遍历模型描述) |
| TASK-007 | 删除 .bak 残留文件 | DONE | [→](../../docs/TASK-DETAIL.md#task-007-删除残留文件) |
| TASK-008 | 整理 rendering/ 下计划文档状态 | DONE | [→](../../docs/TASK-DETAIL.md#task-008-整理-rendering-下计划文档状态) |
| TASK-009 | 删除废弃的 ClusterRenderFeature.cs | DONE | [→](../../docs/TASK-DETAIL.md#task-009-删除废弃的-clusterrenderfeaturecs) |

## Phase 1: Review Existing Core

| ID | Task | Status | Detail |
|---|---|---|---|
| TASK-101 | Dual-Signature 测试覆盖 | DONE | [→](../../docs/TASK-DETAIL.md#task-101-dual-signature-绑定测试覆盖) |
| TASK-102 | DeformCache 功能测试 | DONE | [→](../../docs/TASK-DETAIL.md#task-102-deformcache-功能测试) |
| TASK-103 | 修复 ECS Transform 旋转层级传播回归 | DONE | [→](../../docs/TASK-DETAIL.md#task-103-修复-ecs-transform-旋转层级传播回归) |

## Phase 2: Hardening & Debt Reduction

| ID | Task | Status | Detail |
|---|---|---|---|
| TASK-201 | 补 ECS/QVVS/SourceGen/VRB 文档 | DONE | [→](../../docs/TASK-DETAIL.md#task-201-补-ecsqvvssource-generatorvrb-独立文档) |
| TASK-202 | 资产管线总览文档 | DONE | [→](../../docs/TASK-DETAIL.md#task-202-资产管线总览文档) |

## Phase 3: Next Features

| ID | Task | Status | Detail |
|---|---|---|---|
| TASK-301 | 光照系统强化 | TODO | [→](../../docs/TASK-DETAIL.md#task-301-光照系统强化) |
| TASK-302 | Page 流式加载 | TODO | [→](../../docs/TASK-DETAIL.md#task-302-page-流式加载) |
| TASK-303 | Tessellation | TODO | [→](../../docs/TASK-DETAIL.md#task-303-tessellation) |
| TASK-311 | 统一 FrameTarget 与 History 资源系统 | DONE | [→](../../docs/TASK-DETAIL.md#task-311-统一-frametarget-与-history-资源系统) |
| TASK-311a | └ Unified FrameTargetRegistry | DONE | BATCH-11 Task 1 |
| TASK-311b | └ RenderGraph 资源提取原语 | DONE | BATCH-11 Task 2 |
| TASK-311c | └ History 生命周期集成与 HiZ 迁移 | DONE | BATCH-11 Task 3 |
| TASK-311d | └ Material fallback 资源绑定 | DONE | BATCH-11 Task 4 |
| TASK-311e | └ Runtime / Pipeline 采用统一 FrameTargetRegistry | DONE | BATCH-11 Task 5 |
| TASK-304 | Material 重构 | DONE | [→](../../docs/TASK-DETAIL.md#task-304-material-ecs-重构) |
| TASK-307 | 资产管线破坏性重构 | DONE | [→](../../docs/TASK-DETAIL.md#task-307-资产管线破坏性重构修复) |
| TASK-307a | └ 核心类型重写 (AssetGuid/IAsset/Registry) | DONE | BATCH-07 Task 1 |
| TASK-307b | └ Manifest 精简 + Scanner 重写 | DONE | BATCH-07 Task 2 |
| TASK-307c | └ AssetDatabase 实现 + 旧实现全删 | DONE | BATCH-07 Task 3 |
| TASK-307d | └ 全项目消费处迁移 | DONE | BATCH-07 Task 4 |
| TASK-307e | └ Review 修正（8 项遗留问题） | DONE | BATCH-07 Task 5 |
| TASK-307f | └ Handler 提取 + AssetDatabase 构造注入 | DONE | BATCH-07b |
| TASK-307g | └ 删除静态 Registry + 迁移 Host/Test | DONE | BATCH-07b |
| TASK-307h | └ GltfSourceImporter + 删除 ClusterBuilder 回调 | DONE | BATCH-07b |
| TASK-307i | └ AssetProvider\<T\> + TypedStore\<T\> 替换 IAssetTypeHandler | DONE | BATCH-07c |
| TASK-307j | └ TextureAsset Pipeline | DONE | BATCH-07c |
| TASK-307k | └ Source Generator 自动发现 + Runtime 接入 | DONE | BATCH-07c |
| TASK-308 | 全 Dynamic PSO 简化 + AlbedoMap Bug 修复 | DONE | [→](#) |
| TASK-308a | └ Bug 修复 + MaterialPSOGroup 重命名 | DONE | BATCH-08 |
| TASK-308b | └ 消灭 Dual-Sig，统一全 Dynamic | DONE | BATCH-08 |
| TASK-308c | └ 样板代码消除（StaticPSOInit + SRBPool） | DONE | BATCH-08 |
| TASK-309 | Asset + Material RenderWorld 完整改造 | DONE | [→](../../docs/TASK-DETAIL.md#task-309-asset--material-renderworld-完整改造) |
| TASK-309a | └ Mesh region / authoring binding 重构 | DONE | BATCH-09 Task 1 |
| TASK-309b | └ MaterialAsset pass entity 模型落地 | DONE | BATCH-09 Task 2 |
| TASK-309c | └ RenderWorld extract / prepare / queue 分层 | DONE | BATCH-09 Task 3 |
| TASK-309d | └ 管线派生数据下沉 + ClusterPipeline 迁移 | DONE | BATCH-09 Task 4 |
| TASK-309e | └ Host / Test / Legacy 路径清理 | DONE | BATCH-09 Task 5 |
| TASK-309f | └ Mesh local material 线性表语义收敛 | DONE | BATCH-09b Task 1 |
| TASK-309g | └ RenderWorld extract local-slot 重构 | DONE | BATCH-09b Task 2 |
| TASK-309h | └ Cluster prepare zero-GC slot folding | DONE | BATCH-09b Task 3 |
| TASK-309i | └ Host / Test / Docs / Legacy 清理与守护 | DONE | BATCH-09b Task 4 |

## Phase 3: Render Pipeline Continuation

| ID | Task | Status | Detail |
|---|---|---|---|
| TASK-312 | HDR SceneColor and Post Chain | DONE | [->](../../docs/TASK-DETAIL.md#task-312-hdr-scenecolor-and-post-chain) |
| TASK-312a | BackBuffer / HDR SceneColor FrameTarget contract | DONE | BATCH-12 Task 1 |
| TASK-312b | HDR Cluster shading output | DONE | BATCH-12 Task 2 |
| TASK-312c | Minimal post tonemap pass | DONE | BATCH-12 Task 3 |
| TASK-312d | Runtime / Editor adoption | DONE | BATCH-12 Task 4 |
| TASK-313 | Motion Vectors and Temporal SceneColor History | DONE | [->](../../docs/TASK-DETAIL.md#task-313-motion-vectors-and-temporal-scenecolor-history) |
| TASK-313a | MotionVectors / Temporal history target contract | DONE | BATCH-13 Task 1 |
| TASK-313b | Cluster motion vector pass | DONE | BATCH-13 Task 2 |
| TASK-313c | Temporal SceneColor history update | DONE | BATCH-13 Task 3 |
| TASK-313d | Runtime / Editor adoption and tests | DONE | BATCH-13 Task 4 |
| TASK-314 | Explicit Frame IO and Render History Simplification | DONE | [->](../../docs/TASK-DETAIL.md#task-314-explicit-frame-io-and-render-history-simplification) |
| TASK-314a | Remove FrameTargetRegistry from frame IO | DONE | BATCH-14 Task 1 |
| TASK-314b | Frame resource names and RenderGraph naming guard | DONE | BATCH-14 Task 2 |
| TASK-314c | Independent RenderHistoryRegistry | DONE | BATCH-14 Task 3 |
| TASK-314d | Pipeline / host migration | DONE | BATCH-14 Task 4 |
| TASK-315 | Temporal Resolve and TAA Validation | DONE | [->](../../docs/TASK-DETAIL.md#task-315-temporal-resolve-and-taa-validation) |
| TASK-315a | Temporal resolve contract | DONE | BATCH-15 Task 1 |
| TASK-315b | Bounded temporal resolve shader | DONE | BATCH-15 Task 2 |
| TASK-315c | Cluster pipeline integration | DONE | BATCH-15 Task 3 |
| TASK-315d | Reset and host adoption | DONE | BATCH-15 Task 4 |
| TASK-316 | Temporal Quality and Runtime Validation | DONE | [->](../../docs/TASK-DETAIL.md#task-316-temporal-quality-and-runtime-validation) |
| TASK-316a | Temporal sample pattern and jitter contract | DONE | BATCH-16 Task 1 |
| TASK-316b | Bounded quality resolve parameters | DONE | BATCH-16 Task 2 |
| TASK-316c | Runtime and Editor validation adoption | DONE | BATCH-16 Task 3 |
| TASK-316d | Tests, docs, report, and review | DONE | BATCH-16 Task 4 |
| TASK-317 | Runtime Debug State and Validation Console Rework | DONE | [->](../../docs/TASK-DETAIL.md#task-317-runtime-debug-state-and-validation-console-rework) |
| TASK-317a | Runtime debug state ownership | DONE | BATCH-17 Task 1 |
| TASK-317b | Runtime Validation Console UI | DONE | BATCH-17 Task 2 |
| TASK-317c | Host command routing | DONE | BATCH-17 Task 3 |
| TASK-317d | Verification and artifacts | DONE | BATCH-17 Task 4 |
| TASK-318 | Temporal Validation Hardening | DONE | [->](../../docs/TASK-DETAIL.md#task-318-temporal-validation-hardening) |
| TASK-318a | Shared temporal validation presets | DONE | BATCH-18 Task 1 |
| TASK-318b | Deterministic validation sequence | DONE | BATCH-18 Task 2 |
| TASK-318c | Runtime capture artifacts and metrics | DONE | BATCH-18 Task 3 |
| TASK-318d | Verification, report, and review | DONE | BATCH-18 Task 4 |
| TASK-319 | Standalone RHI Core | DONE | [->](../../docs/TASK-DETAIL.md#task-319-standalone-rhi-core) |
| TASK-319a | API spec and project shell | DONE | BATCH-19 Task 1 |
| TASK-319b | Core contracts | DONE | BATCH-19 Task 2 |
| TASK-319c | Strict Null backend | DONE | BATCH-19 Task 3 |
| TASK-319d | RHI contract tests | DONE | BATCH-19 Task 4 |
| TASK-319e | Review and hardening | DONE | BATCH-19 Task 5 |
| TASK-320 | RHI D3D12 Vertical Slice | DONE | [->](../../docs/TASK-DETAIL.md#task-320-rhi-d3d12-vertical-slice) |
| TASK-320a | Freeze-target core contract | DONE | BATCH-20 Task 1 |
| TASK-320b | Null freeze-target validation | DONE | BATCH-20 Task 2 |
| TASK-320c | D3D12 backend assembly | DONE | BATCH-20 Task 3 |
| TASK-320d | D3D12 resource, memory, and commands | DONE | BATCH-20 Task 4 |
| TASK-320e | Tests, review, and hardening | DONE | BATCH-20 Task 5 |
| TASK-321 | Mature RHI Conformance Import | DONE | [->](../../docs/TASK-DETAIL.md#task-321-mature-rhi-conformance-import) |
| TASK-321a | Source matrix | DONE | BATCH-21 Task 1 |
| TASK-321b | Executable conformance slice | DONE | BATCH-21 Task 2 |
| TASK-321c | Validation and report | DONE | BATCH-21 Task 3 |
| TASK-322 | Mature RHI Corpus Expansion | DONE | [->](../../docs/TASK-DETAIL.md#task-322-mature-rhi-corpus-expansion) |
| TASK-322a | Corpus inventory | DONE | BATCH-22 Task 1 |
| TASK-322b | Executable matrix expansion | DONE | BATCH-22 Task 2 |
| TASK-322c | Validation and review | DONE | BATCH-22 Task 3 |
| TASK-323 | RHI D3D12 Execution Coverage | DONE | [->](../../docs/TASK-DETAIL.md#task-323-rhi-d3d12-execution-coverage) |
| TASK-323a | GPU copy and render-target readback | DONE | BATCH-23 Task 1 |
| TASK-323b | Draw, timestamp, and placed aliasing execution | DONE | BATCH-23 Task 2 |
| TASK-323c | Validation and review | DONE | BATCH-23 Task 3 |
| TASK-324 | RHI Advanced Capability Completion | DONE | [->](batches/BATCH-24-INSTRUCTIONS.md) |
| TASK-324a | Advanced backend completion | DONE | BATCH-24 Task 1 |
| TASK-324b | Generate mips utility | DONE | BATCH-24 Task 2 |
| TASK-324c | Documentation, validation, and review | DONE | BATCH-24 Task 3 |
| TASK-325 | RHI Visible Swapchain Automation | DONE | [->](batches/BATCH-25-INSTRUCTIONS.md) |
| TASK-325a | Harness project | DONE | BATCH-25 Task 1 |
| TASK-325b | Display-path scenarios | DONE | BATCH-25 Task 2 |
| TASK-325c | Automation contract and records | DONE | BATCH-25 Task 3 |
| TASK-326 | Engine Renderer RHI Migration | IN PROGRESS | [->](batches/BATCH-26-INSTRUCTIONS.md) |
| TASK-326a | Build recovery and default Runtime RHI graph path | DONE | BATCH-26 Task 1 |
| TASK-326b | Direct cluster RenderGraph RHI migration | TODO | BATCH-26 Task 2 |
| TASK-326c | Build and validation | DONE | BATCH-26 Task 3 |
| TASK-327 | RG Cluster Owner Repair | IN PROGRESS | [->](batches/BATCH-27-INSTRUCTIONS.md) |
| TASK-327a | RG compile owner | DONE | BATCH-27 Task 1 |
| TASK-327b | Frame entry | IN PROGRESS | BATCH-27 Task 2 |
| TASK-327c | Bind and bin | IN PROGRESS | BATCH-27 Task 3 |
| TASK-327d | Slots pages uploads | TODO | BATCH-27 Task 4 |
| TASK-327e | Docs and gates | IN PROGRESS | BATCH-27 Task 5 |
| TASK-328 | PipelineState Store Refactor | DONE | [->](batches/BATCH-28-INSTRUCTIONS.md) |
| TASK-328a | Store model | DONE | BATCH-28 Task 1 |
| TASK-328b | Native cache and modules | DONE | BATCH-28 Task 2 |
| TASK-328c | Render integration | DONE | BATCH-28 Task 3 |
| TASK-328d | Tests and docs | DONE | BATCH-28 Task 4 |
| TASK-330 | RenderGraph RDG 模式完整重构 | DONE | [->](batches/BATCH-30-INSTRUCTIONS.md) |
| TASK-330a | └ Public authoring contract rewrite | DONE | BATCH-30 Task 1 |
| TASK-330b | └ Compiler rewrite | DONE | BATCH-30 Task 2 |
| TASK-330c | └ Executor and runtime ownership split | DONE | BATCH-30 Task 3 |
| TASK-330d | └ Frame data and history rewrite | DONE | BATCH-30 Task 4 |
| TASK-330e | └ Binding and pass-data rewrite | DONE | BATCH-30 Task 5 |
| TASK-330f | └ Cluster/material/post/host integration rewrite | DONE | BATCH-30 Task 6 |
| TASK-330g | └ Delete legacy model | DONE | BATCH-30 Task 7 |
| TASK-330h | └ Validation, simplify, report, and review | DONE | BATCH-30 Task 8 |
| TASK-331 | RenderGraph Contract Retrofit | DONE | [->](batches/BATCH-31-INSTRUCTIONS.md) |
| TASK-331a | └ Explicit Pass Access Contract | DONE | BATCH-31 Task 1 |
| TASK-331b | └ Dependency, Culling, And Queue Closure | DONE | BATCH-31 Task 2 |
| TASK-331c | └ Resource Class, Lifetime, And History Semantics | DONE | BATCH-31 Task 3 |
| TASK-331d | └ Alias Allocation And Handoff Safety | DONE | BATCH-31 Task 4 |
| TASK-331e | └ Barrier, Queue, And Execution Backend | DONE | BATCH-31 Task 5 |
| TASK-331f | └ In-Repo Migration, Diagnostics, Report, And Review Inputs | DONE | BATCH-31 Task 6 |
