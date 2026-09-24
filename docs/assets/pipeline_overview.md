# 资产管线总览

## 架构概述

SomeEngine 的资产管线将源文件（.slang、.glb 等）转换为引擎可直接使用的二进制 Asset，并通过 GUID Manifest 系统维护全局一致的身份追踪。

```
Source File (.slang, .glb, ...)
    ↓ IAssetImporter
Asset File (.shader.asset / .mesh.asset / .texture.asset / .material.asset)
    ↓ Meta System
Source Meta (.slang.meta)  +  Asset Meta (.shader.asset.meta)
    ↓ AssetDatabase.Import()
AssetManifest (index.json + graph.json)
    ↓ AssetDatabase.Load<T>()
Runtime Object (ShaderAsset / ITexture / Material / ...)
```

当前与渲染绑定相关的约束：

- `MeshAsset` 只保留几何和 region 元数据，不再直接引用具体 `Material`
- `MaterialAsset` 是唯一材质资产，直接持有共享参数和匿名 pass entity snapshots
- mesh-local material linear table 绑定不属于 asset 依赖图，而属于 ECS authoring 数据（`MeshMaterialBindings`）

## 核心接口

### IAsset

唯一的资产标记接口。所有 FlatBuffer Schema 类型通过 partial class 实现。

```csharp
public interface IAsset
{
    AssetGuid AssetGuid { get; }
    string Name { get; }
}
```

### AssetDatabase

唯一入口类。根据运行模式透明切换行为：

```csharp
public sealed class AssetDatabase : IDisposable
{
    // 按源路径加载（用户代码入口）
    // dev: 按需导入 → 加载
    // shipping: manifest 查找 → 加载
    T? Load<T>(string sourcePath, string? subAssetKey = null) where T : class;

    // 按 GUID 加载（跨资产引用、运行时引用）
    T? Load<T>(AssetGuid guid) where T : class;

    // 导入源文件，返回全部产物 GUID（CLI build / 批量预编译）
    IReadOnlyList<AssetGuid> Import(string sourcePath);

    // 路径 → GUID 解析（不加载不导入）
    AssetGuid? Resolve(string sourcePath, string? subAssetKey = null);

    // 查询
    IReadOnlyList<AssetManifestRecord> List(string? assetType = null);

    // 依赖查询
    IReadOnlyList<AssetGuid> GetDependencies(AssetGuid guid);
    IReadOnlyList<AssetGuid> GetReferencers(AssetGuid guid);

    // 校验
    IReadOnlyList<AssetDiagnostic> Validate();

    // 注意：AssetDatabase 不含文件监视。
    // 宿主自行组装 FileSystemWatcher 并调 Import。
}
```

### IAssetProvider / AssetProvider\<T\>（消费端）

泛型 Provider 模型。Provider 负责从 .asset 文件创建运行时对象。
通过 Source Generator 自动发现（SomeEngine.Assets 程序集内），跨程序集 Provider 通过 DI 手动注册。

```csharp
public interface IAssetProvider
{
    string AssetType { get; }
    Type RuntimeType { get; }
    bool Matches(string assetPath);
    object Create(AssetGuid guid, string filePath);
    void Destroy(object resource);
    IReadOnlyList<AssetGuid> GetDependencies(string filePath) => [];
}

public abstract class AssetProvider<T> : IAssetProvider where T : class
{
    public abstract string AssetType { get; }
    public Type RuntimeType => typeof(T);
    public abstract bool Matches(string assetPath);
    public abstract T Create(AssetGuid guid, string filePath);
    public virtual void Destroy(T resource) => (resource as IDisposable)?.Dispose();
    public virtual IReadOnlyList<AssetGuid> GetDependencies(string filePath) => [];
}
```

AssetDatabase 内部使用 `TypedStore<T>` 按 `typeof(T)` 分桶缓存，运行时零装箱。

### IAssetImporter（生产端）

从源文件编译/转换出 .asset 文件：

```csharp
public interface IAssetImporter
{
    string ImporterName { get; }
    IReadOnlyList<string> SourceExtensions { get; }
    bool MatchesSourcePath(string sourcePath);
    IReadOnlyList<ImportedAsset> Import(string projectRoot, string sourcePath);
}
public record ImportedAsset(IAsset Asset, string SubAssetKey, string OutputPath);
```

