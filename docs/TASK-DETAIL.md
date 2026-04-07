# TASK-DETAIL

## Phase 0: Baseline & Documentation

### TASK-001: 同步 cluster_pipeline.md
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/rendering/cluster_pipeline.md`  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-1,2,3`

**Goal:** 修正 GPUCluster 大小描述 (48B→64B)、GpuInstanceHeader 字段描述、Stage 列表 (10→9)

**Current Evidence:**
- `src/SomeEngine.Assets/Data/GPUCluster.cs` — 64 bytes
- `src/SomeEngine.Render/Data/InstanceMetadata.cs` — MaterialSlotOffset
- `Stages/` 目录 — 9 个文件，无 ClusterShadeBinStage/ClusterShadeStage

**Work Required:**
- 更新 GPUCluster 大小和字段描述
- 更新 GpuInstanceHeader 字段（MaterialID→MaterialSlotOffset）
- 修正 Stage 列表为 9 个，说明 ClusterShade 是合并的静态编排器

**Success Conditions:**
- 文档内容与代码一致
- 无过时字段描述

---

### TASK-002: 修正 materials/architecture.md MaterialSlot 残留
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/materials/architecture.md`  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-4`

**Goal:** 移除/更新 `struct MaterialSlot` 定义，改为描述 SOA `ushort[]` 布局

**Work Required:**
- 替换 MaterialSlot struct 代码块为 SOA 布局说明
- 更新 GPU 查找路径代码示例（`MaterialSlotBuffer` → `GetSlotField`）

**Success Conditions:**
- 文档与 `MaterialSlotBuffer.cs` SOA 实现一致

---

### TASK-003: 归档 material_pipeline_full_chain.md
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/rendering/material_pipeline_full_chain.md`  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-5`

**Goal:** 将该文档移入 `docs/archive/`，或在顶部标注为"未来计划"

**Current Evidence:**
- 文档描述 friflo Entity 模型（ShaderEntry/IComponent/MaterialStore）
- 代码中 `ShaderEntry`、`MaterialStore`、`ClusterSWRaster` 等类型均不存在

**Work Required:**
- `git mv docs/rendering/material_pipeline_full_chain.md docs/archive/`
- 更新 `docs/README.md` 索引

**Success Conditions:**
- 文档不再出现在活跃文档区

---

### TASK-004: 同步 shading_pipeline.md 到当前 ClusterShade 实现
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/rendering/shading_pipeline.md`  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-6`

**Goal:** 将 `ClusterShadeBinStage` / `ClusterShadeStage` 残留替换为 `ClusterShade.cs`，并把已落地的 PSO 分组基础设施写清楚

**Work Required:**
- 更新实现文件列表
- 记录 `ShadePSOGroup.ComputeShaderGroups()` 已落地
- 保留 `Sig1` 缓存测试仍待补齐的现状

---

### TASK-005: 同步 rasterization.md 的 DeformCache 命名
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/rendering/rasterization.md`  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-7`

**Goal:** 将主文档中的 `DeformedBuffer` / `Pre-Deform` 主命名更新为 `DeformCache`

---

### TASK-006: 修正 overview.md 遍历模型描述
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/rendering/overview.md`  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-8`

**Goal:** 在高层概览中明确“当前实现 = Queue-Driven Multi-Dispatch，Persistent Threads = 未来计划”

---

### TASK-007: 删除残留文件
**Type:** corrective  
**Status:** DONE  
**Scope:** `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterRenderPass.cs.bak`
**Design Ref:** `BASELINE-REVIEW.md#Risk3`

**Goal:** 删除 80KB 残留文件

---

### TASK-008: 整理 rendering/ 下计划文档状态
**Type:** document  
**Status:** DONE  
**Scope:** `docs/rendering/gpu_pipeline_tag_integration_plan.md`, `docs/rendering/persistent_thread_bvh_traversal.md`

**Goal:** 合并旧计划文档并保留仍有效的未来方案文档

---

### TASK-009: 删除废弃的 ClusterRenderFeature.cs
**Type:** corrective  
**Status:** DONE  
**Scope:** `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterRenderFeature.cs` (91KB)  
**Design Ref:** `BASELINE-REVIEW.md#DEBT-001`

**Goal:** 删除已废弃的 91KB 文件

**Current Evidence:**
- `ClusterPipeline.cs` 是当前管线入口
- 共享类型已提取到 `ClusterPipelineTypes.cs`

**Success Conditions:**
- 文件已删除
- 编译通过
- 无悬挂引用

---

## Phase 1: Review Existing Core

### TASK-101: Dual-Signature 绑定测试覆盖
**Type:** review  
**Status:** DONE  
**Scope:** `src/Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`  
**Design Ref:** `BASELINE-REVIEW.md#DEBT-007`

**Goal:** 为 Sig0/Sig1/s_sig1Cache 静态缓存补充测试

**Current Evidence:**
- `tests/SomeEngine.Tests/Pipelines/ShaderGroupTests.cs` 覆盖 group 形成路径
- 新增 `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs`
- `ClusterShade` 现已将 Sig1 cache key 固定为资源布局 hash，并直接覆盖 layout 排序、cache 复用和 descriptor 构建逻辑

