# BATCH-06: Material ECS 重构 — 瘦 Material + BinGroup 动态 Region

**Batch Number:** BATCH-06  
**Tasks:** TASK-304  
**Phase:** Phase 3 - Next Features  
**Estimated Effort:** full rework  
**Priority:** HIGH  
**Dependencies:** BATCH-05

---

## Onboarding & Workflow

### Developer Instructions
本批是 Material 系统的完整重构。目标是将 `MaterialPass / TagStore / MaterialRegistry` 全部删除，迁移到 friflo `EntityStore`——1 Material = 1 Entity，管线通过 ECS query 消费，BinQueue 通过动态 BinGroup 产出有序 region。

### Required Reading (IN ORDER)
1. **Workflow Guide:** `.dev-workstream/guides/DEV-GUIDE.md`
2. **Onboarding:** `ONBOARDING.md`
3. **Design (核心):** `docs/materials/architecture.md` — v10 全文
4. **Task Details:** `docs/TASK-DETAIL.md` — TASK-304
5. **Previous Review:** `.dev-workstream/reviews/BATCH-05-REVIEW.md`
6. **Current Material Code:** `src/SomeEngine.Render/Materials/` 全部文件
7. **Current Pipeline Code:** `src/SomeEngine.Render/Pipelines/ClusterRender/` — ClusterShade.cs, ClusterPipeline.cs, ShadePSOGroup.cs, RasterPSOBuilder.cs, ClusterMaterialShadePass.cs
8. **Current Loader:** `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`, `MaterialInstanceLoader.cs`
9. **App Entry Points:** `src/SomeEngine.Runtime/Program.cs`, `src/SomeEngine.Editor/Program.cs`
10. **Current Tests:** `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/Assets/`

### Source Code Locations
| Area | Path |
|---|---|
| Material 核心 (改) | `src/SomeEngine.Render/Materials/Material.cs` |
| MaterialPass (删) | `src/SomeEngine.Render/Materials/MaterialPass.cs` |
| MaterialRegistry (删) | `src/SomeEngine.Render/Materials/MaterialRegistry.cs` |
| BinQueue (改) | `src/SomeEngine.Render/Materials/BinQueue.cs` |
| BinSpace (改) | `src/SomeEngine.Render/Materials/BinSpace.cs` |
| MaterialSlotBuffer (不变) | `src/SomeEngine.Render/Materials/MaterialSlotBuffer.cs` |
| MaterialSlotCache (改) | `src/SomeEngine.Render/Materials/MaterialSlotCache.cs` |
| ShaderParamBag (不变) | `src/SomeEngine.Render/Materials/ShaderParamBag.cs` |
| 管线消费 (改) | `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` |
| PSO 构建 (改) | `src/SomeEngine.Render/Pipelines/ClusterRender/ShadePSOGroup.cs`, `RasterPSOBuilder.cs` |
| dispatch pass (改) | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialShadePass.cs` |
| 管线入口 (改) | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs` |
| 资产加载器 (改) | `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`, `MaterialInstanceLoader.cs` |
| Runtime 入口 (改) | `src/SomeEngine.Runtime/Program.cs` |
| Editor 入口 (改) | `src/SomeEngine.Editor/Program.cs` |
| ECS Component (新) | `src/SomeEngine.Render/Pipelines/ClusterRender/Components/` |
| IComponentAuthor (新) | `src/SomeEngine.Render/Materials/IComponentAuthor.cs` |
| 测试 (迁移) | `tests/SomeEngine.Tests/Materials/MaterialAssetPipelineTests.cs` |
| 测试 (迁移) | `tests/SomeEngine.Tests/Pipelines/ShaderGroupTests.cs` |
| 测试 (迁移) | `tests/SomeEngine.Tests/Assets/AssetResolverTests.cs` |

