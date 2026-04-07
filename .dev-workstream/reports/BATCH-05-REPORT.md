# BATCH-05 Report: ShaderAsset Metadata Runtime Path

**Batch:** BATCH-05  
**Date:** 2026-03-30  
**Status:** ✅ COMPLETE

---

## Summary

本批把 `ShaderAsset.Metadata.MaterialBindings` 从“仅 importer 产物”推进成了 runtime 可见输入。`MaterialPass` 的 pass signature 和 `ClusterShade` 的 Sig1 layout / cache key 现在都会优先依据 metadata 过滤资源集合，而不是盲目读取整个 runtime bag。

---

## Files Modified

| File | Change |
|---|---|
| `src/SomeEngine.Render/Materials/ShaderParamBag.cs` | 修改 |
| `src/SomeEngine.Render/Materials/MaterialPass.cs` | 修改 |
| `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` | 修改 |
| `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs` | 修改 |
| `tests/SomeEngine.Tests/Materials/MaterialRegistryTests.cs` | 修改 |
| `docs/TASK-DETAIL.md` | 修改 |
| `.dev-workstream/TASK-TRACKER.md` | 修改 |
| `ONBOARDING.md` | 修改 |
| `docs/DESIGN.md` | 修改 |
| `.dev-workstream/batches/BATCH-05-INSTRUCTIONS.md` | 重写为真实实现批 |

---

## Task Check

- [x] TASK-305 — Shader metadata 已接入 runtime pass signature 与 Sig1 资源布局

---

## Test Results

**Full test summary:**
```text
Passed! - Failed: 0, Passed: 191, Skipped: 0, Total: 191
```

**Targeted tests:**
- `ClusterShadeSig1Tests` — Passed
- `MaterialTests.ComputeSignature_IgnoresUnusedRuntimeResources_WhenMetadataPresent` — Passed

**New / Expanded tests:**
- `BuildSig1Resources_UsesShaderMetadataBindings_WhenPresent`
- `ComputeSig1CacheKey_IgnoresUnusedRuntimeResources_WhenMetadataPresent`
- `ComputeSignature_IgnoresUnusedRuntimeResources_WhenMetadataPresent`

---

## Issues Encountered

### Issue 1: Metadata already existed, but runtime did not consume it
- **Problem:** `shader_asset.fbs` 和 `SlangShaderImporter` 已有 `MaterialBindings`，但 runtime 基本没有用
- **Root cause:** material runtime 仍按“整个 ShaderParamBag”来推断 pass signature 和 Sig1 layout
- **Resolution:** 把 metadata 消费点补到 `MaterialPass` 和 `ClusterShade`

### Issue 2: Bag-wide enumeration polluted pass semantics
- **Problem:** 一个 pass 可能因为共享 bag 中的无关资源而获得错误的 signature / Sig1 layout
- **Root cause:** 没有 pass-level resource filtering
- **Resolution:** metadata 存在时改走 declared bindings；metadata 缺失时保留 fallback

---

## Design Decisions

### Decision 1
- **What:** 在 `MaterialPass` 中新增 resolved-resource 视角
- **Why:** pass signature 和 layout 应该反映 shader 实际声明的资源，而不是 bag 中所有条目
- **Trade-off:** `MaterialPass` 对 `ShaderMetadata` 有了更强依赖，但仍保持 fallback 路径

### Decision 2
- **What:** `ClusterShade` 继续复用 helper，但改用 `MaterialPass.EnumerateResolvedResources()`
- **Why:** 不扩散 metadata 过滤逻辑，避免多个地方各写一套
- **Trade-off:** metadata 语义需要在 `MaterialPass` 层保持稳定

---

## Deviations From Instructions

No meaningful deviations.

---

## Edge Cases / Hidden Findings

- schema 与 importer 这条 metadata 通路其实早就存在，真正的缺口是 runtime consumption
- metadata 缺失时必须 fallback，否则旧 shader / 测试会被直接打断
- 标量参数仍应参与 pass signature，但不应影响 Sig1 layout key

---

## Weak Points / Improvement Opportunities

- `MaterialRegistry` 还没有基于 metadata 做更完整的自动推导
- `MaterialAssetLoader` 仍然是“加载全部纹理到 shared bag”，尚未形成真正的 per-pass resolve
- 未来如果要做 `MaterialStore`，需要先明确如何与现有 metadata 路线衔接

---

## Known Issues

- `TASK-306` 仍未开始
- `TASK-304` 仍未做文档化冻结，但这不影响当前代码主线继续推进

---

## Final Status

- **Batch result:** complete
- **Confidence level:** high
- **Ready for review:** yes
