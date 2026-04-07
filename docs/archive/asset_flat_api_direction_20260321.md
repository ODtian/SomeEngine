# 资产系统扁平 API 草案与当前重构方向

日期：2026-03-21

## 1. 背景

当前围绕 `docs/design/asset_identity_and_source_tracking.md` 已经完成了一批基础设施：

- `AssetGuid` / `SourceGuid`
- `SourceMetaManager` / `AssetMetaManager`
- `SlangShaderImporter` 的稳定 GUID 与依赖追踪
- `IAssetResolver` / `MemoryAssetResolver` / `ManifestAssetResolver`
- `AssetManifest` / `AssetManifestBuilder` / `AssetManifestScanner`
- `AssetProjectValidator`
- orphan / dangling / `PreDeleteCheck` / 反向引用图最小闭环

这些能力方向上没有偏离原设计，但随着功能逐步落地，当前资产系统已经呈现出明显的“**底层分层合理，但上层使用心智偏重**”的问题。

用户在阅读当前实现时，很容易同时看到：

- source 身份
- asset 身份
- source meta
- asset meta
- import trace
- manifest
- resolver
- validator
- 正向依赖图
- 反向引用图

虽然这些概念在职责上是合理拆分的，但如果将它们全部直接暴露给上层调用方，使用体验会越来越复杂。

因此，本文件提出一个新的目标：

> **在不破坏底层 identity / manifest / import 设计的前提下，为编辑器和工具层提供一套更扁平的 API 视图。**

---

## 2. 当前实现进度概览

### 2.1 已完成的底层能力

#### 身份与导入层
- `AssetGuid`
- `SourceGuid`
- `AssetRef<T>`
- `ImportTraceData`
- `DependencyEntryData`
- `SourceMetaManager`
- `AssetMetaManager`

#### 导入链
- `SlangShaderImporter` 已支持：
  - `SourceGuid`
  - `AssetGuid`
  - `.meta`
  - `.asset.meta`
  - include/import 依赖追踪
  - fingerprint 判定

#### schema 引用迁移
- `ShaderAsset.asset_guid`
- `MaterialAsset.asset_guid`
- `MaterialAsset.PassEntry.shader_guid`
- `MaterialInstanceAsset.asset_guid`
- `MaterialInstanceAsset.parent_guid`
- `MeshAsset.asset_guid`
- `MeshAsset.default_material_guids`

#### 运行时与加载
- `Material` / `MaterialPass` / `MaterialRegistry` 已迁移到 guid 优先
- `MaterialAssetLoader` / `MaterialInstanceLoader` / `MeshMaterialResolver` 已支持 guid 优先、legacy fallback
- `IAssetResolver` 已接入运行时加载通路

#### manifest 与校验
- `AssetManifest`
- `AssetManifestBuilder`
- `AssetManifestScanner`
- `ManifestAssetResolver`
- `AssetProjectValidator`
- orphan source meta 检测
- dangling reference 检测
- orphan asset 检测
- `GetReferencers()`
- `PreDeleteCheck()` 最小实现

---

## 3. 当前问题不在底层设计，而在上层使用视图

当前系统的复杂感，主要不是因为底层设计错误，而是因为：

### 3.1 多个内部层次被直接看见了

例如一个普通编辑器调用方，本来只想做：

- 查一个资产
- 看它引用了谁
- 看谁引用它
- 删除前检查
- 校验项目

但当前如果直接面对底层实现，可能要理解：

- `SourceGuid` vs `AssetGuid`
- `SourceMeta` vs `AssetMeta`
- `ImportTrace`
- manifest 的 source index / asset index / dependency graph
- resolver / validator / scanner 分工

这会让“日常查询资产”这种事情，承担过多内部实现知识。

### 3.2 `source` 与 `asset` 的区分是必要的，但不必全部暴露

根据原始设计，`source` 和 `asset` 分离是有必要的，原因包括：

- 一源多产物
- 导入输入与运行时产物职责不同
- source 稳定身份与 asset 稳定身份语义不同

因此：

> **底层仍应保留 source / asset 分离。**

但这不意味着上层 API 也必须同时暴露所有内部对象。

---

## 4. 重构目标

重构目标不是推翻当前实现，而是：

### 4.1 保留底层分层
继续保留：