### Build & Test Commands
```bash
dotnet build src/SomeEngine.Render/SomeEngine.Render.csproj
dotnet build src/SomeEngine.Runtime/SomeEngine.Runtime.csproj
dotnet build src/SomeEngine.Editor/SomeEngine.Editor.csproj
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~Material"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~BinQueue"
```

### Report Submission
完成后提交到：  
`.dev-workstream/reports/BATCH-06-REPORT.md`

如需提问，创建：  
`.dev-workstream/questions/BATCH-06-QUESTIONS.md`

---

## Context

BATCH-05 完成了 ShaderAsset metadata → runtime 通路。本批是 TASK-304 的完整实现，彻底删除 `MaterialPass` / `TagStore` / `MaterialRegistry`，用 friflo ECS 替代。

v10 架构决策摘要（完整设计见 `docs/materials/architecture.md`）：

1. **瘦 Material**：1 Material = 1 shader + 1 ShaderParamBag + tags = 1 Entity
2. **1:1 映射**：Material.Entity 是唯一的 ECS Entity，dispatch 只读 Entity Component
3. **管线 Component**：`ClusterRaster`, `ClusterShade`, `ClusterDeform` 等由管线定义，携带 `ShaderVariantRef` 字段
4. **IComponentAuthor**：Importer 透传 Slang attribute → Author 填 Component。Author 单阶段，只操作当前 Entity
5. **多 pass 角色靠 tag/Component**：overlay 通过在 Entity 上 `AddComponent(new OverlayShade { Layer })` 表达，无容器层级
6. **BinGroup 动态 region**：`RegisterGroup(query, orderKey, sigFunc)` 替代固定 `RegisterRegion`。Rebuild 按 orderKey 自动产出有序 BinRange[]
7. **无跨 Entity 引用**：不存在 ReusePixels{Primary=Entity}。dispatch 顺序由 BinGroup 的 region 排列保证
8. **Stencil**：`StencilState` IComponent，PSO 构建时读取
9. **overlay IndirectArgs 复用**：BinQueue 维护 `_argsBinMap[bin] → argsBin`，overlay bin dispatch 用 primary bin 的 IndirectArgs

**Related Tasks:**
- TASK-304 — Material ECS 重构

---

## Batch Objectives

- 删除 `MaterialPass.cs`、`MaterialRegistry.cs`、`Materials/Tags/` 下所有 `IMaterialTag` 相关类型
- Material 改为 1 shader + 1 Params + 1 Entity，删除 `AddPass()`、`Passes` 属性、multi-pass resolve 逻辑
- 定义管线 Component 类型（`ClusterRaster`, `ClusterShade`, `ClusterDeform`, `OverlayShade`, `StencilState`, `MaterialRef`）
- 实现 `IComponentAuthor` 接口 + 注册表 + `ClusterRasterAuthor` / `ClusterShadeAuthor` / `StencilConfigAuthor`
- BinQueue 从 `RegisterRegion(name, Func<MaterialPass[]>, Func<MaterialPass, ulong>)` 改为 `RegisterGroup(BinGroup)` + 动态 region
- BinSpace / MaterialSlotCache 的数据源从 `MaterialPass` 改为 `Entity`
- 管线消费端（ClusterShade、ShadePSOGroup、RasterPSOBuilder、ClusterPipeline、ClusterMaterialShadePass）全部改为从 Entity Component 读取
- MaterialAssetLoader 改为创建 Entity + 调用 Author
- Runtime/Editor Program.cs 移除 MaterialRegistry DI 注册
- 旧测试（ShaderGroupTests、MaterialAssetPipelineTests、AssetResolverTests）迁移到 Entity-based
- 全量测试通过

---

## Preparation Work (Non-task)

