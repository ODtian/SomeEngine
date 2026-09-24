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
- `src/SomeEngine.Render/Data/InstanceMetadata.cs` — `InstanceHeaderLayout.SlotOffset`
- `Stages/` 目录 — 9 个文件，无 ShadeBinStage/ClusterShadeStage

**Work Required:**
- 更新 GPUCluster 大小和字段描述
- 更新 GPU instance header 材质字段为 `InstanceHeaderLayout.SlotOffset`
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
- 更新 GPU 查找路径代码示例（`ClusterSlotBuffer` → `GetSlotField`）

**Success Conditions:**
- 文档与 `ClusterSlotBuffer.cs` SOA 实现一致

---

### TASK-003: 删除 material_pipeline_full_chain.md
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/rendering/material_pipeline_full_chain.md`（已删除）  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-5`

**Goal:** 将该未实现设计从活跃文档区移除

**Current Evidence:**
- 文档描述 friflo Entity 模型（ShaderEntry/IComponent/MaterialStore）
- 代码中 `ShaderEntry`、`MaterialStore`、`ClusterSWRaster` 等类型均不存在

**Work Required:**
- 删除 `docs/rendering/material_pipeline_full_chain.md`
- 更新 `docs/README.md` 索引

**Success Conditions:**
- 文档不再出现在活跃文档区

---

### TASK-004: 同步 shading_pipeline.md 到当前 ClusterShade 实现
**Type:** corrective  
**Status:** DONE  
**Scope:** `docs/rendering/shading_pipeline.md`  
**Design Ref:** `BASELINE-REVIEW.md#DRIFT-6`

**Goal:** 将 `ShadeBinStage` / `ClusterShadeStage` 残留替换为 `ClusterShade.cs`，并把已落地的 PipelineState 分组基础设施写清楚

**Work Required:**
- 更新实现文件列表
- 记录 Cluster PipelineState grouping 已收敛到 cluster-local bin 执行逻辑
- 更新资源绑定为全 Dynamic 隐式签名（BATCH-08 后已无 Sig0/Sig1）

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
**Scope:** `docs/rendering/persistent_thread_bvh_traversal.md`

**Goal:** 合并计划文档并保留仍有效的未来方案文档

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

### TASK-101: 资源绑定测试覆盖
**Type:** review  
**Status:** DONE (已并入 BATCH-08 全 Dynamic 重构)  
**Scope:** `src/Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`  
**Design Ref:** `BASELINE-REVIEW.md#DEBT-007`

**Goal:** ~~为 Sig0/Sig1/s_sig1Cache 静态缓存补充测试~~ → BATCH-08 已消灭 dual-sig 架构，原测试文件 `ClusterShadeSig1Tests.cs` 随架构一起删除

**Current Evidence:**
- `tests/SomeEngine.Tests/Pipelines/ClusterBinTests.cs` 覆盖 cluster bin run 形成路径
- `ClusterShadeSig1Tests.cs` 已随 dual-sig 架构删除
- `ClusterShade.BuildPSOGroups()` 现使用全 Dynamic 隐式签名，无 sig1 cache

**Success Conditions:**
- `ClusterBinTests` 覆盖 cluster bin run 分组逻辑
- 全量测试通过

---

### TASK-102: DeformCache 功能测试
**Type:** review  
**Status:** DONE  
**Scope:** `ClusterDeformPass.cs`, `cluster_deform.slang`  
**Design Ref:** `BASELINE-REVIEW.md#DEBT-008`

**Goal:** 补充 DeformCache 的功能测试（除已有编译测试外）

**Current Evidence:**
- `DeformDispatchCalc` 已删除；`tests/SomeEngine.Tests/Pipelines/DeformCacheTests.cs` 改为验证当前 direct allocation shader/cache offset contract。
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
**Status:** DONE  
**Scope:** `assets/Shaders/brdf.slang`, 新增 light 管理  
**Design Ref:** `docs/goal.md` — "virtual shadow map", "megalight"

### TASK-302: Page 流式加载
**Type:** feature  
**Status:** TODO  
**Scope:** `src/SomeEngine.Render/Systems/PageStream.cs`, `src/SomeEngine.Render/Systems/PageFaults.cs`
**Design Ref:** `docs/future/page_streaming.md`

### TASK-303: Tessellation
**Type:** feature  
**Status:** TODO  
**Scope:** 新增  
**Design Ref:** `docs/future/tessellation.md`

---

### TASK-311: 统一 FrameTarget 与 History 资源系统
**Type:** feature / architecture  
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Graph/`, `src/SomeEngine.Render/ClusterPipeline.cs`, `src/SomeEngine.Render/Cluster/`, `src/SomeEngine.Render/Materials/`, runtime frame setup  
**Design Ref:** `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md`

**Goal:** 建立统一的 FrameTarget 声明、解析、提取和 history 生命周期系统。用户定义的 frame target 与引擎标准 target 必须没有能力差异；SceneColor、SceneDepth、HiZ 等标准 target 只是预注册的 well-known key，不是拥有特权路径的特殊对象。

**Why:** `RenderGraph.Import(...)` 已经支持外部资源进入 graph，但 graph-created resource 缺少对称的 explicit extraction/history API。当前 runtime 仍手动导入 color/depth，HiZ history 也主要依赖稳定命名和 ping-pong helper。继续扩展下去会自然形成“内置 target 一套、用户 target 一套”的分裂模型，后续 post、TAA、SSR、debug target、editor overlay 和用户 pass 都会被迫绕路。

**Principles:**
- 内置 target 和用户 target 使用同一套声明 API、同一套 resolve 路径、同一套 extraction/history 生命周期。
- 标准 target 只能是 preset/key，例如 `StandardFrameTargets.SceneColor`，不能是特殊存储或特殊分支。
- 不提供 `DeclareBuiltin*` / `DeclareUser*` 这类分裂 API。
- 不强制用户 target 进入 `User.*` namespace；冲突通过 registry 的显式规则处理。
- `FrameTargetKey` 表达跨帧稳定语义；`RenderGraphHandle` 仍然只表达单帧 graph 内资源。
- RenderGraph extraction primitive 不知道 FrameTarget key，只负责 graph resource 的生命周期和最终状态。

**Work Breakdown:**
- `TASK-311a` Unified FrameTargetRegistry：新增 renderer-level registry，保留 texture/import/history 三条真实使用路径、descriptor factory、debug metadata、compatible merge 和 history reset。标准 target 通过同一 API 注册；buffer target、freeze、override 等策略推迟到真实 pass 需要时再加。
- `TASK-311b` RenderGraph 资源提取原语：为 `RenderGraph` 增加 explicit extraction API，使 graph-created texture/buffer 可以在 `Execute` 后成为外部资源、swapchain/debug 输出或下一帧 history 输入。Extraction 必须把 producer pass 视作 graph output，不能被 DCE 裁掉，并且要与现有 `Import(...)` 和自动 barrier/state tracking 协同。
- `TASK-311c` History 生命周期集成与 HiZ 迁移：把 history target 纳入统一 registry，迁移 HiZ ping-pong/history 路径。HiZ 应成为普通 history target 的第一个实用样例，而不是保留独立特殊机制。增加自定义 history target 测试，证明用户 target 与 HiZ 具有相同能力。
- `TASK-311d` Material fallback 资源绑定：关闭 `DEBT-017`。为缺失的 material texture/buffer/scalar slot 提供 renderer-owned deterministic fallback，避免 null binding 或 backend-specific 行为，同时保留诊断信息帮助发现资源缺失。
- `TASK-311e` Runtime / Pipeline 采用统一 FrameTargetRegistry：让 runtime frame setup 和 `ClusterPipeline` 通过 registry import/create/read SceneColor/SceneDepth/HiZ 等 target。增加一个 custom target 集成测试或 sample path，验证自定义 target 可以像标准 target 一样被生产、提取、再消费。

**Acceptance Criteria:**
- 标准 SceneColor 和自定义 target 通过同一 API 声明。
- 自定义 target 可被 RenderGraph pass 写入、提取，并在后续 pass 或下一帧消费。
- Graph-created resource 的 extraction 是显式 API，不再只依赖稳定资源名缓存。
- HiZ history 使用统一 history lifecycle。
- 缺失 material resource slot 会绑定 deterministic fallback。
- RenderGraph DCE、拓扑排序、automatic barrier 和 persisted state 行为仍有测试覆盖。
- 文档和 task tracker 不再把 FrameTarget 分成 privileged built-in 与 second-class custom 两套概念。

---

### TASK-304: Material ECS 重构
**Type:** feature  
**Status:** DONE  
**Scope:** `src/SomeEngine.Render/Materials/`, `src/SomeEngine.Render/Pipelines/ClusterRender/`, `src/SomeEngine.Render/Assets/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `assets/Schema/shader_asset.fbs`, `src/SomeEngine.Assets/Importers/SlangShaderImporter.cs`, `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/Assets/`  
**Design Ref:** `docs/materials/architecture.md`

