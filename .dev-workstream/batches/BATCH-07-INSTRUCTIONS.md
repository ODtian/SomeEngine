# BATCH-07: Asset 管线破坏性重构 — 删接口/删类型/≤1000行

**Batch Number:** BATCH-07  
**Tasks:** TASK-307a, TASK-307b, TASK-307c, TASK-307d, TASK-307e  
**Phase:** Phase 3 - Destructive Asset Pipeline Rework  
**Estimated Effort:** full rework  
**Priority:** CRITICAL  
**Dependencies:** BATCH-06

---

## Onboarding & Workflow

### Complete Engineering Goal
把 `SomeEngine.Assets` 的管线框架层（不含 Importers/、Data/）从当前 17 个文件 2500+ 行、4 个接口、5 个实现类、9 个 report 类的过度分层架构，**破坏性重构**为 8 个文件 ≤1000 行、1 个接口（`IAsset`）、1 个具体类（`AssetDatabase`）的极简架构。同时迁移全项目所有消费处。100% 破坏性，0% 兼容。

### What Does NOT Count As This Batch
- 只改接口名不删实现
- 只删 Report 类不改消费处
- 只改 Assets 项目不改 Render/Runtime/Tests 消费处
- 保留 `IAssetResolver` 或任何旧接口作为兼容层

### Required Reading (IN ORDER)
1. `.dev-workstream/guides/DEV-GUIDE.md`
2. `ONBOARDING.md`
3. `docs/assets/asset_identity.md` — ID 体系、ImportTrace
4. `docs/assets/pipeline_overview.md` — 当前架构总览
5. `.dev-workstream/reviews/BATCH-06-REVIEW.md`
6. `src/SomeEngine.Assets/AssetGuid.cs` — 当前 AssetGuid/AssetId/AssetRef
7. `src/SomeEngine.Assets/IAssetResolver.cs` — 待删接口
8. `src/SomeEngine.Assets/AssetCatalog.cs` — 待删接口+实现
9. `src/SomeEngine.Assets/AssetWorkspace.cs` — 待删接口+实现
10. `src/SomeEngine.Assets/IAssetDatabase.cs` — 待删接口
11. `src/SomeEngine.Assets/ManifestAssetDatabase.cs` — 待删实现
12. `src/SomeEngine.Assets/ManifestAssetResolver.cs` — 待删实现
13. `src/SomeEngine.Assets/MemoryAssetResolver.cs` — 待删实现
14. `src/SomeEngine.Assets/AssetManifest.cs` — 保留重写
15. `src/SomeEngine.Assets/AssetManifestScanner.cs` — 保留重写
16. `src/SomeEngine.Assets/SchemaAssetRecords.cs` — 待删
17. `src/SomeEngine.Assets/AssetAnalysisReports.cs` — 待删
18. `src/SomeEngine.Assets/AssetProjectValidator.cs` — 待删
19. `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` — 消费处
20. `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs` — 消费处
21. `src/SomeEngine.Render/Assets/MeshMaterialResolver.cs` — 消费处
22. `src/SomeEngine.Render/Materials/Material.cs` — 消费处
23. `src/SomeEngine.Runtime/Program.cs` — 消费处
24. `tests/SomeEngine.Tests/Assets/` — 测试全部重写

### Source Code Locations
| Area | Path |
|---|---|
| Asset 管线框架 | `src/SomeEngine.Assets/` （不含 Importers/、Data/） |
| FlatBuffer schema | `assets/Schema/*.fbs` |
| Render 消费处 | `src/SomeEngine.Render/Assets/`、`src/SomeEngine.Render/Materials/Material.cs` |
| Runtime 消费处 | `src/SomeEngine.Runtime/Program.cs` |
| Editor 消费处 | `src/SomeEngine.Editor/Program.cs` |
| 测试 | `tests/SomeEngine.Tests/Assets/`、`tests/SomeEngine.Tests/Materials/` |

