# BATCH-09: Asset + Material RenderWorld 完整改造

**Batch Number:** BATCH-09  
**Tasks:** TASK-309a, TASK-309b, TASK-309c, TASK-309d, TASK-309e  
**Phase:** Phase 3 - Corrective Render Data Model Rework  
**Estimated Effort:** full rework  
**Priority:** HIGH  
**Dependencies:** TASK-304, TASK-307, BATCH-08

---

## Onboarding & Workflow

### Complete Engineering Goal

完成一次完整整改，把当前“mesh asset 直接带默认 material 引用、`Material` 运行时等价单实体、`ClusterPipeline` 直接 query `MaterialSystem.Store`、`BinSpace` 作为材质核心设施”的中间态，完整迁移到新的 authoring/runtime 边界：

- `MeshAsset` 只保留几何和 region 元数据
- `region -> material` 成为 ECS authoring 数据，而不是 asset 依赖
- `MaterialAsset` 仍然只有一种，但直接存储匿名 pass 实体快照
- `GameWorld -> RenderWorld -> prepare/queue` 成为唯一渲染提交流
- 管线仅通过 RenderWorld pass 实体上的强类型 component/tag 决定行为

这是一整块完整整改，不是原型，不是 MVP，不是“先把文档写出来”，也不是对 `TASK-304` / `TASK-307` 的零散修补。

### What Does NOT Count As This Batch

下面这些都不算完成本批：

- 只把 `MeshAsset.default_material_guids` 改名但仍保留 mesh -> material 依赖
- 新增第二种 material asset（如 `CompiledMaterialAsset`）来绕过现有 asset 系统
- 保留 `MaterialSystem.Store` 作为提交真相源，只在外面包一层 extract 外观
- 让 runtime 动态从 shader metadata 临时派生最终 pass，而不是序列化后直接反序列化 pass 实体
- 只更新文档或只补测试，不真正迁移运行时和管线

