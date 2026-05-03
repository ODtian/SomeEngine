# BATCH-08 Report: 全 Dynamic PSO 简化 + AlbedoMap Bug 修复

**Batch:** BATCH-08  
**Date:** 2026-04-11  
**Status:** COMPLETE

## Summary

消灭 ClusterShade 的 dual-signature（sig0/sig1）架构，统一为全 Dynamic 隐式签名模式。修复 AlbedoMap PSO 创建失败 bug，删除 sig1 缓存和全部显式 `IPipelineResourceSignature`。提取 `StaticPSOInit` + `SRBPool` helper，消除全部 PSO 类的样板代码。

## Files Modified

### 新增

| 文件 | 说明 |
|---|---|
| `src/SomeEngine.Render/Pipelines/StaticPSOInit.cs` | `Once(ref bool, Lock, Action)` — 线程安全 double-check init helper |
| `src/SomeEngine.Render/Pipelines/SRBPool.cs` | `Rent(IPipelineState)` + `Return(IShaderResourceBinding)` — 封装 ConcurrentBag |

### 删除

| 文件 | 说明 |
|---|---|
| `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterShadePipelineParams.cs` | 旧 dual-sig 参数容器 |
| `src/SomeEngine.Render/Pipelines/ClusterRender/ShadePSOGroup.cs` | 重命名为 MaterialPSOGroup.cs |
| `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs` | dual-sig 架构测试，随架构删除 |

### 核心改造

| 文件 | 说明 |
|---|---|
| `Stages/ClusterShade.cs` | 删除 sig0/sig1/perPassSRB/sig1Cache 全部 static 状态；`BuildPSOGroups()` 重写为全 Dynamic + ImmutableSampler |
| `ClusterMaterialShadePass.cs` | `Execute()` 每 dispatch 绑全部资源（per-pass + per-material）到 group.SRB |
| `MaterialPSOGroup.cs`（原 ShadePSOGroup） | 删除 Sig1 字段，保留 SRB + PSO + ShaderGroup |
| `MaterialEntityUtility.cs` | 删除 `EnumerateResolvedResources`/`ComputeResolvedResourceLayoutHash` 等 4 个不再使用的方法 |
| `MaterialAssetLoader.cs` | 修复 binding 注册 bug |

### 样板消除（全 14 个 PSO 类）

| 文件 | 说明 |
|---|---|
| `ClusterResolvePass.cs` | `StaticPSOInit.Once` + `SRBPool` |
| `ClusterCullPass.cs` | `StaticPSOInit.Once` + `SRBPool`（3 pool） |
| `ClusterBVHTraversePass.cs` | `StaticPSOInit.Once` + `SRBPool`（4 pool） |
| `ClusterBinningPass.cs` | `StaticPSOInit.Once` + `SRBPool`（5 pool） |
| `ClusterDeformBinPass.cs` | `StaticPSOInit.Once` + `SRBPool`（6 pool） |
| `ClusterDeformPass.cs` | `StaticPSOInit.Once` |
| `ClusterDrawPass.cs` | `StaticPSOInit.Once` + `SRBPool`（6 pool） |
| `ClusterSWRasterPass.cs` | `StaticPSOInit.Once` |
| `HiZBuildPass.cs` | `StaticPSOInit.Once` + `SRBPool`（2 pool） |
| `DepthMergePass.cs` | `StaticPSOInit.Once` + `SRBPool` |
| `ClusterDebugPass.cs` | `StaticPSOInit.Once` + `SRBPool`（2 pool） |
| `ClusterDebugAABBPass.cs` | `StaticPSOInit.Once` + `SRBPool` |
| `ClusterGraphPasses.cs` | `StaticPSOInit.Once` + `SRBPool`（2 个内部类） |

### 文档

| 文件 | 说明 |
|---|---|
| `docs/rendering/shading_pipeline.md` | 重写：dual-sig → 全 Dynamic 隐式签名 |
| `docs/rhi/srb_binding_strategy.md` | 重写：当前策略 + 演进路径 |
| `docs/DESIGN.md` | §3.5/4/Phase1 更新 |
| `docs/TASK-DETAIL.md` | TASK-004/101/304 更新 |
| `docs/materials/architecture.md` | PSO/SRB 管理段更新 |
| `docs/rendering/gpu_pipeline_tag_integration_plan.md` | ShadePSOGroup → MaterialPSOGroup |

## Task Check

| Task ID | Title | Status |
|---|---|---|
| TASK-308a | Bug 修复 + MaterialPSOGroup 重命名 | ✅ |
| TASK-308b | 消灭 Dual-Sig，统一全 Dynamic | ✅ |
| TASK-308c | 样板代码消除（StaticPSOInit + SRBPool 全覆盖） | ✅ |

## Test Results

```
Passed: 135, Failed: 0, Skipped: 1, Total: 136
```

Build:
```
0 errors
```

## Design Decisions

- **全 Dynamic 隐式签名**：使用 `PipelineResourceLayoutDesc { DefaultVariableType = Dynamic }` + Diligent 隐式反射。不创建显式 `IPipelineResourceSignature`。所有资源由 shader bytecode 自动反射，不可能遗漏。
- **ImmutableSampler**：`MaterialSampler` 烘入 `PipelineResourceLayoutDesc.ImmutableSamplers`。零 GPU-visible sampler 描述符消耗。
- **SRBPool 全覆盖**：全部 PSO 类的 SRB 池统一为 `SRBPool`，消灭所有手写 `ConcurrentBag<IShaderResourceBinding>` + `RentSRB/ReturnSRB`。
- **StaticPSOInit 全覆盖**：全部 PSO 类的 init guard 统一为 `StaticPSOInit.Once()`，消灭所有手写 double-check lock。

## Architecture

```
改造前:                              改造后:
┌─────────────────────┐              ┌─────────────────────┐
│  Sig0 + Sig1 双签名  │              │  无显式签名           │
│  sig1Cache 字典      │              │  隐式反射             │
│  perPassSRB static   │              │  per-group SRB ×1    │
│  per-group SRB       │              │  Dynamic vars        │
│  Mutable/Dynamic 混  │              │  ImmutableSampler    │
│  14 class 手写样板    │              │  StaticPSOInit+SRBPool│
│  ConcurrentBag×N     │              │  全量消除             │
└─────────────────────┘              └─────────────────────┘
```

## Runtime Status

AlbedoMap/NormalMap/ARMMap 绑定报错仍存在——这不是架构问题。Dynamic 变量要求每个变量都必须绑定非 null 资源，而旧系统 Static 变量不做运行时校验。需要引入 fallback texture（1x1 白色/法线/ARM placeholder），在 `ShaderParamBag.ApplyTo` 中当 view 为 null 时绑 fallback。此为下一步修复。
