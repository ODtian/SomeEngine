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
- 更新 GpuInstanceHeader 材质字段为 MaterialSlotOffset
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

**Goal:** 将 `ClusterShadeBinStage` / `ClusterShadeStage` 残留替换为 `ClusterShade.cs`，并把已落地的 PSO 分组基础设施写清楚

**Work Required:**
- 更新实现文件列表
- 记录 `MaterialPSOGroup.ComputeShaderGroups()` 已落地
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
- `tests/SomeEngine.Tests/Pipelines/ShaderGroupTests.cs` 覆盖 group 形成路径
- `ClusterShadeSig1Tests.cs` 已随 dual-sig 架构删除
- `ClusterShade.BuildPSOGroups()` 现使用全 Dynamic 隐式签名，无 sig1 cache

**Success Conditions:**
- `ShaderGroupTests` 覆盖分组逻辑
- 全量测试通过

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
**Status:** DONE  
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

### TASK-311: 统一 FrameTarget 与 History 资源系统
**Type:** feature / architecture  
**Status:** TODO  
**Scope:** `src/SomeEngine.Render/Graph/`, `src/SomeEngine.Render/ClusterPipeline.cs`, `src/SomeEngine.Render/Cluster/`, `src/SomeEngine.Render/Materials/`, runtime frame setup  
**Design Ref:** `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md`

**Goal:** 建立统一的 FrameTarget 声明、解析、提取和 history 生命周期系统。用户定义的 frame target 与引擎标准 target 必须没有能力差异；SceneColor、SceneDepth、HiZ 等标准 target 只是预注册的 well-known key，不是拥有特权路径的特殊对象。

**Why:** `RenderGraph.Import(...)` 已经支持外部资源进入 graph，但 graph-created resource 缺少对称的 explicit extraction/history API。当前 runtime 仍手动导入 color/depth，HiZ history 也主要依赖稳定命名和 ping-pong helper。继续扩展下去会自然形成“内置 target 一套、用户 target 一套”的分裂模型，后续 post、TAA、SSR、debug target、editor overlay 和用户 pass 都会被迫绕路。

**Principles:**
- 内置 target 和用户 target 使用同一套声明 API、同一套 resolve 路径、同一套 extraction/history 生命周期。
- 标准 target 只能是 preset/key，例如 `StandardFrameTargets.SceneColor`，不能是特殊存储或特殊分支。
- 不提供 `DeclareBuiltin*` / `DeclareUser*` 这类分裂 API。
- 不强制用户 target 进入 `User.*` namespace；冲突通过 registry 的显式规则处理。
- `FrameTargetHandle` 表达跨帧稳定语义；`RenderGraphTextureHandle` / `RenderGraphBufferHandle` 仍然只表达单帧 graph 内资源。
- RenderGraph extraction primitive 不知道 FrameTarget key，只负责 graph resource 的生命周期和最终状态。

**Work Breakdown:**
- `TASK-311a` Unified FrameTargetRegistry：新增 renderer-level registry，支持 texture/buffer target declaration、descriptor factory、frame-local/history/imported lifetime、debug metadata、resize/format/sample-count invalidation、compatible merge、freeze validation、pre-freeze explicit override。标准 target 通过同一 API 预注册。
- `TASK-311b` RenderGraph 资源提取原语：为 `RenderGraph` 增加 explicit extraction API，使 graph-created texture/buffer 可以在 `Execute` 后成为外部资源、swapchain/debug 输出或下一帧 history 输入。Extraction 必须把 producer pass 视作 graph output，不能被 DCE 裁掉，并且要与现有 `Import(...)` 和自动 barrier/state tracking 协同。
- `TASK-311c` History 生命周期集成与 HiZ 迁移：把 history target 纳入统一 registry，迁移 HiZ ping-pong/history 路径。HiZ 应成为普通 history target 的第一个实用样例，而不是保留独立特殊机制。增加自定义 history target 测试，证明用户 target 与 HiZ 具有相同能力。
- `TASK-311d` Material fallback 资源绑定：关闭 `DEBT-017`。为缺失的 material texture/buffer/scalar slot 提供 renderer-owned deterministic fallback，避免 null binding 或 backend-specific 行为，同时保留诊断信息帮助发现资源缺失。
- `TASK-311e` Runtime / Pipeline 采用统一 FrameTargetRegistry：让 runtime frame setup 和 `ClusterPipeline` 通过 registry 声明、导入、解析 SceneColor/SceneDepth/HiZ 等 target。增加一个 custom target 集成测试或 sample path，验证自定义 target 可以像标准 target 一样被生产、提取、再消费。

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
- `BinQueue` / `BinSpace` / `MaterialSlotCache` 已切换到 Entity 数据源与 `RegisterGroup(BinGroup)` API
- `ClusterPipeline` / `ClusterShade` / `MaterialPSOGroup` / `RasterPSOBuilder` / `ClusterMaterialShadePass` 已按 Entity Component 消费材质
- `ShaderGroupTests` / `MaterialAssetPipelineTests` / `AssetResolverTests` 已迁移到 Entity-based 路线（`ClusterShadeSig1Tests` 随 dual-sig 删除）
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
- `src/SomeEngine.Assets/` 顶层已收敛到 `AssetGuid.cs`、`AssetRecord.cs`、`SchemaPartials.cs`、`MetaManagers.cs`、`AssetManifest.cs`、`AssetDatabase.cs`、`AssetPipelineContracts.cs` 与 provider/importer 实现
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

