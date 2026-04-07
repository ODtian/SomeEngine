# 资产标识与源文件追踪设计 v2

> 面向 `ShaderAsset`、`MaterialAsset`、`MaterialInstanceAsset`、`MeshAsset` 的统一引用模型。
> 本文目标：定义资产间引用的 GUID 体系、源文件回溯机制、磁盘格式与 C# API 分层，以及编辑器友好的扩展点。

---

## 1. 背景与现状问题

### 1.1 当前数据流

```text
.slang ──SlangShaderImporter──► ShaderAsset (FlatBuffer)
                                     │
MaterialAsset.PassEntry.shader ──► string 名称匹配
MaterialInstanceAsset.parent ────► string 路径匹配
MeshAsset.default_material_slots ─► string[] 路径匹配
```

### 1.2 已知问题

| 问题 | 影响 |
|------|------|
| 20+ 处直接 `SlangShaderImporter.Import(path)`，无缓存一致性保证 | Shader 重复编译、身份不稳定 |
| `MaterialAsset.PassEntry.shader` 为纯字符串 | 重命名即断裂，编辑器无法做引用追踪 |
| `MaterialInstanceAsset.parent` 为路径字符串 | 同上 |
| `MeshAsset.default_material_slots` 为路径字符串数组 | 同上 |
| 旧的字符串 shader 身份与 `Name.GetHashCode()` 风格签名 | Hash 碰撞风险，重命名即失效 |
| 无 `.meta` 文件，无 SourceGuid | 源文件重命名/移动后无法关联旧资产 |
| Shader include/import 依赖未追踪 | 被引用文件修改后不触发重编译 |
| 无统一资产数据库 | 编辑器无法实现"引用查找""安全删除"等功能 |

---

## 2. 设计目标

### 必须达成

1. **资产间正式引用统一使用 AssetGuid**，类型安全，编译期防误用
2. **SourceGuid + 依赖链追踪**，覆盖 include/import 场景
3. **重导入幂等**：同一源文件重导入后 AssetGuid 不变
4. **运行时零依赖源文件**：部署包只需 `.asset` + manifest
5. **编辑器友好**：支持引用查找、安全重命名、拖拽赋值、材质面板 Shader 选择

### 非目标（本轮）

- 完整的可视化资产数据库 UI
- 运行时直接加载 `.slang` / `.gltf`
- 资产版本管理 / diff / merge

---

## 3. 核心分层架构

```text
┌─────────────────────────────────────────────────────────┐
│                    Host Layer                             │
│  Runtime / Editor / CLI Build                            │
│  通过 AssetDatabase 统一入口访问资产                      │
├─────────────────────────────────────────────────────────┤
│                    AssetDatabase                          │
│  Load<T>(path) / Load<T>(guid) / Import(path)            │
│  Resolve / List / Validate / RebuildIndex                │
├─────────────────────────────────────────────────────────┤
│                    Registry Layer                         │
│  AssetTypeRegistry                                       │
│  IAssetTypeHandler (消费端: .asset → 对象)               │
│  IAssetImporter    (生产端: source → .asset)             │
├─────────────────────────────────────────────────────────┤
│                    Identity & Meta                        │
│  AssetGuid  SourceGuid  AssetRef<T>  IAsset              │
│  AssetManifest  SourceMeta  AssetMeta  ImportTrace        │
├─────────────────────────────────────────────────────────┤
│                    Serialization                          │
│  FlatBuffer schemas (.fbs)  ←→  C# schema objects        │
│  .meta (JSON)  manifest (JSON)                           │
├─────────────────────────────────────────────────────────┤
│                    Runtime Consumers                      │
│  Material (不实现 IAsset，只持有 AssetGuid 属性)          │
│  只消费 AssetGuid + Schema 类型，不感知 Source / Import   │
└─────────────────────────────────────────────────────────┘
```

---

## 4. ID 体系设计

### 4.1 两种 ID

