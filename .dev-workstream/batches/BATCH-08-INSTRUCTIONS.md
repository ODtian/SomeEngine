# BATCH-08: 全 Dynamic PSO 简化 + AlbedoMap Bug 修复

**Batch Number:** BATCH-08  
**Tasks:** TASK-308a, TASK-308b, TASK-308c  
**Phase:** Phase 3 - Render Pipeline Modernization  
**Estimated Effort:** medium  
**Priority:** HIGH (PSO 创建失败 + sig 架构债)  
**Dependencies:** BATCH-07c  
**Supersedes:** 原 BATCH-08 方案（dual-sig 保留 + Dynamic 迁移），改为全 Dynamic 消灭 dual-sig

---

## Onboarding & Workflow

### Complete Engineering Goal

消灭 ClusterShade 的 dual-signature（sig0/sig1）架构，统一为全 Dynamic 隐式签名模式。同时修复 AlbedoMap PSO 创建失败 bug，消除 sig1 缓存泄漏，减少 ~280 行样板代码。

**现状问题：**
1. `AlbedoMap` 等全局 shader 变量不在显式 `PipelineResourceSignature` 中 → PSO 创建失败
2. `s_sig1Cache`：`Dictionary<ulong, IPipelineResourceSignature>` 非线程安全、永不释放、所有权悬空
3. sig0/sig1/perPassSRB/ClusterShadePipelineParams — 概念过多，`ClusterShade` 身兼状态持有者和 pass 编排者
4. 14 个 PSO 类复制粘贴 `s_initialized + s_initLock + ConcurrentBag + RentSRB/ReturnSRB` 样板
5. `MaterialAssetLoader` 在纹理加载失败时不注册 binding → `ApplyTo` 漏绑

**改造后模型：**
```
PSO 创建 → PipelineResourceLayoutDesc { DefaultVariableType = Dynamic }
           Diligent 从 shader bytecode 自动反射全部资源
           零手动资源列举，不可能遗漏

SRB      → pso.CreateShaderResourceBinding(false)
           每 dispatch 前 Set() 全部资源 → CommitShaderResources
           Dynamic 描述符从 ring buffer 分配，帧结束自动回收

Sampler  → ImmutableSampler 烘进 PipelineResourceLayoutDesc
           零 GPU-visible sampler 描述符消耗
```

**性能验证（已审查 Diligent D3D12 源码）：**
- `CommitShaderResources` 对 Dynamic 变量调用 `CopyDescriptorsSimple`
- 全 Dynamic 比 dual-sig 每 dispatch 多拷贝 ~12 个 descriptor（384 bytes memcpy）
- ~20 material dispatch/帧 × 384 bytes = ~7.5 KB memcpy/帧 ≈ 纳秒级，完全可忽略

### What Does NOT Count As This Batch
- Bindless 描述符索引（后续 batch）
- BinSpace 架构重构
- 修改 shader 编译管线 / Asset Pipeline
- 13 个固定 PSO 类的 init 逻辑重写（仅消除样板，不改架构）

### Required Reading (IN ORDER)
1. `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` — 当前 dual-sig 架构
2. `src/SomeEngine.Render/Pipelines/ClusterRender/ShadePSOGroup.cs` — PSO group + ShaderGroup
3. `src/SomeEngine.Render/Pipelines/ClusterRender/RasterPSOBuilder.cs` — 目标模式（全 Dynamic）
4. `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialShadePass.cs` — Execute 路径
5. `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs` — PSO group 生命周期
6. `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` — texture binding 注册
7. `src/SomeEngine.Render/Materials/MaterialEntityUtility.cs` — 资源推断（将被删除）