**Goal:** 删除旧材质注册路线，迁移到 friflo `EntityStore`，落地 entry-point attribute authoring、BinGroup 动态分组和 Entity-based 管线消费。

**Current Evidence:**
- 旧材质 pass / registry 文件已物理删除
- `Material.cs` 已收敛为瘦 Material；`Instantiate()` 会复制 Params 并创建独立 Entity
- `MaterialSystem` 已作为全局材质 `EntityStore` 入口使用
- `shader_asset.fbs` 与 `SlangShaderImporter.cs` 已支持 `entry_point_attributes`
- `MaterialAssetLoader` / `MaterialInstanceLoader` 已按 Entity + `MaterialRef` 路线加载与实例化材质
- `MaterialItems` 已按 `MaterialPass.Target` 解释材质，维护 `MaterialBin`、SlotBuffer、material binding 和 scalar region
- `ClusterPipeline` 通过 Scene/Raster/Shade/Output stage 消费 `MaterialBin`
- `ClusterBinTests` / `MaterialAssetPipelineTests` / `AssetResolverTests` 已迁移到 Entity-based 路线（`ClusterShadeSig1Tests` 随 dual-sig 删除）
- 真实验证结果：`dotnet build`（Render / Runtime / Editor）通过，`dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj -- RunConfiguration.MaxCpuCount=1` 结果为 `134 passed, 0 failed, 1 skipped`

**Success Conditions:**
- Material ECS 主线完整落地，而不是仅停留在准备/文档阶段
- 无未使用注册路径
- 运行时、编辑器、加载器与测试链路全部切换到 Entity-based
- 全量测试通过