| | SourceGuid | AssetGuid |
|---|---|---|
| 含义 | 源文件（`.slang` / `.gltf` / `.png`）的稳定身份 | 导入产物（`.asset`）的稳定身份 |
| 生成时机 | 首次发现源文件时 | 首次导入生成产物时 |
| 存储位置 | `<source>.meta` | 产物文件内 / `<asset>.meta` |
| 重命名/移动 | 不变 | 不变 |
| 重导入 | 不变 | 不变 |
| 删除后重建 | 新 GUID | 新 GUID |

### 4.2 一源多产物

一个源文件可以产出多个资产（如 `.gltf` → Mesh + Material + Texture）。

```text
SourceGuid("abc")
  ├── AssetGuid("001")  MeshAsset      sub_asset_key = "mesh:Body"
  ├── AssetGuid("002")  MaterialAsset  sub_asset_key = "material:Skin"
  └── AssetGuid("003")  MaterialAsset  sub_asset_key = "material:Eyes"
```

`sub_asset_key` 由导入器定义，格式为 `type:name`，在同一 SourceGuid 下唯一。重导入时由 `(SourceGuid, sub_asset_key)` 二元组定位已有 AssetGuid，保证稳定性。

### 4.3 C# 强类型包装

```csharp
namespace SomeEngine.Assets;

/// <summary>正式资产的稳定标识。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct AssetGuid(Guid Value) : IEquatable<AssetGuid>
{
    public static readonly AssetGuid Empty = new(Guid.Empty);
    public static AssetGuid New() => new(Guid.NewGuid());

    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>FlatBuffer 序列化用 "D" 格式。</summary>
    public string ToFlatString() => Value.ToString("D");
    public static AssetGuid Parse(string s) => new(Guid.Parse(s));

    public override string ToString() => Value.ToString("D");
}

/// <summary>源文件的稳定标识。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SourceGuid(Guid Value) : IEquatable<SourceGuid>
{
    public static readonly SourceGuid Empty = new(Guid.Empty);
    public static SourceGuid New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public string ToFlatString() => Value.ToString("D");
    public static SourceGuid Parse(string s) => new(Guid.Parse(s));
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// 强类型资产引用。序列化时存 AssetGuid，运行时解析为具体对象。
/// 编辑器可据此推断引用类型（如"这个字段只接受 ShaderAsset"）。
/// </summary>
public readonly record struct AssetRef<TAsset>(AssetGuid Id)
    where TAsset : IAssetRecord
{
    public bool IsEmpty => Id.IsEmpty;
    public static AssetRef<TAsset> Empty => new(AssetGuid.Empty);
}
```

> [!IMPORTANT]
> `AssetGuid` 与 `SourceGuid` 是不同类型，编译期即可防止混用。
> `AssetRef<T>` 的泛型参数让编辑器面板可以自动过滤可选资产类型。

---

## 5. 导入追踪（ImportTrace）

### 5.1 数据模型

```csharp
/// <summary>记录一份导入产物与其源文件的关系和校验信息。</summary>
public sealed class ImportTrace
{
    /// <summary>来源源文件的稳定标识。</summary>
    public required SourceGuid SourceGuid { get; init; }

    /// <summary>最近一次发现源文件的路径（调试/重扫描用，不作为正式引用）。</summary>
    public required string LastKnownSourcePath { get; init; }

    /// <summary>同一源文件的第几个产物（一源多产物场景）。</summary>
    public required string SubAssetKey { get; init; }

    /// <summary>
    /// 联合哈希：所有参与编译的文件内容 + 导入配置。
    /// 判断是否需要重导入的唯一依据。
    /// </summary>
    public required string ContentFingerprint { get; init; }

    /// <summary>参与编译的所有文件及其独立哈希（用于精确定位变更来源）。</summary>
    public required IReadOnlyList<DependencyEntry> Dependencies { get; init; }

    /// <summary>导入器版本。版本升级时强制重导入。</summary>
    public required uint ImporterVersion { get; init; }
}

/// <summary>参与编译的单个文件记录。</summary>
public sealed class DependencyEntry
{
    /// <summary>相对于项目根的路径。</summary>
    public required string RelativePath { get; init; }
    /// <summary>该文件的内容哈希（SHA-256 hex, lowercase）。</summary>
    public required string ContentHash { get; init; }
}
```