### Build & Test Commands
```bash
dotnet build SomeEngine.slnx
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~Assets"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~Material"
```

### Report Submission
完成后提交到：  
`.dev-workstream/reports/BATCH-07-REPORT.md`

---

## Context

BATCH-06 完成了 Material ECS 重构。当前 asset pipeline 框架层存在严重的过度设计：

| 问题 | 现状 |
|------|------|
| 接口分层过度 | `IAssetCatalog` / `IAssetResolver` / `IAssetWorkspace` / `IAssetDatabase` 四层 |
| `AssetId` / `AssetGuid` 双轨 | 两个结构体隐式互转，设计冗余 |
| 实现类爆炸 | 5 个实现类做本质相同的事 |
| Report 类爆炸 | 9 个独立 report/problem 类型 |
| Scanner 硬编码类型分发 | if-else 分支判断资产类型 |
| 运行时消费处 ad-hoc | `Program.cs` 直接从 source path 调 importer |

本批的目标是用**一次破坏性重写**彻底消除上述全部问题。

**Related Task:** TASK-307

---

## Batch Objectives

- 删除 `AssetId`，统一只用 `AssetGuid`
- 删除 4 个接口 + 5 个实现类，替换为 1 个 `IAsset` 接口 + 1 个 `AssetDatabase` 具体类
- 删除 9 个 Report 类、`AssetNode`、`AssetProjectValidator`、`AssetManifestBuilder`
- 引入 `AssetTypeRegistry` 注册制：`IAssetTypeHandler`（消费端）+ `IAssetImporter`（生产端）
- `AssetDatabase` API：`Load<T>(path)` / `Load<T>(guid)` / `Import(path)` / `Resolve(path)` / `List` / `Validate` / `Watch`
- `Load<T>(path)` 在 dev 模式透明调用 `Import`；shipping 模式走 manifest 查找
- `AssetDatabase` 不包含文件监视逻辑，watcher 由宿主自行组装
- 迁移 Render 层所有消费处：删除 `IAssetResolver` 参数，只保留 delegate 重载
- `Material` 不再实现 `IAsset`（保留 `AssetGuid` 属性）
- 重写所有 asset 测试
- 管线框架层总代码量 ≤1000 行

---

## Preparation Work (Non-task)

1. 列出 `SomeEngine.Assets/` 下管线框架文件（不含 Importers/、Data/、Pipeline/）的当前行数，确认删除清单
2. `grep -r "IAssetResolver\|IAssetCatalog\|IAssetWorkspace\|IAssetDatabase\|AssetId\b" src/ tests/` 列出全部旧 API 引用点
3. 确认 `AssetTypeRegistry` 需要注册哪些 handler（Shader、Material、MaterialInstance、Mesh）
4. 检查现有测试基线：`dotnet test` 确认当前 134 passed

---

## Tasks

### Task 1: 核心类型重写 — AssetGuid + IAsset + AssetTypeRegistry (TASK-307a)

**Files:**
- `src/SomeEngine.Assets/AssetGuid.cs` — MODIFY（删 `AssetId`，保留 `AssetGuid`/`SourceGuid`/`AssetRef<T>`）
- `src/SomeEngine.Assets/AssetInterfaces.cs` — DELETE
- `src/SomeEngine.Assets/SchemaAssetRecords.cs` — DELETE
- `src/SomeEngine.Assets/AssetAnalysisReports.cs` — DELETE
- `src/SomeEngine.Assets/ImportTraceData.cs` — DELETE（合并进新文件）
- `src/SomeEngine.Assets/AssetRecord.cs` — NEW（`IAsset` + `AssetEntry` + `ImportTraceData` + `AssetDiagnostic`）
- `src/SomeEngine.Assets/AssetTypeRegistry.cs` — NEW（`IAssetTypeHandler` + `IAssetImporter` + 静态注册）
- `src/SomeEngine.Assets/SchemaPartials.cs` — NEW（partial class 让 Schema 类型实现 `IAsset`）

