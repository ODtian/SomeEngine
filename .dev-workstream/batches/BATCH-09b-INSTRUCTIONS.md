# BATCH-09b: Mesh Local Material Table + Zero-GC Cluster Prepare Rework

**Batch Number:** BATCH-09b  
**Tasks:** TASK-309f, TASK-309g, TASK-309h, TASK-309i  
**Phase:** Phase 3 - Corrective Render Submission Model Cleanup  
**Estimated Effort:** medium-large  
**Priority:** HIGH  
**Dependencies:** BATCH-09

---

## Onboarding & Workflow

### Complete Engineering Goal

完成一次完整 corrective rework，把 BATCH-09 留下的中间态继续收紧到真正稳定的 cluster 提交模型：

- `region` 不再作为 cluster runtime 的运行时概念
- mesh 运行时绑定语义收敛为稳定的 mesh-local material linear table / local material slot
- RenderWorld 只保留 `(source entity, local material slot, material pass)` 级别的提交事实
- `RenderWorldMaterialSlotSynchronizer` 与 `ClusterPipelineSlotBindingBuilder` 这类桥接层被删除
- slot / bin folding 收敛到 cluster pipeline 自己的 prepare 路径
- 所有每帧调用热点路径满足两条硬约束：
  - 禁止产生 GC
  - 禁止使用 `Dictionary`

这是一整块完整 corrective rework，不是命名清理，不是局部热修，也不是“先把旧桥接层包起来以后再说”。

### What Does NOT Count As This Batch

下面这些都不算完成本批：

- 只把 `RegionIndex` 改名为 `LocalMaterialIndex`，但实际 prepare/queue 仍按 region 回拼
- 保留 `RenderWorldMaterialSlotSynchronizer` / `ClusterPipelineSlotBindingBuilder`，只换个名字或外壳
- 把 region 映射搬进另一张表、另一组 helper，继续让 cluster runtime 理解 region
- 在任何每帧调用路径上继续使用 `Dictionary`
- 在每帧调用路径上继续保留 `new[]`、`List<>`、`ToArray()`、LINQ、`Array.Resize()` 之类 steady-state 托管分配
- 保留 `_renderWorld ?? _materialSystem.Store` 这种长期双真相源 fallback
- 只补文档或只补测试，不真正迁移运行时路径

### Required Reading (IN ORDER)
1. `.dev-workstream/guides/DEV-GUIDE.md`
2. `ONBOARDING.md`
3. `docs/DESIGN.md`
4. `docs/TASK-DETAIL.md` - `TASK-309`, `TASK-309f`, `TASK-309g`, `TASK-309h`, `TASK-309i`
5. `.dev-workstream/batches/BATCH-09-INSTRUCTIONS.md`
6. `.dev-workstream/reports/BATCH-09-REPORT.md`
7. `src/SomeEngine.Render/Components/MeshMaterialBindingsComponent.cs`
8. `src/SomeEngine.Render/Components/RenderWorldComponents.cs`
9. `src/SomeEngine.Render/Systems/RenderWorldExtractor.cs`
10. `src/SomeEngine.Render/Systems/RenderWorldMaterialSlotSynchronizer.cs`
11. `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipelineSlotBindingBuilder.cs`
12. `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
13. `assets/Shaders/cluster_structures.slang`
14. `assets/Shaders/cluster_binning.slang`
15. `assets/Shaders/cluster_shade_binning.slang`

### Source Code Locations
| Area | Path |
|---|---|
| Authoring binding model | `src/SomeEngine.Render/Components/`, `src/SomeEngine.Assets/Importers/`, `assets/Schema/` |
| RenderWorld extract | `src/SomeEngine.Render/Systems/` |
| Cluster prepare / slot folding | `src/SomeEngine.Render/Pipelines/ClusterRender/`, `src/SomeEngine.Render/Materials/` |
| Shaders | `assets/Shaders/` |
| Runtime / Editor host | `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs` |
| Tests | `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Systems/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/Assets/` |

### Build & Test Commands
```bash
dotnet build SomeEngine.slnx --no-restore -v minimal
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~RenderWorld"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~BinSpace"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~ClusterPipeline"
```

### Report Submission
完成后提交到：  
`.dev-workstream/reports/BATCH-09b-REPORT.md`

如需提问，创建：  
`.dev-workstream/questions/BATCH-09b-QUESTIONS.md`

---

## Context

BATCH-09 完成了 RenderWorld 第一阶段重构，但交付结果仍存在三类核心问题：

1. **语义问题**  
   `MeshMaterialBindings` 与 RenderWorld 组件仍然沿用 `region` 语义，而 shader 和 GPU header 真正消费的是 `local material index + MaterialSlotOffset`。

2. **边界问题**  
   `RenderWorldMaterialSlotSynchronizer` 与 `ClusterPipelineSlotBindingBuilder` 仍然在 extract 之后做一次 `source + region` 回扫拼装，说明 extract 没有把提交事实收敛干净。

3. **运行时质量问题**  
   当前桥接路径在每帧调用路径上仍有 `Dictionary`、排序、动态扩容与托管分配风险，不满足 steady-state frame loop 的工程约束。

本批的目标不是推翻 BATCH-09 的全部结果，而是把它从“中间态”继续推进到真正稳定的运行时提交模型。

---

## Batch Objectives

- 把运行时绑定模型从 `region -> material` 收敛为 mesh-local material linear table / local material slot
- 让 RenderWorld 只保留 cluster prepare 需要的 submission facts，不再承载 region 回拼桥接信息
- 删除 `RenderWorldMaterialSlotSynchronizer` / `ClusterPipelineSlotBindingBuilder`
- 让 `BinSpace` / `BinQueue` / `MaterialSlotBuffer` 继续作为 cluster pipeline 自己的派生数据，但其 prepare 路径不再依赖 region 映射
- 对所有每帧调用热点路径施加 zero-GC / no-Dictionary 硬门槛
- 同步 Runtime / Editor / Tests / Docs / Workstream 到新语义

---

## Preparation Work (Non-task)

进入 Task 1 前必须完成下面准备动作，但这些不算 task 完成：

1. 盘点所有仍把 `region` 当运行时提交概念使用的代码和文档
2. 盘点所有每帧调用路径上的 `Dictionary`、`List<>`、`ToArray()`、LINQ、`Array.Resize()`、排序逻辑
3. 确定 extract / prepare / queue 的热点调用链以及后续 allocation regression 测试落点
4. 固定迁移前 build/test baseline，避免后续 regression 定位漂移

要求：
- 不得把“盘点完成”当成 batch 成功
- 准备动作后必须立即进入实现任务

---

## Tasks

### Task 1: Mesh Local Material Table Semantics Cleanup (TASK-309f)

**File:** `src/SomeEngine.Render/Components/MeshMaterialBindingsComponent.cs`, `src/SomeEngine.Render/Components/RenderWorldComponents.cs`, `assets/Schema/`, `src/SomeEngine.Assets/Importers/`, `docs/` (UPDATE / REFACTOR)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309f-mesh-local-material-table-semantics-cleanup`