1. 通读 `docs/materials/architecture.md` v10 全文
2. 盘点所有引用 `MaterialPass` / `MaterialRegistry` / `TagStore` / `IMaterialTag` 的文件（已列出，详见 Source Code Locations）
3. 确认 friflo ECS 的 `EntityStore`、`IComponent`、`ITag`、archetype query API 可用性（看 `src/Friflo.Engine.ECS/`）
4. **EntityStore 归属**：Material 使用独立的全局 static `EntityStore`，不与 `GameWorld.EntityStore` 共用。Material 生命周期独立于场景，跨场景复用。建议放在 `MaterialSystem.Store`（static 属性）或等效全局入口

### ⚠️ ShaderAsset Schema 现状

当前 `shader_asset.fbs` 的 `ShaderMetadata` 只包含 `tags` 和 `material_bindings`。**不包含 entry point 的 user attribute（如 `[ClusterShade]`、`[StencilConfig]`）**。

Importer（`SlangShaderImporter.cs` L161-192）只从 Slang 反射读取 `[PipelineTag]` attr → `metadata.tags`，不提取其余 entry point attribute。

**本批需要扩展**：
- schema 增加 entry point attribute 表：`ShaderEntryPointAttribute { name: string; args: [string]; }`
- ShaderAsset 增加 `entry_point_attributes: [ShaderEntryPointAttribute]`（每个 entry point 的 attribute 列表）
- Importer 增加对 Slang entry point user attribute 的提取逻辑
- 这些扩展作为 Task 1 的一部分完成（不单列 task）

### argsBinMap 限制

overlay bin → primary bin 的 IndirectArgs 映射假设同一 overlay Entity 总是对应同一个 primary Entity。如果同一 overlay material 在不同 instance 上 overlay 不同的 primary material，需要 overlay bin 按 primary 拆分。当前设计不处理此情况（实际场景中 tattoo 只 overlay 皮肤，不需要拆分）。

### MaterialPass/MaterialRegistry 引用完整清单

以下文件引用了待删除类型，**全部必须改**：

**src/（产品代码）：**
| 文件 | 引用类型 |
|---|---|
| `Materials/MaterialPass.cs` | 定义（删除） |
| `Materials/MaterialRegistry.cs` | 定义（删除） |
| `Materials/Material.cs` | MaterialPass（重构） |
| `Materials/BinQueue.cs` | MaterialPass（重构） |
| `Materials/BinSpace.cs` | MaterialPass, MaterialRegistry（重构） |
| `Materials/MaterialSlotCache.cs` | MaterialPass（重构） |
| `Pipelines/ClusterRender/Stages/ClusterShade.cs` | MaterialPass, MaterialRegistry（重构） |
| `Pipelines/ClusterRender/ShadePSOGroup.cs` | MaterialPass（重构） |
| `Pipelines/ClusterRender/RasterPSOBuilder.cs` | MaterialPass（重构） |
| `Pipelines/ClusterRender/ClusterPipeline.cs` | MaterialRegistry（重构） |
| `Assets/MaterialAssetLoader.cs` | MaterialRegistry（重构） |
| `Assets/MaterialInstanceLoader.cs` | MaterialRegistry（重构） |

**src/（入口点）：**
| 文件 | 引用类型 |
|---|---|
| `SomeEngine.Runtime/Program.cs` | MaterialRegistry DI 注册（L138, L365, L369） |
| `SomeEngine.Editor/Program.cs` | MaterialRegistry 创建（L98） |

**tests/：**
| 文件 | 引用类型 |
|---|---|
| `Pipelines/ShaderGroupTests.cs` | MaterialRegistry（7 处） |
| `Materials/MaterialAssetPipelineTests.cs` | MaterialRegistry + TagStore<MaterialPass>（5 处） |
| `Assets/AssetResolverTests.cs` | MaterialRegistry（2 处） |

---

## Tasks

> **Task 排序原则**：先创建新类型 → 再迁移消费端 → 最后删除旧类型。每个 task 结束时项目必须编译通过。

### Task 1: 定义管线 Component + IComponentAuthor 框架 (TASK-304a)