**Success Conditions:**
- 覆盖 `BuildPSOGroups()` 的 group 形成路径
- 覆盖 `GetOrCreateSig1()` 的 signatureHash 去重和缓存复用
- 覆盖多 shader + 多 bin + 重复材质签名场景

---

### TASK-102: DeformCache 功能测试
**Type:** review  
**Status:** DONE  
**Scope:** `ClusterDeformPass.cs`, `cluster_deform.slang`  
**Design Ref:** `BASELINE-REVIEW.md#DEBT-008`

**Goal:** 补充 DeformCache 的功能测试（除已有编译测试外）

**Current Evidence:**
- 已提取 `DeformDispatchCalc` 并有 `tests/SomeEngine.Tests/Pipelines/DeformCacheTests.cs`
- 当前测试覆盖 dispatch 参数计算、索引解码、flat index round-trip
- 已新增 `CacheAllocCounter` / `CacheOffsets` 分配、cached-path 边界和 byte address 镜像测试

**Work Required:**
- 测试 `CacheAllocCounter` 原子分配
- 测试 `CacheOffsets` 寻址
- 测试 Inline vs Cached 路径切换

---

### TASK-103: 修复 ECS Transform 旋转层级传播回归
**Type:** corrective  
**Status:** DONE  
**Scope:** `src/SomeEngine.Core/ECS/Systems/TransformSystem.cs`, `src/SomeEngine.Core/Math/TransformQvvs.cs`, `tests/SomeEngine.Tests/ECS/TransformSystemTests.cs`  
**Design Ref:** `docs/core/ecs_design.md`, `BASELINE-REVIEW.md`

**Goal:** 恢复父节点旋转对子节点世界坐标的传播，确保 `TransformSystemTests.TestRotation` 通过

**Current Evidence:**
- 根因定位为 `TransformSystemTests` 直接调用 `SystemRoot.Update()` 后未 `Complete()` job dependency
- `GameWorld.Update()` 路径本身正确，`EcsTests.TestRotationHierarchy` 一直通过
- 测试已改为在断言前完成 `_context.GlobalDependency`
- 全量测试当前结果：`191 passed, 0 failed`

**Success Conditions:**
- `TransformSystemTests.TestRotation` 通过
- 不破坏 `TestHierarchyAndTransform` 等已有层级测试
- 全量测试恢复为 `0 failed`

---

## Phase 2: Hardening & Debt Reduction

### TASK-201: 补 ECS/QVVS/Source Generator/VRB 独立文档
**Type:** document  
**Status:** DONE  
**Scope:** `src/Core/ECS/`, `src/Core/Math/`, `src/Generators/`, VRB in `ClusterBuilder.cs`  
**Design Ref:** `BASELINE-REVIEW.md#DEBT-009`

**Goal:** 各补 1 份简明文档

---

### TASK-202: 资产管线总览文档
**Type:** document  
**Status:** DONE  
**Scope:** `docs/assets/`  
**Design Ref:** `docs/README.md` "资产管线总览 — *待编写*"

**Goal:** 产出资产管线完整流程文档

---

## Phase 3: Next Features

### TASK-301: 光照系统强化
**Type:** feature  
**Status:** TODO  
**Scope:** `assets/Shaders/brdf.slang`, 新增 light 管理  
**Design Ref:** `docs/goal.md` — "virtual shadow map", "megalight"

### TASK-302: Page 流式加载
**Type:** feature  
**Status:** TODO  
**Scope:** `src/Render/Systems/ClusterStreamer.cs`  
**Design Ref:** `docs/future/page_streaming.md`

### TASK-303: Tessellation
**Type:** feature  
**Status:** TODO  
**Scope:** 新增  
**Design Ref:** `docs/future/tessellation.md`

---