**Description:**
把 `region` 从 cluster runtime 语义中拿掉，收敛为稳定 mesh-local material linear table / local material slot 模型。

**Requirements:**
- `MeshMaterialBindings` 的 canonical 语义必须是稳定 local material 顺序
- cluster runtime 不再把 `region` 当作运行时映射键
- 如仍需保留 region 元数据，只能停留在 asset/import/debug 层，不能参与 runtime slot folding
- 命名、注释、文档必须同步，避免继续把 local material table 叫成 region 映射

**Tests Required:**
- mesh/import/authoring 路径能稳定产出 local material 顺序
- 多材质 mesh 的 local material slot 顺序在 round-trip 后保持稳定
- 任何 cluster runtime 热点路径都不再依赖 `RegionIndex` 语义

---

### Task 2: RenderWorld Extract Local-Slot Rework (TASK-309g)

**File:** `src/SomeEngine.Render/Systems/`, `src/SomeEngine.Render/Components/`, `tests/SomeEngine.Tests/Systems/`, `tests/SomeEngine.Tests/Pipelines/` (REFACTOR / REPLACE)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309g-renderworld-extract-local-slot-rework`

**Description:**
RenderWorld 只展开 `(source entity, local material slot, material pass)` 提交事实，不再为 region 映射保留桥接结构。

**Requirements:**
- extract 正确处理创建、更新、删除，以及多 local material slot / 多 pass 展开
- RenderWorld pass entity 仍能被强类型 component/tag query 直接消费
- extract 路径不得再为后续 slot folding 额外保留 region mapping 中间态
- 每帧 extract 路径禁止 `Dictionary`
- steady-state extract 调用不得产生托管分配

**Tests Required:**
- 多 material slot / 多 pass 展开数量与内容正确
- source entity 更新、解绑、删除时 RenderWorld 同步正确
- extract 热点路径 allocation regression 为 0 bytes（稳态）

---

### Task 3: Cluster Prepare Zero-GC Slot Folding (TASK-309h)

**File:** `src/SomeEngine.Render/Pipelines/ClusterRender/`, `src/SomeEngine.Render/Materials/`, `src/SomeEngine.Render/Systems/`, `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Pipelines/` (REFACTOR / DELETE / REPLACE)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309h-cluster-prepare-zero-gc-slot-folding`

**Description:**
删除 `RenderWorldMaterialSlotSynchronizer` 和 `ClusterPipelineSlotBindingBuilder`，把 slot folding 直接收敛到 cluster pipeline 自己的 prepare 路径。

**Requirements:**
- `BinSpace` / `BinQueue` / `MaterialSlotBuffer` 继续是 cluster pipeline 派生数据，而不是 material system 核心设施
- slot folding 直接从 extracted local material slot 顺序生成，不再做 `source + region` 回扫
- `RenderWorldMaterialSlotSynchronizer.cs` 必须删除
- `ClusterPipelineSlotBindingBuilder.cs` 必须删除
- 所有每帧调用热点路径禁止 `Dictionary`
- 所有每帧调用热点路径 steady-state 不得产生托管分配
- 不允许把分配或映射逻辑藏到新的 table / helper / cache 外壳里继续保留同样问题