**Files:**
- `src/SomeEngine.Render/Materials/IComponentAuthor.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/ClusterRaster.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/ClusterShadeComponent.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/ClusterDeform.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/OverlayShade.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/StencilState.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/MaterialRef.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Authors/ClusterRasterAuthor.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Authors/ClusterShadeAuthor.cs` (NEW)
- `src/SomeEngine.Render/Pipelines/ClusterRender/Authors/StencilConfigAuthor.cs` (NEW)

**Design Ref:** `docs/materials/architecture.md` — "Shader 变体系统 > Component 字段 = 变体引用"、"导入管线 > IComponentAuthor 接口"

**Description:**
定义 friflo `IComponent` / `ITag` 类型、`IComponentAuthor` 接口，以及 ShaderAsset schema 的 entry point attribute 扩展。纯新增，不改旧代码（除 schema 和 importer）。

**Sub-steps:**

1. **Schema 扩展**：`shader_asset.fbs` 增加 `ShaderEntryPointAttribute { name: string; args: [string]; }`；`ShaderAsset` 增加 `entry_point_attributes: [ShaderEntryPointAttribute]`（按 variant index 排列）
2. **Importer 扩展**：`SlangShaderImporter.cs` 在 L206-300 的 entry point 循环中，使用 Slang 反射 API（`entryPoint.GetUserAttributeCount() / GetUserAttribute(i)`）提取 user attribute name + args，写入 `entry_point_attributes`
3. **IComponentAuthor 接口** + **ComponentAuthorRegistry**
4. **Component / Tag 类型定义**
5. **Author 实现**

**Requirements:**
- `ShaderEntryPointAttribute` FlatBuffers table（参见上面 schema 扩展）
- `IComponentAuthor` 接口：
  ```csharp
  public interface IComponentAuthor
  {
      string AttributeName { get; }  // e.g. "ClusterShade", "StencilConfig"
      void Author(Entity entity, ReadOnlySpan<string> args, int variantIndex, AssetGuid shaderGuid);
  }
  ```
  参数含义：`args` 来自 `ShaderEntryPointAttribute.Args`；`variantIndex` 是 ShaderAsset.Variants 的 index；`shaderGuid` 用于构造 `ShaderVariantRef`
- `ComponentAuthorRegistry` 类：`Register(IComponentAuthor)` + `bool TryAuthor(string attrName, Entity entity, ReadOnlySpan<string> args, int variantIndex, AssetGuid shaderGuid)`
- `ShaderVariantRef` readonly record struct：`(AssetGuid ShaderAsset, int VariantIndex)`
- 所有 Component 实现 friflo `IComponent`
- `ClusterRaster` 包含 5 个 `ShaderVariantRef` 字段：`SWInline`, `SWCached`, `HWVSInline`, `HWVSCached`, `HWPS`
- `ClusterShadeComponent` 包含 `ShaderVariantRef Default`（命名避免与 `Stages/ClusterShade.cs` 静态类冲突）
- `ClusterDeform` 包含 `ShaderVariantRef Default`
- `OverlayShade` 包含 `byte Layer`
- `StencilState` 包含 `byte Ref`, `StencilOp PassOp`, `ComparisonFunc Compare`
- `MaterialRef` 包含 `Material Owner`
- ITag 定义：`struct Opaque : ITag {}`, `struct Masked : ITag {}`, `struct Translucent : ITag {}`, `struct TwoSided : ITag {}`（新建文件 `Components/MaterialTags.cs`）
- Author 单阶段：只操作当前 Entity，不引用其他 Entity
- `ClusterShadeAuthor` 不处理 overlay（overlay 角色由使用侧 runtime 设）
- Slang 反射 API 路径（`external/SlangShaderSharp/src/Reflection/`）：
  - `IEntryPoint.GetFunctionReflection()` → `FunctionReflection`
  - `FunctionReflection.AttributeCount` / `GetAttribute(uint index)` → `AttributeReflection`
  - `AttributeReflection.Name` / `GetArgumentValueString(uint index)` / `ArgumentCount`
