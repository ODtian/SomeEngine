# BATCH-01 REPORT

**Date:** 2026-03-28  
**Status:** DONE

## Completed

- [x] TASK-001: `cluster_pipeline.md` — GPUCluster 64B, MaterialSlotOffset, 9 Stage, 移除 ClusterRenderFeature
- [x] TASK-002: `materials/architecture.md` — `struct MaterialSlot` 替换为 SOA `MaterialSlotBuffer` 描述
- [x] TASK-003: `material_pipeline_full_chain.md` — 顶部标注"待实现（未来计划）"
- [x] TASK-004: `shading_pipeline.md` — `ClusterShade.cs` 作为统一静态编排器写回文档，补齐 PSO 分组现状
- [x] TASK-005: `rasterization.md` — 主文档命名统一为 `DeformCache`
- [x] TASK-006: `overview.md` — BVH Traversal 标注"当前 Queue-Driven / 计划 Persistent Threads 双架构"
- [x] TASK-007: 删除 `ClusterRenderPass.cs.bak` (80KB)
- [x] TASK-008: 合并 `gpu_pipeline_remaining_plan.md` + `material_tag_migration_plan.md` → `gpu_pipeline_tag_integration_plan.md`，标注"待实现"
- [x] TASK-009: 删除 `ClusterRenderFeature.cs` (91KB)，类型定义提取至 `ClusterPipelineTypes.cs`

## Issues Found

- **Pre-existing build errors (3):** `AssetResolverTests.cs:137`, `MaterialAssetPipelineTests.cs:284,285` 存在编译错误，与本次修改无关（删除前就存在）
- **Type extraction needed:** `ClusterRenderFeature.cs` 中定义了 6 个共享类型（CullingUniforms, DrawUniforms, ShadeUniforms, ShadeBinUniforms, CopyUniforms, ClusterDebugMode, HiZDebugMode），需提取到 `ClusterPipelineTypes.cs`
- **SWHWView + Create signature:** git HEAD 版本的类型定义落后于其他代码引用，添加了 `SWHWView` enum 成员和 `screenWidth/screenHeight` 参数

## Debt Changes

### 已解决
- DEBT-001: ClusterRenderFeature 已删除 ✅
- DEBT-003: ClusterRenderPass.cs.bak 已删除 ✅
- DEBT-004: cluster_pipeline.md 已同步 ✅
- DEBT-005: materials/architecture.md MaterialSlot → SOA ✅
- DEBT-006: material_pipeline_full_chain.md 已标注状态 ✅
- DEBT-010: shading_pipeline.md TODO 已确认 ✅
- DEBT-011: rasterization.md 名称已确认 ✅
- DEBT-012: overview.md 术语已更新 ✅

### 新增
- DEBT-013: 测试文件 pre-existing 编译错误（AssetResolverTests/MaterialAssetPipelineTests）
- DEBT-014: `gpu_pipeline_remaining_plan.md` 和 `material_tag_migration_plan.md` 旧文件仍在 git 历史中，fs 已删除

## Verification

- [x] `dotnet build` — 主项目编译通过（0 error），测试项目 3 个 pre-existing error
- [x] DRIFT-1~8 全部经用户确认后解决
- [x] TASK-TRACKER Phase 0 全部标记 DONE
