# 资产管线总览

## 架构概述

SomeEngine 的资产管线将源文件（.slang、.glb 等）转换为引擎可直接使用的二进制 Asset，并通过 GUID Manifest 系统维护全局一致的身份追踪。

```
Source File (.slang, .glb, ...)
    ↓ IAssetImporter
Asset File (.shader.asset / .mesh.asset)
    ↓ Meta System
Source Meta (.slang.meta)  +  Asset Meta (.shader.asset.meta)
    ↓ AssetManifestScanner
AssetManifest (index.json + graph.json)
    ↓ AssetDatabase
Runtime Load & Query
```

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
    TAsset? Load<TAsset>(string sourcePath, string? subAssetKey = null);

    // 按 GUID 加载（跨资产引用、editor 配置存储后的运行时加载）
    TAsset? Load<TAsset>(AssetGuid guid);

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

    // 强制重建索引
    void RebuildIndex();

    // 注意：AssetDatabase 不含文件监视。
    // 宿主自行组装 FileSystemWatcher 并调 Import / RebuildIndex。
}
```

### IAssetTypeHandler（消费端）

识别 .asset 文件并反序列化：

```csharp
public interface IAssetTypeHandler
{
    string AssetType { get; }
    bool MatchesAssetPath(string assetPath);
    IAsset? Load(string assetPath);
    IReadOnlyList<AssetGuid> GetDependencies(IAsset asset);
}
```

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

### AssetTypeRegistry

静态注册 handler 和 importer，消除 if-else 类型分发：

```csharp
public static class AssetTypeRegistry
{
    void Register(IAssetTypeHandler handler);
    void RegisterImporter(IAssetImporter importer);
    IAssetTypeHandler? Match(string assetPath);
    IAssetImporter? MatchImporter(string sourcePath);
}
```

## 三种运行模式

| | 无 Editor 开发 (Bevy 式) | Shipping Runtime | Editor |
|---|---|---|---|
| 用户写法 | `db.Load<ShaderAsset>("pbr.slang")` | 同左 | 拖拽选择 → 存 AssetGuid |
| 内部流程 | 查 manifest → 没有或过期 → Import → 加载 | 查 manifest → 加载 | Resolve → 存 GUID → 运行时 Load by GUID |
| 需要源文件 | 是 | 否 | 是 |
| 文件监视 | 宿主自行接 FileSystemWatcher | 不需要 | 宿主自行接 FileSystemWatcher |

## Importers

| Importer | 输入 | 输出 | 位置 |
|---|---|---|---|
| `SlangShaderImporter` | `.slang` | `ShaderAsset` | [SlangShaderImporter.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/SlangShaderImporter.cs) |
| `ClusterBuilder` | `.glb` | `MeshAsset` | [ClusterBuilder.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/ClusterBuilder.cs) |
| `PrimitiveMeshGenerator` | 参数化 | `MeshAsset` | [PrimitiveMeshGenerator.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/PrimitiveMeshGenerator.cs) |

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