### 5.2 ContentFingerprint 计算

```text
ContentFingerprint = SHA256(
    sorted(dep.RelativePath + ":" + dep.ContentHash)
    + "||" + ImportSettingsJson
    + "||" + ImporterVersion
)
```

- 对 `SlangShaderImporter`：Dependencies 包含主 `.slang` 文件及所有 `import`/`#include` 的文件
- 对 `GltfImporter`（未来）：Dependencies 包含 `.gltf` + 所有引用的 `.bin` 和贴图
- 任何 dependency 的 hash 变化 → ContentFingerprint 变化 → 触发重导入

### 5.3 重导入判定流程

```text
发现源文件变更（文件系统 watcher / 手动触发）
    │
    ├── 读取 <source>.meta → SourceGuid
    ├── 查 manifest → 已有 AssetGuid[]
    ├── 计算新 ContentFingerprint
    │
    ├── 与已有 ImportTrace.ContentFingerprint 比较
    │       │
    │       ├── 相同 → 跳过（已是最新）
    │       └── 不同 → 重导入，复用已有 AssetGuid
    │
    └── manifest 中无记录 → 首次导入，分配新 AssetGuid
```

---

## 6. `.meta` 与 Manifest

### 6.1 Source `.meta`（提交到 VCS）

与源文件同目录，命名规则：`<filename>.meta`

```json
{
    "source_guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "importer": "SlangShaderImporter",
    "importer_settings": {}
}
```

**生命周期策略**：

| 场景 | 行为 |
|------|------|
| 新源文件，无 `.meta` | 导入器自动生成 `.meta`，分配新 SourceGuid |
| `.meta` 存在 | 读取 SourceGuid，不重新分配 |
| `.meta` 被删但源文件在 | 重新生成新 SourceGuid（视为全新源文件）。构建系统发出警告 |
| 源文件被删但 `.meta` 在 | 构建系统标记为"孤儿 meta"，不自动删除，由用户/编辑器清理 |

### 6.2 Asset `.meta`（提交到 VCS）

与 `.asset` 文件同目录，存储 AssetGuid 和 ImportTrace 快照：

```json
{
    "asset_guid": "11223344-5566-7788-99aa-bbccddeeff00",
    "source_guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "sub_asset_key": "shader:main",
    "content_fingerprint": "e3b0c44298fc1c149afbf4c8996fb924...",
    "dependencies": [
        { "path": "assets/Shaders/cluster_shade_material.slang", "hash": "abc123..." },
        { "path": "assets/Shaders/common/brdf.slang", "hash": "def456..." }
    ],
    "importer_version": 1
}
```

> [!TIP]
> AssetGuid 存在 `.meta` 中（而非 `.asset` 文件内），这样即使 `.asset` 是纯二进制格式，GUID 也可被工具解析。
> `.asset` 文件内也冗余存储 AssetGuid，用于运行时自校验。

### 6.3 Manifest（不提交 VCS，构建产物）

由全量扫描/增量构建生成，放在 `Library/AssetManifest/` 下：

```text
Library/AssetManifest/
  ├── source_index.json      // SourceGuid → SourcePath
  ├── asset_index.json       // AssetGuid → AssetPath, AssetType
  └── dependency_graph.json  // AssetGuid → AssetGuid[] (谁引用了谁)
```

运行时部署包只需 `asset_index.json`（或其二进制压缩版） + 所有 `.asset` 文件。

---

## 7. Schema 变更

### 7.1 `shader_asset.fbs`

```diff
+table DependencyEntry {
+    path: string;
+    content_hash: string;
+}
+
+table ImportTrace {
+    source_guid: string;
+    source_path: string;
+    sub_asset_key: string;
+    content_fingerprint: string;
+    dependencies: [DependencyEntry];
+    importer_version: uint;
+}
+
 table ShaderAsset (fs_serializer) {
+    asset_guid: string;
     name: string;
+    import_trace: ImportTrace;
     variants: [ShaderBytecode];
     reflections: [BackendReflection];
     metadata: ShaderMetadata;
 }
```

### 7.2 `material_asset.fbs`