### TASK-307: 资产管线完整重构修复
**Type:** corrective  
**Status:** DONE  
**Scope:** `assets/Schema/`, `src/SomeEngine.Assets/`, `src/SomeEngine.Assets/Importers/`, `src/SomeEngine.Render/Assets/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `tools/`, `tests/SomeEngine.Tests/Assets/`, `tests/SomeEngine.Tests/Materials/`  
**Design Ref:** `docs/assets/asset_identity.md`, `docs/assets/pipeline_overview.md`, `docs/DESIGN.md`

**Goal:** 把 asset pipeline 收敛到统一的 `IAsset + AssetManifest + AssetDatabase + SourceMeta/AssetMeta` 模型。目标是让 source import、manifest query、validate 和 runtime consumption 都走同一入口，同时保持 shipping runtime 只消费 `.asset + manifest`。

**Current Evidence:**
- `AssetId` / `IAssetRecord` / `IAssetResolver` / `IAssetDatabase` / `IAssetWorkspace` / `AssetNode` / `AssetManifestBuilder` / `AssetProjectValidator` / `ManifestAssetDatabase` / `MemoryAssetResolver` / `ManifestAssetResolver` 已全部物理删除
- `src/SomeEngine.Assets/` 顶层已收敛到 `AssetGuid.cs`、`AssetRecord.cs`、`SchemaPartials.cs`、`MetaFiles.cs`、`AssetManifest.cs`、`AssetDatabase.cs`、`AssetPipelineContracts.cs` 与 provider/importer 实现
- `AssetDatabase` 已统一承担 `Load<T>(path/guid)`、`Import`、`Resolve`、`List`、`GetDependencies`、`GetReferencers`、`Validate`
- `AssetDatabase.Load<T>(path)` 不再隐式注册裸 `.asset`；手写 asset 必须显式 `Import()` 后进入 manifest
- Source importer 的 AssetGuid 权威来源已收敛为 `AssetGuid.FromSource(SourceGuid, SubAssetKey)`，`.asset.meta` 只作为 fingerprint / dependencies / 一致性校验数据
- `MaterialAssetLoader` / `MaterialInstanceLoader` 只保留 GUID delegate 路线；`Material` 不再实现 asset interface
- `SomeEngine.Runtime/Program.cs` 已改为统一通过 `AssetDatabase` 查找和加载 shader/material，不再手工 `ScanAndSave + ReloadManifest`，也不再在默认 shader 路径里直接 `SlangShaderImporter.Import(sourcePath)`
- 真实验证结果以当前批次构建/测试记录为准；本文件不再记录固定历史用例数量

**Work Required:**
- 已完成，详见 `.dev-workstream/reports/BATCH-07-REPORT.md`

**Success Conditions:**
- `AssetGuid` 作为唯一资产 ID；`IAsset` 作为唯一顶层接口
- `AssetDatabase` 成为唯一顶层 asset service，统一 source import、query、validate
- Render loader 链路只保留 GUID delegate
- Runtime 默认 asset load 路径通过 `AssetDatabase`
- 全量测试通过，solution 编译通过

---

### TASK-309: Asset + Material RenderWorld 完整改造
**Type:** corrective  
**Status:** DONE  
**Scope:** `assets/Schema/`, `src/SomeEngine.Assets/`, `src/SomeEngine.Core/ECS/Components/`, `src/SomeEngine.Render/Assets/`, `src/SomeEngine.Render/Materials/`, `src/SomeEngine.Render/Pipelines/`, `src/SomeEngine.Render/Systems/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/`, `tests/SomeEngine.Tests/Assets/`, `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/ECS/`  
**Design Ref:** `docs/DESIGN.md`, `docs/materials/architecture.md`, `docs/assets/pipeline_overview.md`, `docs/assets/asset_identity.md`, `docs/core/ecs_design.md`

**Goal:** 把曾经的“mesh 默认材质 + 单实体材质 + 管线直接读材质 store”中间态，完整收敛到新的 authoring/runtime 分层：`MeshAsset` 只保留几何与 region/debug 元数据，实体 authoring 持有材质绑定，`MaterialAsset` 加载为运行时 `Material`，材质入口通过 `MaterialPass.Target` 暴露，渲染提交统一改为 `GameWorld -> RenderWorld -> Pipeline`。

**Current Evidence:**
- `assets/Schema/mesh_asset.fbs` 不再包含 `default_material_guids` / `default_material_slots`
- `MaterialAsset` / `MaterialInstanceAsset` schema 只保留 `shader_guid` / `parent_guid`
- `MeshAssetProvider.GetDependencies()` 不再写入 mesh -> material 依赖
- `Material` 运行时对象已收敛为直接材质字段 + `MaterialPass[]`
- `RenderWorldExtractor` 提供 mesh/material handle 与 dirty state；`MaterialItems` 解释 `MaterialPass.Target` 并生成 `MaterialBin`
- Cluster cull 只保留 Phase1/Phase2 HiZ 路径

#### TASK-309a: Mesh Region / Authoring Binding 重构
**Deliverable:** `MeshAsset` 删除具体 material 引用，只保留稳定 region/section 与 mesh-local material 元数据；实体 authoring 组件接管稳定的 mesh-local material linear table。  
**Key Requirements:**
- `MeshAsset` schema 不再出现 `default_material_guids` / `default_material_slots`
- glTF 导入继续产出 region 信息，但不再把默认材质依赖写回 mesh asset
- `MeshAssetProvider.GetDependencies()` 不再返回 material 依赖
- 新的 ECS authoring 组件可表达稳定 mesh-local material linear table，并为未来 scene/prefab 持久化提供 canonical 结构

#### TASK-309b: MaterialAsset Pass 模型落地
**Deliverable:** `MaterialAsset` 仍保持单一资产层级，加载后生成运行时 `MaterialPass[]`。
**Key Requirements:**
- 不新增第二种 material asset，不新增编译后 material graph 资产
- pass 是运行时 `MaterialPass` 入口记录，不引入额外 feature 容器
- 显式 authoring 与 shader metadata 都能生成 pass；最终运行时只消费同一份显式 `MaterialAsset`
- `Material` 运行时对象持有直接材质字段与 `MaterialPass[]`

#### TASK-309c: RenderWorld Extract / Pipeline 分层
**Deliverable:** 引入 RenderWorld 作为渲染输入源，保留 source entity、mesh handle、material handle、local material slot 与 dirty state。
**Key Requirements:**
- `GameWorld` 保留 authoring 数据；`RenderWorld` 只保留执行态
- Extract 链路正确处理创建、更新、删除和多 material slot
- `ClusterPipeline` 通过 `MaterialItems` 解释 `MaterialPass.Target`
- `ClusterPipeline` 与后续管线不得再直接 query 材质 source store

#### TASK-309d: 管线派生数据下沉与 ClusterPipeline 迁移
**Deliverable:** `MaterialItems` / `ClusterSlotBuffer` 等 stage/bin 派生结构不再作为材质核心设施，而是变为具体管线的派生数据。
**Key Requirements:**
- Cluster pipeline 通过 `MaterialPass.Target` 和 `MaterialItems` 私有 target 解析选择需要提交的工作
- 材质系统不再假设 `gbuffer` / `depthonly` / `overlay` / shader 变量名等任何管线细节
- mesh-local material table 和 pass 解释后的 GPU 提交路径统一生成 SlotBuffer offset
- 迁移后仍保持现有 Cluster 路径的功能完整性和测试可验证性

#### TASK-309e: Host / Test 路径清理
**Deliverable:** Runtime / Editor / Tests / Docs 全部切到新模型，删除 mesh 默认材质运行时提交和单实体材质提交路径。  
**Key Requirements:**
- 删除不再成立的 mesh 默认材质运行时绑定路径
- Runtime / Editor 启动路径按 ECS authoring + RenderWorld extract 工作
- 相关资产、材质、管线、ECS 测试全部迁移并覆盖真实行为
- 文档同步到“asset 扁平化 + material pass 实体 + RenderWorld 提交”模型

**Success Conditions:**
- `MeshAsset` 不再引用具体 `Material`，manifest 中不再存在 mesh -> material 依赖
- ECS authoring 材质绑定作为运行时 extract 的唯一材质入口；当前实现为 mesh-local material table `MeshMaterialBindings`
- `MaterialAsset` 成为唯一材质资产，并能 round-trip `MaterialPass`
- RenderWorld 成为渲染输入真相源，管线只消费 mesh/material handle 与 dirty state
- `MaterialItems` / `ClusterSlotBuffer` 等派生结构不再作为材质核心概念暴露
- Runtime / Editor / Tests / Docs 全部切到新模型
- 全量编译和相关测试通过

#### TASK-309f: Mesh Local Material Table Semantics Cleanup
**Deliverable:** 把 `MeshMaterialBindings` 从“`region -> material` 映射”收敛为稳定的 mesh-local material linear table / local material slot 语义，使 cluster runtime 不再把 `region` 当作运行时映射概念。  
**Key Requirements:**
- `region` 只允许作为 asset/import/debug 元数据存在，不能再成为 cluster runtime 的核心提交概念
- `MeshMaterialBindings` 的 canonical 含义必须是稳定的 mesh-local material 顺序，运行时提交只消费 local material slot
- `cluster_structures.slang` / `cluster_binning.slang` / `cluster_shade_binning.slang` 当前使用的 `local material index + InstanceHeaderLayout.SlotOffset` 语义必须成为主设计真相
- 相关 schema / authoring / importer / docs 命名需要同步，不能继续用“region”误导运行时模型

#### TASK-309g: RenderWorld Extract Local-Slot Rework
**Deliverable:** RenderWorld 只保留 `(source entity, local material slot, material handle)` submission state；extract 路径不再为 `region` 映射保留桥接数据结构。
**Key Requirements:**
- RenderWorld 只保留 pipeline 真正需要的 submission state，不再保留“按 region 回拼”的中间态组件
- extract 路径正确处理创建、更新、删除以及多 material slot
- per-frame extract 路径禁止 `Dictionary`，禁止为 steady-state frame loop 引入新的托管分配
- RenderWorld 仍需保持强类型 component query 能力，不能因为去掉桥接层而退回弱类型查找

#### TASK-309h: Cluster Prepare Zero-GC Slot Folding
**Deliverable:** 把 slot folding 直接收敛到 cluster pipeline 自己的 prepare 路径，并满足每帧 0 GC、禁止字典。  
**Key Requirements:**
- `MaterialItems` / `ClusterSlotBuffer` 仍然是 cluster pipeline 自己的派生数据，而不是 material system 核心设施
- prepare 路径直接从 extracted local material slot 顺序折叠出 slot / bin，不再做 `source + region` 回扫映射
- 所有每帧调用热点路径禁止 `Dictionary`，禁止 `List`/`ToArray`/LINQ/`Array.Resize` 这类会造成 steady-state 分配的写法
- overlay / primary shade / vertex eval 选择逻辑必须在新 prepare 模型里显式收口，不能再依赖启发式桥接器
- GPU header / shader 只消费 pipeline 通过 `InstanceHeaderLayout` 注册并写入的 slot offset

#### TASK-309i: Host / Test / Docs Cleanup And Guardrails
**Deliverable:** Runtime / Editor / Tests / Docs 全部切换到 mesh-local material linear table 语义，并补上 hot-path zero-allocation / no-Dictionary 守护。  
**Key Requirements:**
- 删除与 `region` 运行时映射绑定的旧 API
- Runtime / Editor 启动链路按新 extract / prepare 边界工作，只有一条 authoring -> extract -> prepare 数据路径
- 为 extract / prepare 热点路径补 allocation regression 测试，验证 steady-state 调用不分配托管内存
- 工程记录必须明确写出“每帧 0 GC、禁止字典”约束，不能只留在实现备注里

---

### TASK-312: HDR SceneColor and Post Chain
**Type:** feature
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Frame/`, `src/SomeEngine.Render/Pipelines/`, `assets/Shaders/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `tests/SomeEngine.Tests/`
**Design Ref:** BATCH-11 FrameTargetRegistry route, `.dev-workstream/batches/BATCH-12-INSTRUCTIONS.md`

**Goal:** make `SceneColor` an HDR offscreen frame target instead of an alias for the swapchain, then add the first concrete post-processing route: HDR scene color is tonemapped into `BackBuffer`. `BackBuffer` must be just another `FrameTargetKey`; user-defined targets and built-in targets continue to use the same registry APIs.

**Current Evidence:**
- BATCH-11 introduced a simplified `FrameTargetRegistry` with `Texture`, `ImportTexture`, `HistoryTexture`, `GetTexture`, and `ResetHistory`.
- Runtime and Editor currently import the swapchain texture as `SceneColor`, so the renderer has no real post-chain endpoint yet.
- Cluster shading currently creates an intermediate UAV-capable shade output and copies it into the selected color target.

#### TASK-312a: BackBuffer / HDR SceneColor FrameTarget Contract
**Deliverable:** add `StandardFrameTargets.BackBuffer` and a shared HDR SceneColor descriptor helper, with tests proving SceneColor and BackBuffer coexist through the same registry path.

**Key Requirements:**
- Do not add special lifecycle or registration APIs for built-ins.
- Do not reintroduce FrameTarget declarations, freeze stages, buffer targets, or handle wrappers.
- SceneColor must be created as an HDR offscreen texture with render-target, shader-resource, and UAV usage where needed.

#### TASK-312b: HDR Cluster Shading Output
**Deliverable:** the cluster frame-target path shades into HDR SceneColor while preserving the legacy no-registry LDR path.

**Key Requirements:**
- Keep format selection explicit.
- Do not make ClusterPipeline read private RenderGraph descriptors.
- Existing cluster pass composition must stay compatible with the previous no-registry `AddPasses(graph)` route.

#### TASK-312c: Minimal Post Tonemap Pass
**Deliverable:** add a render graph post pass that reads HDR SceneColor as SRV and writes the BackBuffer RTV, plus a tiny full-screen HLSL shader.

**Key Requirements:**
- The pass must mark BackBuffer as graph output.
- No bloom, TAA, exposure framework, post stack scheduler, or extra target registry layer in this batch.
- The shader should be deterministic and simple enough to compile with the current direct HLSL loading path.

#### TASK-312d: Runtime / Editor Adoption
**Deliverable:** Runtime and Editor setup BackBuffer, HDR SceneColor, and depth through FrameTargetRegistry, clear HDR SceneColor/depth, run ClusterPipeline, then add post tonemap to BackBuffer before ImGui/present.

**Key Requirements:**
- ImGui remains composited after scene/post into the swapchain.
- No changes to asset loading, material extraction, or cluster material semantics.
- Focused tests and full build/test verification must pass before the batch is reported complete.

**Success Conditions:**
- `SceneColor` is no longer the swapchain texture in Runtime/Editor.
- `BackBuffer` is a normal imported frame target endpoint.
- Cluster shading writes HDR scene color in the frame-target route.
- A post tonemap graph pass resolves SceneColor into BackBuffer.
- Tests, report, and review are updated for BATCH-12.

---

### TASK-313: Motion Vectors and Temporal SceneColor History
**Type:** feature
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Frame/`, `src/SomeEngine.Render/Pipelines/`, `src/SomeEngine.Render/Pipelines/ClusterRender/`, `assets/Shaders/`, `tests/SomeEngine.Tests/`
**Design Ref:** BATCH-12 HDR SceneColor/Post route, BATCH-11 `FrameTargetRegistry.HistoryTexture`, `.dev-workstream/batches/BATCH-13-INSTRUCTIONS.md`