- `SourceGuid`
- `AssetGuid`
- `SourceMetaManager`
- `AssetMetaManager`
- `AssetManifest`
- `AssetManifestScanner`
- `AssetProjectValidator`
- `ManifestAssetResolver`

这些仍然是正确且必要的基础设施。

### 4.2 新增一层更扁平的上层 API
为编辑器 / 工具 / 调试入口提供更简单的统一视图。

上层只需要理解少量对象：

- `AssetId`
- `AssetNode`
- `IAssetCatalog`
- `IAssetWorkspace`
- `AssetValidationReport`

这样可以把当前大量内部细节“收进去”，只把高频能力暴露出来。

---

## 5. 扁平 API 草案

> 说明：这里是**方向草案**，不是要求立即把现有底层类型全部改名。
> 更现实的做法是先引入一层 facade / adapter，逐步迁移调用方。

### 5.1 `AssetId`

```csharp
public readonly record struct AssetId(Guid Value);
```

用途：
- 面向上层时的统一正式资产 ID
- 本质上可直接映射到当前 `AssetGuid`

说明：
- 这里不是要删除 `AssetGuid`
- 而是可以把 `AssetId` 作为更扁平 API 的别名或 facade 名称

---

### 5.2 `AssetNode`

```csharp
public sealed class AssetNode
{
    public required AssetId Id { get; init; }
    public required string Name { get; init; }
    public required string AssetType { get; init; }
    public required string AssetPath { get; init; }

    public string SourcePath { get; init; } = string.Empty;
    public string SubAssetKey { get; init; } = string.Empty;

    public IReadOnlyList<AssetId> Dependencies { get; init; } = [];
    public IReadOnlyList<AssetId> Referencers { get; init; } = [];
}
```

用途：
- 统一表示“一个资产在编辑器/工具视角下的完整信息”

它把当前分散在多处的常用信息合并成一个视图：

- 资产正式身份
- 资产路径
- 资产类型
- 源路径
- 正向依赖
- 反向引用

说明：
- `SourceGuid` 可以继续留在底层，不一定要暴露到 `AssetNode`
- 如果后续编辑器确实需要 source identity，也可以再加可选字段

---

### 5.3 `IAssetCatalog`

```csharp
public interface IAssetCatalog
{
    AssetNode? Get(AssetId id);

    bool TryGetPath(AssetId id, out string path);

    IReadOnlyList<AssetNode> List(string? assetType = null);

    IReadOnlyList<AssetNode> GetDependencies(AssetId id);

    IReadOnlyList<AssetNode> GetReferencers(AssetId id);
}
```

用途：
- 面向上层的统一查询接口

对应当前能力：
- `AssetManifest.Assets`
- `AssetManifest.GetDependencies()`
- `AssetManifest.GetReferencers()`
- `IAssetResolver.ListAssets<T>()`
- `IAssetResolver.TryGetPath()`

说明：
- 这里故意不直接暴露大量内部对象
- 让调用方只看到“查询资产”的统一表面

---

### 5.4 `IAssetWorkspace`

```csharp
public interface IAssetWorkspace : IAssetCatalog
{
    AssetValidationReport Validate();

    IReadOnlyList<AssetNode> PreDeleteCheck(AssetId id);

    IReadOnlyList<AssetNode> FindImpact(AssetId id);

    void RebuildIndex();
}
```

用途：
- 编辑器 / 工具层的核心服务入口

能力映射：
- `Validate()` → 当前 `AssetProjectValidator`
- `PreDeleteCheck()` → 当前 manifest `PreDeleteCheck()` + 上层包装
- `FindImpact()` → 后续递归引用分析
- `RebuildIndex()` → 当前 `AssetManifestScanner.ScanAndSave()` 的上层封装

这会比当前直接让编辑器面向 scanner / manifest / validator 更简单。

---

### 5.5 校验结果对象

```csharp
public sealed class AssetValidationReport
{
    public IReadOnlyList<AssetProblem> Problems { get; init; } = [];

    public bool HasErrors => Problems.Any(x => x.Severity == AssetProblemSeverity.Error);
}

public sealed class AssetProblem
{
    public required AssetProblemKind Kind { get; init; }
    public required AssetProblemSeverity Severity { get; init; }

    public AssetId AssetId { get; init; }
    public AssetId RelatedAssetId { get; init; }

    public string Path { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
```