**Goal:** 把曾经的“mesh 默认材质 + 单实体材质 + 管线直接读材质 store”中间态，完整收敛到新的 authoring/runtime 分层：`MeshAsset` 只保留几何与 region/debug 元数据，实体 authoring 持有材质绑定，`MaterialAsset` 成为单一材质资产并直接存储匿名 pass 实体快照，渲染提交统一改为 `GameWorld -> RenderWorld -> prepare/queue`，管线仅通过 RenderWorld pass 实体上的强类型 component/tag 决定行为。

**Current Evidence:**
- `assets/Schema/mesh_asset.fbs` 不再包含 `default_material_guids` / `default_material_slots`
- `MaterialAsset` / `MaterialInstanceAsset` schema 只保留 `shader_guid` / `parent_guid`
- `MeshAssetProvider.GetDependencies()` 不再写入 mesh -> material 依赖
- `Material` 运行时对象已收敛为 `AssetGuid + ShaderParamBag + PassEntities[]`；不再保留首 pass `Entity` alias，也不再保留 `SetSampler()` no-op
- `RenderWorldExtractor` 把 authoring material binding 展开为 pass entities；`ClusterMaterialSlotPreparer` 只读 RenderWorld，不再回扫 `sourceStore`
- Cluster cull 只保留 Phase1/Phase2 HiZ 路径

#### TASK-309a: Mesh Region / Authoring Binding 重构
**Deliverable:** `MeshAsset` 删除具体 material 引用，只保留稳定 region/section 与 mesh-local material 元数据；实体 authoring 组件接管稳定的 mesh-local material linear table。  
**Key Requirements:**
- `MeshAsset` schema 不再出现 `default_material_guids` / `default_material_slots`
- glTF 导入继续产出 region 信息，但不再把默认材质依赖写回 mesh asset
- `MeshAssetProvider.GetDependencies()` 不再返回 material 依赖
- 新的 ECS authoring 组件可表达稳定 mesh-local material linear table，并为未来 scene/prefab 持久化提供 canonical 结构

#### TASK-309b: MaterialAsset Pass Entity 模型落地
**Deliverable:** `MaterialAsset` 仍保持单一资产层级，但直接存储 `root params + pass entity snapshots`。  
**Key Requirements:**
- 不新增第二种 material asset，不新增编译后 material graph 资产
- pass 是直接可反序列化的实体快照，component/tag 直接挂在 pass 实体上，而不是挂在额外 feature 容器上
- 显式 authoring 与 shader metadata 都能生成 pass；最终运行时只消费同一份显式 `MaterialAsset`
- `Material` 运行时对象不再强制等价为单实体，而是持有 root params 与 pass entity/template 集合

#### TASK-309c: RenderWorld Extract / Prepare / Queue 分层
**Deliverable:** 引入 RenderWorld 作为唯一渲染提交真相源，把 authoring 实体展开成 `(source entity, local material slot, material pass)` 级别的执行单元。  
**Key Requirements:**
- `GameWorld` 保留 authoring 数据；`RenderWorld` 只保留执行态
- Extract 链路正确处理创建、更新、删除和多 region / 多 pass 扩张
- 组件/tag 从 `MaterialAsset` pass 快照稳定落到 RenderWorld pass 实体
- `ClusterPipeline` 与后续管线不得再直接 query 材质 pass store

