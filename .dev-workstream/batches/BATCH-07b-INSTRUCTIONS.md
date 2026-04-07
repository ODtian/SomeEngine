# BATCH-07b: DI 驱动的注册模式 + Mesh Importer

**Batch Number:** BATCH-07b  
**Tasks:** TASK-307f, TASK-307g, TASK-307h  
**Phase:** Phase 3 - Destructive Asset Pipeline Rework (续)  
**Estimated Effort:** medium-large  
**Priority:** HIGH  
**Dependencies:** BATCH-07

---

## Onboarding & Workflow

### Complete Engineering Goal

用 `Microsoft.Extensions.DependencyInjection`（Runtime/Editor 已引用）替换当前的静态全局 `AssetTypeRegistry`，消灭所有静态可变状态。同时补全 Mesh 的 `IAssetImporter`，删除 `ClusterBuilder` 的 material 回调。

**现状问题：**
1. `AssetTypeRegistry` 是 static class，handler/importer 存在静态 List 里 —— 不可测试、不可隔离、多实例冲突
2. `AssetTypeRegistration.RegisterBuiltIns()` 把所有 handler 硬编在 Assets 核心库
3. handler 定义是 private nested class，不可复用
4. Mesh 缺 `IAssetImporter`，`ClusterBuilder.Process()` 靠回调解析 material GUID

**改造后模型：**
```csharp
// Runtime/Editor 的 Composition Root
var services = new ServiceCollection();
services.AddSingleton<AssetDatabase>();
services.AddSingleton<IAssetTypeHandler, ShaderAssetTypeHandler>();
services.AddSingleton<IAssetTypeHandler, MaterialAssetTypeHandler>();
services.AddSingleton<IAssetTypeHandler, MaterialInstanceAssetTypeHandler>();
services.AddSingleton<IAssetTypeHandler, MeshAssetTypeHandler>();
services.AddSingleton<IAssetImporter, SlangSourceImporter>();
services.AddSingleton<IAssetImporter, GlbMeshSourceImporter>();
var provider = services.BuildServiceProvider();
var db = provider.GetRequiredService<AssetDatabase>();
```

### What Does NOT Count As This Batch
- 合并 `IAssetTypeHandler` 和 `IAssetImporter` 为单一接口
- 把 DI 引入 Assets 核心库的 csproj（只在 host 层用 DI 容器）
- 改动 `IAsset` / `AssetGuid` / `AssetManifest` 的核心数据结构

### Required Reading (IN ORDER)
1. `src/SomeEngine.Assets/AssetTypeRegistry.cs` — 待删，当前静态注册中心
2. `src/SomeEngine.Assets/AssetTypeRegistration.cs` — 待删，handler nested class 定义
3. `src/SomeEngine.Assets/AssetDatabase.cs` — 核心入口，所有 `AssetTypeRegistry.*` 调用点
4. `src/SomeEngine.Assets/Importers/SlangSourceImporter.cs` — Importer 参考实现
5. `src/SomeEngine.Assets/Importers/ClusterBuilder.cs` L285, L606, L1126-1132 — materialGuidResolver 回调
6. `src/SomeEngine.Runtime/Program.cs` — Runtime 的 composition root
7. `src/SomeEngine.Editor/Program.cs` — Editor 的 composition root

### Source Code Locations
| Area | Path |
|---|---|
| 待删静态 Registry | `src/SomeEngine.Assets/AssetTypeRegistry.cs` |
| 待删注册入口 | `src/SomeEngine.Assets/AssetTypeRegistration.cs` |
| 核心入口 | `src/SomeEngine.Assets/AssetDatabase.cs` |
| Serializers | `src/SomeEngine.Assets/Pipeline/*.cs` |
| Importers | `src/SomeEngine.Assets/Importers/*.cs` |
| Runtime | `src/SomeEngine.Runtime/Program.cs` |
| Editor | `src/SomeEngine.Editor/Program.cs` |
| Tests | `tests/SomeEngine.Tests/Assets/*.cs` |

---

## Task Breakdown

### TASK-307f: Handler 提取 + AssetDatabase 改为构造注入

**目标：** 把 handler 从 nested class 提取为独立 public 类，`AssetDatabase` 从依赖静态 `AssetTypeRegistry` 改为构造注入 `IEnumerable<IAssetTypeHandler>` + `IEnumerable<IAssetImporter>`。

**步骤：**

1. 在 `Pipeline/` 下创建 4 个 handler 文件：
   - `Pipeline/ShaderAssetTypeHandler.cs`
   - `Pipeline/MaterialAssetTypeHandler.cs`
   - `Pipeline/MaterialInstanceAssetTypeHandler.cs`
   - `Pipeline/MeshAssetTypeHandler.cs`
   - 从 `AssetTypeRegistration` 中原样提取，改为 `public sealed class`，命名空间 `SomeEngine.Assets.Pipeline`

