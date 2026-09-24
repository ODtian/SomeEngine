# BATCH-04 Report: Phase 1 Hardening Closeout

**Batch:** BATCH-04  
**Date:** 2026-03-30  
**Status:** ✅ COMPLETE

---

## Summary

本批关闭了 Phase 1 剩余的三个问题：修复了 `TransformSystemTests` 的同步语义错误，补齐了 Dual-Signature 的 Sig1 cache key / descriptor / cache reuse 直接测试，并为 DeformCache 增加了资源级镜像测试。全量测试恢复为 `188 passed, 0 failed`。

---

## Files Modified

| File | Change |
|---|---|
| `tests/SomeEngine.Tests/ECS/TransformSystemTests.cs` | 修改 |
| `src/SomeEngine.Render/Properties/AssemblyInfo.cs` | 新增 |
| `src/SomeEngine.Render/Materials/ShaderParamBag.cs` | 修改 |
| `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` | 修改 |
| `src/SomeEngine.Render/Pipelines/ClusterRender/DeformDispatchCalc.cs` | 修改 |
| `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs` | 新增 |
| `tests/SomeEngine.Tests/Pipelines/DeformCacheTests.cs` | 修改 |
| `docs/rendering/sw_raster/sw_raster.md` | 修改 |

---

## Task Check

- [x] TASK-103 — `TransformSystemTests` 在断言前完成 `_context.GlobalDependency`
- [x] TASK-101 — Sig1 cache key / descriptor / cache reuse helper 和测试补齐
- [x] TASK-102 — DeformCache offset allocation / cached-path boundary / byte address 测试补齐

---

## Test Results

**Full test summary:**
```text
Passed! - Failed: 0, Passed: 188, Skipped: 0, Total: 188
```

**Targeted tests:**
- `TransformSystemTests` — Passed
- `ClusterShadeSig1Tests` — Passed
- `DeformCacheTests` — Passed

**New tests added:**
- `ClusterShadeSig1Tests.ComputeSig1CacheKey_IgnoresScalarValues_And_InsertionOrder` — 验证 Sig1 cache key 只看布局
- `ClusterShadeSig1Tests.BuildSig1Resources_AddsUniforms_First_And_SortsRemainingResources` — 验证 descriptor 构建稳定
- `ClusterShadeSig1Tests.GetOrAddSig1CacheEntry_ReusesCachedValue_ForSameLayout` — 验证 cache reuse
- `DeformCacheTests.AllocateCacheOffsets_AssignsSequentialBaseOffsets` — 验证 offset 分配
- `DeformCacheTests.CacheFits` — 验证 cached-path 边界

---

## Issues Encountered

### Issue 1: Transform rotation “failure” was a sync issue
- **Problem:** `TransformSystemTests.TestRotation` 在 direct-system 路径下失败
- **Root cause:** 测试直接调用 `SystemRoot.Update()` 后没有完成 job dependency
- **Resolution:** 在测试辅助方法中显式 `Complete()` 当前 dependency

### Issue 2: Sig1 cache key was too broad
- **Problem:** 原实现使用 `pass.ComputeSignature()` 作为 Sig1 cache key，会把标量值和资源实例值混进 cache key
- **Root cause:** 运行时 bin signature 和 pipeline resource signature cache key 被复用了同一个概念
- **Resolution:** 新增资源布局级 hash，并让 Sig1 cache 只按资源布局复用

---

## Design Decisions

### Decision 1
- **What:** 给 `ShaderParamBag` 新增 `GetResourceLayoutHash()`
- **Why:** 让 Sig1 cache key 与资源布局绑定，而不是与材质值绑定
- **Trade-off:** 增加一个并行概念，需要和 `GetSignatureHash()` 区分用途

### Decision 2
- **What:** 在 `ClusterShade` 中抽出 `ComputeSig1CacheKey` / `BuildSig1Resources` / `GetOrAddSig1CacheEntry`
- **Why:** 让 cache 语义和 descriptor 生成可直接测试
- **Trade-off:** 增加了一点 helper surface，但换来更稳定的测试入口

### Decision 3
- **What:** 继续把 `DeformDispatchCalc` 作为 shader mirror helper 扩展
- **Why:** 当前项目没有轻量 GPU mock，纯 CPU 镜像是最稳妥的测试落点
- **Trade-off:** 需要保持 helper 与 shader 语义同步

---

## Deviations From Instructions

### Deviation 1
- **What changed:** 顺手修正了 `docs/rendering/sw_raster/sw_raster.md` 的 `DeformedBuffer` 旧命名
- **Why:** 这是当前唯一还挂着的轻量文档漂移之一，修复成本低
- **Benefit:** 关闭 `DEBT-014`
- **Risk:** 仍有其他 sw_raster 章节保留未来计划表述，后续需继续审视
- **Recommendation:** 保留

---

## Edge Cases / Hidden Findings

- `GameWorld.Update()` 路径一直是正确的，失败只发生在 direct-system test harness
- `ShaderParamBag.EnumerateResources()` 如果不排序，会让 Sig1 descriptor 顺序取决于字典插入顺序
- Sig1 cache key 不应复用 `MaterialPass.ComputeSignature()`，因为两者语义不同

---

## Weak Points / Improvement Opportunities

- 仍缺少 Cluster Pipeline 端到端渲染验证
- `Program.cs` 仍是最大的结构债
- `sw_raster` 子文档还有其他未来计划与现状混写的段落，后续可以再统一一次

---

## Known Issues

- `src/SomeEngine.Runtime/Program.cs` 仍然过大且无测试
- 缺少 Dual-Signature 独立设计文档

---

## Final Status

- **Batch result:** complete
- **Confidence level:** high
- **Ready for review:** yes