**Description:**
砍掉 `AssetId` 双轨系统。`IAssetRecord` 改名为 `IAsset`，删除 `IImportedAsset`、`IShaderAssetRecord`。引入 `AssetTypeRegistry`：`IAssetTypeHandler`（消费端，识别 .asset 并反序列化）和 `IAssetImporter`（生产端，从 source 编译出 .asset）。Schema 生成类型通过 partial class 实现 `IAsset`。

**Requirements:**
- `AssetId` 结构体彻底删除，所有 `AssetId` 引用编译报错
- `IAsset` 只有 `AssetGuid AssetGuid { get; }` 和 `string Name { get; }`
- `AssetTypeRegistry` 支持 handler 和 importer 双注册
- 4 种 handler 注册（Shader/Material/MaterialInstance/Mesh），对应 importer 注册
- `AssetGuid.cs` ≤ 50 行、`AssetRecord.cs` ≤ 25 行、`AssetTypeRegistry.cs` ≤ 45 行

**Tests Required:**
- ✅ `AssetGuid` parse/format/equality round-trip
- ✅ `AssetRef<T>` empty/non-empty 判断
- ✅ `AssetTypeRegistry` 注册/查找/路径匹配
- ✅ Schema partial class 实现 `IAsset`

---

### Task 2: AssetManifest 精简 + Scanner 重写 (TASK-307b)

**Files:**
- `src/SomeEngine.Assets/AssetManifest.cs` — REWRITE（内联 Builder、精简 JSON doc 类）
- `src/SomeEngine.Assets/AssetManifestBuilder.cs` — DELETE（内联进 Manifest）
- `src/SomeEngine.Assets/AssetManifestScanner.cs` — REWRITE（走 `AssetTypeRegistry` 分发）
- `src/SomeEngine.Assets/AssetCatalog.cs` — DELETE
- `tests/SomeEngine.Tests/Assets/AssetManifestTests.cs` — REWRITE
- `tests/SomeEngine.Tests/Assets/AssetManifestScannerTests.cs` — REWRITE

**Description:**
`AssetManifest` 内联 Builder 逻辑。所有 `AssetId` → `AssetGuid`。删除 `AssetNode`，查询直接返回 `AssetManifestRecord`。Scanner 用 `AssetTypeRegistry.Match(path)` 替代 if-else 类型判断。

**Requirements:**
- `AssetManifest.cs` ≤ 280 行
- `AssetManifestScanner.cs` ≤ 80 行
- `AssetNode` 类型删除
- Scanner 里没有任何针对具体资产类型的 if/switch

**Tests Required:**
- ✅ manifest 构建/查询/依赖图遍历
- ✅ scanner 通过 registry 发现所有注册类型
- ✅ manifest load/save round-trip

---

### Task 3: AssetDatabase 实现 + 旧实现全删 (TASK-307c)

**Files:**
- `src/SomeEngine.Assets/AssetDatabase.cs` — NEW
- `src/SomeEngine.Assets/MetaManagers.cs` — NEW（合并 `Meta/AssetMetaManager.cs` + `Meta/SourceMetaManager.cs`）
- `src/SomeEngine.Assets/IAssetResolver.cs` — DELETE
- `src/SomeEngine.Assets/IAssetDatabase.cs` — DELETE
- `src/SomeEngine.Assets/AssetWorkspace.cs` — DELETE
- `src/SomeEngine.Assets/ManifestAssetDatabase.cs` — DELETE
- `src/SomeEngine.Assets/ManifestAssetResolver.cs` — DELETE
- `src/SomeEngine.Assets/MemoryAssetResolver.cs` — DELETE
- `src/SomeEngine.Assets/AssetFileWatcher.cs` — DELETE（逻辑内联到 AssetDatabase）
- `src/SomeEngine.Assets/AssetProjectValidator.cs` — DELETE（逻辑内联到 AssetDatabase.Validate()）
- `src/SomeEngine.Assets/Meta/AssetMetaManager.cs` — DELETE（合并）
- `src/SomeEngine.Assets/Meta/SourceMetaManager.cs` — DELETE（合并）
- `tests/SomeEngine.Tests/Assets/AssetDatabaseTests.cs` — REWRITE
- `tests/SomeEngine.Tests/Assets/AssetResolverTests.cs` — REWRITE
- `tests/SomeEngine.Tests/Assets/AssetProjectValidatorTests.cs` — REWRITE