**Goal:** generate a real `MotionVectors` frame target from cluster visibility and maintain an HDR temporal scene-color history target through the unified FrameTargetRegistry route. This prepares later temporal post passes without adding TAA, exposure accumulation, or a broad post stack in this batch.

**Current Evidence:**
- `StandardFrameTargets.MotionVectors` exists as a key but is not generated by the Runtime/Editor cluster route.
- `FrameTargetRegistry.HistoryTexture` already backs HiZ history and can support renderer-owned temporal scene-color history without new registry APIs.
- BATCH-12 made `SceneColor` an HDR offscreen texture and `BackBuffer` a post endpoint, giving temporal passes a clean source and destination.

#### TASK-313a: MotionVectors / Temporal History Target Contract
**Deliverable:** add shared descriptors and standard keys for motion vectors and temporal scene-color history.

**Key Requirements:**
- `MotionVectors` must be an RG floating-point UAV/SRV texture with the same frame dimensions as `SceneColor`.
- Temporal scene-color history must use `FrameTargetRegistry.HistoryTexture`, not a bespoke ping-pong or post-stack resource manager.
- Do not expand `FrameTargetRegistry` with declarations, built-in overrides, buffer targets, or special temporal APIs.

#### TASK-313b: Cluster Motion Vector Pass
**Deliverable:** add a cluster visibility-driven compute pass that writes normalized screen-space motion vectors.

**Key Requirements:**
- The pass must read the current visibility buffer, visible cluster requests, page heap, instance transforms, current camera matrix, and previous camera matrix.
- Pixels with no geometry or no previous history must write zero motion.
- The cluster route must remain valid whether or not the frame also produced deform-cache resources.
- Motion vector sign convention must be documented and covered by tests or shader source assertions.

#### TASK-313c: Temporal SceneColor History Update
**Deliverable:** copy HDR `SceneColor` into the current temporal history target so the next frame can import the previous HDR scene color.

**Key Requirements:**
- The history update pass must be kept alive by RenderGraph extraction and should not mark `BackBuffer` or post output as temporal history.
- The current batch must not make post tonemap consume temporal history; motion correctness must be testable first.
- History target descriptors must match HDR SceneColor dimensions and format.

#### TASK-313d: Runtime / Editor Adoption And Tests
**Deliverable:** wire MotionVectors and temporal history through `ClusterPipeline.AddPasses(graph, frameTargets)` so Runtime and Editor get the route automatically.

**Key Requirements:**
- The legacy no-registry `ClusterPipeline.AddPasses(graph)` path must remain usable.
- Focused tests must cover descriptors, graph pass retention/dependencies, and shader import/entry point compilation.
- Full build/test verification must pass before BATCH-13 is reported complete.

**Success Conditions:**
- Runtime/Editor frame-target cluster route generates `MotionVectors`.
- Temporal HDR SceneColor history is updated through `FrameTargetRegistry.HistoryTexture`.
- Post tonemap remains a non-temporal pass.
- Tests, report, and review are updated for BATCH-13.

**Post-BATCH-14 Correction:** The motion-vector and temporal-history features remain, but their frame-target registry route was intentionally replaced. Runtime/Editor now use explicit `FrameSurfaces`, and temporal scene-color history is maintained by `RenderHistoryRegistry`.

---

### TASK-314: Explicit Frame IO and Render History Simplification
**Type:** corrective architecture
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Frame/`, `src/SomeEngine.Render/Graph/`, `src/SomeEngine.Render/Pipelines/ClusterRender/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `tests/SomeEngine.Tests/`
**Design Ref:** `.dev-workstream/batches/BATCH-14-INSTRUCTIONS.md`, BATCH-13 simplify review

**Goal:** remove the stateful `FrameTargetRegistry` abstraction from frame-local render target ownership. Frame-local IO should be explicit RenderGraph handles passed through `FrameSurfaces`; cross-frame history should live in an independent `RenderHistoryRegistry`.

**Why:** The old registry was doing too much: declaration, import/create resolution, standard-name indirection, and history lifetime. For frame-local textures, string resource names plus RenderGraph handles already provide the needed identity. The only nontrivial behavior worth keeping is cross-frame extraction/history.

**Current Evidence:**
- `FrameTargetRegistry.cs`, `FrameTargetDescriptions.cs`, and `FrameTargetRegistryTests.cs` have been removed.
- `FrameSurfaces` carries explicit `SceneColor`, `SceneDepth`, and `OutputColor` handles plus a `FrameSurfaceContext`.
- `FrameResources` owns frame texture names and `TextureDesc` factory rules.
- `RenderHistoryRegistry` owns texture and default-buffer history through RenderGraph extraction.
- Superseded by TASK-330: `RenderGraph` no longer treats resource names as identity; duplicate debug labels are allowed because handles identify graph resources.
- Runtime and Editor create/import surfaces directly, then call `ClusterPipeline.AddPasses(graph, frameSurfaces, histories)`.

#### TASK-314a: Remove FrameTargetRegistry From Frame IO
**Deliverable:** delete the declaration/resolve registry and replace active frame setup with explicit RenderGraph handles.

**Key Requirements:**
- Do not introduce another frame-local registry.
- Do not add a second handle abstraction on top of `RenderGraphHandle`.
- Preserve HDR `SceneColor`, depth, post tonemap, and ImGui composition behavior.

#### TASK-314b: Frame Resource Names And RenderGraph Naming Guard
**Deliverable:** keep only simple frame resource names/descriptors. The duplicate-name guard was a BATCH-14 safety measure and is superseded by TASK-330's handle-owned RDG identity.

**Key Requirements:**
- Names are debug labels only; graph resources are addressed by `RenderGraphHandle`.
- Do not add `GetOrCreateNamedTexture` or `GetOrCreateNamedBuffer`.
- Share descriptor compatibility logic where RenderGraph caching and history reset need the same comparison.

#### TASK-314c: Independent RenderHistoryRegistry
**Deliverable:** add a registry dedicated to cross-frame texture/buffer history.

**Key Requirements:**
- API returns current, previous, and `HasPrevious`.
- Same-frame compatible duplicate history requests return the same handles.
- Same-frame incompatible requests throw.
- Descriptor changes reset previous history.
- Buffer history only accepts default-usage GPU buffers.

#### TASK-314d: Pipeline / Host Migration
**Deliverable:** migrate ClusterPipeline, HiZ, temporal history, Runtime, and Editor to `FrameSurfaces + RenderHistoryRegistry`.

**Key Requirements:**
- HiZ uses the history registry instead of the old ping-pong helper or frame-target history.
- Temporal HDR SceneColor history uses the same history registry.
- Do not move DeformCache or previous-object motion into frame IO.
- The graph-only `ClusterPipeline.AddPasses(RenderGraph)` route is removed; callers must pass explicit `FrameSurfaces` and `RenderHistoryRegistry`.

**Success Conditions:**
- Frame-local IO is explicit through `FrameSurfaces`.
- Cross-frame HiZ and temporal SceneColor history use `RenderHistoryRegistry`.
- RenderGraph duplicate resource names are allowed and do not alias handles.
- No active references to `FrameTargetRegistry`, `FrameTargetDescriptions`, or `StandardFrameTargets` remain.
- Focused tests, full build, full tests, report, and review are complete for BATCH-14.

---

### TASK-315: Temporal Resolve and TAA Validation
**Type:** feature
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Frame/`, `src/SomeEngine.Render/Pipelines/`, `src/SomeEngine.Render/Pipelines/ClusterRender/`, `assets/Shaders/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `tests/SomeEngine.Tests/`
**Design Ref:** `.dev-workstream/batches/BATCH-15-INSTRUCTIONS.md`, BATCH-14 review next-batch direction

**Goal:** add the first temporal resolve step on top of explicit frame IO. The cluster frame route now consumes current HDR `SceneColor`, current `MotionVectors`, and previous `TemporalSceneColor` history to produce a resolved HDR source before tonemap. When history is valid, the resolve writes directly into the current `TemporalSceneColor` history target; otherwise the current `SceneColor` remains the post-tonemap source while warming history once.

**Current Evidence:**
- `TemporalResolvePass` can write directly to the current `RenderHistoryRegistry` `TemporalSceneColor` target, avoiding an extra resolved-to-history copy.
- `TemporalResolvePass` runs a full-screen resolve when previous history exists and uses a pass-level copy bypass on first frame, resize, camera cut, or missing history.
- `assets/Shaders/temporal_resolve.slang` documents the motion-vector convention as current-frame UV minus previous-frame UV.
- `ClusterPipeline` returns `ClusterFrameOutputs.PostSceneColor`, runs temporal resolve after shading, updates `TemporalSceneColor` from the resolved HDR output, and returns the resolved source to Runtime/Editor for tonemap.
- `ClusterPipeline.ResetTemporal()` provides explicit camera-cut reset behavior.
- Runtime and Editor reset render histories on resize; Runtime also exposes an F7 debug reset path.

