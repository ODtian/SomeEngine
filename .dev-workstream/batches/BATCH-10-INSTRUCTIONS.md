# BATCH-10: Render Prepare And Cache Lifecycle Rework

**Batch Number:** BATCH-10
**Tasks:** TASK-310a, TASK-310b, TASK-310c, TASK-310d
**Phase:** Phase 3 - Corrective Render Pipeline Cleanup
**Priority:** HIGH
**Dependencies:** BATCH-09b

---

## Onboarding & Workflow

### Complete Engineering Goal

完成一次完整 corrective rework，把 material authoring/baker、RenderWorld extract、cluster prepare、PSO/SRB cache 生命周期收敛到同一条清晰链路：

- material pass entity 是 feature component/tag 的唯一 authoring/runtime 来源
- extract 只把 material pass entity 拷贝到 render world 一次，不再二次 bake
- `PrepareFrame` 是 CPU 派生数据准备生命周期
- `AddPasses` 只声明 RenderGraph pass，不重建 bin/PSO group
- `Execute` 不做 PSO/SRB/cache resolve
- PSO 属于 device-scope cache，SRB 属于 material/pipeline prepared group 生命周期
- 禁止反射，禁止 material loader 硬编码 feature/component 白名单

### What Does NOT Count As This Batch

- 在 extract 之后再跑一次 baker
- 给 material baker 增加专用 resolver/generator/reflection
- 在 `MaterialAssetLoader` 里继续写死具体 feature/component 类型
- 在 `AddPasses` 或 pass execute 中做 bin rebuild、PSO group rebuild、shader lookup
- 用新的 helper/table/cache 外壳保留旧的双真相源
- 只写文档不改运行时链路

---

## Required Reading