2. **`AssetDatabase.cs` 构造函数改造：**
   ```csharp
   public sealed class AssetDatabase
   {
       private readonly IReadOnlyList<IAssetTypeHandler> _handlers;
       private readonly IReadOnlyList<IAssetImporter> _importers;

       public AssetDatabase(
           string projectRoot,
           IEnumerable<IAssetTypeHandler> handlers,
           IEnumerable<IAssetImporter> importers,
           string? manifestDirectory = null)
       {
           _handlers = handlers.ToList();
           _importers = importers.ToList();
           // ...existing manifest loading...
       }

       // 替换所有 AssetTypeRegistry.Match(path) 调用为：
       private IAssetTypeHandler? MatchHandler(string path)
           => _handlers.FirstOrDefault(h => h.MatchesAssetPath(path));

       private IAssetImporter? MatchImporter(string path)
           => _importers.FirstOrDefault(i => i.MatchesSourcePath(path));
   }
   ```

3. 把 `AssetDatabase` 内所有 `AssetTypeRegistry.Match(...)` → `MatchHandler(...)`，`AssetTypeRegistry.MatchImporter(...)` → `MatchImporter(...)`

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [NEW] | `Pipeline/ShaderAssetTypeHandler.cs` |
| [NEW] | `Pipeline/MaterialAssetTypeHandler.cs` |
| [NEW] | `Pipeline/MaterialInstanceAssetTypeHandler.cs` |
| [NEW] | `Pipeline/MeshAssetTypeHandler.cs` |
| [MODIFY] | `src/SomeEngine.Assets/AssetDatabase.cs` — 构造注入替换静态 registry |

---

### TASK-307g: 删除静态 Registry + 迁移 Host/Test

**目标：** 删除 `AssetTypeRegistry.cs` 和 `AssetTypeRegistration.cs`。Runtime/Editor 用 DI 容器配置，Test 手动构造。

**步骤：**

1. 删除 `src/SomeEngine.Assets/AssetTypeRegistry.cs`
2. 删除 `src/SomeEngine.Assets/AssetTypeRegistration.cs`
3. **Runtime `Program.cs`**：
   ```csharp
   var services = new ServiceCollection();
   services.AddSingleton<IAssetTypeHandler, ShaderAssetTypeHandler>();
   services.AddSingleton<IAssetTypeHandler, MaterialAssetTypeHandler>();
   services.AddSingleton<IAssetTypeHandler, MaterialInstanceAssetTypeHandler>();
   services.AddSingleton<IAssetTypeHandler, MeshAssetTypeHandler>();
   services.AddSingleton<IAssetImporter, SlangSourceImporter>();
   // TASK-307h 后追加 GlbMeshSourceImporter
   var provider = services.BuildServiceProvider();
   
   var handlers = provider.GetServices<IAssetTypeHandler>();
   var importers = provider.GetServices<IAssetImporter>();
   var assetDb = new AssetDatabase(projectRoot, handlers, importers);
   ```
4. **Editor `Program.cs`**：同上
5. **Tests**：直接 new，不需要 DI 容器
   ```csharp
   var handlers = new IAssetTypeHandler[] {
       new ShaderAssetTypeHandler(),
       new MaterialAssetTypeHandler(),
       new MaterialInstanceAssetTypeHandler(),
       new MeshAssetTypeHandler(),
   };
   var importers = new IAssetImporter[] { new SlangSourceImporter() };
   var db = new AssetDatabase(tempDir, handlers, importers);
   ```

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [DELETE] | `src/SomeEngine.Assets/AssetTypeRegistry.cs` |
| [DELETE] | `src/SomeEngine.Assets/AssetTypeRegistration.cs` |
| [MODIFY] | `src/SomeEngine.Runtime/Program.cs` |
| [MODIFY] | `src/SomeEngine.Editor/Program.cs` |
| [MODIFY] | `tests/SomeEngine.Tests/Assets/AssetDatabaseTests.cs` |
| [MODIFY] | `tests/SomeEngine.Tests/Assets/AssetProjectValidatorTests.cs` |

---

### TASK-307h: GlbMeshSourceImporter + 删除 ClusterBuilder 回调

**目标：** 新增 `GlbMeshSourceImporter`，构造注入 `AssetManifest` 解析 material GUID。删除 `ClusterBuilder.Process()` 的 `materialGuidResolver` 回调。

**步骤：**

1. **`Importers/ClusterBuilder.cs`**：
   - `Process()` L285：删除 `materialGuidResolver` 参数
   - `ProcessRaw()` L606：删除 `materialGuidResolver` 参数
   - L1126-1132：`DefaultMaterialGuids` 改为空数组 `[]`

2. **`AssetManifest.cs`**：添加 `FindByName(string name, string assetType)` 查询方法

