# BATCH-09b Report: Mesh Local Material Table + Zero-GC Cluster Prepare Rework

**Batch:** BATCH-09b  
**Date:** 2026-04-19  
**Status:** COMPLETE

## Summary

完成了 BATCH-09 的 corrective continuation，把 cluster 提交模型从 `region` 语义继续收紧到稳定的 mesh-local material linear table / local material slot 模型，并删除了上一阶段留下的桥接层：

- `MeshMaterialBindings` 现在明确表达稳定的 mesh-local material 线性表，数组下标就是 local material slot
- RenderWorld 提交组件从 `RenderRegionBinding` 收敛为 `RenderMaterialSlotBinding`
- `RenderWorldExtractor` 改为展开 `(source entity, local material slot, material pass)`，并加入 steady-state no-op 帧签名，避免结构未变时重复重建
- 删除 `RenderWorldMaterialSlotSynchronizer` 与 `ClusterPipelineSlotBindingBuilder`
- 新增 pipeline-owned 的 `SlotPreparer`，把 slot folding 收到 cluster pipeline 自己的 prepare 路径
- Runtime / Editor 不再显式持有 extractor/synchronizer，而是统一调用 `ClusterPipeline.PrepareFrame(...)`
- Runtime 每帧材质解析缓存从 `Dictionary` 改为线性 `List<(AssetGuid, Material)>` 查找，满足 no-Dictionary 约束
- 新增两条 steady-state allocation regression 测试，验证 extractor 和 cluster prepare 热路径在结构不变时为 0 bytes

## Files Modified

### Runtime / Pipeline

- `src/SomeEngine.Render/Components/MeshMaterialBindingsComponent.cs`
- `src/SomeEngine.Render/Components/RenderWorldComponents.cs`
- `src/SomeEngine.Render/Systems/RenderWorldExtractor.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/SlotPreparer.cs` (new)
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
- `src/SomeEngine.Render/Materials/MaterialEntityUtility.cs`
- `src/SomeEngine.Render/Materials/MaterialSlotCache.cs`
- `src/SomeEngine.Runtime/Program.cs`
- `src/SomeEngine.Editor/Program.cs`

### Deleted Bridge Paths

- `src/SomeEngine.Render/Systems/RenderWorldMaterialSlotSynchronizer.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipelineSlotBindingBuilder.cs`

### Tests

- `tests/SomeEngine.Tests/Assets/AssetResolverTests.cs`
- `tests/SomeEngine.Tests/Systems/RenderWorldExtractorTests.cs`
- `tests/SomeEngine.Tests/Pipelines/ClusterPipelineEntityQueriesTests.cs`
- `tests/SomeEngine.Tests/Pipelines/SlotPreparerTests.cs` (new)
- `tests/SomeEngine.Tests/Pipelines/ClusterPipelineSlotBindingSelectorTests.cs` (deleted)
- `tests/SomeEngine.Tests/Pipelines/RenderWorldMaterialSlotSynchronizerTests.cs` (deleted)

### Docs / Workstream

- `docs/DESIGN.md`
- `docs/TASK-DETAIL.md`
- `docs/materials/architecture.md`
- `docs/assets/pipeline_overview.md`
- `docs/assets/asset_identity.md`
- `.dev-workstream/TASK-TRACKER.md`

## Task Check

| Task ID | Title | Status |
|---|---|---|
| TASK-309f | Mesh local material 线性表语义收敛 | ✅ |
| TASK-309g | RenderWorld extract local-slot 重构 | ✅ |
| TASK-309h | Cluster prepare zero-GC slot folding | ✅ |
| TASK-309i | Host / Test / Docs / Legacy 清理与守护 | ✅ |

## Test Results

Focused hot-path verification:

```text
dotnet test ... --filter "FullyQualifiedName~RenderWorld|FullyQualifiedName~SlotPreparer|FullyQualifiedName~ClusterPipelineQueryCache|FullyQualifiedName~LoaderDelegateIntegration"
Passed: 11, Failed: 0
```

Zero-allocation regression verification:

```text
dotnet test ... --filter "FullyQualifiedName~RenderWorldExtractorTests.Rebuild_SteadyState_DoesNotAllocateManagedMemory|FullyQualifiedName~SlotPreparerTests.Prepare_SteadyState_DoesNotAllocateManagedMemory"
Passed: 2, Failed: 0
```