#### TASK-315a: Temporal Resolve Contract
**Deliverable:** add a temporal resolve pass API that writes a resolved HDR source when previous history exists and leaves first-frame/no-history fallback to the caller.

**Key Requirements:**
- Do not add a post-stack scheduler or frame-local registry.
- The no-history path must not sample an invalid previous texture.
- The resolved target must remain HDR and shader-readable for post tonemap.

#### TASK-315b: Bounded Temporal Resolve Shader
**Deliverable:** add a simple full-screen shader that reprojects previous HDR scene color with motion vectors and blends it into current HDR color.

**Key Requirements:**
- Motion vectors use the BATCH-13 convention: current-frame UV minus previous-frame UV.
- History is clamped near current HDR color before blending to keep the first validation pass bounded.
- No bloom, exposure, jitter, neighborhood clipping, responsive masks, or broad TAA framework in this batch.

#### TASK-315c: Cluster Pipeline Integration
**Deliverable:** integrate temporal resolve into the cluster frame route before post tonemap.

**Key Requirements:**
- `PostTonemapPass` must consume the resolved HDR source when temporal resolve is enabled.
- The temporal history update copy must copy the resolved HDR source into current `TemporalSceneColor` history, not the tonemapped backbuffer.
- Motion-vector generation remains in place and still uses the current visibility path.

#### TASK-315d: Reset And Host Adoption
**Deliverable:** add explicit reset hooks and host adoption for resize/camera-cut behavior.

**Key Requirements:**
- Resize resets history explicitly in Runtime and Editor.
- Camera cuts use `ResetTemporal()`; normal camera movement must not reset history.
- Descriptor mismatch in `RenderHistoryRegistry` still provides a second resize safety net.

**Success Conditions:**
- Temporal resolve runs before tonemap in Runtime and Editor.
- First frame, missing previous history, resize, and requested camera cuts fall back to current HDR scene color.
- Temporal history is updated from resolved HDR scene color.
- Tests cover resolve retention, no-history bypass, resolved-history feedback, and shader source invariants.
- Focused tests, full build, full tests, report, and review are complete for BATCH-15.

---

### TASK-316: Temporal Quality and Runtime Validation
**Type:** feature / validation
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Frame/`, `src/SomeEngine.Render/Pipelines/`, `src/SomeEngine.Render/Pipelines/ClusterRender/`, `assets/Shaders/`, `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`, `tests/SomeEngine.Tests/`
**Design Ref:** `.dev-workstream/batches/BATCH-16-INSTRUCTIONS.md`, BATCH-15 review next-batch direction

**Goal:** turn the first temporal resolve into a quality-validation feature slice. Runtime and Editor should be able to use deterministic temporal jitter, the resolve shader should expose bounded quality controls, and Runtime should provide practical validation controls for jitter, resolve enablement, history weight, and reset.

**Current Evidence:**
- BATCH-15 resolves current HDR `SceneColor`, `MotionVectors`, and previous `TemporalSceneColor` before tonemap.
- Missing history, resize, camera cut, and missing motion-vector production bypass the shader and warm history from current HDR color.
- Runtime has an F7 reset path, but no deterministic jitter or quality controls.
- The shader uses a single-pixel current-color clamp and one history weight.

#### TASK-316a: Temporal Sample Pattern And Jitter Contract
**Deliverable:** deterministic frame-indexed jitter helpers and projection application utilities shared by Runtime and Editor.

**Key Requirements:**
- Samples are centered around zero in pixel space and bounded inside half a pixel.
- Jitter applies to projection matrices without mutating camera state.
- Normal camera movement and normal jitter progression must not reset temporal history.

#### TASK-316b: Bounded Quality Resolve Parameters
**Deliverable:** explicit temporal resolve settings and shader uniforms for history weight, neighborhood clamp strength, clamp minimum, and motion response.

**Key Requirements:**
- CPU clamps settings before upload.
- Shader uses a small current-frame neighborhood, not just one current pixel, for history bounds.
- High motion lowers history contribution deterministically.
- No exposure, jitter feedback, reactive masks, previous-object motion, or broad TAA framework in this task.

#### TASK-316c: Runtime And Editor Validation Adoption
**Deliverable:** Runtime and Editor apply deterministic jitter when temporal resolve is enabled; Runtime exposes validation controls for temporal resolve, jitter, weight, and reset.

**Key Requirements:**
- Runtime/Editor keep the existing explicit frame IO path.
- Runtime validation controls should be simple and in the existing debug UI/input path.
- F7 reset remains explicit and does not become a camera-event system.

#### TASK-316d: Tests, Docs, Report, And Review
**Deliverable:** focused tests plus BATCH-16 report/review/tracker updates.

**Key Requirements:**
- Tests cover sample determinism, bounds, projection jitter, settings clamping, shader-source invariants, and graph retention.
- Full build and full test project run must pass before BATCH-16 is reported complete.

**Success Conditions:**
- Runtime and Editor use deterministic temporal jitter by default when temporal resolve is enabled.
- Temporal resolve settings are explicit, bounded, and covered by tests.
- Shader clamps history using a small current-frame neighborhood and reduces history under high motion.
- Runtime exposes validation controls for resolve/jitter/weight/reset.
- Focused tests, full build, full tests, report, and review are complete for BATCH-16.

---

### TASK-317: Runtime Debug State and Validation Console Rework
**Type:** corrective rework
**Status:** DONE
**Scope:** `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Runtime/DebugUi/`
**Design Ref:** `.dev-workstream/batches/BATCH-17-INSTRUCTIONS.md`, BATCH-16 residual Runtime UI/debug-state risk

**Goal:** clean up Runtime debug-state ownership and replace the monolithic ImGui debug window with a bounded Runtime Validation Console. This is not an Editor implementation; it is a rendering/runtime validation surface.

**Current Evidence:**
- BATCH-16 added temporal validation controls directly into the existing Runtime ImGui block.
- `Program.cs` had accumulated key edge booleans, forced LOD, temporal frame index, mesh/import UI selections, entity spawn counters, HiZ preview SRV IDs, culling stats, page controls, and temporal settings in one main-loop scope.
- The next capture-validation batch needs a cleaner state/command foundation.

#### TASK-317a: Runtime Debug State Ownership
**Deliverable:** move Runtime debug state into dedicated Runtime debug types.

**Key Requirements:**
- Key edge tracking uses one small tracker instead of many `_key*Pressed` locals.
- Forced LOD and temporal frame index live in debug/validation state.
- HiZ preview SRV registration cache is owned by a helper with explicit cleanup.
- Mesh/import UI selection and status state are not raw `Program.cs` locals.

#### TASK-317b: Runtime Validation Console UI
**Deliverable:** reimplement Runtime ImGui as a bounded validation console.

**Key Requirements:**
- Tabs: Frame, Rendering, Temporal, Scene, Assets.
- Rendering contains render toggles, culling stats, HiZ preview, page controls.
- Temporal contains resolve/jitter toggles, reset, settings sliders, and bounded presets.
- Scene contains validation scene construction helpers only.
- Assets contains mesh refresh/load/import helpers.
- Do not carry forward the generic per-entity transform inspector.

#### TASK-317c: Host Command Routing
**Deliverable:** route host-side actions through explicit Runtime debug commands.

**Key Requirements:**
- UI drawing does not load/import meshes, spawn entities, evict pages, or trigger dumps directly.
- `Program.cs` processes commands and remains responsible for resource ownership and host actions.
- Existing hotkeys remain: 1/2/3/4 and F5/F6/F7/F8/F9.

#### TASK-317d: Verification And Artifacts
**Deliverable:** build verification and BATCH-17 workstream artifacts.

**Key Requirements:**
- `dotnet build SomeEngine.slnx --no-restore -v minimal -m:1` passes.
- Report and review are complete.
- The next batch direction is capture validation, not a full editor.

**Success Conditions:**
- Runtime debug state has a clear home under `DebugUi`.
- Runtime UI is reimplemented as a validation console, not an editor.
- Host actions are command-routed.
- Build passes.
- BATCH-18 can add deterministic capture validation on top of the cleaned UI/state foundation.

### TASK-318: Temporal Validation Hardening
**Type:** feature hardening
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Frame/`, `src/SomeEngine.Runtime/`, temporal tests
**Design Ref:** `.dev-workstream/batches/BATCH-18-INSTRUCTIONS.md`, BATCH-17 residual capture-validation risk

**Goal:** make the current temporal path repeatable, inspectable, and harder to mistake for working when it is bypassed. This batch upgrades temporal validation from manual UI toggling into a deterministic Runtime validation flow with shared presets, motion, capture artifacts, and image-difference metrics.

