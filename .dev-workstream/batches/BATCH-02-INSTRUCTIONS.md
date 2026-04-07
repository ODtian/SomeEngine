# BATCH-02: 核心管线加固（第一阶段）

**Type:** Core Review Hardening  
**Scope:** TASK-101, TASK-102  
**Estimated Effort:** ~4-6 小时  
**Prerequisite:** BATCH-01 完成

---

## Objective

把 Phase 1 的硬化工作推进到“有可复用 helper + 有纯 CPU 测试基线”的状态，为后续更深层的 GPU / SRB 行为测试打底。

---

## Required Reading

- `docs/DESIGN.md`
- `docs/rendering/shading_pipeline.md`
- `docs/rendering/rasterization.md`
- `.dev-workstream/reviews/BATCH-01-REVIEW.md`
- `docs/TASK-DETAIL.md`

---

## Source Code Locations

- `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ShadePSOGroup.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/DeformDispatchCalc.cs`
- `tests/SomeEngine.Tests/Pipelines/ShaderGroupTests.cs`
- `tests/SomeEngine.Tests/Pipelines/DeformCacheTests.cs`

---

## Build & Test Commands

```bash
dotnet build SomeEngine.slnx
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj
```

---

## Tasks & Success Criteria

### TASK-101: Dual-Signature 测试覆盖（第一阶段）
- [ ] 提取可测试的纯 CPU 分组逻辑
- [ ] 补 `ShaderGroupTests`
- [ ] 在 report 中明确哪些 Sig1 / SRB 路径仍未覆盖

### TASK-102: DeformCache 功能测试（第一阶段）
- [ ] 提取 dispatch 参数计算 / 索引解码 helper
- [ ] 补 `DeformCacheTests`
- [ ] 在 report 中明确 `CacheAllocCounter` / `CacheOffsets` / inline-vs-cached 仍未覆盖

---

## Report Requirements

产出 `.dev-workstream/reports/BATCH-02-REPORT.md`，至少包含：

- 已完成内容
- 仍缺的覆盖项
- build/test 结果
- 是否应将父任务标记为 `PARTIAL`
