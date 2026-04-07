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
| TASK-304 | Material 重构 | DONE | [→](../../docs/TASK-DETAIL.md#task-304-material-ecs-重构) |
| TASK-307 | 资产管线破坏性重构 | IN-PROGRESS | [→](../../docs/TASK-DETAIL.md#task-307-资产管线破坏性重构修复) |
| TASK-307a | └ 核心类型重写 (AssetGuid/IAsset/Registry) | DONE | BATCH-07 Task 1 |
| TASK-307b | └ Manifest 精简 + Scanner 重写 | DONE | BATCH-07 Task 2 |
| TASK-307c | └ AssetDatabase 实现 + 旧实现全删 | DONE | BATCH-07 Task 3 |
| TASK-307d | └ 全项目消费处迁移 | DONE | BATCH-07 Task 4 |
| TASK-307e | └ Review 修正（8 项遗留问题） | DONE | BATCH-07 Task 5 |