- 注意：`MaterialRef { Material Owner }` 是 struct 持有 class 引用。friflo IComponent 是 struct，但 C# struct 可以持有引用类型字段，技术上可行

**Tests Required:**
- ✅ ClusterRasterAuthor 从 mock args 正确填充各 variant 字段
- ✅ ClusterShadeAuthor 填充 Default 字段
- ✅ StencilConfigAuthor 从 args 填充 StencilState（e.g. args=["1", "always", "replace"]）
- ✅ ComponentAuthorRegistry 按 AttributeName 正确分发，未知 name 返回 false
- ✅ Importer 对带 `[ClusterShade]` attribute 的 entry point 正确产出 `ShaderEntryPointAttribute`

---

### Task 2: BinQueue 改为 BinGroup 动态 Region (TASK-304b)

**Files:**
- `src/SomeEngine.Render/Materials/BinQueue.cs` (REFACTOR)

**Design Ref:** `docs/materials/architecture.md` — "Bin 系统 > BinGroup"

**Description:**
BinQueue 内部改为 Entity-based。`RegisterRegion` 改为 `RegisterGroup`。旧的 `MaterialPass` 相关 API 全部改为 `Entity`。

注意：此 task 改 BinQueue 内部实现。BinQueue 的旧调用方（BinSpace、ClusterPipeline 等）在 Task 4/5 中改。为了中间状态编译通过，保留旧的 `RegisterRegion` 签名为 `[Obsolete]` 空壳，Task 5/6 改完调用方后在 Task 6 删除。

**Requirements:**
- `BinGroup` struct：`Func<Entity[]> Query`, `Func<Entity, int> OrderKey`, `Func<Entity, ulong> SignatureFunc`
- `RegisterGroup(BinGroup)` 新方法
- Rebuild 逻辑：执行所有 group query → 按 orderKey 分组 → 每组内按 signature 去重 → region 按 key 升序排列
- `GetRanges()` 返回所有 region 的有序 `BinRange[]`
- `GetEntity(int binIndex)` 返回该 bin 的代表 Entity（等价于旧的 `GetPass`）
- `BinOf(Entity)` 反向查找
- `_argsBinMap[bin]` 支持 overlay → primary IndirectArgs 映射：
  - `GetArgsBin(int bin)` 返回 overlay bin 对应的 primary bin index
  - primary bin → 返回自身
  - overlay bin → 返回 orderKey=0 的 region 中、与该 overlay Entity 同 SignatureFunc 值的 primary bin
- `RegisterRegion` 标记 `[Obsolete]`，保留空壳供中间状态编译

**Tests Required:**
- ✅ 注册多个 BinGroup，Rebuild 后 region 按 orderKey 升序排列
- ✅ 同一 orderKey 的 Entity 归入同一 region
- ✅ 不同 orderKey 产出不同 BinRange
- ✅ 同 region 内相同 signature 的 Entity 合并为同一 bin
- ✅ GetEntity(bin) 返回正确 Entity
- ✅ GetArgsBin 对 primary bin 返回自身，对 overlay bin 返回正确的 primary bin

---

### Task 3: BinSpace / MaterialSlotCache 数据源迁移 (TASK-304c)

**Files:**
- `src/SomeEngine.Render/Materials/BinSpace.cs` (REFACTOR)
- `src/SomeEngine.Render/Materials/MaterialSlotCache.cs` (REFACTOR)
- `src/SomeEngine.Render/Materials/MaterialSlots.cs` (REFACTOR if needed)

**Design Ref:** `docs/materials/architecture.md` — "Bin 系统 > BinSpace"

**Description:**
BinSpace 和 MaterialSlotCache 的所有 `MaterialPass` 参数改为 `Entity`。

