# BATCH-07c: 泛型 AssetProvider + TextureAsset Pipeline

**Batch Number:** BATCH-07c  
**Tasks:** TASK-307i, TASK-307j, TASK-307k  
**Phase:** Phase 3 - Asset Pipeline Modernization (续)  
**Estimated Effort:** medium  
**Priority:** HIGH  
**Dependencies:** BATCH-07b

---

## Onboarding & Workflow

### Complete Engineering Goal

用泛型 `AssetProvider<T>` 替换 `IAssetTypeHandler`，引入类型安全的 `TypedStore<T>` 缓存（零装箱），新增 TextureAsset 管线以统一纹理加载路径，删除所有硬编码纹理管理代码。

**现状问题（BATCH-07b 完成后）：**
1. `IAssetTypeHandler.Load()` 返回 `IAsset`，运行时需要强转——Render 层需要 `ITexture` 等非 IAsset 类型时无法使用
2. 纹理加载散落在 `RenderGraph.GetOrCreatePersistentTexture`、`TextureFileLoader.CreateTexture`、`ClusterMaterials.CreateDefault1x1Texture` 等多处
3. `AssetDatabase` 缓存用 `object` 字典，每次加载都有装箱开销
4. 默认纹理（白图/法线/ARM）使用硬编码路径字符串，`MaterialAsset` 的 `TextureBinding.Path` 存文件路径而非 GUID

**改造后模型：**
```csharp
// AssetProvider<T> — 类型安全，Provider 自主决定 IO
public abstract class AssetProvider<T> : IAssetProvider where T : class
{
    public abstract T Create(AssetGuid guid, string filePath);
    public virtual void Destroy(T resource) => (resource as IDisposable)?.Dispose();
}

// TypedStore<T> — 按类型分桶缓存，零装箱
AssetDatabase db = ...;
ITexture? tex = db.Load<ITexture>(guid);      // GPU 纹理
TextureAsset? data = db.Load<TextureAsset>(guid); // CPU 数据
ShaderAsset? shader = db.Load<ShaderAsset>(guid);
```

### What Does NOT Count As This Batch
- 改动 `AssetManifest` / `AssetGuid` 核心数据结构
- 引入异步加载
- 改动 Slang 编译管线
- 实现 streaming/热重载

### Required Reading (IN ORDER)
1. `src/SomeEngine.Assets/IAssetProvider.cs` — 新泛型 Provider 接口
2. `src/SomeEngine.Assets/AssetDatabase.cs` — TypedStore 缓存 + Load<T>
3. `src/SomeEngine.Assets/Pipeline/*.cs` — 5 个 Provider 实现
4. `src/SomeEngine.Assets/WellKnownAssets.cs` — 默认纹理 GUID 常量
5. `src/SomeEngine.Assets/Importers/GltfSourceImporter.cs` — TextureAsset 提取
6. `src/SomeEngine.Render/Assets/TextureAssetProvider.cs` — GPU 纹理 Provider
7. `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` — GUID 纹理引用
8. `src/SomeEngine.Generators/AssetPipelineCatalogGenerator.cs` — Source Generator
9. `tools/GenerateDefaultAssets/Program.cs` — 默认资产生成

### Source Code Locations
| Area | Path |
|---|---|
| Provider 接口 | `src/SomeEngine.Assets/IAssetProvider.cs` |
| 核心入口 | `src/SomeEngine.Assets/AssetDatabase.cs` |
| Assets 层 Provider | `src/SomeEngine.Assets/Pipeline/` |
| Render 层 Provider | `src/SomeEngine.Render/Assets/TextureAssetProvider.cs` |
| Texture Schema | `assets/Schema/texture_asset.fbs` |
| Well-known GUIDs | `src/SomeEngine.Assets/WellKnownAssets.cs` |
| Source Generator | `src/SomeEngine.Generators/AssetPipelineCatalogGenerator.cs` |
| Default Assets Tool | `tools/GenerateDefaultAssets/Program.cs` |
| Tests | `tests/SomeEngine.Tests/Assets/AssetDatabaseTests.cs` |

---

## Task Breakdown

### TASK-307i: AssetProvider\<T\> + TypedStore\<T\> 替换 IAssetTypeHandler