**Current Evidence:**
- BATCH-15/BATCH-16 made temporal resolve functional, with HDR history, motion vectors, bounded clamping, motion rejection, and jitter.
- Runtime validation is still manual unless a developer operates the ImGui controls.
- Static validation can hide motion-vector and history-reset defects.
- Existing reports call out missing screenshot/capture validation as residual risk.

**Non-goals:**
- No bloom, exposure, SSR, post-stack scheduler, or broad TAA framework.
- No editor timeline, scene persistence, or external automation framework.
- No new temporal settings object beyond `TemporalResolveSettings`.

#### TASK-318a: Shared Temporal Validation Presets
**Deliverable:** define reusable temporal validation presets for off, resolve-only, jitter+resolve, stable-history, and high-rejection modes.

**Key Requirements:**
- Runtime UI and automated validation use the same preset definitions.
- Presets expose resolve/jitter flags and bounded `TemporalResolveSettings`.
- Applying a preset resets temporal history through the existing reset path.

#### TASK-318b: Deterministic Validation Sequence
**Deliverable:** add a deterministic validation sequence controller with warmup, capture count, and a non-static camera path.

**Key Requirements:**
- Each preset starts from reset history and reset jitter frame index.
- Captures happen only after warmup.
- The camera path is deterministic and exercises temporal motion.

#### TASK-318c: Runtime Capture Artifacts and Metrics
**Deliverable:** add Runtime automation activated by CLI to run the sequence and write per-preset captures plus metadata.

**Key Requirements:**
- `SomeEngine.Runtime --temporal-validation` runs the sequence.
- Captures do not include the ImGui overlay.
- Output includes image files and a JSON summary with preset settings, frame metadata, and image-difference metrics against the temporal-off baseline.
- Unsupported capture formats fail clearly.

#### TASK-318d: Verification, Report, and Review
**Deliverable:** focused tests, build verification, report, review, and residual-risk routing.

**Key Requirements:**
- Unit tests cover preset mapping, sequence timing, deterministic motion, and image-diff metrics.
- Existing temporal resolve/jitter/motion/history focused tests remain green.
- `BATCH-18-REPORT.md` and `BATCH-18-REVIEW.md` record remaining quality gaps separately from validation infrastructure.

**Success Conditions:**
- Temporal validation has shared presets, deterministic motion, warmup/capture sequencing, image artifacts, and numeric diff metrics.
- Runtime can run the validation sequence without manual ImGui interaction.
- Manual Runtime temporal controls remain available.

**Completion Evidence:**
- `TemporalValidationPresets`, `TemporalValidationRun`, `TemporalCameraPath`, and `TemporalImageDiff` live under `SomeEngine.Render.Frame`.
- Runtime validation automation can be enabled with `--temporal-validation`.
- Runtime automation writes `.tga` captures and `summary.json` with preset settings, frame metadata, jitter metadata, and diff metrics against the temporal-off baseline.
- Focused temporal validation tests pass: 29 / 29.
- `dotnet build SomeEngine.slnx --no-restore -v minimal -m:1` passes with the existing `tools/DagVisualizer` NU1903 warning.

### TASK-319: Standalone RHI Core
**Type:** foundational module
**Status:** DONE
**Scope:** `src/SomeEngine.Rhi/`, `tests/SomeEngine.Rhi.Tests/`, `docs/rhi/`
**Design Ref:** `docs/rhi/design_baseline.md`, `docs/rhi/api_spec.md`, `.dev-workstream/batches/BATCH-19-INSTRUCTIONS.md`

**Goal:** start the RHI from zero as an independent module, using a modern explicit API shape derived from high-performance maintained RHI designs. This task does not migrate the existing Diligent renderer.

**Current Evidence:**
- Earlier RHI notes established that the API should follow explicit device/queue/resource/view/pipeline/pass/sync boundaries rather than convenience wrappers.
- The API cannot be frozen before a D3D12 vertical slice, but the Null backend can validate the contract shape immediately.
- Existing renderer work in the workspace currently has independent `SomeEngine.Render.RHI` deletions/changes; the standalone RHI must not depend on those incomplete changes.

#### TASK-319a: API Spec And Project Shell
**Deliverable:** record the current public API target and add independent project/test wiring.

**Key Requirements:**
- `SomeEngine.Rhi` has no Diligent dependency.
- Public names are short inside `SomeEngine.Rhi`; no noisy `Rhi` prefix on every type.
- API spec records the actual public entry points and freeze gate.
- Tests live in an isolated `SomeEngine.Rhi.Tests` project so Render project state cannot mask RHI correctness.

#### TASK-319b: Core Contracts
**Deliverable:** typed handles, descriptors, validation errors, and public interfaces.

**Key Requirements:**
- Handles carry id/generation and default invalid semantics.
- Resources, views, shader modules, binding layouts, pipeline layouts, binding sets, pipelines, command buffers, fences, and swapchains are explicit handles.
- Hot-path binding uses set/binding/array indices, not strings.
- Shaders accept backend-native bytecode only; RHI does not compile shaders and does not perform shader reflection.
- Resource barriers, copy regions, queue waits/signals, render passes, and compute passes are explicit.

#### TASK-319c: Strict Null Backend
**Deliverable:** a complete Null backend that validates contract usage.

**Key Requirements:**
- Invalid/stale handles fail fast.
- Resource descriptors validate dimensions, flags, ownership, and view ranges.
- Binding sets validate declared slots, descriptor counts, array indices, and view kinds.
- Pipelines validate shader stages and attachment formats.
- Command encoders are one-shot and reject pass nesting errors.
- Queue submit validates command-buffer lifetime and timeline fence waits/signals.
- Swapchain resize destroys old texture/view handles and exposes the new current texture/view.

#### TASK-319d: RHI Contract Tests
**Deliverable:** focused external tests for the standalone module.

**Key Requirements:**
- Buffer creation, mapping, and invalid descriptors.
- Graphics pass, explicit barriers, one-shot submit, fence signal, swapchain present, resize, and swapchain-owned texture lifetime.
- Compute pipeline, binding layout/set compatibility, dispatch, and missing-resource failure.

#### TASK-319e: Review And Hardening
**Deliverable:** subagent review, fixes, report, and review notes.

**Key Requirements:**
- Review must check API/architecture and Null backend/test correctness separately.
- Blocking review items must be fixed before the batch is marked done.
- Full solution build is not a success criterion while unrelated existing Render project changes leave `SomeEngine.Render.RHI` unresolved; standalone RHI project and standalone RHI tests are the validation gate for this batch.

**Current Completion Evidence:**
- `dotnet build src\SomeEngine.Rhi\SomeEngine.Rhi.csproj -v minimal` passes with 0 warnings and 0 errors.
- `dotnet test tests\SomeEngine.Rhi.Tests\SomeEngine.Rhi.Tests.csproj -v minimal` passes 7 / 7.
- First review blockers were addressed by changing the architecture rather than adding compatibility shims: command buffers now record operations without side effects, queue submit validates before executing, command buffers carry queue class, texture state is tracked per subresource, binding set compatibility uses set-layout signatures, and query/debug/push-constant APIs are executable in Null.
- Attempting to run the old broad `SomeEngine.Tests` project fails before RHI tests because current workspace Render sources reference a missing `SomeEngine.Render.RHI` namespace. The RHI tests were therefore split into an isolated test project instead of coupling this task to that unrelated incomplete renderer state.

### TASK-320: RHI D3D12 Vertical Slice
**Type:** foundational backend module
**Status:** DONE
**Scope:** `src/SomeEngine.Rhi/`, `src/SomeEngine.Rhi.D3D12/`, `tests/SomeEngine.Rhi.Tests/`, `docs/rhi/`
**Design Ref:** `docs/rhi/api_spec.md`, `docs/rhi/design_baseline.md`, `.dev-workstream/batches/BATCH-20-INSTRUCTIONS.md`

**Goal:** bring the standalone RHI public contract up to the D3D12 freeze-target API, harden Null validation for that contract, and add the first D3D12 backend assembly. Existing Diligent renderer code is not migrated in this task.

**Current Evidence:**
- BATCH-19 delivered the strict Null-backed core but left D3D12 unimplemented.
- `docs/rhi/api_spec.md` now requires committed and placed memory, aliasing barriers, transient binding packets, ranged mapping, swapchain options, query/fence additions, and backend factory selection before API freeze.
- D3D12 backend code must stay in `SomeEngine.Rhi.D3D12`; `SomeEngine.Rhi` must not reference Vortice or native D3D12 types.
- BATCH-20 delivered the D3D12 backend vertical slice and expanded strict Null validation. Final scoped validation passed: core build, D3D12 build, and 85/85 RHI tests.