### Source Code Locations
| Area | Path |
|---|---|
| Shade 编排 | `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` |
| PSO Group 定义 | `src/SomeEngine.Render/Pipelines/ClusterRender/ShadePSOGroup.cs` |
| Raster PSO 构建 | `src/SomeEngine.Render/Pipelines/ClusterRender/RasterPSOBuilder.cs` |
| Pipeline 主体 | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs` |
| 材质 Shade Pass | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialShadePass.cs` |
| SW Raster Pass | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterSWRasterPass.cs` |
| Deform Pass | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterDeformPass.cs` |
| 材质加载 | `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` |
| PipelineParams（删） | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterShadePipelineParams.cs` |
| 资源推断工具 | `src/SomeEngine.Render/Materials/MaterialEntityUtility.cs` |

---

## Task Breakdown

### TASK-308a: Bug 修复 + 类型重命名

**目标：** 修复 `MaterialAssetLoader` binding 漏洞，重命名 `ShadePSOGroup` → `MaterialPSOGroup`，删除 `Sig1` 字段。

**步骤：**

1. `MaterialAssetLoader.cs` line 100 — 去掉 `if (view != null)` 条件，始终调用 `SetTexture(name, view)`
2. `ShadePSOGroup.cs` 重命名为 `MaterialPSOGroup.cs`：
   - 类名 `ShadePSOGroup` → `MaterialPSOGroup`
   - 删除 `Sig1` 字段（不再有显式签名）
   - `ShaderGroup` record 保留
3. 全项目 `ShadePSOGroup` → `MaterialPSOGroup` 类型名替换（~15 处）：
   - `ClusterPipeline.cs`（3 字段 + DisposePSOGroups）
   - `ClusterMaterialShadePass.cs`、`ClusterSWRasterPass.cs`、`ClusterDeformPass.cs`
   - `ClusterSWDrawStage.cs`、`ClusterHiZStage.cs`
   - `RasterPSOBuilder.cs`
   - `ShaderGroupTests.cs`

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [MODIFY] | `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` |
| [RENAME+MODIFY] | `ShadePSOGroup.cs` → `MaterialPSOGroup.cs` |
| [MODIFY] | 9 个消费者文件（类型名替换） |
| [MODIFY] | `tests/.../ShaderGroupTests.cs` |

---

### TASK-308b: 消灭 Dual-Sig，统一全 Dynamic

**目标：** `ClusterShade.BuildPSOGroups` 从 dual-sig 改为全 Dynamic（与 `RasterPSOBuilder` 相同模式），消灭 sig0/sig1/perPassSRB/sig1Cache，重写 `ClusterMaterialShadePass.Execute`。

**步骤：**

1. `ClusterShade.cs` 删除全部 sig 相关代码：
   - 删除 5 个 static 字段：`s_sig0`, `s_perPassSRB`, `s_sig1Cache`, `Sig0`, `PerPassSRB`
   - 删除方法：`CreateSig1()`, `BuildSig1Resources()`, `ComputeSig1CacheKey()`
   - `EnsureInitialized()` 只保留 binning/resolve 初始化
2. `ClusterShade.BuildPSOGroups()` 重写为全 Dynamic 模式：
   - `PipelineResourceLayoutDesc { DefaultVariableType = Dynamic, ImmutableSamplers = [MaterialSampler] }`
   - PSO 通过 `GlobalPsoCache.GetOrCreateComputePSO()` 缓存
   - SRB 从 PSO 创建（`pso.CreateShaderResourceBinding(false)`）
3. `ClusterShade.AddPasses()` 签名移除对 `PerPassSRB` 的使用
4. `ClusterMaterialShadePass.Execute()` 重写：
   - 删除 `ClusterShade.PerPassSRB` 引用
   - 删除 `ClusterShadePipelineParams` 使用
   - 每 dispatch 前绑全部资源（per-pass + per-material）到 `group.SRB`
   - 单次 `CommitShaderResources` per dispatch
5. 删除 `ClusterShadePipelineParams.cs`
6. `MaterialEntityUtility.cs` 删除不再使用的方法：
   - `EnumerateResolvedResources`, `ComputeResolvedResourceLayoutHash`
   - `ComputeResolvedParamSignature`, `GetOrderedMaterialBindings`

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [MODIFY] | `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` |
| [MODIFY] | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialShadePass.cs` |
| [DELETE] | `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterShadePipelineParams.cs` |
| [MODIFY] | `src/SomeEngine.Render/Materials/MaterialEntityUtility.cs` |

---

### TASK-308c: 样板代码消除

**目标：** 提取 `StaticPSOInit` + `SRBPool` 通用 helper，消除 13 个静态 PSO 类的重复样板代码。

**步骤：**

1. 新增 `StaticPSOInit.cs`：`Once(ref bool, Lock, Action)` — 线程安全 double-check init
2. 新增 `SRBPool.cs`：`Rent(IPipelineState)` + `Return(IShaderResourceBinding)` — 封装 ConcurrentBag
3. 改造 13 个 PSO 类：
   - 替换 `s_initialized + s_initLock + EnsureInitialized` → `StaticPSOInit.Once()`
   - 替换 `ConcurrentBag + RentSRB + ReturnSRB` → `SRBPool`
   - 每个类减少 ~10 行