**Tests Required:**
- cluster pipeline 仍能生成正确 slot / bin / dispatch 提交结果
- primary shade / overlay / vertex eval 选择逻辑在新 prepare 模型下正确
- slot folding 热点路径 allocation regression 为 0 bytes（稳态）
- code review 需明确确认每帧路径没有 `Dictionary`

---

### Task 4: Host / Test / Docs / Legacy Cleanup And Guardrails (TASK-309i)

**File:** `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `tests/SomeEngine.Tests/`, `docs/`, `.dev-workstream/` (UPDATE / DELETE / REFACTOR)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309i-host-test-docs-legacy-cleanup-and-guardrails`

**Description:**
同步 Runtime / Editor / Tests / Docs 到新语义，并补上 hot-path 守护，形成 BATCH-09b 完成态。

**Requirements:**
- Runtime / Editor 启动链路按新 extract / prepare 边界工作
- 不保留旧 region-centric 术语与 bridge 类型作为长期兼容层
- 文档、tracker、report、review 术语同步到 local material table / local material slot
- 为热点路径补 allocation regression 测试与批次级质量门槛说明

**Tests Required:**
- full build 通过
- 全量测试通过
- RenderWorld / Materials / ClusterPipeline focused suites 通过
- 至少有一组稳态零分配回归测试覆盖 extract 或 prepare 热点链路

---

## Mandatory Workflow: Test-Driven Task Progression

**必须严格顺序推进，不得跳步：**

1. **TASK-309f**：实现 -> 写测试 -> 当前相关测试全部通过  
2. **TASK-309g**：实现 -> 写测试 -> 当前相关测试全部通过  
3. **TASK-309h**：实现 -> 写测试 -> 当前相关测试全部通过  
4. **TASK-309i**：实现 -> 写测试 -> 当前相关测试全部通过  

**当前任务未绿，不得进入下一个任务。**

---

## Testing Requirements

- **Minimum:** Materials / Systems / Pipelines 至少各有一组真实行为测试被新增或重写
- **Allocation guard:** 必须新增稳态 allocation regression 测试，验证 extract 或 prepare 热点路径为 0 bytes
- **Hot-path rules:** 每帧调用路径必须在 code review 和测试说明中明确列出，不允许模糊处理
- **No-Dictionary rule:** 每帧调用路径禁止 `Dictionary`，这不是建议，是硬约束
- **No-GC rule:** 每帧调用路径 steady-state 0 GC，这不是优化项，是完成条件

---

## Quality Standards

- 不引入新的 region mapping 中间层
- 不把 region 语义换个名字后继续留在 cluster runtime
- 不在每帧调用路径上使用 `Dictionary`
- 不在每帧调用路径上引入 steady-state 托管分配
- 不通过新的 table / helper 外壳掩盖旧桥接结构
- 不保留 RenderWorld 和旧 material store 双真相源作为长期状态
- 不允许以 TODO/FIXME 收尾
- 不允许新增 build/test failure 或新 warning

---

## Report Requirements

报告文件：`.dev-workstream/reports/BATCH-09b-REPORT.md`

必须包含：
- Summary
- Files Modified
- Task Check
- Test Results
- Issues Encountered
- Design Decisions
- Deviations
- Edge Cases
- Weak Points / Improvement Opportunities
- Known Issues

建议问题：
1. 哪些旧结构证明 `region` 其实不是 cluster runtime 概念，最终怎么收敛到 local material table？
2. 哪些桥接类被删除了，删除后 slot folding 放到了哪里？
3. 哪些路径被明确判定为每帧热点，如何验证它们 steady-state 0 GC？
4. 为什么 batch9b 明确禁止 `Dictionary`，对应替代方案是什么？

---

## Success Criteria

- [ ] TASK-309f 完成
- [ ] TASK-309g 完成
- [ ] TASK-309h 完成
- [ ] TASK-309i 完成
- [ ] `region` 不再作为 cluster runtime 运行时概念
- [ ] `RenderWorldMaterialSlotSynchronizer.cs` 已删除
- [ ] `ClusterPipelineSlotBindingBuilder.cs` 已删除
- [ ] cluster prepare 路径直接基于 local material slot 进行 slot folding
- [ ] 每帧调用热点路径满足 zero-GC
- [ ] 每帧调用热点路径不使用 `Dictionary`
- [ ] 全量 build / tests 通过
- [ ] `BATCH-09b-REPORT.md` 已提交

---

## Reference Materials

- `docs/DESIGN.md`
- `docs/TASK-DETAIL.md`
- `.dev-workstream/batches/BATCH-09-INSTRUCTIONS.md`
- `.dev-workstream/reports/BATCH-09-REPORT.md`
- `src/SomeEngine.Render/Systems/RenderWorldExtractor.cs`
- `src/SomeEngine.Render/Systems/RenderWorldMaterialSlotSynchronizer.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipelineSlotBindingBuilder.cs`
- `assets/Shaders/cluster_structures.slang`
- `assets/Shaders/cluster_binning.slang`
- `assets/Shaders/cluster_shade_binning.slang`