### Required Reading (IN ORDER)
1. `.dev-workstream/guides/DEV-GUIDE.md`
2. `ONBOARDING.md`
3. `docs/DESIGN.md`
4. `docs/TASK-DETAIL.md` — `TASK-309`
5. `docs/materials/architecture.md`
6. `docs/assets/pipeline_overview.md`
7. `docs/assets/asset_identity.md`
8. `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`
9. `src/SomeEngine.Render/Materials/Material.cs`
10. `src/SomeEngine.Render/Materials/BinSpace.cs`
11. `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
12. `assets/Schema/material_asset.fbs`
13. `assets/Schema/mesh_asset.fbs`

### Source Code Locations
| Area | Path |
|---|---|
| Asset schemas | `assets/Schema/` |
| Asset import / provider | `src/SomeEngine.Assets/Importers/`, `src/SomeEngine.Assets/Pipeline/` |
| ECS authoring components | `src/SomeEngine.Core/ECS/Components/` |
| Material runtime / loaders | `src/SomeEngine.Render/Materials/`, `src/SomeEngine.Render/Assets/` |
| Render pipeline | `src/SomeEngine.Render/Pipelines/`, `src/SomeEngine.Render/Systems/` |
| Runtime / Editor host | `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/` |
| Tests | `tests/SomeEngine.Tests/Assets/`, `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/ECS/` |

### Build & Test Commands
```bash
dotnet build SomeEngine.slnx --no-restore -v minimal
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~Assets"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~Materials"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~Pipelines"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~ECS"
```

### Report Submission
完成后提交到：  
`.dev-workstream/reports/BATCH-09-REPORT.md`

如需提问，创建：  
`.dev-workstream/questions/BATCH-09-QUESTIONS.md`

---

## Context

`TASK-304` 和 `TASK-307` 已经分别把材质系统和 asset pipeline 从旧模型推进到了“可用但边界仍不对”的中间态：

- asset pipeline 已经足够扁平：`source -> importer -> .asset -> manifest -> provider -> runtime object`
- material 已经切到 ECS 路线，但仍保留了 `1 Material = 1 Entity`
- mesh asset 仍然把默认材质作为依赖写入 manifest
- cluster pipeline 仍把 `MaterialSystem.Store` 当成提交真相源
- `BinSpace` / `MaterialSlotBuffer` 仍位于材质层，并混入 stage/bin 派生逻辑

本批的完整工程目标，就是把这套中间态完整翻到新的 authoring/runtime 边界，而不是再追加一层补丁结构。

**Related Tasks:**
- `TASK-304` — 材质 ECS 中间态实现
- `TASK-307` — asset pipeline 扁平化重写
- `DEBT-015` — `asset_identity.md` material identity 叙述漂移
- `DEBT-017` — fallback texture 绑定问题（需在迁移过程中一并校正，不得被新架构遗漏）

---

## Batch Objectives

- 删除 mesh-owned material 依赖，把 `region -> material` 收敛为 ECS authoring 事实来源
- 保持单一 `MaterialAsset`，但让它直接存储匿名 pass 实体快照，而不是单实体材质折叠结果
- 引入 RenderWorld 作为唯一渲染提交真相源，完成 `extract / prepare / queue` 分层
- 把 stage/bin 派生结构下沉到具体管线，彻底切断材质系统对管线细节的假设
- 让 Runtime / Editor / Tests / Docs 全部切到新模型，形成真正完成态

---

## Preparation Work (Non-task)

进入 Task 1 前必须完成下面准备动作，但这些动作不算 task 完成：

1. 盘点所有 `default_material_guids` / `default_material_slots` 使用点
2. 盘点所有 `MaterialSystem.Store.Query` / `MaterialRef` 直接作为提交源的路径
3. 盘点 `MaterialSlotOffset`、`BinSpace`、`MeshMaterialResolver`、`Program.cs` 里的 mesh 默认材质运行时路径
4. 固定迁移前 build/test baseline，避免后续 regression 定位漂移

要求：
- 不得把“盘点完成”当成 batch 成功
- 准备动作后必须立即进入实现任务

---

## Tasks

### Task 1: Mesh Region / Authoring Binding 重构 (TASK-309a)

**File:** `assets/Schema/mesh_asset.fbs`, `src/SomeEngine.Assets/Importers/GltfSourceImporter.cs`, `src/SomeEngine.Assets/Pipeline/MeshAssetProvider.cs`, `src/SomeEngine.Core/ECS/Components/` (UPDATE / REFACTOR / NEW FILES)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309a-mesh-region--authoring-binding-重构`

**Description:**
删除 `MeshAsset` 中的具体 material 引用，把稳定 region/section 元数据留在 mesh asset 内，把 `region -> material` 绑定迁到 ECS authoring 组件。

**Requirements:**
- `MeshAsset` 不再存具体 material guid/path
- glTF 导入保留 region 信息，但 mesh import 不再产出 mesh -> material 依赖
- 引入 ECS authoring 组件，能稳定表达按 region 绑定 material
- 新组件设计不能依赖未来额外 scene asset 层；它本身就是 canonical 数据结构

**Tests Required:**
- ✅ mesh import 后 region 元数据稳定可读
- ✅ `MeshAssetProvider.GetDependencies()` 不再返回 material 依赖
- ✅ ECS authoring 组件可表示多 region -> 多 material 绑定
- ✅ 旧的默认材质路径不再被新的加载/导入链引用

---

### Task 2: MaterialAsset Pass Entity 模型落地 (TASK-309b)

