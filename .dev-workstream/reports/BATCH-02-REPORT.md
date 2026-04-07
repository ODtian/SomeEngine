# BATCH-02 REPORT

**Date:** 2026-03-29  
**Status:** PARTIAL

## Completed

- [x] TASK-101 第一阶段：提取 `ShadePSOGroup.ComputeShaderGroups()`，新增 `ShaderGroupTests`
- [x] TASK-102 第一阶段：提取 `DeformDispatchCalc`，新增 `DeformCacheTests`
- [x] 清理并修复当时的 pre-existing 测试失败，使测试集恢复可用

## Remaining Gaps

- `ClusterShade.GetOrCreateSig1()` / `s_sig1Cache` 仍无直接测试
- `BuildPSOGroups()` 的 SRB 构建路径仍需更贴近真实材质场景的覆盖
- `ClusterDeformPass` 的 `CacheAllocCounter` / `CacheOffsets` / inline-vs-cached 路径仍无行为级测试

## Verification

- `dotnet build SomeEngine.slnx`
- `dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj`
- 当前复核基线（2026-03-30）：`177 passed, 1 failed`
- 失败项：`TransformSystemTests.TestRotation`（ECS 旋转层级传播，非本批直接范围）

## Tracker Impact

- `TASK-101` 应标记为 `PARTIAL`
- `TASK-102` 应标记为 `PARTIAL`