**Description:**
一个 `AssetDatabase` 类吃掉查询/加载/校验/监视/导入。FileWatcher 逻辑内联。Validator 逻辑内联为 `Validate()` 方法。Meta 管理器合并为一个文件。

**Requirements:**
- `AssetDatabase.cs` ≤ 230 行
- `MetaManagers.cs` ≤ 130 行
- 旧接口/实现/报告类全部物理删除
- `AssetDatabase` 公开 API：
  - `Load<T>(string sourcePath, string? subAssetKey)` — 按源路径加载（dev 模式透明导入）
  - `Load<T>(AssetGuid guid)` — 按 GUID 加载（跨资产引用）
  - `Import(string sourcePath)` — 导入源文件，返回全部产物 GUID
  - `Resolve(string sourcePath, string? subAssetKey)` — 路径 → GUID（不加载不导入）
  - `List(string? assetType)` — 查询
  - `GetDependencies` / `GetReferencers` — 依赖查询
  - `Validate()` — 校验
  - `StartWatching` / `StopWatching` — 文件监视
  - `SourceChanged` event — 监视事件
  - `RebuildIndex()` — 强制重建索引
- 删除后整个 solution 编译通过

**Tests Required:**
- ✅ `AssetDatabase.Load<T>(guid)` 按 GUID 加载资产
- ✅ `AssetDatabase.Load<T>(path)` 按源路径加载（透明导入）
- ✅ `AssetDatabase.List` 按类型过滤
- ✅ `AssetDatabase.Validate` 检出孤儿/悬挂引用
- ✅ `AssetDatabase.Import` 导入新 source 生成 GUID
- ✅ 重复 Import 同一 source 复用原 AssetGuid

---

### Task 4: 全项目消费处迁移 (TASK-307d)

**Files:**
- `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` — MODIFY（删所有 `IAssetResolver` 重载）
- `src/SomeEngine.Render/Assets/MaterialInstanceLoader.cs` — MODIFY（删所有 `IAssetResolver` 重载）
- `src/SomeEngine.Render/Assets/MeshMaterialResolver.cs` — MODIFY（删所有 `IAssetResolver` 重载）
- `src/SomeEngine.Render/Materials/Material.cs` — MODIFY（删 `IAssetRecord` 实现，改为不实现 `IAsset`）
- `src/SomeEngine.Runtime/Program.cs` — MODIFY（`ManifestAssetDatabase` → `AssetDatabase`）
- `src/SomeEngine.Editor/Program.cs` — MODIFY（确认编译通过）
- `tests/SomeEngine.Tests/Materials/MaterialAssetPipelineTests.cs` — MODIFY
- `tests/SomeEngine.Tests/Materials/MaterialCreationTests.cs` — MODIFY

**Description:**
Render 层的 3 个 Loader 删除所有 `IAssetResolver` 参数重载，只保留已有的 delegate 重载（`ShaderLoadFunc`、`TextureLoadFunc`、`ParentMaterialLoadFunc`）。`Material` 去掉 `IAssetRecord` 实现。Runtime 的 `Program.cs` 改用新 `AssetDatabase` API。

**Requirements:**
- Render 项目不再依赖 `IAssetResolver`、`IAssetCatalog`、`IAssetWorkspace`、`IAssetDatabase`、`AssetNode`、`AssetId`
- `Material` 类不再实现任何 asset 接口
- Runtime `Program.cs` 用 `AssetDatabase` 替代 `ManifestAssetDatabase`
- 全 solution 编译通过
- 全量测试通过

