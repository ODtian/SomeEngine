# BATCH-07c Report: 泛型 AssetProvider + TextureAsset Pipeline

**Batch:** BATCH-07c  
**Date:** 2026-04-10  
**Status:** COMPLETE

## Summary

完成了 `IAssetTypeHandler` 到泛型 `AssetProvider<T>` 的全面替换，引入了 `TypedStore<T>` 零装箱缓存，新增了完整的 TextureAsset 管线（FlatBuffer schema + CPU/GPU 双 Provider），统一了纹理加载路径，删除了所有硬编码纹理管理代码。

## Files Modified

### 新增

| 文件 | 说明 |
|---|---|
| `src/SomeEngine.Assets/IAssetProvider.cs` | 非泛型 `IAssetProvider` + 泛型 `AssetProvider<T>` 基类 |
| `src/SomeEngine.Assets/WellKnownAssets.cs` | 3 个默认纹理稳定 GUID 常量 |
| `src/SomeEngine.Assets/Pipeline/TextureAssetDataProvider.cs` | CPU 侧 TextureAsset 反序列化 Provider |
| `src/SomeEngine.Assets/Pipeline/TextureAssetSerializer.cs` | TextureAsset FlatBuffer 序列化工具 |
| `src/SomeEngine.Render/Assets/TextureAssetProvider.cs` | GPU 侧纹理 Provider，从 payload 解码创建 `ITexture` |
| `assets/Schema/texture_asset.fbs` | TextureAsset FlatBuffer schema |

### 修改

| 文件 | 说明 |
|---|---|
| `src/SomeEngine.Assets/AssetDatabase.cs` | TypedStore\<T\> 缓存 + IDisposable + Load\<T\> where T : class |
| `src/SomeEngine.Assets/AssetPipelineContracts.cs` | 删除 `IAssetTypeHandler` 接口 |
| `src/SomeEngine.Assets/SchemaPartials.cs` | 新增 TextureAsset : IAsset partial |
| `src/SomeEngine.Assets/Pipeline/ShaderAssetProvider.cs` | handler → Provider |
| `src/SomeEngine.Assets/Pipeline/MaterialAssetProvider.cs` | handler → Provider |
| `src/SomeEngine.Assets/Pipeline/MaterialInstanceAssetProvider.cs` | handler → Provider |
| `src/SomeEngine.Assets/Pipeline/MeshAssetProvider.cs` | handler → Provider |
| `src/SomeEngine.Assets/Importers/GltfSourceImporter.cs` | 提取 GLTF 图片 → `.texture.asset`，TextureBinding 改为 GUID |
| `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` | 纹理引用从路径改为 AssetGuid |
| `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs` | 纹理覆盖改为 GUID 解析 |
| `src/SomeEngine.Render/Graph/RenderGraph.cs` | 删除 GetOrCreatePersistentTexture |
| `src/SomeEngine.Render/Utils/TextureFileLoader.cs` | 删除 CreateTexture 方法 |
| `src/SomeEngine.Generators/AssetPipelineCatalogGenerator.cs` | 自动发现 IAssetProvider 替代 IAssetTypeHandler |
| `src/SomeEngine.Runtime/Program.cs` | 接入新管线：assetDb.Load\<ITexture\> + provider 列表 |
| `tools/GenerateDefaultAssets/Program.cs` | 生成默认 .texture.asset 文件 |

### 删除

| 文件 | 说明 |
|---|---|
| `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterials.cs` | 死代码：CreateDefault1x1Texture + SetupDefaultSlots（review 阶段清理） |

## Task Check

| Task ID | Title | Status |
|---|---|---|
| TASK-307i | AssetProvider\<T\> + TypedStore\<T\> 替换 IAssetTypeHandler | ✅ |
| TASK-307j | TextureAsset Pipeline | ✅ |
| TASK-307k | Source Generator 自动发现 + Runtime 接入 | ✅ |

## Test Results

```
Passed: 140, Failed: 0, Skipped: 1, Total: 141
```

Build:
```
0 errors, 17 warnings (全部 pre-existing)
```

## Design Decisions

- **`where T : class` 而非 `where T : IAsset`**：`Load<T>` 放宽泛型约束，支持 `ITexture` 等 Diligent 接口类型作为 Provider 输出
- **TypedStore 按 `typeof(T)` 分桶**：同一 `.texture.asset` 文件可被 `TextureAssetDataProvider` (T=TextureAsset) 和 `TextureAssetProvider` (T=ITexture) 两个 Provider 分别加载，互不干扰
- **Provider 自主 IO**：Provider 内部决定 IO 策略（File.ReadAllBytes / mmap / etc.），AssetDatabase 仅负责调度和缓存
- **Source Generator 自动发现**：SomeEngine.Assets 程序集内的 Provider/Importer 由 `AssetPipelineCatalogGenerator` 自动发现，跨程序集 Provider（如 Render 层的 TextureAssetProvider）在 Runtime composition root 手动追加
- **1x1 raw RGBA 快路**：TextureAssetProvider 对 4 字节 payload 走原始 RGBA 直传，避免 StbImageSharp 解码开销

## Deviations

- BATCH-07b INSTRUCTIONS 中未预告 `IAssetTypeHandler` → `AssetProvider<T>` 的替换，这是 07c 新增的架构决策
- `ClusterMaterials.cs` 删除在 review 阶段执行，而非 batch 实现阶段

## Weak Points / Follow-ups

- `.material.asset` / `.mesh.asset` 文件因纹理绑定逻辑变更（路径→GUID）需重新生成
- `MaterialCreationTests.GenerateDefaultMaterials` 测试已跳过（被 `GenerateDefaultAssets` 工具取代）
- 运行时纹理渲染效果尚未最终验证（需启动 Runtime 确认）
- `docs/assets/pipeline_overview.md` 已在 review 阶段同步更新

## Documentation Updates

- `docs/assets/pipeline_overview.md` 已重写：删除过期的 `IAssetTypeHandler` / `AssetTypeRegistry` 段落，替换为 `AssetProvider<T>` 模型、Providers 表、Importers 表、Default Textures 段落、Source Generator 段落
