# BATCH-09 Report: Asset + Material RenderWorld 完整改造

**Batch:** BATCH-09  
**Date:** 2026-04-18  
**Status:** COMPLETE

## Summary

完成了 asset + material 的完整 corrective rework，核心变化如下：

- `MeshAsset` 不再引用具体 `Material`，只保留 region 元数据
- `region -> material` 迁移为 ECS authoring 数据（`MeshMaterialBindings`）
- `MaterialAsset` 仍保持单一资产层级，但直接存储匿名 pass entity snapshots
- `Material` 运行时对象从单实体演化为 `root params + PassEntities[]`
- 引入 `RenderWorld`、`RenderWorldExtractor` 和 `RenderWorldMaterialSlotSynchronizer`
- `ClusterPipeline` 增加 RenderWorld query 边界，host 侧改为在 `world.Update()` 之前做 `extract + slot sync`
- 删除 `MeshMaterialResolver`，同步运行时、编辑器、测试和活跃设计文档

这次 batch 交付的是完整整改结果，不是预研，也不是只把 schema 改掉。

## Files Modified

### Asset / Schema

- `assets/Schema/mesh_asset.fbs`
- `assets/Schema/material_asset.fbs`
- `src/SomeEngine.Assets/Importers/ClusterBuilder.cs`
- `src/SomeEngine.Assets/Importers/GltfSourceImporter.cs`
- `src/SomeEngine.Assets/Pipeline/MeshAssetProvider.cs`

### Material / Render Runtime

- `src/SomeEngine.Render/Materials/Material.cs`
- `src/SomeEngine.Render/Materials/MaterialPassComponentRegistry.cs`
- `src/SomeEngine.Render/Materials/MaterialSlotBinding.cs`
- `src/SomeEngine.Render/Materials/BinSpace.cs`
- `src/SomeEngine.Render/Materials/MaterialSlotCache.cs`
- `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`
- `src/SomeEngine.Render/Assets/MeshMaterialResolver.cs` (deleted)

### RenderWorld / Pipeline

- `src/SomeEngine.Render/Components/MeshMaterialBindingsComponent.cs`
- `src/SomeEngine.Render/Components/RenderWorldComponents.cs`
- `src/SomeEngine.Render/Systems/RenderWorld.cs`
- `src/SomeEngine.Render/Systems/RenderWorldExtractor.cs`
- `src/SomeEngine.Render/Systems/RenderWorldMaterialSlotSynchronizer.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipelineEntityQueries.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipelineSlotBindingSelector.cs`

### Host

- `src/SomeEngine.Runtime/Program.cs`
- `src/SomeEngine.Editor/Program.cs`

### Tests

- `tests/SomeEngine.Tests/Assets/AssetResolverTests.cs`
- `tests/SomeEngine.Tests/Assets/GltfSourceImporterTests.cs`
- `tests/SomeEngine.Tests/ClusterBuilderTests.cs`
- `tests/SomeEngine.Tests/Materials/MaterialAssetPipelineTests.cs`
- `tests/SomeEngine.Tests/Materials/BinSpaceSlotBindingTests.cs`
- `tests/SomeEngine.Tests/Systems/RenderWorldExtractorTests.cs`
- `tests/SomeEngine.Tests/Pipelines/ClusterPipelineEntityQueriesTests.cs`
- `tests/SomeEngine.Tests/Pipelines/ClusterPipelineSlotBindingSelectorTests.cs`
- `tests/SomeEngine.Tests/Pipelines/RenderWorldMaterialSlotSynchronizerTests.cs`

### Docs / Workstream

- `docs/DESIGN.md`
- `docs/materials/architecture.md`
- `docs/assets/pipeline_overview.md`
- `docs/assets/asset_identity.md`
- `docs/TASK-DETAIL.md`
- `.dev-workstream/TASK-TRACKER.md`
- `ONBOARDING.md`

## Task Check

| Task ID | Title | Status |
|---|---|---|
| TASK-309a | Mesh region / authoring binding 重构 | ✅ |
| TASK-309b | MaterialAsset pass entity 模型落地 | ✅ |
| TASK-309c | RenderWorld extract / prepare / queue 分层 | ✅ |
| TASK-309d | 管线派生数据下沉 + ClusterPipeline 迁移 | ✅ |
| TASK-309e | Host / Test / Legacy 路径清理 | ✅ |

