# BATCH-07b Report: DI 驱动的注册模式 + Mesh Importer

**Batch:** BATCH-07b  
**Date:** 2026-04-07  
**Status:** COMPLETE

## Summary

完成了 `AssetTypeRegistry` 静态全局注册模式到构造注入模式的迁移，同时补全了 GLTF/GLB 的 `IAssetImporter`。

**核心变更：**
1. `AssetTypeRegistry.cs` 和 `AssetTypeRegistration.cs` 已删除，零静态可变状态
2. 4 个 handler 从 nested class 提取为 `Pipeline/` 下独立 public 类
3. `AssetDatabase` 构造函数改为接受 `IEnumerable<IAssetTypeHandler>` + `IEnumerable<IAssetImporter>`
4. 新增 `GltfSourceImporter`（原 `GlbMeshSourceImporter`），走 `IAssetImporter` 统一路径
5. `ClusterBuilder.Process()` 的 `materialGuidResolver` 回调已删除
6. Runtime `TryImportModelToMesh()` 已简化为 `assetDb.Import()`

## Files Modified

| 操作 | 文件 |
|---|---|
| [DELETE] | `src/SomeEngine.Assets/AssetTypeRegistry.cs` |
| [DELETE] | `src/SomeEngine.Assets/AssetTypeRegistration.cs` |
| [MODIFY] | `src/SomeEngine.Assets/AssetDatabase.cs` — 构造注入替换静态 registry |
| [MODIFY] | `src/SomeEngine.Assets/Importers/ClusterBuilder.cs` — 删除 materialGuidResolver |
| [MODIFY] | `src/SomeEngine.Assets/AssetManifest.cs` — 添加 FindByName |
| [NEW] | `src/SomeEngine.Assets/Importers/GltfSourceImporter.cs` |
| [MODIFY] | `src/SomeEngine.Runtime/Program.cs` — DI 配置、TryImportModelToMesh 简化 |
| [MODIFY] | `tests/SomeEngine.Tests/Assets/AssetDatabaseTests.cs` — 手动构造替换静态 registry |

## Task Check

| Task ID | Title | Status |
|---|---|---|
| TASK-307f | Handler 提取 + AssetDatabase 构造注入 | ✅ |
| TASK-307g | 删除静态 Registry + 迁移 Host/Test | ✅ |
| TASK-307h | GltfSourceImporter + 删除 ClusterBuilder 回调 | ✅ |

## Test Results

```
Passed: 134+, Failed: 0, Skipped: 1
```

## Design Decisions

- `GltfSourceImporter` 改名自 plan 中的 `GlbMeshSourceImporter`，因为它同时支持 `.gltf` 和 `.glb`
- Importer 从磁盘读 manifest 而非注入 `AssetDatabase`，避免循环依赖
- Source Generator (`AssetPipelineCatalogGenerator`) 自动发现 handler/importer 实现类，替代手动 DI 注册

## Deviations

- Plan 中设计使用 `Microsoft.Extensions.DependencyInjection` 容器注册，实际实现改为 Source Generator 自动发现 + `AssetDatabase` 直接构造注入，更轻量
- Plan 中的 `IAssetTypeHandler` 接口在 07c 中进一步替换为 `AssetProvider<T>` 泛型模型