3. **创建 `Importers/GlbMeshSourceImporter.cs`**：
   ```csharp
   public sealed class GlbMeshSourceImporter : IAssetImporter
   {
       private readonly AssetManifest _manifest;
   
       public GlbMeshSourceImporter(AssetManifest manifest)
           => _manifest = manifest;
   
       public string ImporterName => "GlbMeshImporter";
       public IReadOnlyList<string> SourceExtensions => [".glb", ".gltf"];
   
       public bool MatchesSourcePath(string path)
           => SourceExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
   
       public IReadOnlyList<ImportedAsset> Import(string projectRoot, string sourcePath)
       {
           string fullPath = Path.GetFullPath(Path.Combine(projectRoot, sourcePath));
           var mesh = ClusterBuilder.Process(fullPath);
   
           mesh.DefaultMaterialGuids = mesh.DefaultMaterialSlots?
               .Select(name => _manifest.FindByName(name, nameof(MaterialAsset))
                                   ?.Guid.ToFlatString() ?? "")
               .ToArray() ?? [];
   
           string outPath = Path.ChangeExtension(fullPath, ".mesh.asset");
           MeshAssetSerializer.Save(mesh, outPath);
           return [new ImportedAsset(mesh, "mesh:main", outPath)];
       }
   }
   ```

4. **Runtime `Program.cs`**：
   - DI 注册追加 `services.AddSingleton<IAssetImporter, GlbMeshSourceImporter>()`
   - `GlbMeshSourceImporter` 构造注入 `AssetManifest`（从 `AssetDatabase.Manifest` 获取，或单独注册）
   - `TryImportModelToMesh()` 简化为 `assetDb.Import(modelPath)`
   - 删除 `ResolveMaterialGuidByName` 局部回调

> [!NOTE]
> DI 注册 `AssetManifest` 时注意：manifest 由 `AssetDatabase` 创建和管理。可以用 factory 注册：
> `services.AddSingleton(sp => sp.GetRequiredService<AssetDatabase>().Manifest);`
> 或者让 `GlbMeshSourceImporter` 直接注入 `AssetDatabase`（但这引入循环依赖——db 依赖 importer，importer 依赖 db）。
> 更简单的做法：`GlbMeshSourceImporter` 不通过 DI 注入 manifest，而是在 `Import()` 里从 `projectRoot` 读磁盘上的 manifest。这保持了 importer 和 db 的单向依赖关系。

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [MODIFY] | `src/SomeEngine.Assets/Importers/ClusterBuilder.cs` — 删除 materialGuidResolver 参数 |
| [MODIFY] | `src/SomeEngine.Assets/AssetManifest.cs` — 添加 FindByName 查询方法 |
| [NEW] | `src/SomeEngine.Assets/Importers/GlbMeshSourceImporter.cs` |
| [MODIFY] | `src/SomeEngine.Runtime/Program.cs` — 简化 TryImportModelToMesh + DI 注册 |

---

## Execution Order

1. **TASK-307f** → 提取 handler + AssetDatabase 构造注入（核心改造）
2. **TASK-307g** → 删除静态 Registry + 迁移 Host/Test（编译验证）
3. **TASK-307h** → ClusterBuilder 删回调 + GlbMeshSourceImporter + Runtime 简化

> Task 1 和 Task 2 可以合并执行。

---

## Testing Requirements

- **TASK-307f/g：** 全量 `dotnet test` ≥ 134 passed, 0 failed（行为不变，仅迁移注册方式）
- **TASK-307h：** 新增 ≥ 3 个测试
  - `GlbMeshSourceImporter_MatchesSourcePath_ForGlbAndGltf`
  - `GlbMeshSourceImporter_DoesNotMatch_NonMeshExtensions`
  - `Import_GlbSource_ProducesMeshAsset`（集成测试）

---

## Quality Standards

- `AssetTypeRegistry.cs` 彻底删除（零静态全局可变状态）
- `AssetTypeRegistration.cs` 彻底删除
- 零 `RegisterBuiltIns()` 残留
- 4 个 handler 为独立 public 类
- `AssetDatabase` 通过构造函数接收 handler/importer
- `ClusterBuilder.Process()` 无 `materialGuidResolver` 参数
- GlbMeshSourceImporter 走 `IAssetImporter` 统一路径
- Runtime `TryImportModelToMesh()` 只调 `assetDb.Import()`

---

## Success Criteria

- [ ] `AssetTypeRegistry.cs` 已删除
- [ ] `AssetTypeRegistration.cs` 已删除
- [ ] 4 个 handler 提取为 `Pipeline/` 下的独立 public 类
- [ ] `AssetDatabase` 构造函数接受 `IEnumerable<IAssetTypeHandler>` + `IEnumerable<IAssetImporter>`
- [ ] Runtime/Editor 用 DI 容器配置 handler/importer
- [ ] `ClusterBuilder.Process()` 不再接受 `materialGuidResolver` 回调
- [ ] `GlbMeshSourceImporter` 实现 `IAssetImporter`
- [ ] Runtime `TryImportModelToMesh()` 走 `assetDb.Import()` 统一路径
- [ ] 全量测试 ≥ 137 passed, 0 failed
- [ ] 报告已提交