**目标：** 用泛型 `AssetProvider<T>` 和 `IAssetProvider` 非泛型桥接替换 `IAssetTypeHandler`。`AssetDatabase` 引入 `TypedStore<T>` 按类型分桶缓存。

**步骤：**


1. 新建 `IAssetProvider.cs`：非泛型 `IAssetProvider`（`AssetType`, `RuntimeType`, `Matches`, `Create`, `Destroy`, `GetDependencies`）+ 泛型 `AssetProvider<T>`
2. `AssetDatabase.cs` 重写：
   - 内部 `TypedStore<T> : ITypedStore` 缓存
   - 构造函数接受 `IEnumerable<IAssetProvider>` 替代 `IEnumerable<IAssetTypeHandler>`
   - `Load<T>(guid)` 约束从 `where T : IAsset` 改为 `where T : class`
   - `RegisterStoreViaReflection` 通过反射为每个 Provider 创建对应 `TypedStore<T>`
   - 实现 `IDisposable` — Dispose 时清理所有 TypedStore
3. Pipeline/ 下 4 个 handler 改写为 Provider：
   - `ShaderAssetTypeHandler` → `ShaderAssetProvider : AssetProvider<ShaderAsset>`
   - `MaterialAssetTypeHandler` → `MaterialAssetProvider : AssetProvider<MaterialAsset>`
   - `MaterialInstanceAssetTypeHandler` → `MaterialInstanceAssetProvider : AssetProvider<MaterialInstanceAsset>`
   - `MeshAssetTypeHandler` → `MeshAssetProvider : AssetProvider<MeshAsset>`
4. 删除 `IAssetTypeHandler` 接口（`AssetPipelineContracts.cs`）

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [NEW] | `src/SomeEngine.Assets/IAssetProvider.cs` |
| [MODIFY] | `src/SomeEngine.Assets/AssetDatabase.cs` |
| [MODIFY] | `src/SomeEngine.Assets/AssetPipelineContracts.cs` — 删除 IAssetTypeHandler |
| [MODIFY] | `Pipeline/ShaderAssetProvider.cs` — 从 handler 重写为 Provider |
| [MODIFY] | `Pipeline/MaterialAssetProvider.cs` |
| [MODIFY] | `Pipeline/MaterialInstanceAssetProvider.cs` |
| [MODIFY] | `Pipeline/MeshAssetProvider.cs` |

---

### TASK-307j: TextureAsset Pipeline

**目标：** 新增 TextureAsset FlatBuffer schema + 双 Provider（CPU data + GPU texture）+ GLTF 纹理提取 + WellKnownAssets 默认纹理。

**步骤：**

1. 新建 `assets/Schema/texture_asset.fbs`：
   - `TextureAsset` 表：`asset_guid`, `name`, `width`, `height`, `format`, `payload` (fs_vector:"Memory")
2. `SchemaPartials.cs` 新增 `TextureAsset : IAsset` partial
3. Pipeline/ 新增：
   - `TextureAssetDataProvider : AssetProvider<TextureAsset>` — CPU 侧反序列化
   - `TextureAssetSerializer` — Save/Load 工具
4. Render 层新增 `TextureAssetProvider : AssetProvider<ITexture>` — GPU 侧，从 payload 解码（StbImageSharp）创建 Diligent ITexture
5. `WellKnownAssets.cs` 新增 3 个稳定 GUID 常量
6. `GltfSourceImporter` 更新：提取 GLTF 图片 → `.texture.asset`，`TextureBinding.Path` 改为 GUID
7. `MaterialAssetLoader` / `MaterialInstanceLoader` 更新：纹理引用从路径改为 `AssetGuid.TryParse()`
8. `tools/GenerateDefaultAssets/Program.cs` 更新：生成 1x1 default `.texture.asset`
9. 删除 `RenderGraph.GetOrCreatePersistentTexture`
10. 删除 `TextureFileLoader.CreateTexture`

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [NEW] | `assets/Schema/texture_asset.fbs` |
| [MODIFY] | `src/SomeEngine.Assets/SchemaPartials.cs` |
| [NEW] | `src/SomeEngine.Assets/Pipeline/TextureAssetDataProvider.cs` |
| [NEW] | `src/SomeEngine.Assets/Pipeline/TextureAssetSerializer.cs` |
| [NEW] | `src/SomeEngine.Assets/WellKnownAssets.cs` |
| [MODIFY] | `src/SomeEngine.Assets/Importers/GltfSourceImporter.cs` |
| [NEW] | `src/SomeEngine.Render/Assets/TextureAssetProvider.cs` |
| [MODIFY] | `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` |
| [MODIFY] | `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs` |
| [MODIFY] | `src/SomeEngine.Render/Graph/RenderGraph.cs` |
| [MODIFY] | `src/SomeEngine.Render/Utils/TextureFileLoader.cs` |
| [MODIFY] | `tools/GenerateDefaultAssets/Program.cs` |

