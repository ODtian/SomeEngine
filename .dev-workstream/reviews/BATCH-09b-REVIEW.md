# BATCH-09b Review: Mesh Local Material Table + Zero-GC Cluster Prepare Rework

**Batch:** BATCH-09b  
**Date:** 2026-04-19  
**Reviewer:** Codex (self-review)  
**Decision:** APPROVED

## Review Summary

本批交付与 `BATCH-09b` 指令目标一致：

- cluster runtime 不再把 `region` 当运行时映射概念
- `MeshMaterialBindings` 收敛为稳定的 mesh-local material linear table
- `RenderWorldMaterialSlotSynchronizer` 与 `ClusterPipelineSlotBindingBuilder` 已删除
- slot folding 已收口到 cluster pipeline 自己的 prepare 路径
- 热点路径 zero-allocation regression 测试已落地并通过
- 每帧热点路径的显式 `Dictionary` 使用已从本批涉及链路中移除

## Verification Evidence

已重新运行并确认：

```text
dotnet build SomeEngine.slnx --no-restore -v minimal
0 errors
```

```text
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet
Passed: 147, Failed: 0, Skipped: 0
```

```text
dotnet test ... --filter "FullyQualifiedName~RenderWorld|FullyQualifiedName~SlotPreparer|FullyQualifiedName~ClusterPipelineQueryCache|FullyQualifiedName~LoaderDelegateIntegration"
Passed: 11, Failed: 0
```

```text
dotnet test ... --filter "FullyQualifiedName~RenderWorldExtractorTests.Rebuild_SteadyState_DoesNotAllocateManagedMemory|FullyQualifiedName~SlotPreparerTests.Prepare_SteadyState_DoesNotAllocateManagedMemory"
Passed: 2, Failed: 0
```

## Findings

无阻塞 findings。

## Residual Risks

1. `SlotPreparer` 目前采用线性扫描 source cache 与 RenderWorld pass buffer；steady-state 无 GC 已验证，但在更大规模场景下仍需继续观察 CPU 成本。
2. `Runtime Program.cs` 仍有既有可空 warning，没有在本批顺手清理。
3. 历史工件 `BATCH-09-INSTRUCTIONS.md` 与 `BATCH-09-REPORT.md` 保留了上一阶段的叙述，这是刻意保留历史记录，不代表当前架构状态。

## Debt Routing

- 未新增必须切出新 corrective batch 的 P1 问题
- 现有 build warnings 继续保持在既有 debt / known issues 范畴