**File:** `assets/Schema/material_asset.fbs`, `assets/Schema/material_instance_asset.fbs`, `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`, `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs`, `src/SomeEngine.Render/Materials/` (UPDATE / REFACTOR / NEW FILES)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309b-materialasset-pass-entity-模型落地`

**Description:**
保持单一 `MaterialAsset` 层级不变，把材质内容改为 `root params + pass entity snapshots`。pass 上的 component/tag 直接作为实体快照被序列化和反序列化。

**Requirements:**
- 不允许新增第二种 material asset 规避复杂度
- pass 不是 feature 容器，而是直接可恢复为 ECS 实体的快照
- component/tag 直接挂在 pass 实体上
- material root params 与 pass-local override 边界明确：共享参数在 material 上，pass 只持有局部差异
- shader metadata 和显式 authoring 都能生成同一份最终 `MaterialAsset`
- runtime 不再临时隐式合并出另一份材质

**Tests Required:**
- ✅ `MaterialAsset` 能 round-trip 多 pass 实体快照
- ✅ 反序列化后 component/tag 直接挂在 pass 实体上
- ✅ material root params 与 pass-local override 解析正确
- ✅ material instance 路线在新模型下仍能覆盖参数且不破坏 pass 实体快照

---

### Task 3: RenderWorld Extract / Prepare / Queue 分层 (TASK-309c)

**File:** `src/SomeEngine.Render/Systems/`, `src/SomeEngine.Render/Materials/`, `src/SomeEngine.Core/ECS/`, `src/SomeEngine.Render/Pipelines/` (NEW FILES / REFACTOR)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309c-renderworld-extract--prepare--queue-分层`

**Description:**
引入 RenderWorld 作为唯一提交真相源，把 authoring 实体展开成 `(source entity, region, material pass)` 级别的执行单元，并建立 extract / prepare / queue 边界。

**Requirements:**
- `GameWorld` 保留 authoring，`RenderWorld` 保留执行态
- extract 能正确处理创建、更新、删除
- 多 region、多 material、多 pass 能正确展开
- RenderWorld pass 实体保留 material pass 上的强类型 component/tag
- 任何管线不得再直接 query `MaterialSystem.Store`

**Tests Required:**
- ✅ 单实体多 region、多 pass 展开数量正确
- ✅ 更新绑定或移除实体时 RenderWorld 同步正确
- ✅ RenderWorld 中的 pass 实体仍能被强类型 query 命中
- ✅ extract 过程中没有把 authoring-only 状态泄漏到 queue/submit 语义

---

### Task 4: 管线派生数据下沉 + ClusterPipeline 迁移 (TASK-309d)

**File:** `src/SomeEngine.Render/Materials/BinSpace.cs`, `src/SomeEngine.Render/Materials/BinQueue.cs`, `src/SomeEngine.Render/Materials/MaterialSlotBuffer.cs`, `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`, `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/` (REFACTOR / MOVE / REPLACE)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309d-管线派生数据下沉与-clusterpipeline-迁移`

**Description:**
把 `BinSpace` / `BinQueue` / `MaterialSlotBuffer` 等 stage/bin 派生结构从材质核心里拿出来，变成具体管线 prepare/queue 阶段的派生数据；并完成 Cluster pipeline 对新 RenderWorld 模型的迁移。

**Requirements:**
- 材质系统不再假设 `gbuffer` / `depthonly` / `overlay` / shader 变量名等任何管线细节
- Cluster pipeline 只通过 RenderWorld pass 实体上的 component/tag 选择提交单元
- 原 `MaterialSlotOffset` 直连模型必须被新区域绑定 + pass 展开路径替代
- 如果保留 `BinSpace` / `BinQueue` / `MaterialSlotBuffer` 名称，也必须明确它们已是 pipeline-owned 派生结构，而不是材质系统核心设施
- 迁移后 Cluster 路径行为和测试必须仍然成立

**Tests Required:**
- ✅ cluster pipeline 能基于 RenderWorld pass 实体形成正确分组/提交
- ✅ 不同 region 绑定不同 material 时 GPU 提交路径正确
- ✅ 多 pass 材质不会依赖固定 pass 名字也能被管线消费
- ✅ 现有 pipelines/materials 相关测试迁移并保持通过

---

### Task 5: Host / Test / Legacy 路径清理 (TASK-309e)

**File:** `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/`, `src/SomeEngine.Render/Assets/MeshMaterialResolver.cs`, `tests/SomeEngine.Tests/`, `docs/` (REFACTOR / DELETE / UPDATE)  
**Task Definition:** `docs/TASK-DETAIL.md#task-309e-host--test--legacy-路径清理`