4. 适用类清单：
   - `ClusterResolvePSOs`, `ClusterCullPSOs`, `ClusterBVHTraversePSOs`
   - `ClusterBinningPSOs`, `ClusterDeformBinPSOs`, `ClusterDeformPSOs`
   - `ClusterDrawPass`, `ClusterSWRasterPSOs`, `HiZBuildPSOs`
   - `DepthMergePSOs`, `ClusterDebugPSOs`, `ClusterDebugAABBPSOs`
   - `ClusterGraphPasses`（2 个内部类）

**产出文件清单：**
| 操作 | 文件 |
|---|---|
| [NEW] | `src/SomeEngine.Render/Pipelines/StaticPSOInit.cs` |
| [NEW] | `src/SomeEngine.Render/Pipelines/SRBPool.cs` |
| [MODIFY] | 13 个 `*PSOs` / Pass 类 |

---

## Execution Order

1. **TASK-308a** → Bug 修复 + 类型重命名（最小改动，立即修复 PSO 创建失败）
2. **TASK-308b** → 核心架构简化（消灭 dual-sig，重写 Execute）
3. **TASK-308c** → 样板代码消除（收尾，不影响功能）

---

## Testing Requirements

- **TASK-308a:** `dotnet build` 通过 + `dotnet test` 全量通过
- **TASK-308b:** `dotnet build` 通过 + `dotnet test` 全量通过 + Runtime 启动无 PSO 报错 + 材质正常渲染
- **TASK-308c:** `dotnet build` 通过 + `dotnet test` 全量通过

---

## Quality Standards

- 零显式 `IPipelineResourceSignature`（全部由 Diligent 从 shader reflection 自动创建）
- 零 SRB/Sig 泄漏（`MaterialPSOGroup.Dispose()` 释放 SRB，PSO 由 `GlobalPsoCache` 管理）
- 材质资源变量类型全部为 Dynamic
- `MaterialSampler` 为 ImmutableSampler（零 GPU-visible sampler 描述符消耗）
- BinSpace 接口无变化
- 禁止 `unsafe`，使用 Span API

---

## Success Criteria

- [ ] `MaterialAssetLoader` 始终注册 binding name（即使 texture view 为 null）
- [ ] `ShadePSOGroup` → `MaterialPSOGroup`，删除 `Sig1` 字段
- [ ] `ClusterShade` 删除全部 sig0/sig1/perPassSRB/sig1Cache static 状态
- [ ] `BuildPSOGroups` 使用 `PipelineResourceLayoutDesc { DefaultVariableType = Dynamic }`
- [ ] `ClusterMaterialShadePass.Execute` 每 dispatch 绑全部资源（per-pass + per-material）
- [ ] 删除 `ClusterShadePipelineParams.cs`
- [ ] `MaterialEntityUtility` 删除 `EnumerateResolvedResources` 等不再使用的方法
- [ ] `StaticPSOInit` + `SRBPool` 消除 13 个类的样板代码
- [ ] Runtime 正常启动 + 正确渲染（unlit + PBR）
- [ ] 全量测试通过

---

## Architecture Notes

### 为什么从 Dual-Sig 改为全 Dynamic

| | Dual-Sig（旧） | 全 Dynamic（新） |
|---|---|---|
| 资源发现 | 手动列举 → 遗漏 AlbedoMap | 自动反射 → 不可能遗漏 |
| 签名管理 | 2 个显式 sig + 缓存字典 | 0 个显式 sig |
| SRB 模型 | perPassSRB + per-group SRB | per-group SRB 唯一 |
| 概念数量 | 6 个（sig0/sig1/cache/perPassSRB/PipelineParams/ShaderGroup） | 2 个（PSO + SRB） |
| 每 dispatch 多拷贝 | — | ~12 descriptor（384B memcpy，纳秒级） |
| 代码行数 | ~400 行 sig 管理 | 0 |

### 向 Bindless 演进路径

```
BATCH-07c:   Mutable SRB per-bin  → 泄漏
BATCH-08旧:  Dynamic SRB per-layout + dual-sig → 复杂
BATCH-08新:  全 Dynamic 隐式签名 → 简洁
下一步:      Bindless (1 SRB, descriptor index in uniform)
```
