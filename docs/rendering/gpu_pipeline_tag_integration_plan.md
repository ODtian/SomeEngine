# GPU 管线集成计划 — Tag 迁移 + 剩余问题

> ⚠️ **状态：待实现（未来计划）**
> 合并自 `gpu_pipeline_remaining_plan.md` 和 `material_tag_migration_plan.md`。此计划中描述的 friflo Tag 系统、模式 Tag、Stage Tag 等均未实现。
>
> **前置设计：** ShaderEntry 定义、完整数据流链路见 [`material_pipeline_full_chain.md`](material_pipeline_full_chain.md)。本文档是该设计的迁移/集成执行计划。

---

## 第一部分：材质 Tag 系统迁移至 friflo ECS EntityStore

### 概述

用独立的 friflo `EntityStore` 替代自定义 `TagStore<MaterialPass>`，
统一材质 tag、跨 pass 引用、自动推导为 ECS 原生能力。

### 已确认决策

| 决策 | 结论 |
|------|------|
| Store 独立性 | 独立 EntityStore，不与 GameWorld 共享 |
| 序列化 | friflo `StoreToJson` 嵌入 FlatBuffer `tag_store_json: string` |
| Entity ↔ Pass 关联 | `PassInfo { PassIndex }` 作为稳定 key，MaterialID 运行时分配 |

### 映射

| 自定义 TagStore | friflo ECS |
|----------------|------------|
| `IMaterialTag` 无数据标记 | `ITag` |
| `IMaterialTag` 有数据 | `IComponent` |
| `OverlayTag.PrimaryPass` 引用 | `ILink<Entity>` |
| `TagStore.Query<T1, T2>()` | `store.Query().AllTags(Tags.Get<T1, T2>())` |
| `TagStore.SetTag<T>(item)` | `entity.AddTag<T>()` |
| `TagStore.Version` | 手动计数（entity add/remove 时递增） |

### 新增类型

#### Tags（ITag，零数据）
```csharp
struct Opaque        : ITag { }
struct Masked        : ITag { }
struct TwoSided      : ITag { }
struct ClusterShader : ITag { }
struct ClusterRaster : ITag { }
struct VertexDeform  : ITag { }
struct ForwardShader : ITag { }
struct ShadowCaster  : ITag { }
```

#### Components（IComponent，有数据）
```csharp
struct PassInfo      : IComponent { int PassIndex; }
struct StencilRef    : IComponent { byte Value; }
struct OverlayInfo   : IComponent { byte LayerIndex; }
struct MultiPassInfo : IComponent { byte OverlayCount; }
```

#### Links（ILink，跨 pass 引用）
```csharp
struct OverlayOf  : ILink<Entity> { Entity Primary; }
struct DeformLink : ILink<Entity> { Entity Deform; }
```

### MaterialStore
简单 wrapper：
- 持有独立 `EntityStore`
- 维护 `MaterialPass ↔ Entity` 双向映射
- 暴露 tag/component/link/query 操作
- `StoreToJson()` / `LoadFromJson()` 序列化

### 导入管线
```
MaterialAssetImporter.Import(定义)
  1. 创建临时 Material + MaterialStore
  2. 为每个 pass 创建 entity + PassInfo
  3. 应用显式 tags（从资产定义）
  4. 运行 ITagDeriver 链:
     [0]   ShaderAutoTagDeriver   — shader metadata → tags
     [100] OverlayDeriver         — 同角色分组 → MultiPass/Overlay
     [200] 用户自定义 deriver
  5. StoreToJson → 写入 MaterialAsset.tag_store_json
```

### 运行时加载
```
MaterialAssetLoader.LoadFromAsset()
  1. 创建 Material + Passes + 加载 shader/texture
  2. LoadFromJson(tag_store_json) → 恢复 EntityStore
  3. 遍历 entity → material.Passes[PassInfo.PassIndex] → 建立映射
  4. registry.Register(material) → 分配 MaterialID（零推导）
```

### Schema 变更
```fbs
table PassEntry {
    shader_guid: string;
    shader: string;
    entry_point: string;
    // tags 字段移除
}

table MaterialAsset (fs_serializer) {
    ...
    tag_store_json: string;   // NEW: friflo EntityStore 序列化
}
```

### 废弃项
- `TagStore<T>` 类
- `IMaterialTag` 接口
- `[MaterialTag]` attribute
- `MaterialTagResolver` source generator
- `MaterialTagDeserializerGenerator` source generator
- `MaterialRegistry` 中所有 tag delegation 方法（移至 `MaterialStore`）

