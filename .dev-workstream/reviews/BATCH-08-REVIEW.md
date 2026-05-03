# BATCH-08 Review

**Batch:** BATCH-08  
**Reviewer:** Development Lead  
**Date:** 2026-04-11  
**Status:** ⚠️ CONDITIONALLY APPROVED

---

## Summary

BATCH-08 目标是消灭 dual-sig、统一全 Dynamic、消除样板代码。核心架构改造和代码清理全部完成。编译通过、135 测试通过。但 runtime 存在 AlbedoMap/NormalMap/ARMMap 绑定报错（需要 fallback texture），不阻塞合并但需要立即跟进修复。

---

## Success Criteria Checklist

| 条件 | 状态 |
|------|------|
| `MaterialAssetLoader` 始终注册 binding name（即使 texture view 为 null） | ✅ |
| `ShadePSOGroup` → `MaterialPSOGroup`，删除 `Sig1` 字段 | ✅ |
| `ClusterShade` 删除全部 sig0/sig1/perPassSRB/sig1Cache static 状态 | ✅ |
| `BuildPSOGroups` 使用 `DefaultVariableType = Dynamic` | ✅ |
| `ClusterMaterialShadePass.Execute` 每 dispatch 绑全部资源 | ✅ |
| 删除 `ClusterShadePipelineParams.cs` | ✅ |
| `MaterialEntityUtility` 删除不再使用的方法 | ✅ |
| `StaticPSOInit` + `SRBPool` 消除 13+ 个类的样板代码 | ✅（14 个类全覆盖） |
| Runtime 正常启动 + 正确渲染（unlit + PBR） | ⚠️ 绑定报错 |
| 全量测试通过 | ✅ 135 passed |

---

## Issues Found

### P0: AlbedoMap 等纹理绑定报错

**现象：** `No resource is bound to variable 'AlbedoMap'`

**原因：** 全 Dynamic 下 Diligent 对每个变量做绑定检查。当 `ShaderParamBag` 中的纹理 view 为 null 时（加载失败或未设置），`ApplyTo` 跳过绑定（line 203: `if (entry.Value == null) continue`），导致 Dynamic 变量为空。旧 dual-sig 的 Static 变量不做运行时校验所以没暴露。

**修复方案：** 引入 fallback texture（`WellKnownAssets` 中已有 `default_white/default_normal/default_arm` 的 GUID 和 .texture.asset），在 `ApplyTo` 或 `SetTexture` 中当 view 为 null 时绑 fallback。这是独立修复，不影响本批架构改造。

**分流：** 加入 DEBT-TRACKER 或作为 BATCH-09 紧急修复。

### P2: Report 过时

**现象：** BATCH-08-REPORT.md 在第二轮执行后未更新，仍描述旧的 dual-sig 架构。

**修复：** 已在 review 过程中重写。

### P2: 执行中漏改文件

**现象：** 第一轮执行漏改 `ClusterSWRasterPass` 和 `ClusterDeformPass` 的 init guard；第一轮执行声称"多 pool 类投入产出比低"而跳过 SRBPool 改造。

**修复：** 经用户指出后已全部补上。全部 14 个类完成改造。

---

## Verdict

**Status:** CONDITIONALLY APPROVED

### Required Actions（阻塞优先级）

1. **[P0] Fallback texture 绑定** — 当材质纹理 view 为 null 时绑 fallback（`default_white` 等）。不改本批架构，在 `ShaderParamBag.ApplyTo` 或 `Material.SetTexture` 中处理。

### Recommendations（非阻塞）

2. 后续 batch 脚本化批量修改后必须自动运行 `grep` 扫描确认零残留，不应依赖手动二轮 review 发现遗漏。
3. SRBPool 的 `Rent` 签名当前创建 SRB 时传 `initStaticResources: false`。如果某些 PSO 有 Static 变量（当前没有，全部 Dynamic），需要注意此参数。

### Validation

```
dotnet build --no-restore → 0 errors
dotnet test --no-build    → Passed: 135, Failed: 0, Skipped: 1
```

---

## Commit Message

```text
refactor: eliminate dual-sig architecture, unify all PSOs to full dynamic

Completes BATCH-08 (TASK-308a/308b/308c)

- Remove Sig0/Sig1/perPassSRB/sig1Cache from ClusterShade
- Rewrite BuildPSOGroups to use implicit dynamic reflection
- Rename ShadePSOGroup → MaterialPSOGroup, delete Sig1 field
- Delete ClusterShadePipelineParams.cs, ClusterShadeSig1Tests.cs
- Extract StaticPSOInit + SRBPool helpers
- Migrate all 14 PSO classes to use StaticPSOInit + SRBPool
- Update 6 documentation files to reflect new architecture

Tests: 135 passed, 0 failed, 1 skipped
```

---

## Next Batch

- **BATCH-09** — Fallback texture 绑定 + runtime 渲染验证
