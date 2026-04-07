# BATCH-04: Phase 1 Hardening Closeout

**Batch Number:** BATCH-04  
**Tasks:** TASK-103, TASK-101, TASK-102  
**Phase:** Phase 1 - Review Existing Core  
**Estimated Effort:** 4-8 hours  
**Priority:** HIGH  
**Dependencies:** BATCH-03

---

## Onboarding & Workflow

### Developer Instructions
本批是 combined corrective batch，目标是收口当前基线问题并恢复测试全绿。任务必须按顺序推进，当前任务不绿不得进入下一个任务。

### Required Reading (IN ORDER)
1. **Workflow Guide:** `.dev-workstream/guides/DEV-GUIDE.md`
2. **Onboarding:** `ONBOARDING.md`
3. **Design:** `docs/DESIGN.md` — Phase 1 / Dual-Signature / DeformCache
4. **Task Details:** `docs/TASK-DETAIL.md` — TASK-101 / TASK-102 / TASK-103
5. **Previous Review:** `.dev-workstream/reviews/BATCH-03-REVIEW.md`
6. **Baseline:** `.dev-workstream/reviews/BASELINE-REVIEW.md`

### Source Code Locations
| Area | Path |
|---|---|
| ECS test harness | `tests/SomeEngine.Tests/ECS/TransformSystemTests.cs` |
| Material layout hash | `src/SomeEngine.Render/Materials/ShaderParamBag.cs` |
| Sig1 cache logic | `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` |
| Sig1 tests | `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs` |
| Deform helpers | `src/SomeEngine.Render/Pipelines/ClusterRender/DeformDispatchCalc.cs` |
| Deform tests | `tests/SomeEngine.Tests/Pipelines/DeformCacheTests.cs` |
| Related doc sync | `docs/rendering/sw_raster/sw_raster.md` |

### Build & Test Commands
```bash
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~TransformSystemTests"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~ClusterShadeSig1Tests"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~DeformCacheTests"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj
```

### Report Submission
完成后提交到：  
`.dev-workstream/reports/BATCH-04-REPORT.md`

如需提问，创建：  
`.dev-workstream/questions/BATCH-04-QUESTIONS.md`

---

## Context

本批用于关闭 Phase 1 剩余问题。进入本批前，仓库状态是 `177 passed, 1 failed`，并且 Dual-Signature / DeformCache 只有 helper 级测试。

**Related Tasks:**
- `TASK-103` — 修复 ECS Transform 旋转层级传播回归
- `TASK-101` — Dual-Signature 测试覆盖
- `TASK-102` — DeformCache 功能测试

---

## Batch Objectives

- 修复并解释 ECS 旋转测试失败的根因
- 为 Sig1 cache key / descriptor / cache reuse 增加直接测试
- 为 DeformCache 资源级镜像行为增加测试
- 恢复全量测试全绿

---

## Tasks

### Task 1: Fix ECS Transform Rotation Regression (TASK-103)

**File:** `tests/SomeEngine.Tests/ECS/TransformSystemTests.cs`  
**Task Definition:** `docs/TASK-DETAIL.md#task-103-修复-ecs-transform-旋转层级传播回归`

**Description:**
找出 `TransformSystemTests.TestRotation` 失败的真实原因，并修复测试或实现，使 direct-system 路径的断言具备正确同步语义。

**Requirements:**
- 明确区分运行时逻辑错误和测试同步错误
- 保持 `GameWorld.Update()` 路径行为不变
- 修复后 `TransformSystemTests` 全通过

**Tests Required:**
- ✅ `TransformSystemTests`
- ✅ 不破坏 `EcsTests.TestRotationHierarchy`

---

### Task 2: Close Sig1 Cache Test Gap (TASK-101)

**File:** `src/SomeEngine.Render/Materials/ShaderParamBag.cs`, `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`, `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs`  
**Task Definition:** `docs/TASK-DETAIL.md#task-101-dual-signature-绑定测试覆盖`

**Description:**
把 Sig1 cache key 和 descriptor 构建抽到可直接测试的 helper，并修正 cache key 不应受标量值或字典插入顺序影响的问题。

**Requirements:**
- cache key 仅反映资源布局
- descriptor 构建顺序稳定
- cache reuse 行为可直接验证

**Tests Required:**
- ✅ 同布局不同标量值命中同一 cache key
- ✅ 不同资源布局产生不同 key
- ✅ descriptor 列表稳定排序且包含 Uniforms
- ✅ cache 命中时 factory 不重复执行

---

### Task 3: Close DeformCache Resource-Level Test Gap (TASK-102)

**File:** `src/SomeEngine.Render/Pipelines/ClusterRender/DeformDispatchCalc.cs`, `tests/SomeEngine.Tests/Pipelines/DeformCacheTests.cs`  
**Task Definition:** `docs/TASK-DETAIL.md#task-102-deformcache-功能测试`

**Description:**
把 shader 中 `CacheAllocCounter` / `CacheOffsets` / cached-path 边界 的纯逻辑镜像到 CPU helper，并补齐测试。

**Requirements:**
- 显式覆盖 offset 分配顺序
- 覆盖越界 / 长度不匹配边界
- 覆盖 cached-path 精确边界和溢出边界

**Tests Required:**
- ✅ 顺序分配 offset
- ✅ invalid input 抛错
- ✅ exact-fit / overflow 边界
- ✅ cache byte address 公式

---

## Mandatory Workflow: Test-Driven Task Progression

1. **Task 1:** 实现 -> 写测试/修测试 -> **当前任务相关测试全绿**
2. **Task 2:** 实现 -> 写测试 -> **当前任务相关测试全绿**
3. **Task 3:** 实现 -> 写测试 -> **当前任务相关测试全绿**
4. 最后跑 **完整测试集**

**不得跳步。**

---

## Testing Requirements

- **Minimum:** 10+ meaningful assertions across the three tasks
- **Quality bar:** 验证真实行为，不要只验证存在性
- **Required categories:** 同步语义 / cache key / cache reuse / 资源分配边界
- 提交前运行完整测试集

---

## Quality Standards

- 不要把 runtime 逻辑 bug 和 test harness bug 混为一谈
- 不要让 Sig1 cache 依赖字典顺序或标量值
- 纯 helper 测试必须明确说明其 shader mirror 关系
- 文档顺手修正时不要扩大范围

---

## Report Requirements

报告文件：`.dev-workstream/reports/BATCH-04-REPORT.md`

必须包含：
- 全量测试结果
- 每个任务的完成度
- root cause / design decision / deviation
- 顺手修复的文档项

---

## Success Criteria

- [ ] TASK-103 完成
- [ ] TASK-101 完成
- [ ] TASK-102 完成
- [ ] `dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj` 全绿
- [ ] report 已提交

---

## Reference Materials

- `docs/DESIGN.md`
- `docs/TASK-DETAIL.md`
- `.dev-workstream/DEBT-TRACKER.md`
- `.dev-workstream/reviews/BASELINE-REVIEW.md`