**Tests Required:**
- ✅ `MaterialAssetLoader` 通过 delegate 加载 shader
- ✅ `MaterialInstanceLoader` 通过 delegate 加载 parent
- ✅ runtime asset 路径不依赖旧 API
- ✅ 全量测试 ≥ 134 passed, 0 failed

---

### Task 5: Review 修正 — 审查发现的 8 个遗留问题 (TASK-307e)

**来源：** BATCH-07 实现审查（全部 19 条问题中 15 条已在 Task 1-4 修期修复，剩余 4 条 + 质量审查新增 4 条 = 共 8 条需本 Task 处理）

**Files:**
- `src/SomeEngine.Assets/AssetDatabase.cs` — MODIFY（修 5a/5d/5e/5f/5h）
- `src/SomeEngine.Assets/AssetManifestScanner.cs` — MODIFY（修 5b/5c）
- `src/SomeEngine.Assets/AssetIoHelpers.cs` — MODIFY（修 5d）
- `src/SomeEngine.Assets/AssetTypeRegistry.cs` — MODIFY（修 5e）
- `src/SomeEngine.Assets/SchemaPartials.cs` — 不改（`ImportTraceData` 保留供调试，但 Scanner 不再依赖）

#### 5a. `Load<T>(path)` 无条件 Import — 性能退化

**位置：** `AssetDatabase.cs:37-40`
**问题：** 修复 B3（不检查过期）时矫枉过正，变成**每次调用都无条件 Import**。`Import` → `RebuildIndex` → `ScanAndSave`，导致每次 Load 都做完整磁盘扫描。
**修法：**
```csharp
private bool IsUpToDate(string fullSourcePath, string sourcePath)
{
    string manifestPath = ToManifestPath(sourcePath);
    if (!Manifest.TryGetSourceGuid(manifestPath, out SourceGuid sourceGuid))
        return false;
    IReadOnlyList<AssetGuid> assets = Manifest.GetAssetsBySource(sourceGuid);
    if (assets.Count == 0)
        return false;
    DateTime sourceTime = File.GetLastWriteTimeUtc(fullSourcePath);
    foreach (AssetGuid guid in assets)
    {
        if (!Manifest.TryGetAsset(guid, out AssetManifestRecord record))
            return false;
        string assetPath = ToProjectFullPath(record.Path);
        if (!File.Exists(assetPath) || File.GetLastWriteTimeUtc(assetPath) < sourceTime)
            return false;
    }
    return true;
}
```
`Load<T>(path)` 中改为 `if (... && !IsUpToDate(fullPath, sourcePath)) { Import(sourcePath); }`。
**约束：** 0 个新类型，0 个新接口。

#### 5b. Scanner 对 ShaderAsset 硬编码类型判断

**位置：** `AssetManifestScanner.cs:80`
```csharp
ImportTraceData? trace = asset is ShaderAsset shader ? shader.GetImportTraceData() : null;
```
**问题：** Scanner 有 `is ShaderAsset` 分支，违反"零硬编码类型分支"。根因是 ShaderAsset 内嵌 ImportTrace（FlatBuffer），但 SlangShaderImporter 已同时写 `.asset.meta`（确认 `SlangShaderImporter.cs:349`）。
**修法：** Scanner `TryInspect` 中删除 `ImportTraceData` 回退路径，只从 `.asset.meta` 读 SourceGuid/SubAssetKey：
```csharp
AssetMeta? meta = AssetMetaManager.TryLoad(fullPath);
SourceGuid sourceGuid = meta?.SourceGuid ?? SourceGuid.Empty;
string subAssetKey = meta?.SubAssetKey ?? string.Empty;
sourcePath = string.Empty; // source path 由 manifest source index 负责
```
删除 `using SomeEngine.Assets.Schema;`。

#### 5c. `ImportTraceData` 与 `AssetMeta` 字段重叠

