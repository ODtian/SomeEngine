# SomeEngine 设计文档

> **定位：** 基于当前代码状态的顶层架构总览，引用而非替代各领域文档。
>
> **Date:** 2026-04-03

---

## 1. Project Goal

全 C# ECS 游戏引擎，核心聚焦 GPU-Driven Cluster Rendering（Nanite-like）。

### 当前边界
- ✅ GPU 渲染管线（BVH → HiZ → SW/HW Raster → Compute Shade）
- ✅ 材质系统（MaterialSystem + Entity-based Material + BinQueue/BinSpace）
- ✅ 资产管线（GUID Manifest + Scanner + Validator + Workspace）
- ✅ Slang 着色器集成（编译 + 反射 + 依赖追踪）
- ✅ ECS / QVVS / Job System 基础能力
- 📋 光照系统（PBR directional light 已有，无 point/area/IBL/shadow map）
- 📋 流式加载（框架已有 `ClusterStreamer`，Page 管理未完善）
- ❌ 编辑器 / 物理 / 动画 / UI / 粒子

### 不解决什么
- 不做网络 / 音频 / 脚本系统
- 不做移动端优化（当前仅 D3D12 / Vulkan）

详见 [`docs/goal.md`](goal.md)

---

## 2. Current Architecture Snapshot

### 模块分层

```text
Runtime (Program.cs)
  -> Render
     -> Graph / Materials / Pipelines / Systems / RHI
  -> Assets
     -> Importers / Pipeline / Meta / Data
  -> Core
     -> ECS / Jobs / Math(QVVS)
  -> Generators
  -> External
```

### 关键依赖

| 依赖 | 作用 |
|---|---|
| DiligentCore | RHI：D3D12 / Vulkan |
| Friflo.Engine.ECS | ECS 框架 |
| SlangShaderSharp | Slang C# 绑定 |
| meshoptimizer | Mesh cluster 化 + 简化 |
| FlatSharp | FlatBuffer 序列化 |
| Silk.NET | 窗口 + 输入 |
| ImGui.NET | Debug UI |

---

## 3. Verified Current Behavior

### 3.1 核心渲染管线流程

```text
Upload Globals
  -> BVH Traverse
  -> 2-Phase HiZ Cull
  -> DeformBin / RasterBin
  -> SW/HW Raster
  -> DepthMerge
  -> ShadeBin
  -> MaterialShade
  -> Output
```

### 3.2 三层结构

| 层级 | 当前代码 | 状态 |
|---|---|---|
| RenderPass | `ClusterCullPass`, `ClusterSWRasterPass`, `ClusterBinningPass` 等 | `stable` |
| Stage（静态函数） | `ClusterTraverseStage`, `ClusterRasterBinStage`, `ClusterHiZStage`, `ClusterShade` 等 | `stable` |
| Pipeline / Feature | `ClusterPipeline.cs` | `current-entry` |

### 3.3 材质系统

- `Material`：瘦材质对象，持有 `ShaderParamBag` 和唯一 `Entity`
- `MaterialSystem`：全局材质 `EntityStore`，负责材质实体生命周期
- `MaterialRef`：Entity 回指 `Material`，供管线读取 `Params`
- `BinQueue` / `BinSpace`：基于 `Entity` 做 `BinGroup` 查询、动态 region 和 layout
- `MaterialSlotBuffer`：GPU 侧 `ushort[]` SOA 布局
- `ShaderAsset.Metadata.MaterialBindings` + `EntryPointAttributes` 已接入资源布局和组件 authoring 路径

### 3.4 GPU 数据结构

| 结构 | 当前状态 |
|---|---|
| `GPUCluster` | 64B |
| `GpuInstanceHeader` | `BVHRootIndex + MaterialSlotOffset + MetadataOffset + MetadataCount + BoundsExpansion` |
| `ClusterShade` | 统一编排 ShadeBin + MaterialShade |

### 3.5 Dual-Signature 绑定

- **Sig0**（BindingIndex=0）：管线全局资源，per-pass SRB
- **Sig1**（BindingIndex=1）：由 `MaterialRef.Owner.Params` + shader metadata 推导，per-material SRB
- `ShadePSOGroup.ComputeShaderGroups()` 已有纯 CPU 测试
- Sig1 cache key、descriptor 构建和 cache reuse 已有直接测试

---

## 4. Current Gaps And Next Priorities

Phase 0、Phase 1、Phase 2 的当前基线工作已完成，当前剩余问题主要转为中长期维护与新系统补全：

- `src/SomeEngine.Runtime/Program.cs` 仍然过大且无测试
- 缺少专门的 Dual-Signature 设计文档
- `docs/assets/asset_identity.md` 仍保留部分 pre-ECS material identity 叙述
- 光照 / page streaming / tessellation 仍处于下一阶段
- 编辑器 / 物理 / 动画 / UI 仍未真正落地

---

## 5. Evolution Principles

1. **以当前代码 + 当前测试为准**：文档与实现冲突时，优先同步到现状
2. **Stage 无状态**：Stage 是纯组合函数，状态归 Pipeline 或 pass 持有
3. **Render Graph 管理资源状态**：避免手动 barrier
4. **Span API 优先**：避免 `unsafe`
5. **先 hardening 再前推功能**：基线未稳时先做 corrective；当前已可进入下一阶段

---

## 6. Implementation Phases

### Phase 0: Baseline & Documentation
- 状态：`DONE`
- 文档基线、tracker、debt、onboarding、batch artifacts 已接入

### Phase 1: Core Hardening
- 状态：`DONE`
- ECS 测试同步点、Sig1 cache、DeformCache 资源级镜像测试均已补齐

### Phase 2: Documentation Coverage
- 状态：`DONE`
- ECS / QVVS / Source Generator / VRB / 资产管线文档已补齐

### Phase 3: Rendering Features
- 状态：`CURRENT`
- material 主线：`TASK-304`（Material ECS 重构）已完成
- 光照系统强化
- Page 流式加载
- HW / SW 双路径继续稳定化

### Phase 4: Engine Systems
- 动画系统
- 物理系统
- 编辑器
- UI 系统

---

## 7. References

- [文档索引](README.md)
- [渲染架构总览](rendering/overview.md)
- [Cluster Pipeline](rendering/cluster_pipeline.md)
- [光栅化](rendering/rasterization.md)
- [Compute Shade Pipeline](rendering/shading_pipeline.md)
- [材质架构](materials/architecture.md)
- [资产管线总览](assets/pipeline_overview.md)
- [ECS 设计](core/ecs_design.md)
- [QVVS 坐标系统](core/qvvs.md)