#### TASK-320a: Freeze-Target Core Contract
**Deliverable:** update core handles, descriptors, enums, interfaces, and validation helpers to match the agreed spec.

**Key Requirements:**
- Keep `*Handle` value tokens and do not introduce object wrappers.
- Add `MemoryHeapHandle`, memory requirements, memory heap descriptors, budget/allocation info, and placed resource APIs.
- Remove allocation ownership from `BufferDesc` / `TextureDesc`; ownership is queried through allocation info.
- Add aliasing barrier value types without a generic public `ResourceRef`.
- Add transient `SetBindings`, ranged map/flush/invalidate, CPU fence wait, swapchain buffer count/tearing/color-space fields, query begin/end, resolve texture, and debug marker push/pop/insert names.

#### TASK-320b: Null Freeze-Target Validation
**Deliverable:** implement the new public contract in Null with strict validation and observable behavior where appropriate.

**Key Requirements:**
- Validate placed heap kind, alignment, lifetime, allocation info, and aliasing barrier endpoints.
- Keep hot paths span/value based; do not add managed allocation-heavy wrappers.
- Preserve sparse Null texture backing and pooled command operation storage.
- Expand tests to cover the new core contract and failure paths.

#### TASK-320c: D3D12 Backend Assembly
**Deliverable:** add `SomeEngine.Rhi.D3D12` as an isolated backend project.

**Key Requirements:**
- Depend on Vortice only from the D3D12 assembly.
- Provide backend factory registration/creation through `DeviceDesc.Backend`.
- Enumerate adapters and create a D3D12 device/queues.
- Unsupported capabilities fail fast with RHI error categories.

#### TASK-320d: D3D12 Resource, Memory, And Commands
**Deliverable:** implement the D3D12 vertical slice primitives needed by standalone RHI tests/samples.

**Key Requirements:**
- Committed and placed buffers/textures, memory heaps, memory requirements, budget query, resource allocation info.
- CPU and shader-visible descriptor heap basics for views/binding.
- Explicit barriers including aliasing barriers, one-shot command buffers, queue submit/fence retirement.
- Swapchain clear/present/resize, timestamp query, upload/readback path, and debug markers.

#### TASK-320e: Tests, Review, And Hardening
**Deliverable:** focused tests, report, subagent review loops, and fixes until the batch is judged deliverable.

**Key Requirements:**
- Keep standalone RHI tests independent from current dirty Render project state.
- Run `SomeEngine.Rhi` and `SomeEngine.Rhi.Tests` validation.
- Use subagents for code review; fix blockers before reporting complete.

### TASK-321: Mature RHI Conformance Import
**Type:** validation hardening
**Status:** DONE
**Scope:** `docs/rhi/conformance_sources.md`, `tests/SomeEngine.Rhi.Tests/`, `.dev-workstream/batches/BATCH-21-INSTRUCTIONS.md`
**Design Ref:** `docs/rhi/api_spec.md`, `.dev-workstream/batches/BATCH-21-INSTRUCTIONS.md`

**Goal:** port Diligent and other mature RHI test-suite intent into runnable SomeEngine RHI conformance tests without importing incompatible API models or third-party build dependencies.

**Current Evidence:**
- Local Diligent source exists under `external/DiligentCore` with extensive API tests.
- Mature RHI projects cover resource creation, binding layouts, descriptor arrays, copy/resolve validation, explicit barriers, memory placement, command-buffer lifecycle, debug markers, queries, fences, and swapchain behavior.
- SomeEngine RHI must keep its explicit handle/descriptors/spans model and must not absorb Diligent SRB/name-variable/auto-transition semantics.
- First imported conformance slice is implemented in `tests/SomeEngine.Rhi.Tests/MatureRhiConformanceTests.cs` and passes with the full RHI test suite: 91/91.

#### TASK-321a: Source Matrix
**Deliverable:** record source suites, paths, licenses, and imported coverage categories.

#### TASK-321b: Executable Conformance Slice
**Deliverable:** add C# xUnit tests that port mature RHI test intent to the SomeEngine API.

**Key Requirements:**
- Buffer creation, initial data, mapping, and access validation.
- Descriptor arrays and static sampler conflict validation.
- Memory requirements, heap compatibility, placed allocation info, and heap lifetime.
- Texture view dimension/range validation.
- Copy footprint validation and source region observability.
- Debug marker stack, one-shot command buffers, duplicate submit, and fence monotonicity.

#### TASK-321c: Validation And Report
**Deliverable:** run scoped RHI tests and record report/review notes.

### TASK-322: Mature RHI Corpus Expansion
**Type:** validation hardening
**Status:** DONE
**Scope:** `docs/rhi/conformance_sources.md`, `tests/SomeEngine.Rhi.Tests/`, `.dev-workstream/batches/BATCH-22-INSTRUCTIONS.md`
**Design Ref:** `docs/rhi/api_spec.md`, `docs/rhi/conformance_sources.md`, `.dev-workstream/batches/BATCH-22-INSTRUCTIONS.md`

**Goal:** expand the mature RHI import from a first slice into a corpus-level inventory plus a larger executable matrix. This task exists because the local Diligent test tree is hundreds of files and more than one thousand raw test macro hits; the first slice must not be treated as full coverage.

**Current Evidence:**
- Local Diligent snapshot recorded in `docs/rhi/conformance_sources.md`: 566 test source/header files under `external/DiligentCore/Tests` and 1452 raw test macro/name hits.
- The raw count is not a unique runnable-test count. It includes include-only checks, parameterized helpers, backend reference tests, shader/tooling/archive tests, and Diligent-only public API concepts.
- SomeEngine RHI imports coverage intent into its own explicit v0 contract: handles/descriptors, explicit binding layouts, bytecode-only shaders, explicit barriers, placed memory, command lifecycle, queries, fences, and swapchain lifecycle.
- Expanded executable matrix added in `tests/SomeEngine.Rhi.Tests/MatureRhiCorpusMatrixTests.cs`.
- Scoped RHI validation passes: 291 / 291.
- Review P2s were fixed before closing the batch: compressed UAV format is tested as texture descriptor validation, unreachable MSAA mip-view coverage was replaced with a real MSAA single-slice view case, and same-subresource copy was renamed to the explicit state-precondition case it actually validates.

#### TASK-322a: Corpus Inventory
**Deliverable:** record corpus size, coverage buckets, and disposition.

**Key Requirements:**
- Record local Diligent source-file counts and raw test macro/name hit count.
- Separate runnable SomeEngine RHI contract coverage from out-of-bound Diligent API/tooling/include/refcount coverage.
- Keep deferred-feature categories visible without adding placeholder API to v0.

#### TASK-322b: Executable Matrix Expansion
**Deliverable:** add matrix xUnit tests for high-frequency mature RHI validation categories.

**Key Requirements:**
- Buffer, texture, view, sampler, binding layout/set, shader module, pipeline layout, and pipeline descriptor validation.
- Committed/placed memory heap, heap kind, alignment, overlap, and aliasing validation.
- Render pass, command encoder, copy, resolve, query, swapchain, mapping, and submit-time state-precondition validation.
- Valid shape smoke rows for common resource/view/binding/memory/command/swapchain paths.

#### TASK-322c: Validation And Review
**Deliverable:** run scoped RHI tests and review the expansion.

**Key Requirements:**
- Run `tests/SomeEngine.Rhi.Tests`.
- Use subagent review and fix blockers before marking the batch complete.

### TASK-323: RHI D3D12 Execution Coverage
**Type:** backend validation hardening
**Status:** DONE
**Scope:** `tests/SomeEngine.Rhi.Tests/NullRhiContractTests.cs`, `tests/SomeEngine.Rhi.Tests/D3D12BackendCoverageTests.cs`, `.dev-workstream/batches/BATCH-23-INSTRUCTIONS.md`
**Design Ref:** `docs/rhi/api_spec.md`, `.dev-workstream/reports/BATCH-22-REPORT.md`

**Goal:** add real D3D12 backend execution tests for GPU work that produces observable readback data. This closes part of the BATCH-22 D3D12 execution gap without changing the frozen public API.