**位置：** `SchemaPartials.cs:65-73` vs `MetaManagers.cs:15-24`
**问题：** 5 个字段重复。
**修法：** 5b 修复后 Scanner 不再依赖 `ImportTraceData`。`ImportTraceData` + `GetImportTraceData()` 保留在 `SchemaPartials.cs` 中供调试/工具使用（不删），但加注释标明 "仅用于调试，Scanner 不依赖此数据"。不引入新类型。

#### 5d. `ShouldSkip` 重复

**位置：** `AssetDatabase.cs:386-393` 和 `AssetManifestScanner.cs:97-104`
**问题：** 逻辑完全相同（检查 `Library/`、`.git/`、`/bin/`、`/obj/`），两份拷贝。
**修法：** 移到 `AssetIoHelpers`：
```csharp
internal static bool ShouldSkipPath(string projectRoot, string path)
{
    string normalized = ToManifestPath(projectRoot, path);
    return normalized.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
}
```
`AssetDatabase` 和 `AssetManifestScanner` 都改调 `AssetIoHelpers.ShouldSkipPath`。

#### 5e. `EnsureRegistryConfigured` 重复

**位置：** `AssetDatabase.cs:408-414` 和 `AssetManifestScanner.cs:106-112`
**问题：** 逻辑完全相同，两份拷贝。
**修法：** 移到 `AssetTypeRegistry` 作为 `internal static void EnsureConfigured()`：
```csharp
internal static void EnsureConfigured()
{
    if (ListAssetTypes().Count == 0)
        throw new InvalidOperationException("No asset handlers are registered. Call AssetTypeRegistration.RegisterBuiltIns() or register custom handlers.");
}
```
`AssetDatabase` 和 `Scanner` 都改调 `AssetTypeRegistry.EnsureConfigured()`。

#### 5f. 空 `catch` 吞异常

**位置：** `AssetDatabase.cs:135` 和 `AssetManifestScanner.cs:28`
**问题：** meta 文件读取失败时 `catch { }` 静默跳过，零日志。
**修法：** 改为 `catch (Exception ex) { Console.Error.WriteLine($"[AssetPipeline] Failed to read meta: {ex.Message}"); }`。用 `Console.Error` 而非 logging 框架（当前项目无 logging 依赖）。

#### 5g. `IsRelevant` 每次 watcher 事件遍历所有 importer 扩展名

**位置：** `AssetDatabase.cs:382-383`
```csharp
AssetTypeRegistry.GetRegisteredImporters().Any(importer =>
    importer.SourceExtensions.Any(extension => normalized.EndsWith(extension, ...)));
```
**问题：** watcher 高频路径，O(importers × extensions) 嵌套 `Any`。
**修法：** `AssetTypeRegistry` 加 `internal static IReadOnlyList<string> GetAllSourceExtensions()` 缓存扩展名列表（注册时刷新），`IsRelevant` 直接查缓存。

#### 5h. `Validate` 误报 OrphanAsset

**位置：** `AssetDatabase.cs:179-195`
**问题：** `incoming` 引用计数为 0 就报 `OrphanAsset`。但根资产（Material、Mesh）本来就不被任何资产引用——它们是叶子节点，只被代码引用。会产生大量误报。
**修法：** OrphanAsset 检测只对有 SourceGuid 且类型不是根资产的记录生效。或者把 `OrphanAsset` 的语义改为 "无 SourceGuid 且无 referrer 的资产"。最简单：删除 OrphanAsset 检测（当前场景无实际价值），只保留 `OrphanSourceMeta` 和 `DanglingReference`。

**Requirements:**
- 0 个新 public 接口
- 0 个新 public 类型
- 净减少代码行数
- Scanner 零硬编码类型分支（`is ShaderAsset` 删除）
- 全量测试 ≥ 124 passed, 0 failed

#### 5i. Watcher 逻辑从 AssetDatabase 中拆出

