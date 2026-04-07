# BATCH-06 Report: Material ECS Refactor

**Batch:** BATCH-06  
**Date:** 2026-04-02  
**Status:** COMPLETE

## Summary

本轮已经把 batch6 的主干切到了 ECS 材质模型：

- 删除 `MaterialPass` / `MaterialRegistry`，`Material` 收敛为 `1 Material = 1 Entity`
- 引入 `ClusterRaster` / `ClusterShadeComponent` / `ClusterDeform` / `OverlayShade` / `StencilState` / `MaterialRef`
- `Authoring` 改成直接面向 Slang `AttributeReflection` 的签名，并用固定分发替代 registry 查找
- `BinQueue` / `BinSpace` / `MaterialSlotCache` 迁移到 Entity 数据源
- `MaterialAssetLoader` / `MaterialInstanceLoader` / Runtime / Editor / 相关测试迁移到 Entity-based 路线
- `docs/README.md` / `docs/DESIGN.md` / `docs/TASK-DETAIL.md` / `docs/rendering/shading_pipeline.md` / `docs/rendering/feature_use_cases.md` 已同步到 ECS 材质现状

当前代码层面已经完整编译，batch6 直接相关测试和完整测试集都已通过。本批此前卡住的点不是业务断言失败，而是 Slang 原生测试长跑中的 test-host 崩溃；现已定位并修复。

## Files Modified

### Material / Loader / Runtime

- `src/SomeEngine.Render/Materials/Material.cs`
- `src/SomeEngine.Render/Materials/MaterialSystem.cs`
- `src/SomeEngine.Render/Materials/MaterialEntityTags.cs`
- `src/SomeEngine.Render/Materials/MaterialEntityUtility.cs`
- `src/SomeEngine.Render/Materials/BinQueue.cs`
- `src/SomeEngine.Render/Materials/BinSpace.cs`
- `src/SomeEngine.Render/Materials/MaterialSlotCache.cs`
- `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`
- `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs`
- `src/SomeEngine.Runtime/Program.cs`
- `src/SomeEngine.Editor/Program.cs`

### New ClusterRender ECS Types

- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/ShaderVariantRef.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/ClusterRaster.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/ClusterShadeComponent.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/ClusterDeform.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/OverlayShade.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/StencilState.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/MaterialRef.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/MaterialTags.cs`

### Authoring / Pipeline Consumption

- `src/SomeEngine.Render/Pipelines/ClusterRender/Authorings/ClusterRasterAuthoring.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Authorings/ClusterShadeAuthoring.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Authorings/StencilConfigAuthoring.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Authorings/ClusterRenderAuthoring.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ShadePSOGroup.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/RasterPSOBuilder.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialShadePass.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`

### Shader Import / Schema

- `assets/Schema/shader_asset.fbs`
- `src/SomeEngine.Assets/Importers/SlangShaderImporter.cs`

### Docs / Workflow Closeout

- `docs/README.md`
- `docs/DESIGN.md`
- `docs/TASK-DETAIL.md`
- `docs/rendering/shading_pipeline.md`
- `docs/rendering/feature_use_cases.md`

### Deleted

- `src/SomeEngine.Render/Materials/MaterialPass.cs`
- `src/SomeEngine.Render/Materials/MaterialRegistry.cs`

## Task Check

- [x] Task 1: Component / Authoring 骨架 + Shader entry-point attribute schema
- [x] Task 2: BinQueue 动态 group / region
- [x] Task 3: BinSpace / MaterialSlotCache Entity 化
- [x] Task 4: Cluster pipeline / shade / raster 消费端 Entity 化
- [x] Task 5: Material loader / runtime / editor 迁移
- [x] Task 6: 删除旧类型并迁移 batch6 直接相关测试
- [x] Full suite green

## Test Results

### Build

- `dotnet build src/SomeEngine.Render/SomeEngine.Render.csproj` ✅
- `dotnet build tests/SomeEngine.Tests/SomeEngine.Tests.csproj` ✅

### Targeted Tests

- `MaterialAssetRoundtripTests` ✅
- `MaterialInstanceRoundtripTests` ✅
- `ResolverIntegrationTests` ✅
- `ShaderGroupTests` ✅
- `ClusterShadeSig1Tests` ✅

Result:

```text
Passed: 20, Failed: 0, Skipped: 0
```

### Full Suite

`dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-build -- RunConfiguration.MaxCpuCount=1`

Result:

```text
Passed: 134, Failed: 0, Skipped: 1, Total: 135
```

## Design Decisions

- `Authoring` 不再通过 registry/dictionary 查找，改成固定 `switch attr.Name` 分发
- `ShaderVariantRef` 不再存 guid + backend variant index，而是直接持有 `ShaderAsset + EntryPoint`
- `MaterialSystem` 只保留 `EntityStore` owner 语义，不承担名称/Guid 索引
- 现有 `.mat` 仍保留 `passes` schema，但运行时把多个 pass shader author 到同一个 material entity 上，作为兼容桥
- Slang 根因修复：去掉调用方手写锁，调用方直接使用 `SlangShaderImporter.GlobalSession`；内部使用 thread-local `IGlobalSession`，避免跨线程共享同一个 Slang global session

## Deviations

- 为了满足“不做 dictionary / 线性 registry 查询”的约束，Task 1 的 registry 设计改成固定分发
- `ShaderVariantRef` 从 batch 指令里的 guid/index 方案调整为 direct reference + logical entry point，以避免后端 variant 覆盖问题

## Known Issues

- `docs/assets/asset_identity.md` 中仍保留部分 pre-ECS `MaterialPass / MaterialRegistry` 身份追踪叙述，需后续单独同步或归档

## Final Status

- **Code migration:** complete
- **Batch status:** approved
- **Review:** `.dev-workstream/reviews/BATCH-06-REVIEW.md`
