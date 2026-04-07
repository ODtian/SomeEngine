# BATCH-02 Review

**Batch**: BATCH-02 — 核心管线加固 + Pre-existing 测试修复  
**Reviewer**: Workstream Audit (retrospective)  
**Date**: 2026-03-29

## Summary

BATCH-02 包含 TASK-101（PSO 分组逻辑提取）、TASK-102（DeformCache 纯 CPU helper 测试）和 pre-existing 测试修复。

## Verdict: ✅ APPROVED

该批次交付可接受，但 TASK-101 / TASK-102 只完成了 helper 层的第一阶段覆盖，父任务在 tracker 中应保持 `PARTIAL`。

## Task Completion

| Task | 完成 | 质量 |
|---|---|---|
| TASK-101 PSO 分组提取 | PARTIAL | `ShadePSOGroup.ComputeShaderGroups` + 7 个 ShaderGroupTests，`Sig1` 缓存未直接覆盖 |
| TASK-102 DeformCache 测试 | PARTIAL | `DeformDispatchCalc` + 17 个 DeformCacheTests，`CacheAllocCounter/CacheOffsets` 未直接覆盖 |
| pre-existing 修复 | ✅ | 历史失败项已清理；当前复核另发现 ECS `TransformSystemTests.TestRotation` 失败 |

## Design Decisions

1. **逻辑提取 > GPU mock** — 将 shader 逻辑镜像为纯 CPU 函数，而非 mock GPU device
2. **显式 Tag 设置** — `MaterialRegistry.Register` 不再自动推导 MultiPassTag/OverlayTag，测试改为显式设置
3. **unsafe 移除** — TestDisassemble.cs 从 unsafe 指针操作改为 `AsString` 扩展方法

## Issues Found

1. **SlangNoMangleTests 硬编码路径** — `d:\SomeEngine\` 改为相对路径（环境无关）
2. **SlangIntegrationTests meta path 错误** — `.slang.asset` 改为 `.shader.asset`（匹配 importer 实际输出）
3. **TestDisassemble shader source 无效** — 无 `[shader]` 属性导致 Slang 编译失败

## Test Quality Assessment

- ShaderGroupTests: 验证真实输出值（bin 分组结果），包含空输入/边界/null 等
- DeformCacheTests: 验证 dispatch 参数计算精确值和索引解码正确性，包含 SW/HW 边界、round-trip
- Remaining gap: `ClusterShade.GetOrCreateSig1()` 与 `ClusterDeformPass` 资源级行为仍需后续 batch 覆盖

## Commit Message

```
batch-02: 核心管线加固 & pre-existing 测试修复

- 提取 ShadePSOGroup.ComputeShaderGroups (TASK-101)
- 提取 DeformDispatchCalc + 17 个测试 (TASK-102)
- 修复 11 个 pre-existing 测试失败 (6 个文件)
- inst.md 添加 pre-existing 测试修复规则

Build: 0 errors, Test: 161/161 passed
```

## Next Batch

Phase 2 (BATCH-03): 文档补全 — TASK-201 ECS/QVVS/SourceGen/VRB + TASK-202 资产管线总览