```diff
 table PassEntry {
-    shader: string;
+    shader_guid: string;
+    shader: string;  // 迁移期回退，新写出不填
     tags: [TagEntry] (fs_vector:"IList");
 }

 table MaterialAsset (fs_serializer) {
+    asset_guid: string;
     name: string;
     passes: [PassEntry] (fs_vector:"IList");
     textures: [TextureBinding] (fs_vector:"IList");
     scalars: [ScalarParam] (fs_vector:"IList");
 }
```

### 7.3 `material_instance_asset.fbs`

```diff
 table MaterialInstanceAsset (fs_serializer) {
+    asset_guid: string;
-    parent: string;
+    parent_guid: string;
+    parent: string;  // 迁移期回退
     overrides: [ParamOverride] (fs_vector:"IList");
     scalar_overrides: [ScalarOverride] (fs_vector:"IList");
     tag_overrides: [TagOverride] (fs_vector:"IList");
 }
```

### 7.4 `mesh_asset.fbs`

```diff
 table MeshAsset (fs_serializer) {
+    asset_guid: string;
     name: string;
     ...
-    default_material_slots: [string] (fs_vector:"IList");
+    default_material_guids: [string] (fs_vector:"IList");
+    default_material_slots: [string] (fs_vector:"IList");  // 迁移期回退
 }
```

---

## 8. C# API 设计

### 8.1 唯一接口 `IAsset`

```csharp
/// <summary>所有可持久化资产的标记接口。</summary>
public interface IAsset
{
    AssetGuid AssetGuid { get; }
    string Name { get; }
}
```

> 没有 `IImportedAsset`、`IShaderAssetRecord` 等中间接口。
> ImportTrace 信息在 `.asset.meta` 文件中，不在 `IAsset` 上。
> FlatBuffer Schema 类型通过 partial class 实现 `IAsset`。

### 8.2 `AssetDatabase` — 唯一入口类

没有 `IAssetResolver`/`IAssetCatalog`/`IAssetWorkspace`/`IAssetDatabase` 接口。
一个具体类，三种运行模式透明切换。

```csharp
public sealed class AssetDatabase : IDisposable
{
    // === 按源路径加载（用户代码入口）===
    // dev: 自动导入（如过期或不存在）→ 加载
    // shipping: manifest 查找 → 加载
    public TAsset? Load<TAsset>(string sourcePath, string? subAssetKey = null)
        where TAsset : class, IAsset;

    // === 按 GUID 加载（跨资产引用） ===
    // 材质引用 shader → 存 AssetGuid → 运行时用这个加载
    public TAsset? Load<TAsset>(AssetGuid guid)
        where TAsset : class, IAsset;

    // === 导入（CLI build / 批量预编译） ===
    // 返回该源文件的全部产物 GUID
    public IReadOnlyList<AssetGuid> Import(string sourcePath);

    // === 解析（不加载不导入）===
    public AssetGuid? Resolve(string sourcePath, string? subAssetKey = null);

    // === 查询 ===
    public IReadOnlyList<AssetManifestRecord> List(string? assetType = null);
    public IReadOnlyList<AssetGuid> GetDependencies(AssetGuid guid);
    public IReadOnlyList<AssetGuid> GetReferencers(AssetGuid guid);

    // === 校验 ===
    public IReadOnlyList<AssetDiagnostic> Validate();

    // === 重建索引 ===
    public void RebuildIndex();

    // 注意：AssetDatabase 不负责文件监视。
    // 文件系统 watcher 由宿主自行组装：
    //   var watcher = new FileSystemWatcher(projectRoot);
    //   watcher.Changed += (_, e) => db.Import(e.FullPath);
}
```

### 8.3 注册制类型分发