**Description:**
迁移 Runtime / Editor / Tests / Docs 到新模型，删除 mesh 默认材质路径、单实体材质提交路径以及其它 legacy 结构，形成 batch 完成态。

**Requirements:**
- 删除或停用 `MeshMaterialResolver` 和 mesh 默认材质运行时路径
- Runtime / Editor 启动路径按 ECS authoring + RenderWorld extract 工作
- 与旧模型绑定的 helper / dead path / transitional API 一并清理
- 文档同步到“asset 扁平化 + material pass 实体 + RenderWorld 提交”模型
- `DEBT-017` 中的 fallback texture 绑定不得在新模型中继续遗漏

**Tests Required:**
- ✅ full build 通过
- ✅ 全量测试通过
- ✅ 相关 Assets / Materials / Pipelines / ECS 套件都通过
- ✅ runtime 启动路径至少完成一次 smoke 验证并记录结果

---

## Mandatory Workflow: Test-Driven Task Progression

**必须严格顺序推进，不得跳步：**

1. **TASK-309a**：实现 → 写测试 → 当前相关测试全部通过  
2. **TASK-309b**：实现 → 写测试 → 当前相关测试全部通过  
3. **TASK-309c**：实现 → 写测试 → 当前相关测试全部通过  
4. **TASK-309d**：实现 → 写测试 → 当前相关测试全部通过  
5. **TASK-309e**：实现 → 写测试 → 当前相关测试全部通过  

**当前任务未绿，不得进入下一个任务。**

---

## Testing Requirements

- **Minimum:** Assets / Materials / Pipelines / ECS 至少各有一组真实行为测试被新增或重写
- **Quality bar:** 验证真实输出和真实迁移行为，不允许只验证对象存在、字符串存在或“编译通过”
- **Required categories:** schema / provider / loader round-trip、extract 行为、pipeline grouping、边界条件、删除/更新路径
- 提交前必须运行完整相关测试集
- 文档和测试是交付要求，不是独立 task

---

## Quality Standards

- 不增加 asset 系统层级复杂度
- 不新增第二种 material asset
- 不允许 runtime 隐式合并出未落盘的最终材质
- component/tag 必须直接挂在 pass 实体上
- 管线代码只能从 RenderWorld query 自己关心的 pass 实体
- 不得留下“旧模型和新模型双真相源”并存状态
- 不允许以 TODO/FIXME 作为收尾
- 不允许新增 build/test failure 或明显 warning

---

## Report Requirements

报告文件：`.dev-workstream/reports/BATCH-09-REPORT.md`

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
1. 这次整改中最难的迁移边界是什么，最终怎么收敛的？
2. 哪些旧类型/旧 API 被整块删除，为什么必须删？
3. `RenderWorld` 最终选择了哪些最小概念，哪些概念被故意留在具体管线里？
4. 哪些地方看起来还能继续拆，但本批故意没有再扩 scope？

---

## Success Criteria

- [ ] TASK-309a 完成
- [ ] TASK-309b 完成
- [ ] TASK-309c 完成
- [ ] TASK-309d 完成
- [ ] TASK-309e 完成
- [ ] `MeshAsset` 不再引用具体 material
- [ ] `MaterialAsset` 成为唯一材质资产，并直接存储 pass 实体快照
- [ ] RenderWorld 成为唯一渲染提交真相源
- [ ] 管线只 query pass 实体上的 component/tag
- [ ] 所有要求测试完成并通过
- [ ] 完整整改已经落地
- [ ] 报告已提交

---

## Reference Materials

- `docs/DESIGN.md`
- `docs/TASK-DETAIL.md` — `TASK-309`
- `docs/materials/architecture.md`
- `docs/assets/pipeline_overview.md`
- `docs/assets/asset_identity.md`
- `.dev-workstream/DEBT-TRACKER.md`
- `.dev-workstream/reports/BATCH-07c-REPORT.md`
- `.dev-workstream/reports/BATCH-08-REPORT.md`