**Requirements:**
- `BinSpace.RegisterField` 接收 `BinQueue`（已改为 Entity-based）
- `RegisterSlots` 接收 `Entity[]`，slot cache key 从 Entity 上的 Component hash
- RebuildIfDirty 正确产出 `ushort[]` SOA 数据
- MaterialSlotBuffer 本身不改（纯数据容器）
- 编译通过

**Tests Required:**
- ✅ RegisterSlots 用 Entity 注册后 GetSlotData 产出正确 SOA 布局
- ✅ 相同 Component 组合的 Entity 共享 slot 区间
- ✅ 不同 Component 组合产出不同 slot 区间

---

### Task 4: 管线消费端迁移 (TASK-304d)

**Files:**
- `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` (REFACTOR)
- `src/SomeEngine.Render/Pipelines/ClusterRender/ShadePSOGroup.cs` (REFACTOR)
- `src/SomeEngine.Render/Pipelines/ClusterRender/RasterPSOBuilder.cs` (REFACTOR)
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs` (REFACTOR)
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialShadePass.cs` (REFACTOR)

**Design Ref:** `docs/materials/architecture.md` — "PSO/SRB 管理"、"多 Pass 组合 > BinQueue 直接 query"

**Description:**
管线代码全部从 `MaterialPass` 改为 Entity Component。

具体改动：
- `ShadePSOGroup.ComputeShaderGroups`：从 BinQueue 的 `GetEntity(bin)` 读 `ClusterShadeComponent.Default` variant
- `ClusterShade.BuildPSOGroups`：从 Entity 读 ShaderVariantRef
- `ClusterShade.BuildSig1Resources`：从 Entity 的 `MaterialRef.Owner.Params` 读取资源布局
- `ClusterShade.ComputeSig1CacheKey`：从 Entity 读取
- `ClusterPipeline`：移除 `MaterialRegistry` 字段，改为 `EntityStore` 引用。BinQueue 注册改为 `RegisterGroup`（primary/overlay/masked）。dispatch 循环改为 `foreach range in GetRanges() → dispatch → barrier`
- `ClusterMaterialShadePass`：dispatch 循环改用 `GetArgsBin(bin)` 获取 IndirectArgs 偏移
- PSO 构建时从 Entity 读 `StencilState` Component 合入 DepthStencilDesc

**Requirements:**
- 零 `MaterialPass` 引用
- 零 `MaterialRegistry` 引用
- `ClusterPipeline` 构造函数 / 注入改为接收 `EntityStore`
- dispatch 循环：`foreach range in GetRanges() → dispatch bins → barrier`
- Sig1 cache key 从 Entity Component 读取

**Tests Required:**
- ✅ 编译通过，零 MaterialPass / MaterialRegistry 残留
- ✅ ShadePSOGroup 能从 Entity Component 构建 shader groups
- ✅ 已有 ClusterShadeSig1Tests 迁移到 Entity-based 后通过

---

### Task 5: 资产加载器 + 入口点迁移 (TASK-304e)

**Files:**
- `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` (REFACTOR)
- `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs` (REFACTOR)
- `src/SomeEngine.Runtime/Program.cs` (REFACTOR — 移除 MaterialRegistry DI)
- `src/SomeEngine.Editor/Program.cs` (REFACTOR — 移除 MaterialRegistry 创建)

**Design Ref:** `docs/materials/architecture.md` — "材质资产 (.mat) > 加载流程"

**Description:**
加载器改为：
1. 创建 Material 对象（Name, ShaderAssetName, Params）
2. 加载 ShaderAsset
3. 在 EntityStore 创建 Entity，挂 `MaterialRef { Owner = material }`
4. 从 .mat tags 设 `entity.AddTag<Opaque>()` 等
5. 遍历 ShaderAsset variant 的 entry point attributes → 调用 `ComponentAuthorRegistry` 中注册的 Author
6. `material.Entity = entity`