### TASK-304: Material ECS 重构
**Type:** feature  
**Status:** DONE  
**Scope:** `src/SomeEngine.Render/Materials/`, `src/SomeEngine.Render/Pipelines/ClusterRender/`, `src/SomeEngine.Render/Assets/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `assets/Schema/shader_asset.fbs`, `src/SomeEngine.Assets/Importers/SlangShaderImporter.cs`, `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/Assets/`  
**Design Ref:** `docs/materials/architecture.md`

**Goal:** 删除 `MaterialPass / MaterialRegistry / TagStore<MaterialPass>` 路线，迁移到 friflo `EntityStore`，落地 `1 Material = 1 Entity`、entry-point attribute authoring、BinGroup 动态 region 和 Entity-based 管线消费。

**Current Evidence:**
- `src/SomeEngine.Render/Materials/MaterialPass.cs` 与 `src/SomeEngine.Render/Materials/MaterialRegistry.cs` 已物理删除
- `Material.cs` 已收敛为瘦 Material；`Instantiate()` 会复制 Params 并创建独立 Entity
- `MaterialSystem` 已作为全局材质 `EntityStore` 入口使用
- `shader_asset.fbs` 与 `SlangShaderImporter.cs` 已支持 `entry_point_attributes`
- `MaterialAssetLoader` / `MaterialInstanceLoader` 已按 Entity + `MaterialRef` 路线加载与实例化材质
- `BinQueue` / `BinSpace` / `MaterialSlotCache` 已切换到 Entity 数据源与 `RegisterGroup(BinGroup)` API
- `ClusterPipeline` / `ClusterShade` / `ShadePSOGroup` / `RasterPSOBuilder` / `ClusterMaterialShadePass` 已按 Entity Component 消费材质
- `ShaderGroupTests` / `MaterialAssetPipelineTests` / `AssetResolverTests` / `ClusterShadeSig1Tests` 已迁移到 Entity-based 路线
- 真实验证结果：`dotnet build`（Render / Runtime / Editor）通过，`dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj -- RunConfiguration.MaxCpuCount=1` 结果为 `134 passed, 0 failed, 1 skipped`

**Success Conditions:**
- Material ECS 主线完整落地，而不是仅停留在准备/文档阶段
- 旧类型与旧注册路径彻底移除
- 运行时、编辑器、加载器与测试链路全部切换到 Entity-based
- 全量测试通过

**Historical Note:**
- 旧的 `TASK-305` / `TASK-306` 规划已并入本次 `TASK-304` / `BATCH-06` 的完整实现，不再作为独立 active task 继续追踪
---

### TASK-307: 资产管线完整重构修复
**Type:** corrective  
**Status:** DONE  
**Scope:** `assets/Schema/`, `src/SomeEngine.Assets/`, `src/SomeEngine.Assets/Importers/`, `src/SomeEngine.Render/Assets/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `tools/`, `tests/SomeEngine.Tests/Assets/`, `tests/SomeEngine.Tests/Materials/`  
**Design Ref:** `docs/assets/asset_identity.md`, `docs/assets/pipeline_overview.md`, `docs/DESIGN.md`

**Goal:** 把当前 asset pipeline 从“GUID 设计已建立，但顶层仍残留 `AssetId` / `Resolver` / `Workspace` / `ManifestAssetDatabase` 等 legacy facade”的半迁移状态，收敛到统一的 `IAsset + AssetManifest + AssetDatabase + AssetTypeRegistry + SourceMeta/AssetMeta` 模型。目标是让 source import、asset scan、manifest query、validate、watch 和 runtime consumption 都走同一入口，同时保持 shipping runtime 仍然只消费 `.asset + manifest`。

**Current Evidence:**
- `AssetId` / `IAssetRecord` / `IAssetResolver` / `IAssetDatabase` / `IAssetWorkspace` / `AssetNode` / `AssetManifestBuilder` / `AssetProjectValidator` / `ManifestAssetDatabase` / `MemoryAssetResolver` / `ManifestAssetResolver` 已全部物理删除
- `src/SomeEngine.Assets/` 顶层已收敛到 `AssetGuid.cs`、`AssetRecord.cs`、`SchemaPartials.cs`、`MetaManagers.cs`、`AssetTypeRegistry.cs`、`AssetManifest.cs`、`AssetManifestScanner.cs`、`AssetDatabase.cs`
- `AssetTypeRegistry` 已注册 `ShaderAsset` / `MaterialAsset` / `MaterialInstanceAsset` / `MeshAsset` 4 个 handler，并保留 `SlangShaderImporter` 作为 source importer
- `AssetDatabase` 已统一承担 `Load<T>(path/guid)`、`Import`、`Resolve`、`List`、`GetDependencies`、`GetReferencers`、`Validate`、`StartWatching` / `StopWatching`（当前为空实现）、`RebuildIndex`
- `MaterialAssetLoader` / `MaterialInstanceLoader` / `MeshMaterialResolver` 已删除全部 `IAssetResolver` 重载，只保留 GUID delegate 路线；`Material` 不再实现 asset interface
- `SomeEngine.Runtime/Program.cs` 已改为统一通过 `AssetDatabase` 查找和加载 shader/material，不再手工 `ScanAndSave + ReloadManifest`，也不再在默认 shader 路径里直接 `SlangShaderImporter.Import(sourcePath)`
- Scanner / Database 已改为 `assetRoots` 定向扫描，而不是从项目根全盘递归后再排除
- 真实验证结果：`dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj -- RunConfiguration.MaxCpuCount=1` 结果为 `129 passed, 0 failed, 1 skipped`；`dotnet build SomeEngine.slnx` 通过

**Work Required:**
- 已完成，详见 `.dev-workstream/reports/BATCH-07-REPORT.md`

**Success Conditions:**
- `AssetGuid` 作为唯一资产 ID；`IAsset` 作为唯一顶层接口
- `AssetDatabase` 成为唯一顶层 asset service，统一 source import、asset scan、query、validate、watch
- Render loader 链路只保留 GUID delegate，不再依赖旧 resolver
- Runtime 默认 asset load 路径通过 `AssetDatabase`
- 全量测试通过，solution 编译通过