---

### TASK-307k: Source Generator 自动发现 + Runtime 接入

**目标：** `AssetPipelineCatalogGenerator` 自动发现 `IAssetProvider`/`IAssetImporter` 实现，替代手动注册。Runtime 接入新管线。

**步骤：**

1. `AssetPipelineCatalogGenerator.cs` 更新：
   - 扫描 `IAssetProvider` 实现（替代 `IAssetTypeHandler`）
   - 生成 `GeneratedAssetPipelineCatalog.CreateProviders()`
   - 生成 `GeneratedAssetPipelineCatalog.CreateDatabase()`
2. Runtime `Program.cs` 更新：
   - `TextureAssetProvider` (Render 层) 手动追加到 provider 列表
   - `assetDb.Load<ITexture>(guid)` 替代直接纹理加载
   - `assetDb.Load<MaterialAsset>(path)` 替代手动反序列化

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [MODIFY] | `src/SomeEngine.Generators/AssetPipelineCatalogGenerator.cs` |
| [MODIFY] | `src/SomeEngine.Runtime/Program.cs` |

---

## Execution Order

1. **TASK-307i** → Provider 替换 handler + TypedStore 缓存（核心接口改造）
2. **TASK-307j** → TextureAsset 管线 + 纹理统一加载（功能扩展）
3. **TASK-307k** → Source Generator 更新 + Runtime 接入（集成验证）

---

## Testing Requirements

- **TASK-307i:** 全量 `dotnet test` ≥ 137 passed, 0 failed（接口替换，行为不变）
- **TASK-307j:** 纹理 Provider 创建验证、WellKnownAssets 常量验证
- **TASK-307k:** 全量 `dotnet test` ≥ 140 passed, 0 failed；Runtime 编译通过

---

## Quality Standards

- `IAssetTypeHandler` 接口彻底删除（零残留）
- `GetOrCreatePersistentTexture` 彻底删除
- `TextureFileLoader.CreateTexture` 彻底删除
- `AssetDatabase.Load<T>()` 约束为 `where T : class`（支持非 IAsset 类型如 ITexture）
- TypedStore 按 `typeof(T)` 分桶，运行时零装箱
- `MaterialAsset.TextureBinding.Path` 存 GUID 字符串而非文件路径
- 默认纹理 GUID 在 `WellKnownAssets` 中定义，由 `GenerateDefaultAssets` 工具生成 `.texture.asset`

---

## Success Criteria

- [x] `IAssetTypeHandler` 已删除
- [x] 5 个 `AssetProvider<T>` 实现已落地（Shader/Material/MaterialInstance/Mesh/TextureData）
- [x] `TextureAssetProvider` (GPU, Render 层) 已落地
- [x] `TypedStore<T>` 零装箱缓存已实现
- [x] `AssetDatabase` 实现 `IDisposable`
- [x] `texture_asset.fbs` schema 已创建
- [x] `WellKnownAssets` 3 个默认纹理 GUID 已定义
- [x] `GltfSourceImporter` 产出 `.texture.asset` + MaterialAsset 纹理引用改为 GUID
- [x] `GetOrCreatePersistentTexture` 已删除
- [x] `TextureFileLoader.CreateTexture` 已删除
- [x] Source Generator 自动发现 `IAssetProvider` 实现
- [x] 全量测试 ≥ 140 passed, 0 failed
- [x] 报告已提交
