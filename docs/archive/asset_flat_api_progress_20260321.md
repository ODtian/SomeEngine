# 资产系统扁平 API 当前进度总结

日期：2026-03-21

## 1. 当前结论

当前资产系统已经从“概念设计 + 底层最小闭环”进入到“**可用的扁平 facade 基础设施**”阶段。

目前的状态不是继续搭骨架，而是：

- 底层 identity / manifest / meta / validator 分层已基本稳定
- 上层扁平 facade 已经有统一查询、分析、校验入口
- source 维度、一源多产物、删除后果分析都已有最小可用实现
- 现阶段后续工作重心更偏向**分析精度增强**和**业务导入链接入**，而不是继续补零散 utils

---

## 2. 已完成能力

### 2.1 身份与基础类型

已完成：

- `AssetGuid`
- `SourceGuid`
- `AssetId`
- `AssetRef<T>`
- `ImportTraceData`
- `DependencyEntryData`

当前分层策略：

- 底层继续保留 `AssetGuid` / `SourceGuid`
- facade / 上层入口开始统一使用 `AssetId`
- `AssetId` 当前本质上是 `AssetGuid` 的扁平别名，不破坏既有底层实现

---

### 2.2 meta 与导入追踪基础

已完成：

- `SourceMetaManager`
- `AssetMetaManager`
- `SlangShaderImporter` 的稳定 GUID 导入链
- include/import dependency 追踪
- fingerprint 判定
- `.meta` / `.asset.meta` 最小闭环

当前 `SlangShaderImporter` 已支持：

- `SourceGuid`
- `AssetGuid`
- `SubAssetKey`
- `ContentFingerprint`
- `ImporterVersion`
- 依赖文件追踪
- 历史 fingerprint 快速跳过

---

### 2.3 schema 与运行时引用迁移

已完成迁移：

- `ShaderAsset.asset_guid`
- `MaterialAsset.asset_guid`
- `MaterialAsset.PassEntry.shader_guid`
- `MaterialInstanceAsset.asset_guid`
- `MaterialInstanceAsset.parent_guid`
- `MeshAsset.asset_guid`
- `MeshAsset.default_material_guids`

运行时已支持：

- `Material` / `MaterialPass` / `MaterialRegistry` guid 优先
- `MaterialAssetLoader` guid 优先 + legacy fallback
- `MaterialInstanceLoader` guid 优先 + legacy fallback
- `MeshMaterialResolver` guid 优先 + legacy fallback
- `IAssetResolver` 已接入运行时加载链路

---

### 2.4 manifest 与图查询能力

已完成：

- `AssetManifest`
- `AssetManifestBuilder`
- `AssetManifestScanner`
- `ManifestAssetResolver`
- `AssetProjectValidator`

manifest 当前已支持的索引/反查：

- `TryGetAsset(AssetGuid)`
- `TryGetAssetByPath(string)`
- `TryGetSourcePath(SourceGuid)`
- `TryGetSourceGuid(string)`
- `GetAssetsBySource(SourceGuid)`
- `GetAssetsBySourcePath(string)`
- `TryGetAssetBySourceAndSubAssetKey(SourceGuid, string)`

manifest 当前已支持的图能力：

- `GetDependencies(AssetGuid)`
- `GetReferencers(AssetGuid)`
- `PreDeleteCheck(AssetGuid)`
- `GetDependencyClosure(AssetGuid)`
- `GetReferencerClosure(AssetGuid)`
- `GetTransitiveDependencies(AssetGuid)`
- `GetTransitiveReferencers(AssetGuid)`

这意味着目前已经具备：

- 直接依赖查询
- 反向引用查询
- 传递闭包查询
- 一源多产物查询
- `(SourceGuid, SubAssetKey)` 二元组稳定定位资产

---

## 3. 扁平 facade 当前进度

### 3.1 `AssetNode` 与 `IAssetCatalog`

已完成：

- `AssetNode`
- `IAssetCatalog`
- `ManifestAssetCatalog`
- `AssetCatalogExtensions.List<TAsset>()`

当前 `AssetNode` 已提供：

- `Id`
- `Name`
- `AssetType`
- `AssetPath`
- `SourcePath`
- `SubAssetKey`
- `Dependencies`
- `Referencers`
- `HasSource`

当前 `IAssetCatalog` 已提供：

- `Get(AssetId)`
- `TryGetPath(AssetId)`
- `TryGetId(string assetPath)`
- `TryGetSourcePath(SourceGuid)`
- `TryGetSourceGuid(string sourcePath)`
- `GetAssetsBySource(SourceGuid)`
- `GetAssetsBySourcePath(string)`
- `List(string? assetType)`
- `List<TAsset>()`
- `GetDependencies(AssetId)`
- `GetReferencers(AssetId)`

说明：

- `ManifestAssetCatalog` 已经可以作为绝大多数调试/工具查询的统一入口
- 路径、source、asset、依赖图这几类高频查询都已具备

---

### 3.2 `IAssetWorkspace` 与统一入口

已完成：

- `IAssetWorkspace`
- `ManifestAssetWorkspace`

并且当前 `IAssetWorkspace` 同时继承：

- `IAssetCatalog`
- `IAssetResolver`

这意味着一个 workspace 实例现在已经能统一承担：

- 扁平资产查询
- 资产解析/加载
- source 分析
- 影响分析
- 删除分析
- 项目校验
- manifest 重建

当前 `IAssetWorkspace` 已提供：

- `Validate()`
- `AnalyzeSource(SourceGuid|string)`
- `AnalyzeSourceImport(SourceGuid|string)`
- `PreDeleteCheck(AssetId)`
- `AnalyzeDelete(AssetId)`
- `AnalyzeDeleteConsequences(AssetId)`
- `FindImpact(AssetId)`
- `AnalyzeImpact(AssetId)`
- `RebuildIndex()`
- `Load<TAsset>()`
- `ListAssets<TAsset>()`