1. `src/SomeEngine.Render/Materials/IBaker.cs`
2. `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`
3. `src/SomeEngine.Render/Materials/Material.cs`
4. `src/SomeEngine.Render/Systems/MaterialPassBaker.cs`
5. `src/SomeEngine.Render/Systems/RenderWorldExtractor.cs`
6. `src/SomeEngine.Render/Materials/BinSpace.cs`
7. `src/SomeEngine.Render/Pipelines/ClusterRender/SlotPreparer.cs`
8. `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
9. `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`
10. `src/SomeEngine.Render/Pipelines/ClusterRender/RasterPSOBuilder.cs`
11. `src/SomeEngine.Render/Pipelines/GlobalPsoCache.cs`
12. `src/SomeEngine.Render/Pipelines/SRBPool.cs`
13. `src/SomeEngine.Assets/AssetDatabase.cs`

---

## Source Code Locations

| Area | Path |
|---|---|
| Material pass authoring/baker | `src/SomeEngine.Render/Materials/`, `src/SomeEngine.Render/Assets/` |
| RenderWorld extract | `src/SomeEngine.Render/Systems/` |
| Cluster prepare/bin/PSO groups | `src/SomeEngine.Render/Pipelines/ClusterRender/`, `src/SomeEngine.Render/Materials/` |
| Cache lifecycle | `src/SomeEngine.Render/Pipelines/`, `src/SomeEngine.Render/RHI/`, host `Program.cs` |
| Asset pipeline no-reflection guard | `src/SomeEngine.Assets/` |
| Tests | `tests/SomeEngine.Tests/Systems/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Assets/` |

---

## Build & Test Commands

```bash
dotnet build SomeEngine.slnx --no-restore -v minimal
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~MaterialPassBaker|FullyQualifiedName~RenderWorldExtractor|FullyQualifiedName~SlotPreparer|FullyQualifiedName~BinSpace|FullyQualifiedName~ShaderGroup|FullyQualifiedName~AssetDatabase|FullyQualifiedName~GeneratedAssetPipelineCatalog"
```

Per project instruction, run `dotnet build/test` outside the sandbox if restore or SDK setup is needed.

---

## Context

本批收敛前的痛点：

- `MaterialPassBaker` 只创建空 render entity，导致材质 pass 上的 ECS tags/components 没有进入 render world。
- `Material.Instantiate()` 只复制 `MaterialRef`，实例材质丢失父材质 pass feature。
- `RenderWorldExtractor` 只 hash source/material guid/pass count，不感知 material/pass version，热更新或 feature 变化可能不触发 extract。
- `SlotPreparer` 把所有 bin field 指向第一个 pass entity，raster/shade/deform 语义丢失。
- `BinSpace.RebuildIfDirty()` 实际每次强制 rebuild，`AddPasses` 仍在做 CPU prepare 工作。
- `ClusterPipeline.RebuildPSOGroups()` 当前清空 PSO groups，管线无法从 render world query 消费 feature 集合。
- `GlobalPsoCache` / `SRBPool` 生命周期不清晰，host 侧创建的 PSO cache 没有统一释放。
- `AssetDatabase` 通过 `MakeGenericType` / `Activator.CreateInstance` 建 store，违反项目禁止反射约束。

---

## Batch Objectives

- 用 ECS `CopyEntity` 完成 pass entity 到 render world 的直接复制，extract 不再二次 bake。
- 让 material instance 复制 pass feature，并通过 material revision 驱动 RenderWorld 变化检测。
- 把 cluster CPU 准备统一放进 `PrepareFrame`，包括 render world extract、feature query、slot folding、bin rebuild、PSO group rebuild。
- 让 `AddPasses` 只消费 prepared `BinSpace` / `MaterialPSOGroup[]` 并声明 RenderGraph pass。
- 给 `BinSpace` 增加真实 dirty 生命周期，避免每帧无条件 rebuild。
- 恢复 cluster pipeline 按 feature component/tag query 生成 shade/raster/deform PSO groups。
- 去掉 production 反射注册，asset provider store 由 typed provider 自己完成。
- 补齐行为测试，覆盖 pass feature copy、instance feature preserve、prepare dirty、no-reflection store。

---

## Tasks

### Task 1: One-Copy Material Pass Extract (TASK-310a)

**Files:** `Material.cs`, `MaterialPassBaker.cs`, `RenderWorldExtractor.cs`

**Requirements:**
- render world entity 必须直接拷贝 source material pass entity 的 components/tags。
- `RenderSourceEntity` / `RenderMaterialSlotBinding` 只能作为 render world 元数据追加。
- `Material.Instantiate()` 必须保留 pass ECS features，并把 `MaterialRef.Owner` 改为实例。
- `RenderWorldExtractor` 必须把 material revision 纳入 frame hash。

**Tests Required:**
- material pass tags/components 能被 render world query 到。
- material instance 保留 pass feature。
- material revision 变化会触发 render world rebuild。

### Task 2: PrepareFrame Owns Derived CPU Data (TASK-310b)

**Files:** `BinSpace.cs`, `SlotPreparer.cs`, `ClusterPipeline.cs`

**Requirements:**
- `BinSpace.RebuildIfDirty()` 只在 dirty 时 rebuild。
- slot folding 变化必须 mark dirty。
- `PrepareFrame` 完成 extract、feature query、slot folding、bin rebuild、PSO group rebuild。
- `AddPasses` 不再调用 `RebuildIfDirty()` / `RebuildPSOGroups()`。

**Tests Required:**
- steady-state prepare 不提升 `BinSpace.Version`。
- slot binding 能按 raster/shade/deform 不同 feature 指向不同 pass entity。

### Task 3: Feature Query Driven PSO Groups (TASK-310c)

**Files:** `ClusterPipeline.cs`, `ClusterShade.cs`, `RasterPSOBuilder.cs`, `MaterialPSOGroup.cs`

**Requirements:**
- cluster pipeline 在 render world 上 query `ClusterShadeComponent` / `ClusterRaster` / `ClusterDeform` feature 集合。
- PSO group 只在 prepared bin version 变化后重建。
- shade/raster/deform 使用各自 feature entity，不再共享错误代表实体。
- `Execute` 热路径不做 shader/PSO/SRB resolve。

**Tests Required:**
- feature query group 能产生对应 bin。
- PSO group CPU grouping 保持按 shader variant 连续合并。

### Task 4: Cache Lifecycle And No-Reflection Cleanup (TASK-310d)

**Files:** `GlobalPsoCache.cs`, `SRBPool.cs`, `ShaderExtensions.cs`, `AssetDatabase.cs`, host `Program.cs`

**Requirements:**
- production asset database 不使用 `MakeGenericType` / `Activator.CreateInstance`。
- PSO cache 明确由 host 或 owner 释放。
- SRB pool 提供 dispose 生命周期。
- cached shader wrapper 不在 PSO 创建后立即 `Dispose()`。

**Tests Required:**
- asset database typed load/cache 路径仍通过。
- focused render/material/assets tests 通过。

---

## Testing Requirements

- Focused tests 必须覆盖 Systems / Materials / Pipelines / Assets。
- 编译必须通过。
- 不允许新增反射点。
- 不允许新增 material feature hardcode 到 `MaterialAssetLoader`。
- 不允许用兼容 fallback 保留旧路径。

---

## Success Criteria

- [ ] TASK-310a 完成
- [ ] TASK-310b 完成
- [ ] TASK-310c 完成
- [ ] TASK-310d 完成
- [ ] extract 不再二次 bake
- [ ] render world pass entity 保留 material pass feature tags/components
- [ ] `PrepareFrame` 是唯一 CPU derived data 准备点
- [ ] `AddPasses` 不再 rebuild bin/PSO groups
- [ ] production 代码无 `MakeGenericType` / `Activator.CreateInstance`
- [ ] focused tests 通过
- [ ] `BATCH-10-REPORT.md` 已提交
