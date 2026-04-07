# BATCH-01: 文档同步与清理

**Type:** Documentation Baseline + Debt Cleanup  
**Scope:** Phase 0 的 TASK-001 ~ TASK-009  
**Estimated Effort:** ~2-3 小时  
**Prerequisite:** BASELINE-REVIEW 完成 ✅

---

## Objective

人工审查 8 处文档-代码矛盾，清理残留文件和废弃代码，建立可信文档基线。

---

## Required Reading

- `docs/DESIGN.md`
- `.dev-workstream/reviews/BASELINE-REVIEW.md`
- `docs/rendering/cluster_pipeline.md`
- `docs/materials/architecture.md`
- `docs/rendering/shading_pipeline.md`
- `docs/rendering/rasterization.md`
- `docs/rendering/overview.md`

---

## Source Code Locations

- `src/SomeEngine.Assets/Data/GPUCluster.cs`
- `src/SomeEngine.Render/Data/InstanceMetadata.cs`
- `src/SomeEngine.Render/Materials/MaterialSlotBuffer.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipelineTypes.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/`

---

## Build & Test Commands

```bash
dotnet build SomeEngine.slnx
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj
```

---

## Tasks & Success Criteria

### 1. TASK-001: 审查 cluster_pipeline.md DRIFT-1,2,3
- [ ] 人工确认 GPUCluster 大小差异（48B vs 64B）哪方反映当前意图，然后更新文档或代码
- [ ] 人工确认 GpuInstanceHeader 字段差异（MaterialID vs MaterialSlotOffset），然后更新
- [ ] 人工确认 Stage 数量差异（10 vs 9），然后更新
- [ ] 验证：全文无未解决的矛盾

### 2. TASK-002: 审查 materials/architecture.md DRIFT-4
- [ ] 人工确认 `struct MaterialSlot` vs SOA `ushort[]` 哪方反映当前意图
- [ ] 根据确认结果更新文档或代码
- [ ] 验证：文档与实际实现一致

### 3. TASK-003: 审查 material_pipeline_full_chain.md DRIFT-5
- [ ] 人工确认该文档定位：未来计划 / 已废弃 / 待实现
- [ ] 根据确认结果移入 `docs/archive/` 或标注为计划
- [ ] 更新 `docs/README.md` 索引

### 4. TASK-004: 审查 shading_pipeline.md DRIFT-6
- [ ] 人工确认 PSOGroup TODO 是否已完全满足，然后更新标记

### 5. TASK-005: 审查 rasterization.md DRIFT-7
- [ ] 人工确认 DeformedBuffer vs DeformCache 名称差异是否为有意重命名
- [ ] 根据确认结果更新文档或代码

### 6. TASK-006: 审查 overview.md DRIFT-8
- [ ] 人工确认 Persistent Threads vs Queue-Driven 是术语习惯差异还是架构变更
- [ ] 根据确认结果更新文档

### 7. TASK-007: 删除残留文件
- [ ] `git rm src/SomeEngine.Render/Pipelines/ClusterRender/ClusterRenderPass.cs.bak`

### 8. TASK-008: 确认 rendering/ 计划文档
- [ ] 检查 `material_tag_migration_plan.md` — 确认是否已完成或归档
- [ ] 检查 `gpu_pipeline_remaining_plan.md` — 确认是否仍有效
- [ ] 检查 `persistent_thread_bvh_traversal.md` — 确认与实际实现关系

### 9. TASK-009: 删除废弃的 ClusterRenderFeature.cs
- [ ] `git rm src/SomeEngine.Render/Pipelines/ClusterRender/ClusterRenderFeature.cs`
- [ ] `dotnet build SomeEngine.slnx` 确认编译通过
- [ ] 清理对该文件的任何引用

---

## Verification

- [ ] `dotnet build SomeEngine.slnx` 编译通过（文档修改不影响编译，但确认无文件引用断裂）
- [ ] 全文搜索验证各矛盾点已修正
- [ ] 更新 `.dev-workstream/TASK-TRACKER.md` 中 Phase 0 任务状态

---

## Report Requirements

完成后产出 `.dev-workstream/reports/BATCH-01-REPORT.md`：

```markdown
# BATCH-01 REPORT

**Date:** YYYY-MM-DD  
**Status:** DONE / PARTIAL

## Completed
- [x] TASK-001 ~ TASK-XXX

## Issues Found
- [issue description]

## Debt Changes
- [new / resolved debt items]
```