Runtime/Editor Program.cs：
- 移除 `MaterialRegistry` 的创建 / DI 注册 / 获取
- 改为注册 `EntityStore`（如果还没注册）和 `ComponentAuthorRegistry`

**Requirements:**
- 加载器不解释管线概念
- Author 注册表通过 DI 或参数注入
- 编译通过（所有 `MaterialRegistry` 引用已消除）

**Tests Required:**
- ✅ 加载带 `[ClusterShade]` attribute 的 ShaderAsset 后 Entity 有 ClusterShadeComponent
- ✅ 加载带 `[StencilConfig]` attribute 的 ShaderAsset 后 Entity 有 StencilState
- ✅ .mat tags: `[opaque]` 加载后 Entity 有 Opaque ITag
- ✅ Runtime/Editor 编译通过

---

### Task 6: Material 瘦化 + 旧类型删除 + 测试迁移 (TASK-304f)

**Files:**
- `src/SomeEngine.Render/Materials/Material.cs` (REFACTOR)
- `src/SomeEngine.Render/Materials/MaterialPass.cs` (DELETE)
- `src/SomeEngine.Render/Materials/MaterialRegistry.cs` (DELETE)
- `src/SomeEngine.Render/Materials/Tags/` (DELETE 目录)
- `src/SomeEngine.Render/Materials/BinQueue.cs` (清理：删除 `[Obsolete]` `RegisterRegion` 空壳)
- `tests/SomeEngine.Tests/Pipelines/ShaderGroupTests.cs` (迁移到 Entity-based)
- `tests/SomeEngine.Tests/Materials/MaterialAssetPipelineTests.cs` (迁移到 Entity-based)
- `tests/SomeEngine.Tests/Assets/AssetResolverTests.cs` (迁移到 Entity-based)

**Design Ref:** `docs/materials/architecture.md` — "Material — 资产/参数容器"

**Description:**
- Material 改为瘦模型：删除 `_passes` 字段、`Passes` 属性、`AddPass()`、`Resolve()`、`InvalidateResolvedPasses()`
- Material 保留：`Name`, `AssetGuid`, `ShaderAssetName`, `AssetPath`, `Params`, `Entity`, `SetTexture()`, `SetSampler()`, `SetBuffer()`, `Instantiate()`, `Dispose()`
- `Instantiate()` 改为：clone Params + 在 EntityStore 创建新 Entity（复制原 Entity 的所有 Component/Tag）
- 删除 `MaterialPass.cs` 和 `MaterialRegistry.cs` 文件
- 删除 `Materials/Tags/` 目录
- 删除 BinQueue 中 `RegisterRegion` 的 `[Obsolete]` 空壳
- 迁移 3 个测试文件：移除 `MaterialRegistry` / `TagStore<MaterialPass>` 用法，改为 EntityStore + Component query

**Requirements:**
- `MaterialPass.cs` 文件物理删除
- `MaterialRegistry.cs` 文件物理删除
- `Material.cs` 无 `MaterialPass` 引用
- 全量编译通过
- 全量测试通过

**Tests Required:**
- ✅ Material 创建后有且仅有 1 个 Entity
- ✅ `Instantiate()` 产出独立 Entity + 独立 Params，引用相同纹理
- ✅ `SetTexture` 修改 Params 后通过 `MaterialRef.Owner.Params` 可读到
- ✅ ShaderGroupTests 迁移后全部通过
- ✅ MaterialAssetPipelineTests 迁移后全部通过
- ✅ AssetResolverTests 迁移后全部通过
- ✅ 全量测试通过

---

## Mandatory Workflow: Test-Driven Task Progression

1. **Task 1:** Component + Author 定义（纯新增）→ 写测试 → **全绿** ✅
2. **Task 2:** BinQueue BinGroup（内部重构 + 保留旧 API 空壳）→ 写测试 → **全绿** ✅
3. **Task 3:** BinSpace/SlotCache 迁移 → 写测试 → **全绿** ✅
4. **Task 4:** 管线消费端迁移 → 写测试 → **全绿** ✅
5. **Task 5:** 加载器 + 入口点迁移 → 写测试 → **全绿** ✅
6. **Task 6:** Material 瘦化 + 删旧类型 + 测试迁移 → **全量测试全绿** ✅

