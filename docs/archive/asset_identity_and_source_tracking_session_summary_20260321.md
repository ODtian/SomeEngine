# 资产标识与源文件追踪阶段总结（对话纪要）

日期：2026-03-21

## 1. 本轮目标来源

本次实现围绕 `docs/design/asset_identity_and_source_tracking.md` 推进，核心目标是：

- 为 `ShaderAsset`、`MaterialAsset`、`MaterialInstanceAsset`、`MeshAsset` 建立统一 `AssetGuid` 引用链
- 为 shader 导入建立 `SourceGuid + ImportTrace + .meta` 的追踪基础设施
- 在不破坏旧资产的前提下，采用“新增字段 + 兼容回退”的方式渐进迁移
- 引入统一 `IAssetResolver` 抽象，为未来 manifest / editor database 铺路

---

## 2. 已完成的修改

### 2.1 基础资产标识类型
已新增：

- `src/SomeEngine.Assets/AssetGuid.cs`
- `src/SomeEngine.Assets/AssetInterfaces.cs`
- `src/SomeEngine.Assets/ImportTraceData.cs`

实现内容：

- `AssetGuid`
- `SourceGuid`
- `AssetRef<T>`
- `IAssetRecord`
- `IImportedAsset`
- `IShaderAssetRecord`
- `ImportTraceData`
- `DependencyEntryData`

这些类型构成了整个设计稿中的资产层基础。

### 2.2 `.meta` 读写基础设施
已新增：

- `src/SomeEngine.Assets/Meta/SourceMetaFiles.cs`
- `src/SomeEngine.Assets/Meta/AssetMetaFiles.cs`

实现内容：

- 自动创建和读取 `<source>.meta`
- 自动读取和写入 `<asset>.meta`
- 在 `.asset.meta` 中保存：
  - `asset_guid`
  - `source_guid`
  - `sub_asset_key`
  - `content_fingerprint`
  - `dependencies`
  - `importer_version`

这部分已形成最小闭环，但还没有扩展到完整 manifest / orphan 检测。

### 2.3 FlatBuffer schema 增量迁移
已修改：

- `assets/Schema/shader_asset.fbs`
- `assets/Schema/material_asset.fbs`
- `assets/Schema/material_instance_asset.fbs`
- `assets/Schema/mesh_asset.fbs`

新增字段：

#### `shader_asset.fbs`
- `asset_guid`
- `ImportTrace`
- `DependencyEntry`

#### `material_asset.fbs`
- `asset_guid`
- `PassEntry.shader_guid`

#### `material_instance_asset.fbs`
- `asset_guid`
- `parent_guid`

#### `mesh_asset.fbs`
- `asset_guid`
- `default_material_guids`

兼容策略：

- 旧字段保留，不删除
- 运行时优先读取 guid 字段
- 无 guid 时回退旧字符串字段

这一点与原始设计稿的 Phase 0~3 完全一致。

### 2.4 `SlangShaderImporter` 改造
已修改：

- `src/SomeEngine.Assets/Importers/SlangShaderImporter.cs`

已落地的功能：

- 保留兼容入口 `Import(path)`
- 自动调用 `SourceMetaFiles.GetOrCreate(...)`
- 自动读取 `AssetMetaFiles.TryLoad(...)`
- 写入 `ShaderAsset.asset_guid`
- 写入 `ShaderAsset.import_trace`
- 写出 `.asset` 与 `.asset.meta`
- `sub_asset_key` 当前固定为 `shader:main`

#### 依赖追踪已升级为两阶段判定
当前逻辑：

1. 先读取历史 `.asset.meta` 中的 `Dependencies`
2. 重新计算当前 fingerprint
3. 若 fingerprint 未变化，直接复用缓存 `.asset`
4. 若需要编译，则执行 Slang 编译
5. 编译后使用 `IModule.GetDependencyFileCount()` / `GetDependencyFilePath()` 提取精确依赖
6. 用新的精确依赖重新计算 fingerprint
7. 回写 `ImportTrace` 与 `.asset.meta`

这意味着：

- `#include` 已能被追踪
- include 文件变化可触发重导入
- 同一 shader 重导入后 `AssetGuid` 保持稳定