Full suite:

```text
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet
Passed: 147, Failed: 0, Skipped: 0
```

Full build:

```text
dotnet build SomeEngine.slnx --no-restore -v minimal
0 errors
```

Build warnings still present:

- `tools/DagVisualizer/SomeEngine.DagVisualizer.csproj` 的 `NU1903`
- `src/SomeEngine.Runtime/Program.cs` 现有若干 `CS8602`

这些是本次验证输出中仍存在的 warning，但不影响本批完成条件。

## Issues Encountered

1. `dotnet` 在沙盒里无法稳定返回真实编译/测试输出，必须切到非沙盒并设置 `DOTNET_CLI_HOME=F:\SomeEngine\.dotnet_home` 才能拿到可靠验证证据。
2. 解决 steady-state allocation 测试时，最初测到了测试本身重复创建 resolver lambda 的 64B 噪音，后续改为复用同一 delegate，才把测量口径对准真正热路径。
3. solution build 早期的并行 build/test 会碰到 obj 文件锁；最终改成顺序验证后解决。

## Design Decisions

1. **不把 local material slot 再显式存回 authoring binding 字段。**  
   `MeshMaterialBindings.Bindings[i]` 的数组下标就是 canonical local material slot，避免再次引入“索引字段和数组顺序双真相源”。

2. **RenderWorld 继续保留 pass entities，但不再保留 region 回拼桥。**  
   提交事实变成 `(source entity, local material slot, material pass)`，不再让 cluster runtime 理解 region 映射。

3. **slot folding 仍然是 cluster-specific prepare 数据。**  
   `BinSpace` / `BinQueue` / `MaterialSlotBuffer` 没有被抬进 MaterialSystem，它们继续是 pipeline-owned 派生结构。

4. **steady-state 零分配优先靠 no-op 帧签名，而不是盲目把所有内部结构改成常驻增量同步。**  
   结构未变时直接 short-circuit，比每帧重复 touch ECS / slot cache 更符合当前批次目标和复杂度边界。

5. **每帧 no-Dictionary 约束落实到实际 host 路径。**  
   Runtime 的材质解析缓存改成线性列表查找，避免 extractor/prepare 每帧经过 `Dictionary`。

## Deviations

- BATCH-09b 指令里“slot folding 直接收敛到 cluster pipeline 自己的 prepare 路径”在实现上落成了 `ClusterPipeline` 持有的 `SlotPreparer`。这是 pipeline-owned 的内部 prepare 状态对象，不再是跨层桥接器。
- 没有额外跑交互式 runtime smoke。当前批次的完成证据来自 full build、full test、focused suite 和 zero-allocation regression。

## Edge Cases

- 某个 local material slot 没有可解析的 material 或没有对应 pass 时，prepare 仍会保留空 slot 占位，避免破坏 GPU 侧 `MaterialSlotOffset + localMatIdx` 的寻址语义。
- 多 pass 材质里如果同时出现 primary shade 和 overlay shade，`SlotPreparer` 会优先把 primary shade 填进 shading field，overlay 不会覆盖 primary。
- 当 source entity / bindings 未变化时，extract 与 prepare 都会直接 no-op，避免 steady-state 触碰 ECS/slot 分配路径。

## Weak Points / Improvement Opportunities

- 当前 local material slot 的 canonical 语义已经稳定，但 import/authoring 层仍然保留 region/section 元数据。后续可以进一步把 asset 层和 runtime 层的命名边界写得更清楚，避免再次混淆。
- `SlotPreparer` 目前用线性扫描 source cache 和 RenderWorld pass buffer，steady-state 已经满足无 GC，但在极大规模场景下可能还需要进一步优化扫描成本。
- Runtime `Program.cs` 里仍有若干可空 warning，没有在本批顺手清理。

## Known Issues

- solution build 仍包含现有的第三方漏洞 warning：`Tmds.DBus.Protocol 0.15.0`
- `SomeEngine.Runtime/Program.cs` 仍有既有 `CS8602` warning
- `.dev-workstream/batches/BATCH-09-INSTRUCTIONS.md` 与 `.dev-workstream/reports/BATCH-09-REPORT.md` 作为历史工件保留了上一阶段的 `region` / synchronizer 叙述，没有被回写为当前完成态