**Current Evidence:**
- BATCH-22 broadens Null/backend-neutral contract coverage, but explicitly leaves D3D12 execution parity as separate backend work.
- D3D12 tests now exercise texture upload/readback, render-pass clear readback, graphics triangle draw readback, ordered timestamp resolve, placed aliased texture barrier enforcement, instance-rate vertex input through the public pipeline path, a single instance-color vertex attribute using the RHI `Location n -> ATTRIBn` D3D12 shader contract, hidden-HWND swapchain clear/present/resize, persistent and transient descriptor execution including sampler sampling, shader-visible descriptor reuse pressure, submitted transient descriptor retirement/reuse, retired command-buffer lifetime, map/flush/invalidate validation, timestamp query invalid cases, texture-to-texture copy readback, selected-subresource barriers, valid UAV barriers, indexed draw viewport/scissor output, and set-0 `b100+` CBV reservation validation.
- Scoped RHI validation passes: 309 / 309.
- D3D12/DXC environment gating now uses visible xUnit skip attributes; if execution reaches a D3D12 test after discovery, missing Windows/adapter/DXC is a fail-fast error instead of a silent pass.
- The triangle test uses explicit `CullMode.None`; the default RHI rasterizer still remains back-face culling with counter-clockwise fronts.
- D3D12 push constants are root constants at `b100+i, space0`; descriptor set index maps to native register space, and binding index maps to native register.

#### TASK-323a: GPU Copy And Render-Target Readback
**Deliverable:** D3D12 tests that prove copy and render-pass clear paths can write GPU resources and read back expected bytes.

**Key Requirements:**
- Upload an RGBA8 pattern through `CopyToTexture`.
- Transition the texture explicitly and read it back through `CopyToBuffer`.
- Clear a render target in a render pass, transition it, and read back quantized clear color bytes.

#### TASK-323b: Draw, Timestamp, And Placed Aliasing Execution
**Deliverable:** D3D12 tests that cover graphics draw output, timestamp ordering, and placed texture aliasing state.

**Key Requirements:**
- Compile simple DXIL vertex/pixel shaders when `dxc.exe` is available.
- Draw a triangle into an RGBA8 render target and verify a covered pixel reads back red.
- Draw a triangle whose only vertex input is an instance-rate color attribute and verify the readback pixel, locking the D3D12 `ATTRIB0` vertex-input semantic contract.
- Write two timestamp queries around GPU work and verify resolved values are ordered.
- Create overlapping placed textures on an aliasing heap, reject use without an aliasing barrier, and accept use after `AliasingBarrier.Between`.

#### TASK-323c: Validation And Review
**Deliverable:** run scoped RHI tests and review the added backend execution coverage.

**Key Requirements:**
- Run `tests/SomeEngine.Rhi.Tests`.
- Use subagent review for test robustness and fix blockers before closing the batch.
- Keep remaining gaps explicit: true onscreen presentation/HDR/exclusive fullscreen, long-run frame-retirement soak, performance stress, unsupported features, and Vulkan parity are still future work.

### TASK-330: RenderGraph RDG 模式完整重构
**Type:** destructive renderer refactor
**Status:** DONE
**Scope:** `src/SomeEngine.Render/Graph/`, `src/SomeEngine.Render/Pipelines/`, `src/SomeEngine.Runtime/`, `src/SomeEngine.Editor/`, `tests/SomeEngine.Tests/`, `docs/rendering/`
**Design Ref:** `docs/rendering/render_graph_rdg_rewrite.md`, `.dev-workstream/batches/BATCH-30-INSTRUCTIONS.md`

**Goal:** replace the current mixed-model RenderGraph with one RDG-style authoring model: frame-local graph authority, explicit import/extract boundaries, typed pass data, blackboard-driven same-frame exchange, unified compiler queue logic, and explicit renderer-owned persistent history. This is a full rewrite. It is not a compatibility migration.

**Authority Override:**
- When this task conflicts with `TASK-326b` or `TASK-327` on graph authoring, frame ownership, history ownership, binding routing, or queue-resolution model, `TASK-330` wins.
- Older batches remain useful only as local findings and migration evidence. They are not the final architecture authority.

**Current Evidence:**
- Current graph rebuild behavior is frame-local and acceptable, but frame-local truth is still split across `RenderGraph`, `FrameSurfaces`, `RenderHistoryRegistry`, runtime host code, and mutable pipeline fields.
- `ShareResource` / `GetSharedResource` still permit string-based publication that is not a real graph edge.
- Binding/resource routing still contains string-driven hot-path logic in cluster/material passes.
- Queue assignment is still split across multiple compile helpers instead of one compiled fact source.
- `profile-and-optimize-progress.md` shows `Runtime.RenderGraph.BeginFrame.RecordGraph` and queue/submit paths are real costs; localized patches do not solve the ownership problem.

#### TASK-330a: Public Authoring Contract Rewrite
**Deliverable:** replace the public frame authoring surface with one RDG-style contract.

**Key Requirements:**
- Keep `RenderGraph`, `RenderGraphBuilder`, and `RenderGraphContext`.
- Add `RenderGraphBlackboard` as the graph-lifetime same-frame exchange surface.
- Keep `ImportTexture` / `ImportBuffer` and `CreateTexture` / `CreateBuffer`.
- Replace current `ExportTexture` / `ExportBuffer` semantics with `ExtractTexture` / `ExtractBuffer`.
- Remove `ShareResource`, `GetSharedResource`, and `MarkOutput`.
- Replace generic command/compute pass overload sprawl with explicit `AddRasterPass`, `AddComputePass`, and `AddCopyPass` families using typed pass data.

#### TASK-330b: Compiler Rewrite
**Deliverable:** one compiler path that owns dependency derivation, culling, queue assignment, synchronization, lifetime, and barriers.

**Key Requirements:**
- Internal graph model uses graph-owned `Pass` and `Resource` objects, following UE RDG's `FRDGPass` / `FRDGResource` naming direction rather than `*Node` types.
- Queue assignment comes from one resolver only; remove split queue-decision helpers.
- Extraction is a cull root.
- Resource names become debug labels only.
- Compile caches remain internal and keyed by graph fingerprint only; they must not create a second public authoring model.

#### TASK-330c: Executor And Runtime Ownership Split
**Deliverable:** move frame execution and long-lived runtime services out of the frame authoring object.

**Key Requirements:**
- Add `RenderGraphCompiler` and `RenderGraphExecutor`.
- `RenderGraph` becomes frame-local authoring state only.
- Long-lived transient pools, cached views, bind-set caches, command-list reuse, and fence retirement move behind renderer-owned runtime services used by the executor.
- Execution cannot rediscover dependencies or queue ownership.

#### TASK-330d: Frame Data And History Rewrite
**Deliverable:** replace frame-local surface/history registries with typed frame data plus explicit history import/extract.

**Key Requirements:**
- Add typed immutable `FrameData`, `ViewData`, and `SceneTextures` graph data.
- Add explicit renderer-owned persistent `ViewHistory`.
- Delete `FrameSurfaces` and `RenderHistoryRegistry`.
- HiZ, temporal scene color, motion vectors, and future history resources use the same import/extract path.
- Backbuffer/present remains an imported external resource with final `Present` state.

#### TASK-330e: Binding And Pass-Data Rewrite
**Deliverable:** remove string-driven resource routing and make pass declaration/binding slot-based.

**Key Requirements:**
- Graph usage declaration is emitted from typed pass data, not `binding.Name` switches.
- Hot paths do not call resource routers like `FindShadeResource` or `AccessFor(string, ...)`.
- Reflection may still produce binding metadata, but steady-state pass execution binds by slot/index only.
- `PassBindings` may survive only as a slot-based convenience builder with immutable packets.

#### TASK-330f: Cluster / Material / Post / Host Integration Rewrite
**Deliverable:** migrate renderer authoring code to the new graph contract.

**Key Requirements:**
- `ClusterPipeline` loses hidden frame-local mutable state and graph-only entry points.
- Cluster/material/post/debug/ImGui/runtime/editor paths exchange graph resources through typed pass data and `RenderGraphBlackboard`.
- Frame-local IO is graph-authored; persistent history is renderer-owned and imported/extracted.
- No broad Diligent compatibility layer is introduced.

#### TASK-330g: Delete Legacy Model
**Deliverable:** remove the old mixed graph model rather than keeping it behind fallback switches.

**Key Requirements:**
- Delete string-based publication/exchange APIs.
- Delete frame-local surface/history registries.
- Delete graph-only legacy cluster entry.
- Delete dual API paths that represent the same ownership concept.
- Remove stale docs that still describe the superseded model.

#### TASK-330h: Validation, Simplify, Report, And Review
**Deliverable:** correctness gates, profiler evidence, simplify passes, and final batch artifacts.

**Key Requirements:**
- Graph compile tests cover extraction liveness, import write rules, duplicate names, queue assignment stability, and subresource state propagation.
- Cluster/material/post/runtime integration tests cover same-frame outputs and next-frame history reuse.
- Runtime bounded frame run passes on the default RHI backend.
- External-profiler evidence is recorded before and after the rewrite.
- Mandatory middle and final `simplify` passes are recorded in the batch report.

**Success Conditions:**
- One RDG-style authoring model remains.
- Renderer runtime still executes the required cluster/material/post/runtime path on the rewritten graph.
- No legacy graph compatibility path remains in product code.