### 2.5 运行时材质系统 GUID 迁移
已修改：

- `src/SomeEngine.Render/Materials/Material.cs`
- `src/SomeEngine.Render/Materials/MaterialPass.cs`
- `src/SomeEngine.Render/Materials/MaterialRegistry.cs`

实现内容：

#### `Material`
新增：
- `AssetGuid`
- `ShaderRef`
- `ResolvedShader`

保留兼容字段：
- `ShaderAssetName`
- `ShaderAsset`

此外：
- `Material` 现在实现了 `IAssetRecord`
- `SetTexture/SetSampler/SetBuffer` 会主动 `InvalidateResolvedPasses()`

#### `MaterialPass`
- `ComputeSignature()` 已改为优先使用 `Owner.ShaderRef.Id`
- 若无 guid，再回退到旧的 shader name hash

#### `MaterialRegistry`
新增：
- `GetMaterial(AssetGuid guid)`

### 2.6 Loader 迁移
已修改：

- `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`
- `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs`
- `src/SomeEngine.Render/Assets/MeshMaterialResolver.cs`

#### `MaterialAssetLoader`
- 支持 `shader_guid` 优先加载
- 支持 legacy `shader` 字符串回退
- 支持旧接口与新接口并存
- 支持 `IAssetResolver` 入口

#### `MaterialInstanceLoader`
- 支持 `parent_guid` 优先解析 parent material
- 支持旧的直接传 `parent: Material` 接口
- 支持 `IAssetResolver` 入口
- 实例化后会写入 `instance.AssetGuid`

#### `MeshMaterialResolver`
- 支持 `default_material_guids` 优先
- 支持 `default_material_slots` 回退
- 支持 `IAssetResolver` 入口

### 2.7 统一解析器抽象
已新增：

- `src/SomeEngine.Assets/IAssetResolver.cs`
- `src/SomeEngine.Assets/MemoryAssetResolver.cs`
- `src/SomeEngine.Assets/SchemaAssetRecords.cs`

实现内容：

#### `IAssetResolver`
提供：
- `Load<TAsset>(AssetRef<TAsset>)`
- `TryGetPath(...)`
- `ListAssets<TAsset>()`

#### `MemoryAssetResolver`
提供最小内存实现：
- 注册资产
- 按 guid 加载资产
- 路径查询
- 列表查询

#### `SchemaAssetRecords.cs`
通过 partial class 给 FlatSharp 生成类型补充：
- `ShaderAsset : IImportedAsset, IShaderAssetRecord`
- `MaterialAsset : IAssetRecord`
- `MaterialInstanceAsset : IAssetRecord`
- `MeshAsset : IAssetRecord`

这使 schema 类型和运行时解析接口对接起来，而无需改生成代码。

### 2.8 测试
已新增/修改测试：

- `tests/SomeEngine.Tests/SlangIntegrationTests.cs`
- `tests/SomeEngine.Tests/Materials/MaterialAssetPipelineTests.cs`
- `tests/SomeEngine.Tests/Materials/MaterialRegistryTests.cs`
- `tests/SomeEngine.Tests/Assets/AssetResolverTests.cs`

已覆盖：

#### Shader 导入链路
- 首次导入生成稳定 `AssetGuid`
- 自动生成 `.meta` / `.asset.meta`
- include 依赖被写入 `ImportTrace.Dependencies`
- include 文件变化后 `ContentFingerprint` 变化，但 `AssetGuid` 不变

#### Material 链路
- `shader_guid` 优先
- legacy `shader` 字符串回退
- `Material.Instantiate()` 保留 guid/ref
- `MaterialRegistry.GetMaterial(AssetGuid)` 可用

#### MaterialInstance / Mesh 链路
- `asset_guid` / `parent_guid` roundtrip
- 按 `parent_guid` 查找父材质
- `default_material_guids` 优先，legacy slots 回退

#### Resolver 链路
- `MemoryAssetResolver` 的 register/load/list/path
- `MaterialAssetLoader` / `MaterialInstanceLoader` / `MeshMaterialResolver` 可通过 resolver 工作

---

## 3. 还没有完成的修改