**排序原则：先建新 → 再迁消费端 → 最后删旧。每步编译通过。不得跳步。**

---

## Testing Requirements

- **Minimum:** 15 个以上新增/修改测试
- **Quality bar:** 验证真实行为——Component 填充、BinQueue 排序/region 产出、PSO 构建、加载链路
- **Required categories:** Component Author / BinQueue 动态 region / Material 1:1 Entity / 管线消费 / 加载链路
- 提交前运行完整测试集
- 旧测试文件（ShaderGroupTests、MaterialAssetPipelineTests、AssetResolverTests）必须迁移，不得简单删除

---

## Quality Standards

- 零 `MaterialPass` 残留（编译级保证）
- 零 `MaterialRegistry` 残留
- 零 `IMaterialTag` / `TagStore` 残留
- 不引入新容器层级（无 MaterialStack、无 InstanceManager 角色列表）
- overlay 角色只通过 Entity 上的 `OverlayShade` Component 表达
- BinQueue region 无固定名称，纯 orderKey 排序
- 所有管线概念（overlay、stencil、raster mode）由管线代码定义，材质系统核心不含管线语义
- 不引入跨 Entity 引用（无 ReusePixels{Primary=Entity}）
- `BinQueue.GetEntity(bin)` 是管线获取 bin 对应 Entity 的唯一入口

---

## Report Requirements

报告文件：`.dev-workstream/reports/BATCH-06-REPORT.md`

必须包含：
- 删除的文件和类型完整清单
- 新增的 Component / Author / BinGroup 类型清单
- BinQueue API 变更对比（旧 RegisterRegion vs 新 RegisterGroup + GetRanges + GetArgsBin）
- 管线消费端改动摘要（每个文件改了什么）
- Program.cs 入口点改动
- 迁移的测试文件和旧/新用法对比
- 全量测试结果
- 迁移过程中的设计决策和偏差

---

## Success Criteria

- [ ] `MaterialPass.cs` 已删除
- [ ] `MaterialRegistry.cs` 已删除
- [ ] `Materials/Tags/` 目录已清空或删除
- [ ] Material 为 1 shader + 1 Params + 1 Entity，无 AddPass/Passes
- [ ] 管线 Component 类型已定义（ClusterRaster, ClusterShadeComponent, ClusterDeform, OverlayShade, StencilState, MaterialRef）
- [ ] IComponentAuthor 接口 + ComponentAuthorRegistry + 3 个 Author 实现已完成
- [ ] BinQueue 改为 BinGroup 动态 region，支持 GetRanges + GetEntity + GetArgsBin
- [ ] BinSpace / MaterialSlotCache 数据源从 MaterialPass 迁移到 Entity
- [ ] ClusterShade / ShadePSOGroup / RasterPSOBuilder / ClusterPipeline / ClusterMaterialShadePass 从 Entity Component 消费
- [ ] 加载器从 .mat + ShaderAsset attr → Entity + Author 链路打通
- [ ] Runtime/Editor Program.cs 移除 MaterialRegistry，改为 EntityStore + ComponentAuthorRegistry
- [ ] ShaderGroupTests / MaterialAssetPipelineTests / AssetResolverTests 迁移到 Entity-based
- [ ] 全量测试通过
- [ ] report 已提交

---

## Reference Materials

- `docs/materials/architecture.md` — v10 全文（核心设计文档）
- `docs/TASK-DETAIL.md` — TASK-304
- `.dev-workstream/TASK-DEFINITIONS.md`
- `.dev-workstream/reviews/BATCH-05-REVIEW.md`
- `.dev-workstream/DEBT-TRACKER.md`