```csharp
/// <summary>消费端：识别 .asset 文件并反序列化。</summary>
public interface IAssetTypeHandler
{
    string AssetType { get; }
    bool MatchesAssetPath(string assetPath);
    IAsset? Load(string assetPath);
    IReadOnlyList<AssetGuid> GetDependencies(IAsset asset);
}

/// <summary>生产端：从源文件编译/转换出 .asset 文件。</summary>
public interface IAssetImporter
{
    string ImporterName { get; }
    IReadOnlyList<string> SourceExtensions { get; }
    bool MatchesSourcePath(string sourcePath);
    IReadOnlyList<ImportedAsset> Import(string projectRoot, string sourcePath);
}

public record ImportedAsset(IAsset Asset, string SubAssetKey, string OutputPath);

/// <summary>静态注册，消除 Scanner/AssetDatabase 中的 if-else 类型分支。</summary>
public static class AssetTypeRegistry
{
    public static void Register(IAssetTypeHandler handler);
    public static void RegisterImporter(IAssetImporter importer);
    public static IAssetTypeHandler? Match(string assetPath);
    public static IAssetImporter? MatchImporter(string sourcePath);
}
```

### 8.4 三种使用场景

**场景 1：无 Editor 开发（Bevy 式，代码配置资产）**

```csharp
var db = new AssetDatabase(projectRoot);
// 用户用源路径引用资产，引擎透明处理导入
var shader = db.Load<ShaderAsset>("assets/Shaders/pbr.slang");
var mesh   = db.Load<MeshAsset>("assets/Models/character.gltf");
var skin   = db.Load<MaterialAsset>("assets/Models/character.gltf", "mat:Skin");
```

**场景 2：Shipping Runtime（预编译，无源文件）**

```csharp
// 同样的代码，manifest 已由 CLI build 预构建
var shader = db.Load<ShaderAsset>("assets/Shaders/pbr.slang");
// 内部流程：manifest 查找 → .asset 文件反序列化 → 返回
```

**场景 3：Editor（UI 配置资产引用）**

```text
用户在 Material Inspector 中点击 "Shader" 字段的选择器
    │
    ├── 调用 db.List("ShaderAsset")
    │   → 返回 [{Guid, Name, Path}, ...] 用于填充下拉列表
    │
    ├── 用户选择了 "cluster_shade_material"
    │   → 序列化 AssetGuid 到 MaterialAsset.PassEntry.shader_guid
    │
    └── 运行时 MaterialAssetLoader 读 shader_guid
        → db.Load<ShaderAsset>(guid) → 返回
```

---

## 9. 运行时对象适配

### 9.1 `Material` 变更

`Material` **不实现 `IAsset`**（它是运行时渲染对象，不是磁盘资产）。
保留 `AssetGuid` 属性作为缓存键。

```diff
 public class Material : IDisposable
 {
+    public AssetGuid AssetGuid { get; set; }
     public string Name { get; set; } = "";
-    // 不再实现 IAssetRecord / IAsset
     ...
 }
```

> `MaterialPass` 和 `MaterialRegistry` 已在 BATCH-06 删除。当前材质系统使用 `MaterialSystem`（EntityStore-based）。

### 9.2 Loader 适配

Render 层的三个 Loader 删除 `IAssetResolver` 参数重载，只保留 delegate 重载：

```csharp
// MaterialAssetLoader — 已有 delegate 签名，不变：
public delegate ShaderAsset? ShaderLoadFunc(AssetGuid guid);
public delegate ITexture? TextureLoadFunc(string name);

// MaterialInstanceLoader — 已有 delegate 签名，不变：
public delegate Material? ParentMaterialLoadFunc(AssetGuid guid);

// MeshMaterialResolver — 已有 delegate 签名，不变
```

宿主负责将 `AssetDatabase` 包装为 delegate 传入：

```csharp
// Runtime Program.cs
ShaderLoadFunc shaderLoad = guid => db.Load<ShaderAsset>(guid);
MaterialAssetLoader.Load(materialAsset, shaderLoad, textureLoad);
```

这样 Render 层不依赖 `AssetDatabase` 具体类型，只依赖 `AssetGuid` 值类型和 Schema 类型。

---

## 10. SlangShaderImporter 改造

### 10.1 依赖收集策略

#### 核心思路：编译后从 Slang API 提取精确依赖

Slang 的 `IModule` 接口提供了内置的依赖追踪 API：