以下内容在原始设计里已经出现，但当前还未完全落地：

### 3.1 `SlangShaderImporter` 仍未完全完成的点
虽然 include 追踪已经接上，但还存在未完成项：

- `import` 依赖是否 100% 覆盖，还缺一个显式测试确认
- `ImportSettingsJson` 还没有接入 fingerprint
- `_cache` 目前仍然以 `string path` 为键，没有改为 `AssetGuid`
- 还没有“一源多产物”的稳定映射场景（当前只固定 `shader:main`）

### 3.2 Manifest 尚未落地
原始设计中的：

- `Library/AssetManifest/source_index.json`
- `Library/AssetManifest/asset_index.json`
- `Library/AssetManifest/dependency_graph.json`

都还没有实现。

因此当前：
- 有 `IAssetResolver`
- 有 `MemoryAssetResolver`
- 但**没有真正的 manifest resolver**

### 3.3 `IAssetDatabase` 尚未实现
原始设计里的编辑器层接口尚未落地：

- `FindReferences`
- `Rename`
- `PreDeleteCheck`
- `AssetChanged` / `AssetDeleted` 事件

也就是说，当前还没有真正的引用图数据库。

### 3.4 引用图与孤儿检测未实现
未完成：

- orphan source meta 检测
- orphan asset 检测
- dangling reference 检测
- project validate 入口

### 3.5 Runtime 仍未真正依赖 manifest
原始目标要求：

- 运行时只消费 `.asset` + manifest
- 不感知 source / import 概念

当前还没有完整做到：

- runtime 仍主要靠内存注册和直接加载
- manifest 路径解析尚未接入

### 3.6 命名仍有少量漂移
设计稿用的是：
- `ImportTrace`
- `DependencyEntry`

当前手写领域模型是：
- `ImportTraceData`
- `DependencyEntryData`

虽然可用，但和文档概念还没完全统一。

---

## 4. 对话推进经历（工作流纪要）

本轮对话的推进顺序如下：

1. **先阅读设计稿**，确认方向是“统一 AssetGuid 引用 + SourceGuid/ImportTrace + 兼容迁移”。
2. **扫描现有代码**，确认当前工程仍以字符串引用为主，`SlangShaderImporter` 缺乏 `.meta` 和依赖追踪。
3. **先做最小闭环**：
   - 资产标识类型
   - `.meta` 基础设施
   - schema 新字段
   - shader/material/material instance/mesh 的 GUID 迁移
4. **逐步补 loader 和运行时对象**，确保 guid 优先、字符串回退，不破坏旧数据。
5. **引入 `IAssetResolver` 和 `MemoryAssetResolver`**，把分散的回调式解析统一到一个抽象层。
6. **回顾原始设计稿**，确认没有偏离方向，只是当前仍处于 Phase 0~3 的“最小闭环”阶段。
7. **继续阅读设计稿第 10 节**，按其中的依赖收集方案推进 `SlangShaderImporter`：
   - 改成“历史依赖快速判定 + 编译后从 Slang API 精确提取依赖”的两阶段流程。
8. **补 include 依赖测试**，确认 `ContentFingerprint` 会随 include 文件变化而变化，同时 `AssetGuid` 保持稳定。

整体过程属于：

- 先建立最小结构骨架
- 再逐步把设计稿里关键机制补实
- 始终保持兼容迁移，避免一次性破坏旧链路

---

## 5. 当前阶段结论

当前实现状态可以概括为：

- **方向正确**：仍然严格沿着设计稿的 Phase 0 ~ 3 推进
- **基础闭环已成**：GUID、meta、schema、loader、resolver 都已经接上
- **核心导入链路已明显升级**：`SlangShaderImporter` 已不再只是主文件时间戳/主文件 hash 判定，而是开始使用真实依赖列表
- **尚未进入完整资产数据库阶段**：manifest、dependency graph、editor database 仍未实现

因此更准确的阶段描述是：

> 已完成“统一资产标识 + 最小导入追踪 + 兼容迁移 + 统一解析接口”的主体工作，下一阶段应转向 `AssetManifest`、完整依赖图与编辑器资产数据库实现。