### Source Generator 自动注册

`AssetCatalogGen` 扫描 SomeEngine.Assets 程序集中所有实现 `IAssetProvider` 和 `IAssetImporter` 的具体类，生成 `AssetCatalog`：

```csharp
public static class AssetCatalog
{
    public static IReadOnlyList<IAssetProvider> CreateProviders();
    public static IReadOnlyList<IAssetImporter> CreateImporters();
    public static AssetDatabase CreateDatabase(string projectRoot, string? manifestDirectory = null);
}
```

跨程序集的 Provider（如 `TextureAssetProvider` 在 Render 层）在 Runtime 的 `RuntimeApp.cs` 中手动追加到 provider 列表。

## 三种运行模式

| | 无 Editor 开发 (Bevy 式) | Shipping Runtime | Editor |
|---|---|---|---|
| 用户写法 | `db.Load<ShaderAsset>("pbr.slang")` | 同左 | 拖拽选择 → 存 AssetGuid |
| 内部流程 | 查 manifest → 没有或过期 → Import → 加载 | 查 manifest → 加载 | Resolve → 存 GUID → 运行时 Load by GUID |
| 需要源文件 | 是 | 否 | 是 |
| 文件监视 | 宿主自行接 FileSystemWatcher | 不需要 | 宿主自行接 FileSystemWatcher |

## Providers

| Provider | 输出类型 | 匹配文件 | 位置 |
|---|---|---|---|
| `ShaderAssetProvider` | `ShaderAsset` | `.shader.asset` / `.slang.asset` | [ShaderAssetProvider.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Pipeline/ShaderAssetProvider.cs) |
| `MeshAssetProvider` | `MeshAsset` | `.mesh.asset` | [MeshAssetProvider.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Pipeline/MeshAssetProvider.cs) |
| `MaterialAssetProvider` | `MaterialAsset` | `.material.asset` / `.mat.asset` | [MaterialAssetProvider.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Pipeline/MaterialAssetProvider.cs) |
| `MaterialInstanceProvider` | `MaterialInstanceAsset` | `.materialinstance.asset` / `.matinst.asset` | [MaterialInstanceProvider.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Pipeline/MaterialInstanceProvider.cs) |
| `TextureAssetDataProvider` | `TextureAsset` | `.texture.asset` | [TextureAssetDataProvider.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Pipeline/TextureAssetDataProvider.cs) |
| `TextureAssetProvider` (GPU) | `ITexture` | `.texture.asset` | [TextureAssetProvider.cs](file:///f:/SomeEngine/src/SomeEngine.Render/Assets/TextureAssetProvider.cs) |

## Importers

| Importer | 输入 | 输出 | 位置 |
|---|---|---|---|
| `SlangShaderImporter` | `.slang` | `ShaderAsset` (.shader.asset) | [SlangShaderImporter.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/SlangShaderImporter.cs) |
| `GltfSourceImporter` | `.gltf` / `.glb` | `MeshAsset` + `MaterialAsset` + `TextureAsset` | [GltfSourceImporter.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/GltfSourceImporter.cs) |

## Default Textures

默认材质生成工具会在 `assets/Textures/` 下写出需要的 1x1 占位纹理资产，并在生成材质时直接读取这些 `.texture.asset` 的现有 GUID。当前不再保留单独的静态常量层来表示这几个默认纹理。

## Meta System

双层 Meta 确保身份稳定：

- **SourceMeta** (`.slang.meta`)：持有 `SourceGuid`，提交到 VCS
- **AssetMeta** (`.shader.asset.meta`)：持有 `AssetGuid` + `ContentFingerprint` + `Dependencies`

## 身份系统

| ID 类型 | 格式 | 生成方式 | 稳定性 |
|---|---|---|---|
| `SourceGuid` | UUID v4 | 首次导入时生成，存入 `.meta` | ✅ 跨重命名稳定 |
| `AssetGuid` | UUID v5 | 从 SourceGuid + SubAssetKey 派生 | ✅ 确定性可重现 |
| `ContentFingerprint` | SHA-256 | 编译后字节码 hash | 内容变 → hash 变 |

> **详细设计**：[asset_identity.md](file:///f:/SomeEngine/docs/assets/asset_identity.md)