```csharp
// SlangShaderSharp/src/IModule.cs
/// Get the number of dependency files that this module depends on.
/// This includes both the explicit source files, as well as any
/// additional files that were transitively referenced (e.g., via
/// a `#include` directive).
int GetDependencyFileCount();

/// Get the path to a file this module depends on.
string GetDependencyFilePath(int index);
```

此 API 返回编译器**实际解析**的所有文件，包含通过 `#include` 和 `import` 传递引用的全部依赖。
相比正则静态扫描，它天然处理条件编译、宏控制的 include、搜索路径解析等所有边界情况。

#### 两阶段判定流程（解决鸡蛋问题）

```text
发现源文件变更
    │
    ├── 读取历史 ImportTrace（从 .asset.meta）
    │       │
    │       ├── 有历史记录
    │       │       │
    │       │       ├── 逐一检查 Dependencies 列表中每个文件的 hash
    │       │       │       │
    │       │       │       ├── 全部 hash 未变 → 跳过编译 ✓
    │       │       │       │
    │       │       │       └── 有 hash 变化 → 执行重编译
    │       │       │               └── 编译后从 IModule 提取新的精确依赖列表
    │       │       │                   → 存入 ImportTrace（自修正）
    │       │       │
    │       │       └── 注：这里的 Dependencies 来自上次编译后的精确结果
    │       │
    │       └── 无历史记录（首次编译）
    │               └── 直接编译 → 从 IModule 提取依赖列表 → 存入 ImportTrace
    │
    └── 关键保证：
          - 首次编译后即拥有完整依赖信息
          - 新增 #include → 主文件 hash 变 → 触发重编译 → 依赖列表自动更新
          - 删除 #include → 同上，自修正
```

> [!IMPORTANT]
> 此方案的核心巧妙之处在于**自修正**：即使历史依赖列表不完整（首次编译场景），
> 编译后也会从 Slang 拿到 100% 精确的依赖列表并存储。后续检查始终基于上次编译器
> 提供的精确结果做判断。

### 10.2 核心变更

```csharp
public static class SlangShaderImporter
{
    public const uint ImporterVersion = 1;

    public static ShaderAsset Import(
        string sourcePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
        ImportSettings? settings = null)
    {
        sourcePath = Path.GetFullPath(sourcePath);

        // 1. 用历史依赖做快速判定（跳过不必要的编译）
        if (existingAsset != null)
        {
            var historicalDeps = existingAsset.Dependencies;
            var fingerprint = ComputeFingerprint(historicalDeps, settings, ImporterVersion);
            if (existingAsset.ContentFingerprint == fingerprint)
            {
                return LoadCachedAsset(existingAsset.AssetPath);
            }
        }

        // 2. 执行编译（现有 Slang 编译逻辑）
        //    LoadModuleFromSource → module
        var module = CompileShader(sourcePath);

        // 3. 编译后从 IModule 提取精确依赖
        var deps = CollectDependenciesFromModule(module, projectRoot);

        // 4. 基于精确依赖计算最终 fingerprint
        var finalFingerprint = ComputeFingerprint(deps, settings, ImporterVersion);

        // 5. 构建 asset，填充 ImportTrace
        var asset = BuildShaderAsset(module, ...);
        asset.AssetGuid = existingAsset?.AssetGuid.ToFlatString()
                        ?? AssetGuid.New().ToFlatString();
        asset.ImportTrace = new ImportTrace
        {
            SourceGuid = sourceMeta.SourceGuid.ToFlatString(),
            SourcePath = sourcePath,
            SubAssetKey = "shader:main",
            ContentFingerprint = finalFingerprint,
            Dependencies = deps,    // ← 精确依赖列表
            ImporterVersion = ImporterVersion,
        };
        return asset;
    }

    /// <summary>
    /// 编译后从 Slang IModule 提取精确的依赖文件列表。
    /// 包含主文件本身及所有通过 #include / import 传递引用的文件。
    /// </summary>
    private static List<DependencyEntryData> CollectDependenciesFromModule(
        IModule module, string projectRoot)
    {
        int count = module.GetDependencyFileCount();
        var deps = new List<DependencyEntryData>(count);
        for (int i = 0; i < count; i++)
        {
            string absPath = module.GetDependencyFilePath(i);
            string rel = Path.GetRelativePath(projectRoot, absPath).Replace('\\', '/');
            deps.Add(new DependencyEntryData
            {
                RelativePath = rel,
                ContentHash = ComputeSha256(File.ReadAllText(absPath)),
            });
        }
        return deps;
    }
}
```