用途：
- 将当前 `AssetValidationIssue` / `AssetValidationResult` 再向上收敛成更稳定的编辑器消费模型

---

## 6. 推荐的分层方式

### 6.1 底层继续保留
这些对象仍然是当前实现的基础，不建议删除：

- `SourceMetaManager`
- `AssetMetaManager`
- `AssetManifest`
- `AssetManifestBuilder`
- `AssetManifestScanner`
- `AssetProjectValidator`
- `ManifestAssetResolver`

### 6.2 中层新增 adapter / facade
建议新增一层把底层数据装配成 `AssetNode`：

例如：

```csharp
public sealed class ManifestAssetCatalog : IAssetCatalog
{
    private readonly AssetManifest _manifest;

    public AssetNode? Get(AssetId id) { ... }
    public IReadOnlyList<AssetNode> GetDependencies(AssetId id) { ... }
    public IReadOnlyList<AssetNode> GetReferencers(AssetId id) { ... }
}
```

### 6.3 顶层编辑器只依赖 `IAssetWorkspace`
编辑器最终不直接依赖：

- scanner
- validator
- raw manifest 字典
- source/asset meta manager

而是只依赖：

- `IAssetWorkspace`

---

## 7. 当前实现与扁平 API 的映射关系

### 7.1 当前对象 → 建议上层对象

| 当前实现 | 建议上层视图 |
|---|---|
| `AssetGuid` | `AssetId` |
| `AssetManifestRecord` + dependencies + referencers | `AssetNode` |
| `AssetManifest` | `IAssetCatalog` 背后的实现 |
| `AssetProjectValidator` | `IAssetWorkspace.Validate()` |
| `AssetManifestScanner` | `IAssetWorkspace.RebuildIndex()` |
| `ManifestAssetResolver` | runtime loader / catalog 内部实现 |

### 7.2 不建议直接暴露给大多数调用方的对象

这些对象仍然必要，但建议主要作为内部实现：

- `SourceMeta`
- `AssetMeta`
- `DependencyEntryData`
- `ImportTraceData`
- 原始 source index / asset index / dependency graph 文档对象

---

## 8. 分阶段重构建议

### Phase A：先加 facade，不推翻现有实现
目标：
- 不动现有 manifest / validator / resolver 主体
- 先加一个扁平 API 适配层

建议新增：
- `AssetNode`
- `IAssetCatalog`
- `IAssetWorkspace` 草接口
- `ManifestAssetCatalog` 或类似 facade

### Phase B：编辑器/工具调用方逐步切换
目标：
- 让调用方从“直接操作底层字典/validator/scanner”
- 过渡到“只操作 workspace/catalog”

### Phase C：决定是否统一命名
目标：
- 视实际使用情况决定是否引入 `AssetId` 作为正式上层命名
- 或保留 `AssetGuid`，仅引入 `AssetNode` / `IAssetWorkspace`

> 当前更推荐先做 facade，不急着做全项目命名替换。

---

## 9. 为什么这条路线更适合当前工程

### 9.1 不会破坏已经完成的基础设施
当前已经实现的：
- meta
- guid
- scanner
- manifest
- validator
- resolver

都可以被直接复用。

### 9.2 不会误把 source / asset 身份强行合并
这避免了一源多产物、导入跟踪、运行时消费等问题重新恶化。

### 9.3 能显著降低上层心智负担
最终调用方只需要理解：

- 一个资产节点 `AssetNode`
- 一个查询入口 `IAssetCatalog`
- 一个工作区服务 `IAssetWorkspace`

而不需要同时掌握所有底层组件。

---

## 10. 当前阶段结论

当前资产系统底层设计总体仍然正确，问题更多在于：

> **底层分层已经逐渐完整，但对上层来说暴露面过多，缺少一层更扁平的统一查询与工作区 API。**

因此下一阶段最合适的方向不是推翻既有 identity / manifest / validator 设计，而是：

1. 保留底层 source / asset 分离
2. 新增 `AssetNode` 统一视图
3. 新增 `IAssetCatalog` / `IAssetWorkspace` facade
4. 逐步把调用方迁移到更扁平的上层接口

---

## 11. 一句话总结

> 底层继续保持“source identity + asset identity + manifest + validator”的正确分层；上层新增“AssetNode + Catalog + Workspace”的扁平 facade，把复杂度收进去，而不是把 identity 体系打平。