**位置：** `AssetDatabase.cs:211-372`（~140 行）
**问题：** AssetDatabase 425 行中 140 行是 watcher 逻辑（`StartWatching`/`StopWatching`/`Enqueue`/`OnDebounceElapsed`/`FindAffectedSources`/`IsRelevant`）。AssetDatabase 因此变成 `IDisposable`，构造函数管两个不同职责。且当前 watcher 只 fire `SourceChanged` 事件，没有任何消费者监听——完全是死代码。
**修法：** 删除 AssetDatabase 中所有 watcher 相关代码。包括 `_watchers`、`_debounceTimer`、`_watchLock`、`_pendingChanges` 字段，`StartWatching`/`StopWatching`/`OnDebounceElapsed`/`Enqueue`/`IsRelevant`/`FindAffectedSources` 方法，`SourceChanged` event，`IDisposable` 实现。如果宿主需要 watcher，自行组装 `FileSystemWatcher` + 调 `db.Import()` / `db.RebuildIndex()`。
**约束：** AssetDatabase 不再是 `IDisposable`。预计删掉 ~140 行。

#### 5j. `ShouldSkip` 硬编码路径

**位置：** `AssetDatabase.cs:386-393` / `AssetManifestScanner.cs:97-104`
**问题：** `Library/`、`.git/`、`/bin/`、`/obj/` 全是魔法字符串。其中 `Library/` 应该从 `manifestDirectory` 推导，而不是硬写。`/bin/`、`/obj/` 是 .NET 约定但不应该在资产管线里硬编码。且根因是 Scanner 用 `Directory.EnumerateFiles(projectRoot, "*.meta", AllDirectories)` 全盘扫。
**修法：** Scanner 改为只扫指定的资产根目录（由宿主构造时传入），或改为只扫 manifest 已知文件。`ShouldSkip` 彻底删除。

#### 5k. `Material.PrimaryShader` / `PrimaryShaderGuid` 死字段

**位置：** `Material.cs:26-27`
**问题：** BATCH-06（Material ECS 重构）后，shader 信息已通过 `ApplyShaderAuthoring` 写入 ECS 组件（`ShaderVariantRef` 等）。`PrimaryShader` 和 `PrimaryShaderGuid` 只在 `MaterialAssetLoader.cs:83-84` 写入、`Instantiate():40-41` 拷贝，但**没有任何地方读取**来做渲染。完全是 BATCH-06 遗留的死字段。
**修法：** 删除 `Material.PrimaryShader` 和 `Material.PrimaryShaderGuid`。`MaterialAssetLoader.cs:81-85` 删掉赋值。`Instantiate()` 删掉拷贝。shader 信息只存在于 ECS 组件。

#### 5l. Scanner 全盘扫目录树

**位置：** `AssetManifestScanner.cs:16,33`
**问题：** Scanner 用 `SearchOption.AllDirectories` 从项目根递归扫全部 `.meta` 和 `.asset` 文件，然后再用 `ShouldSkip` 排除。根因是扫描策略错误——不应该全盘扫再排除。
**修法：** `AssetDatabase` 构造时接受 `assetRoots`（默认 `["assets/"]`），Scanner 只扫指定目录。或改为 manifest-driven：只遍历 manifest 已知记录，检查文件是否存在。两种方案都不再需要 `ShouldSkip`。

**Tests Required:**
- ✅ `Load<T>(path)` 在 source 未变时不触发重导入（验证 `IsUpToDate` 生效）
- ✅ `Load<T>(path)` 在 source 变更后触发重导入
- ✅ Scanner 在 `.asset.meta` 缺失时仍能扫描（graceful degradation）
- ✅ `Validate()` 不误报根资产为 OrphanAsset
- ✅ AssetDatabase 不是 IDisposable（watcher 已移除）
- ✅ Scanner 只扫指定资产目录，不全盘扫
- ✅ Material 无 PrimaryShader / PrimaryShaderGuid 字段

---

## Mandatory Workflow: Test-Driven Task Progression