#### TASK-309d: 管线派生数据下沉与 ClusterPipeline 迁移
**Deliverable:** `BinSpace` / `BinQueue` / `MaterialSlotBuffer` 等 stage/bin 派生结构不再作为材质核心设施，而是变为具体管线的 prepare/queue 派生数据。  
**Key Requirements:**
- Cluster pipeline 通过 RenderWorld pass 实体上的强类型 component/tag 选择需要提交的工作
- 材质系统不再假设 `gbuffer` / `depthonly` / `overlay` / shader 变量名等任何管线细节
- mesh-local material table 和 pass 展开后的 GPU 提交路径统一生成 `MaterialSlotOffset`
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
- `MaterialAsset` 成为唯一材质资产，并能 round-trip `pass entity snapshot`
- RenderWorld 成为唯一渲染提交真相源，管线只 query pass 实体上的 component/tag
- `BinSpace` / `MaterialSlotBuffer` 等派生结构不再作为材质核心概念暴露
- Runtime / Editor / Tests / Docs 全部切到新模型
- 全量编译和相关测试通过

#### TASK-309f: Mesh Local Material Table Semantics Cleanup
**Deliverable:** 把 `MeshMaterialBindings` 从“`region -> material` 映射”收敛为稳定的 mesh-local material linear table / local material slot 语义，使 cluster runtime 不再把 `region` 当作运行时映射概念。  
**Key Requirements:**
- `region` 只允许作为 asset/import/debug 元数据存在，不能再成为 cluster runtime 的核心提交概念
- `MeshMaterialBindings` 的 canonical 含义必须是稳定的 mesh-local material 顺序，运行时提交只消费 local material slot
- `cluster_structures.slang` / `cluster_binning.slang` / `cluster_shade_binning.slang` 当前使用的 `local material index + MaterialSlotOffset` 语义必须成为主设计真相
- 相关 schema / authoring / importer / docs 命名需要同步，不能继续用“region”误导运行时模型

#### TASK-309g: RenderWorld Extract Local-Slot Rework
**Deliverable:** RenderWorld 只展开 `(source entity, local material slot, material pass)` 提交事实；extract 路径不再为 `region` 映射保留桥接数据结构。  
**Key Requirements:**
- RenderWorld pass entity 只保留 queue/prepare 真正需要的 submission facts，不再保留“按 region 回拼”的中间态组件
- extract 路径正确处理创建、更新、删除以及多 material slot / 多 pass 展开
- per-frame extract 路径禁止 `Dictionary`，禁止为 steady-state frame loop 引入新的托管分配
- RenderWorld 仍需保持强类型 component/tag query 能力，不能因为去掉桥接层而退回弱类型查找

#### TASK-309h: Cluster Prepare Zero-GC Slot Folding
**Deliverable:** 把 slot folding 直接收敛到 cluster pipeline 自己的 prepare 路径，并满足每帧 0 GC、禁止字典。  
**Key Requirements:**
- `BinSpace` / `BinQueue` / `MaterialSlotBuffer` 仍然是 cluster pipeline 自己的派生数据，而不是 material system 核心设施
- prepare 路径直接从 extracted local material slot 顺序折叠出 slot / bin，不再做 `source + region` 回扫映射
- 所有每帧调用热点路径禁止 `Dictionary`，禁止 `List`/`ToArray`/LINQ/`Array.Resize` 这类会造成 steady-state 分配的写法
- overlay / primary shade / vertex eval 选择逻辑必须在新 prepare 模型里显式收口，不能再依赖启发式桥接器
- GPU header / shader 只消费 prepare 阶段生成的 `MaterialSlotOffset`

#### TASK-309i: Host / Test / Docs Cleanup And Guardrails
**Deliverable:** Runtime / Editor / Tests / Docs 全部切换到 mesh-local material linear table 语义，并补上 hot-path zero-allocation / no-Dictionary 守护。  
**Key Requirements:**
- 删除与 `region` 运行时映射绑定的旧 API
- Runtime / Editor 启动链路按新 extract / prepare 边界工作，只有一条 authoring -> extract -> prepare 数据路径
- 为 extract / prepare 热点路径补 allocation regression 测试，验证 steady-state 调用不分配托管内存
- 工程记录必须明确写出“每帧 0 GC、禁止字典”约束，不能只留在实现备注里