说明：

- 现在上层后续完全可以只依赖一个 `IAssetWorkspace`
- 不必再把 scanner / manifest / validator / resolver 分散暴露给调用方

---

## 4. 报告模型当前进度

### 4.1 校验报告

已完成：

- `AssetProblem`
- `AssetValidationReport`
- `AssetProjectValidator.ValidateFlat()`

当前 `AssetProblem` 已覆盖：

- `OrphanSourceMeta`
- `OrphanAsset`
- `DanglingReference`

并提供：

- `HasAsset`
- `HasRelatedAsset`

说明：

- 旧 `AssetValidationIssue` 仍保留
- 新的 flat report 已可直接给 facade / CLI / 工具层消费

---

### 4.2 source 报告

已完成：

- `SourceAssetsReport`
- `SourceImportSummary`

`SourceAssetsReport` 当前用于：

- 给定 source，查看它产出的全部资产

`SourceImportSummary` 当前用于：

- source 对应 importer 摘要
- fingerprint 摘要
- importer version
- sub-asset key
- dependency 列表
- source 对应的资产列表

当前摘要数据来源优先级：

- `SourceMeta`
- `AssetMeta`

说明：

- 这是当前 workflow 真正需要的最小 source 调试摘要
- 已经能回答“这个 source 用什么 importer 导入”“当前 fingerprint 是什么”“依赖了哪些文件”等关键问题

---

### 4.3 impact / delete 报告

已完成：

- `AssetImpactReport`
- `AssetDeleteReport`
- `AssetDeleteConsequenceReport`

`AssetImpactReport` 当前提供：

- `RootId`
- `Root`
- `DirectReferencers`
- `TransitiveReferencers`
- `AllImpactedAssets`
- `HasImpact`

`AssetDeleteReport` 当前提供：

- `TargetId`
- `Target`
- `DirectBlockers`
- `TransitiveBlockers`
- `CanDelete`

`AssetDeleteConsequenceReport` 当前提供：

- `TargetId`
- `Target`
- `DirectBlockers`
- `TransitiveBlockers`
- `DirectDanglingRiskAssets`
- `TransitiveDanglingRiskAssets`
- `DanglingRiskAssets`
- `OrphanRiskAssets`
- `RemovalSet`
- `HasDanglingRisk`
- `HasOrphanRisk`
- `CanDelete`

说明：

- 删除分析已经从“只看直接引用方”推进到“看级联删除链与 orphan/dangling 风险”
- 这已经足够支撑后续命令行、调试输出、删除确认、force delete 风险提示等工作

---

## 5. 当前测试进度

当前已为以下能力补充定向测试：

- manifest roundtrip
- manifest resolver 加载
- referencer / dependency 基础行为
- dependency / referencer closure
- transitive dependency / referencer 查询
- path -> asset 反查
- source -> assets 查询
- `(SourceGuid, SubAssetKey)` 反查
- catalog 节点投影视图
- workspace impact 分析
- workspace delete 分析
- workspace delete consequence 分析
- workspace source 分析
- workspace source import summary
- missing source import summary 边界行为
- orphan risk 边界行为
- validator flat report 映射

当前定向测试结果：

- `AssetManifestTests`
- `AssetProjectValidatorTests`
- `AssetResolverTests`

共 **21 项通过，0 项失败**。

---

## 6. 当前还未完成的部分

### 6.1 删除后果分析仍可继续精细化

虽然 `AssetDeleteConsequenceReport` 已经是可用版本，但仍有增强空间：

- direct dangling 与 transitive dangling 的更严格语义界定
- force delete 场景下的 removal strategy
- 删除后哪些资产会从“正常”变为“仅剩孤立存在”
- 删除后哪些问题应标记 warning，哪些应标记 error

当前状态：

- **已可用，但不是最终版**

---

### 6.2 source import 摘要仍不完整

当前 `SourceImportSummary` 已有最关键字段，但还缺少：

- importer settings 摘要
- why dirty / why rebuild 的差异解释
- 多产物 source 时的“主摘要”选择策略统一化
- 不同 importer 的摘要标准化模型

当前状态：

- **关键字段可用，诊断深度还可继续增强**

---

### 6.3 非 shader 的一源多产物业务链路仍未全面接入

当前 source/asset 模型已经能承载：

- 一源多产物
- `SubAssetKey`
- stable asset lookup

但实际完整落地主要仍是 shader 路径。

未来像：

- `gltf -> mesh + material + texture`

这类链路，还需要 importer 真正接入当前 manifest / meta / facade 体系。

当前状态：

- **架构已就绪，业务接入未完成**

---

### 6.4 树形/层级分析模型尚未引入

当前全部分析结果基本还是 list/report 模型。

还未完成：

- dependency tree node
- referencer tree node
- source -> sub-asset tree
- delete cascade tree

这不是当前 workflow 的阻塞点，但后续会明显提高调试和展示效果。

---

## 7. 当前优先级建议

如果继续推进，建议优先级如下：

### 第一优先级
1. 删除后果分析继续精细化
2. source import summary 更完整

### 第二优先级
3. 非 shader 一源多产物 importer 接入
4. 统一不同 importer 的摘要结构

### 第三优先级
5. 树形分析模型
6. 后续 CLI / 调试界面消费层

---

## 8. 一句话总结

当前进度可以总结为：

> 资产系统扁平 API 的底层与 facade 主骨架已经落地完成，source 查询、impact/delete/source 分析、manifest 反查与校验报告都已具备最小可用能力；后续重点已从“搭结构”转向“增强分析精度”和“接入更多真实 importer 业务链路”。