### 迁移顺序
1. 新建 `MaterialStore` + 新 tag/component/link 类型
2. `MaterialRegistry` 持有 `MaterialStore`，委托查询
3. 迁移所有 `SetTag/HasTag/Query` 调用到 `MaterialStore`
4. 替换 `MaterialAssetLoader` 的 tag 加载逻辑
5. 新建 `MaterialAssetImporter` + ITagDeriver 链
6. 更新 `MaterialCreationTests`
7. 删除 `TagStore<T>` + source gen

---

## 第二部分：核心设计理念 — 方案 A（基于 ECS Tag 的显式映射）

管线**不依赖硬编码名字**（如 `CSDeformStatic`）。
管线**不依赖宏变体**打切换开关。
管线把所有行为抽象为 **角色 Tag**（如 `ClusterSWRaster`, `VertexDeform`）和 **模式 Tag**（如 `InlineMode`, `CachedMode`）。

---

## 问题 1：运行时模式（Inline vs DeformCache）的 Entry Point 解析

### 现状
当前管线在 `ClusterPipeline` 中存在对 `CSDeformStatic` 等入口的硬编码依赖。

### 方案
1. **新增模式 Tags**
   ```csharp
   public struct InlineMode : ITag { }
   public struct CachedMode : ITag { }
   ```

2. **材质资产范例**
   ```fbs
   { entry_point: "MyInlineRaster", tags: ["ClusterSWRaster", "InlineMode"] }
   { entry_point: "MyCachedRaster", tags: ["ClusterSWRaster", "CachedMode"] }
   { entry_point: "MyDeform",       tags: ["VertexDeform"] }
   ```

3. **管线查询**
   - DeformCache 模式：查询 `[ClusterSWRaster] + [CachedMode]` 的 Pass。
   - Inline 模式：查询 `[ClusterSWRaster] + [InlineMode]` 的 Pass。

---

## 问题 2：HW Draw PSO 的 VS + PS 构建

### 方案
Schema 零改动。用**两个 Pass + Stage Tag** 组合表达一个 GraphicsPSO 所需的多 Stage：
```fbs
{ shader: "my_raster", entry_point: "VSRaster", tags: ["ClusterHWRaster", "VertexStage"] }
{ shader: "my_raster", entry_point: "PSRaster", tags: ["ClusterHWRaster", "PixelStage"] }
```

管线查询：
```csharp
var vsPass = store.Query().AllTags<ClusterHWRaster, VertexStage>()...;
var psPass = store.Query().AllTags<ClusterHWRaster, PixelStage>()...;
BuildGraphicsPSO(vsPass.EntryPoint, psPass.EntryPoint);
```

新增 Stage Tags：
```csharp
public struct VertexStage : ITag { }
public struct PixelStage  : ITag { }
public struct MeshStage   : ITag { }  // 未来
```

---

## 问题 3：CPU BinSpace Fields 对齐

3 个 Field 设计自洽，无需结构修改。
唯一改动：BinSpace 计算 Hash 时，从 Tag 查出的 Pass 取 Shader 变体 Hash，而非硬编码。

---

## 问题 4：ClusterSWRasterPSOs 死代码清理

1. 删除 `ClusterSWRasterPSOs` 静态类。
2. 移除从本地路径读取 `sw_raster.slang` 的代码。
3. `ClusterSWRasterPass.Execute` 迭代 `ShadePSOGroup[]`，使用 `RasterPSOBuilder` 动态生成的 PSO。

---

## 完整 Tag 一览

| Tag | 类型 | 用途 |
|-----|------|------|
| `Opaque` | ITag | 不透明 |
| `Masked` | ITag | Alpha Test |
| `TwoSided` | ITag | 双面 |
| `ClusterShader` | ITag | Cluster Shade 角色 |
| `ClusterSWRaster` | ITag | Cluster SW Raster 角色 |
| `ClusterHWRaster` | ITag | Cluster HW Raster 角色 |
| `VertexDeform` | ITag | Deform CS 角色 |
| `ForwardShader` | ITag | Forward 管线角色 |
| `ShadowCaster` | ITag | 阴影角色 |
| `InlineMode` | ITag | 内联顶点评估模式 |
| `CachedMode` | ITag | DeformCache 模式 |
| `VertexStage` | ITag | VS 入口标记 |
| `PixelStage` | ITag | PS 入口标记 |
| `MeshStage` | ITag | MS 入口标记（未来） |

---

## 执行顺序

1. **[Phase 1]** friflo Tag 迁移（本文档第一部分）。
2. **[Phase 2]** 方案 A 支撑结构：模式 Tags + Stage Tags + `RasterPSOBuilder` Tag 查询。
3. **[Phase 3]** HW Draw PSO 构建（通过 Stage Tag 组合 VS+PS，Schema 零改动）。
4. **[Phase 4]** 清理 `ClusterSWRasterPSOs` 死代码。
