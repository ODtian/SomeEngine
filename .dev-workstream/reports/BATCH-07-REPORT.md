# BATCH-07 Report: Asset Pipeline Destructive Rework

**Batch:** BATCH-07  
**Date:** 2026-04-03  
**Status:** COMPLETE

## Summary

本批已把 `SomeEngine.Assets` 顶层从旧的 `AssetId + Catalog/Resolver/Workspace/Database + Validator/Watcher/Reports` 多层 facade，收敛到统一的 `IAsset + ImportedAsset + AssetManifest + AssetDatabase + AssetTypeRegistry + SourceMeta/AssetMeta` 路线。

同时，Render loader 侧的 `IAssetResolver` 重载与 legacy string fallback 已全部删除，Runtime 默认材质路径也已切到 `AssetDatabase.Load<T>(path)`，Registry 的 builtin 注册改为宿主显式调用 `AssetTypeRegistration.RegisterBuiltIns()`。Scanner/Database 现在只扫描 `assetRoots`，不再从项目根目录全盘递归；watcher 暂时保留为 no-op 空实现，不承载任何监视逻辑。

本次 Task 5 corrective 进一步完成了：`Load<T>(path)` 改为仅在 source 过期时触发 Import、Scanner 删除 `is ShaderAsset` 分支并只依赖 `.asset.meta`、扫描策略改为 `assetRoots` 定向扫描、`AssetDatabase` 不再实现 `IDisposable`、`Validate()` 不再输出 `OrphanAsset` 误报。

## Final Structure

`src/SomeEngine.Assets/` 顶层现状：

| File | Lines |
|---|---:|
| `AssetDatabase.cs` | 220 |
| `AssetGuid.cs` | 41 |
| `AssetIoHelpers.cs` | 44 |
| `AssetManifest.cs` | 268 |
| `AssetManifestScanner.cs` | 97 |
| `AssetRecord.cs` | 36 |
| `AssetTypeRegistration.cs` | 63 |
| `AssetTypeRegistry.cs` | 92 |
| `MetaManagers.cs` | 123 |
| `SchemaPartials.cs` | 64 |
| `SomeEngine.Assets.csproj` | 30 |

- 顶层总行数（含 `.csproj`）：`1078`
- 顶层总行数（不含 `.csproj`）：`1048`

## Deleted

- `AssetAnalysisReports.cs`
- `AssetCatalog.cs`
- `AssetFileWatcher.cs`
- `AssetImportCoordinator.cs`
- `AssetInterfaces.cs`
- `AssetManifestBuilder.cs`
- `AssetProjectValidator.cs`
- `AssetWorkspace.cs`
- `IAssetDatabase.cs`
- `IAssetResolver.cs`
- `ImportTraceData.cs`
- `ManifestAssetDatabase.cs`
- `ManifestAssetResolver.cs`
- `MemoryAssetResolver.cs`
- `SchemaAssetRecords.cs`
- `Meta/SourceMetaManager.cs`
- `Meta/AssetMetaManager.cs`

## Delivered APIs

### `AssetDatabase`

- `Load<TAsset>(string sourcePath, string? subAssetKey = null)`
- `Load<TAsset>(AssetGuid guid)`
- `Import(string sourcePath)`
- `Resolve(string sourcePath, string? subAssetKey = null)`
- `List(string? assetType = null)`
- `GetDependencies(AssetGuid guid)`
- `GetReferencers(AssetGuid guid)`
- `Validate()`
- `StartWatching()`
- `StopWatching()`
- `RebuildIndex()`

Watcher current status:
- `StartWatching()` / `StopWatching()` 为 no-op 空实现
- `AssetDatabase` 不实现 `IDisposable`
- 文件监视需由宿主自行组装

### `AssetTypeRegistry`

- Registered handlers:
  - `ShaderAsset`
  - `MaterialAsset`
  - `MaterialInstanceAsset`
  - `MeshAsset`
- Registered importers:
  - `SlangShaderImporter`
- Registration mode:
  - 宿主显式调用 `AssetTypeRegistration.RegisterBuiltIns()`

## Consumer Migration

### Render

- `MaterialAssetLoader` 删除全部 `IAssetResolver` 与 `LegacyShaderLoadFunc` 路线，只保留 GUID delegate
- `MaterialInstanceLoader` 删除全部 `IAssetResolver` 重载，只保留 parent delegate 路线
- `MeshMaterialResolver` 删除 `IAssetResolver` / path fallback 路线，只保留 GUID delegate
- `Material` 不再实现任何 asset interface，并移除 `ShaderAssetName` / `AssetPath`

### Runtime

- `Program.cs` 从 `ManifestAssetDatabase` 切换到 `AssetDatabase`
- 启动阶段改为统一 `AssetTypeRegistration.RegisterBuiltIns() + AssetDatabase.RebuildIndex()`
- 默认材质通过 `AssetDatabase.Load<T>(path)` 解析；shader 仅通过 GUID delegate 传入 loader

## Tests

### Asset-focused

- `dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~SomeEngine.Tests.Assets" -- RunConfiguration.MaxCpuCount=1`

Result:

```text
Passed: 23, Failed: 0, Skipped: 0
```

### Full Suite

- `dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj -- RunConfiguration.MaxCpuCount=1`

Result:

```text
Passed: 129, Failed: 0, Skipped: 1, Total: 130
```

### Build

- `dotnet build SomeEngine.slnx`

Result:

```text
0 warnings, 0 errors
```

## Design Decisions

- 采用 `Schema partial + explicit interface implementation`，让 FlatSharp 生成类型直接实现 `IAsset`
- `AssetManifest` 改为内联 add/save/load，`List()` 直接返回 `AssetManifestRecord`
- `AssetManifestScanner` 只用 registry 做“识别+反序列化+依赖提取”，source/meta 信息统一从 `.asset.meta` 提取，`ImportTraceData` 仅保留给调试/工具使用
- 全量扫描策略从“项目根递归 + skip”改为 `assetRoots` 定向扫描，根因级消除 `bin/obj/Library/.git` 误扫
- Registry 不再内置默认注册，builtin handler/importer 改由 `AssetTypeRegistration.RegisterBuiltIns()` 显式注入
- `AssetDatabase.Load<T>(path)` 对 source 路径先做 `IsUpToDate()` 判定，仅在 source 过期时才走 importer；watcher 暂时为空实现，不参与导入路径

## Deviations

- `AssetDatabase` 顶层代码已明显收敛到 `220` 行，框架层总行数仍为 `1048`（不含 `.csproj`），虽然已接近预算，但还未压到 `≤1000`
- 当前 registry 中只有 `SlangShaderImporter` 是真正的 source importer；`MaterialAsset` / `MaterialInstanceAsset` / `MeshAsset` 仍按“直接 asset 文件 + scanner”模型接入，而不是额外引入新的 source importer
- `MaterialAsset` / `MaterialInstanceAsset` / `MeshAsset` 的 legacy schema 字段仍保留在 FlatBuffer schema 中，用于旧资产读取兼容，但 runtime loader 已不再消费这些 fallback

## Weak Points / Follow-ups

- `ClusterBuilder` 仍会同时写 `DefaultMaterialSlots`，后续若要完全去掉 schema 级 legacy 字段，需要单独切掉 mesh 写出侧的旧字段
- Render pipeline 内部仍有若干直接 `SlangShaderImporter.Import(...)` 的 shader compile 路径，本批未一并收口到 `AssetDatabase`
- `docs/assets/asset_identity.md` 已同步到新的顶层服务模型，但更细的 editor workflow / UI 能力仍属于下一阶段