## Test Results

Full suite:

```text
Passed: 146, Failed: 0, Skipped: 1, Total: 147
```

Focused verification groups run during the batch:

- mesh / region / importer tests
- material pass snapshot round-trip tests
- RenderWorld extractor tests
- Cluster pipeline RenderWorld query tests
- BinSpace per-field slot binding tests
- RenderWorld material slot synchronizer tests

Solution build:

```text
dotnet build SomeEngine.slnx --no-restore -v minimal
0 errors
```

Runtime smoke:

- `dotnet run --project src/SomeEngine.Runtime/SomeEngine.Runtime.csproj --no-build`
- 15s timeout reached without startup exception output
- 视为“启动未立即崩溃”的 smoke；未做交互式人工渲染检查

## Issues Encountered

1. `ClusterPipeline.cs` 原文件编码不是 UTF-8，导致 `apply_patch` 直接修改失败。先做了编码规范化，再继续接线。
2. `MaterialPassComponentRegistry` 初版反序列化没有处理 public fields，导致 `OverlayShade.Layer` 丢成 0；后改为 `JsonSerializerOptions { IncludeFields = true }`。
3. `BinSpace` 旧模型假设每个 slot 只有一个 entity，无法支撑 RenderWorld pass 分离；本批把 slot cache 升级为 per-field entity 绑定。
4. `MaterialSlotCache.ComputeHash()` 初版没有处理空 field entity，导致 synchronizer 测试中触发空引用；已改为对空实体写入稳定零值 hash。

## Design Decisions

1. **不新增第二种 material asset。** 继续保持单一 `MaterialAsset`，只扩展它的 pass snapshot 表达能力。
2. **`Material` 保留为参数容器。** 运行时 `Material` 持有共享参数和 `PassEntities[]`，`Entity` 属性退化为首个 pass 的兼容别名。
3. **RenderWorld 先用整帧重建。** 先把 authoring -> render 的边界立住，后续如果要做增量 diff 再单独批次优化。
4. **slot bridge 采用 per-field entity 选择。** 这让当前 Cluster GPU 路径在不重写 shader slot 格式的前提下，也能从 RenderWorld 选 `Raster / Shading / VertexEval` 对应的 pass entity。
5. **`ClusterPipeline` 先做 query source 抽离 + RenderWorld 注入。** 这样保留旧 fallback，同时允许 runtime/editor 切到新数据源。

## Deviations

- `ClusterPipeline` 仍然保留 `_renderWorld ?? _materialSystem.Store` 的 fallback，而不是一次性删光旧路径。这是为了避免在同一个 batch 里同时重写所有测试夹具和非运行时调用点。
- runtime smoke 只做了短时启动验证，没有做交互式人工画面确认。

## Edge Cases

- `GltfSourceImporter` 现在在缺少 importer settings 时抛出明确的 `InvalidOperationException`，不再跌到模板文件找不到的次级异常。
- `MaterialAsset` 多 pass 中，某些 field 暂时没有对应 pass entity 时，slot binding 允许空 entity，占位为默认值而不是崩溃。
- `RenderWorldMaterialSlotSynchronizer` 每次同步前都会释放上一轮分配的 slot 区间，避免 refcount 无界增长。

## Weak Points / Improvement Opportunities

- `ClusterPipeline` 虽然已经能从 RenderWorld query，但 GPU 路径的 slot/bin 表达仍然是 Cluster-specific bridge；如果后面要彻底去掉这一层，需要单独批次重写 shader-side material indirection。
- `RenderWorldExtractor` 当前是整帧重建，规模变大后需要增量同步。
- `MaterialPassComponentRegistry` 目前只显式支持了最小需要的 snapshot component；后续可以演进到注册表驱动。

## Known Issues

- solution build 仍有一个第三方依赖漏洞警告：
  - `tools/DagVisualizer/SomeEngine.DagVisualizer.csproj`
  - `Tmds.DBus.Protocol 0.15.0`
  - 这是外部包告警，不是本批引入
- 仓库里仍存在若干既有编译器/analyzer warnings，本批未额外清扫