1. **Task 1:** 核心类型重写 → 写测试 → **全绿**
2. **Task 2:** Manifest/Scanner 重写 → 写测试 → **全绿**
3. **Task 3:** AssetDatabase 实现 + 旧实现全删 → 写测试 → **全绿**（此步后旧 API 全部消失，可能引起大量编译错误，需和 Task 4 紧密衔接）
4. **Task 4:** 全项目消费处迁移 → 全量测试 → **全绿**
5. **Task 5:** Review 修正（5a-5h）→ 写测试 → **全绿**

> Task 3 和 Task 4 可能需要同步推进以保持编译通过。允许合并执行但报告中须分别说明。

---

## Testing Requirements

- **Minimum:** 15 个新增/修改测试
- **Required categories:** GUID identity / registry dispatch / manifest CRUD / database load-query-validate / render loader delegate / runtime integration
- **Reject:** 只验证文件存在、只验证 build 通过、只 assert not null
- **Before closing:** 全量 `dotnet test` 结果 ≥ 134 passed, 0 failed

---

## Quality Standards

- 管线框架层总代码 ≤ 1000 行（不含 Importers/、Data/、Pipeline/）
- 零旧接口残留（`IAssetResolver`/`IAssetCatalog`/`IAssetWorkspace`/`IAssetDatabase`/`AssetId`/`AssetNode`/any Report class）
- `AssetDatabase` 作为唯一入口类，不做继承
- Scanner 零硬编码类型分支
- Render 层只通过 delegate 消费 asset，不依赖 `AssetDatabase` 除 `AssetGuid` 值类型以外的具体类型

---

## Report Requirements

报告文件：`.dev-workstream/reports/BATCH-07-REPORT.md`

必须包含：
- 删除清单和最终文件结构
- 管线框架层总行数统计
- 各文件实际行数 vs 预算对比
- `AssetDatabase` 公开 API 列表
- `AssetTypeRegistry` 注册的 handler 列表
- Render 层消费处改动总结
- Runtime 消费处改动总结
- 测试结果
- 发现的设计薄弱点 / 后续改进机会

---

## Success Criteria

- [x] `AssetId` 彻底删除
- [x] 4 个旧接口彻底删除
- [x] 5 个旧实现类彻底删除
- [x] 9 个 Report 类彻底删除
- [x] `AssetNode` 彻底删除
- [x] `AssetDatabase` 作为唯一管线入口类
- [x] `IAsset` 作为唯一接口
- [x] `AssetTypeRegistry` 注册分发取代硬编码
- [x] Render 层所有 `IAssetResolver` 重载删除
- [x] `Material` 不实现 `IAsset`
- [x] Runtime `Program.cs` 迁移到新 API
- [x] 管线框架层 ≤ 1000 行
- [x] 全量测试 ≥ 124 passed, 0 failed（实际 129 passed）
- [x] Scanner 零硬编码类型分支（`is ShaderAsset` 删除）
- [x] `Load<T>(path)` 只在 source 过期时触发 Import
- [x] `ShouldSkip` / `EnsureRegistryConfigured` 无重复拷贝
- [x] 空 `catch` 已加错误输出
- [x] `Validate` 不误报根资产
- [x] AssetDatabase 不包含 watcher 逻辑，不是 IDisposable
- [x] `ShouldSkip` 不硬编码 `Library/`/`bin/`/`obj/`（已删除 ShouldSkip）
- [x] `Import()` 增量注册 manifest，不触发全盘扫描
- [x] 构造函数不扫描，加载已有 manifest 或空 manifest
- [x] `Material.cs` 删除 PrimaryShader/PrimaryShaderGuid 死字段
- [x] 报告已提交

---

## Reference Materials

- `docs/assets/asset_identity.md`
- `docs/assets/pipeline_overview.md`
- `docs/DESIGN.md`
- `.dev-workstream/DEBT-TRACKER.md`
- `.dev-workstream/reviews/BATCH-06-REVIEW.md`