### 10.3 兼容过渡

现有 20+ 处调用 `SlangShaderImporter.Import(path)` 的代码，提供一个兼容包装：

```csharp
/// <summary>
/// 兼容旧调用方式。自动处理 .meta 查找/创建。
/// 新代码应直接使用带 SourceMeta 参数的重载。
/// </summary>
[Obsolete("Use Import(path, sourceMeta, existingAsset) overload")]
public static ShaderAsset Import(string filePath, string? source = null)
{
    var sourceMeta = SourceMetaManager.GetOrCreate(filePath);
    var existingAsset = AssetMetaManager.TryLoad(filePath + ".asset");
    return Import(filePath, sourceMeta, existingAsset);
}
```

---

## 11. 孤儿资产与引用完整性

### 11.1 孤儿检测

```text
定义：
  - 孤儿源 meta：.meta 文件存在，但对应的源文件不存在
  - 孤儿资产：.asset 存在，但没有任何资产引用它
  - 悬挂引用：资产 A 引用了 AssetGuid X，但 X 不存在于 manifest

检测时机：
  - 编辑器启动时全量扫描
  - 构建前验证
  - 用户手动触发 "Validate Project"
```

### 11.2 处理策略

| 场景 | 行为 |
|------|------|
| 源文件删除 | 标记对应 `.asset` 为"源丢失"，不自动删除。编辑器弹窗提示 |
| 资产无引用 | 仅报告，不自动删除（可能是入口资产） |
| 悬挂引用 | 编辑器高亮显示，构建时报 Warning |
| `.meta` 丢失 | 重新生成新 SourceGuid + 关联断裂警告 |

---

## 12. 性能考量

### 12.1 运行时

- `AssetGuid` 是 16-byte 值类型（`Guid`），比较和哈希为 O(1)
- Manifest 加载后构建 `Dictionary<AssetGuid, string>` 索引
- 不在运行时做任何 `.meta` 解析

### 12.2 编辑器

- 增量 manifest 更新：文件系统 watcher 监听变更，只重算变更文件的 fingerprint
- 依赖图用邻接表存储，引用查找为 O(degree)
- Shader 选择面板缓存 `ListAssets<ShaderAsset>()` 结果，仅在 manifest 版本号变化时刷新

### 12.3 导入

- `ContentFingerprint` 比较避免无谓重编译
- 内存缓存 `Dictionary<AssetGuid, ShaderAsset>` 避免重复反序列化
- `SlangShaderImporter._cache` 的键从 `string path` 改为 `AssetGuid`

---

## 13. 迁移策略

### Phase 0：基础设施（无破坏性变更）

1. 引入 `AssetGuid`、`SourceGuid`、`AssetRef<T>` 类型
2. 引入 `ImportTrace`、`DependencyEntry` 模型
3. 引入 `SourceMetaManager`（读写 `.meta`）和 `AssetMetaManager`
4. FlatBuffer schema 中**新增**字段（不删旧字段）

### Phase 1：ShaderAsset 导入链

1. `SlangShaderImporter` 改造：生成 `.meta`、填充 `AssetGuid` 和 `ImportTrace`
2. 所有调用点通过兼容包装继续工作
3. `ShaderAssetSerializer` 序列化/反序列化新字段

### Phase 2：MaterialAsset 引用迁移

1. `MaterialAsset.PassEntry` 新增 `shader_guid`
2. `MaterialAssetLoader` 优先用 `shader_guid`、回退 `shader`
3. `Material` 对象增加 `AssetGuid` 和 `ShaderRef` 字段
4. `MaterialRegistry` 增加按 `AssetGuid` 查找

### Phase 3：全量迁移

1. `MaterialInstanceAsset.parent` → `parent_guid`
2. `MeshAsset.default_material_slots` → `default_material_guids`
3. 删除 `IAssetResolver` / `IAssetDatabase` / `IAssetWorkspace` facade，统一改为 `AssetDatabase`

