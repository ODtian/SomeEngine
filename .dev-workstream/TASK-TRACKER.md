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