### Phase 4：编辑器能力补全

1. 在已落地的 `AssetDatabase` 之上补 editor-facing UI / tooling
2. 引用查找 / 安全删除 / 拖拽赋值
3. 材质面板 Shader 选择器
4. 依赖图可视化

---

## 14. 验证清单

| # | 场景 | 预期 |
|---|------|------|
| 1 | 修改 shader 源文件内容 | ContentFingerprint 变化，触发重导入，AssetGuid 不变 |
| 2 | 修改 shader import 的公共模块 | dependency hash 变化 → fingerprint 变化 → 重导入 |
| 3 | 重命名 shader 源文件 | SourceGuid 不变（从 .meta 读），ShaderAsset 可重关联 |
| 4 | 运行时无源文件部署包 | Material → ShaderAsset 仍正常解析（通过 manifest） |
| 5 | 编译期类型安全 | 不可能把 SourceGuid 赋给 AssetRef<ShaderAsset> |
| 6 | 同一 ShaderAsset 重导入 | AssetGuid 不变 |
| 7 | 删除源文件 | 编辑器标记"源丢失"，已编译的 .asset 仍可用 |
| 8 | 一源多产物 | 每个产物有独立 AssetGuid，通过 sub_asset_key 稳定映射 |
| 9 | 编辑器材质面板选 Shader | 下拉列表展示所有 ShaderAsset 的 Name+GUID |
| 10 | 引用查找 | 查询"哪些 Material 使用了这个 Shader"返回正确结果 |

---

## 15. 与现有工程的对应点

| 现有文件 | 需要的变更 |
|----------|-----------|
| `assets/Schema/shader_asset.fbs` | 新增 `asset_guid`、`ImportTrace` |
| `assets/Schema/material_asset.fbs` | `PassEntry` 新增 `shader_guid`；`MaterialAsset` 新增 `asset_guid` |
| `assets/Schema/material_instance_asset.fbs` | 新增 `asset_guid`、`parent_guid` |
| `assets/Schema/mesh_asset.fbs` | 新增 `asset_guid`、`default_material_guids` |
| `src/SomeEngine.Assets/Importers/SlangShaderImporter.cs` | 大改：支持 .meta、ImportTrace、依赖收集 |
| `src/SomeEngine.Assets/Pipeline/ShaderAssetSerializer.cs` | 适配新字段 |
| `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` | ShaderLoadFunc 签名变更，GUID 优先查找 |
| `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs` | parent_guid 查找 |
| `src/SomeEngine.Render/Materials/Material.cs` | 新增 `AssetGuid`、`ShaderRef` |
| `src/SomeEngine.Render/Materials/MaterialPass.cs` | 已在 BATCH-06 删除（历史项） |
| `src/SomeEngine.Render/Materials/MaterialRegistry.cs` | 已在 BATCH-06 删除（历史项） |
| **新文件** `src/SomeEngine.Assets/AssetGuid.cs` | 强类型 `AssetGuid` / `SourceGuid` / `AssetRef<T>` |
| **新文件** `src/SomeEngine.Assets/AssetRecord.cs` | `IAsset` / `ImportedAsset` / `DependencyEntryData` / `AssetDiagnostic` |
| **新文件** `src/SomeEngine.Assets/MetaManagers.cs` | `SourceMetaManager` + `AssetMetaManager` 合并 |
| **新文件** `src/SomeEngine.Assets/AssetIoHelpers.cs` | path / manifest path / json 共享 helper |
| **新文件** `src/SomeEngine.Assets/AssetTypeRegistration.cs` | builtin handler / importer 显式注册入口 |
| **新文件** `src/SomeEngine.Assets/AssetTypeRegistry.cs` | handler / importer 注册与分发 |
| **新文件** `src/SomeEngine.Assets/SchemaPartials.cs` | Schema partial 实现 `IAsset` |
| **新文件** `src/SomeEngine.Assets/AssetDatabase.cs` | 统一 load / import / resolve / validate / watch 入口 |
