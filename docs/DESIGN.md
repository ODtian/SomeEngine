# SomeEngine 设计文档

> **定位：** 基于当前代码状态的顶层架构总览，引用而非替代各领域文档。
>
> **Date:** 2026-04-03

---

## 1. Project Goal

全 C# ECS 游戏引擎，核心聚焦 GPU-Driven Cluster Rendering（Nanite-like）。

### 当前边界
- ✅ GPU 渲染管线（BVH → HiZ → SW/HW Raster → Compute Shade）
- ✅ 材质系统（MaterialPass + MaterialItems + SlotBuffer）
- ✅ 资产管线（GUID Manifest + Scanner + Validator + Workspace）
- ✅ Slang 着色器集成（编译 + 反射 + 依赖追踪）
- ✅ ECS / QVVS / Job System 基础能力
- 📋 光照系统（PBR directional light 已有，无 point/area/IBL/shadow map）
- 📋 流式加载（框架已有 `PageFaults` / `PageStream`，Page 管理未完善）
- ❌ 编辑器 / 物理 / 动画 / UI / 粒子

### 不解决什么
- 不做网络 / 音频 / 脚本系统
- 不做移动端优化（当前仅 D3D12 / Vulkan）

详见 [`docs/goal.md`](goal.md)

---

## 2. Current Architecture Snapshot

### 模块分层

```text
Runtime (RuntimeApp.cs)
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
| Pass | `BvhPatchPass`, `ClusterTraversePass`, `RasterBinPass`, `ClusterDrawPass` 等 | `stable` |
| Stage（struct 编排器） | `ClusterSceneStage`, `ClusterRasterStage`, `ClusterShadeStage`, `ClusterOutputStage` | `stable` |
| Pipeline / Feature | `ClusterPipeline.cs` | `current-entry` |

### 3.3 材质系统

- `MeshAsset`：只保留几何和 region 元数据，不再直接引用具体 `Material`
- `MeshMaterialBindings`：当前 ECS authoring 数据，数组下标就是 mesh-local material slot
- `MaterialAsset`：单一材质资产，加载后生成运行时 `Material`
- `Material`：运行时材质容器，持有直接材质字段和 `MaterialPass[]`
- `RenderWorld`：extract 阶段只提供 mesh/material handle 与 instance dirty state
- `MaterialItems`：解释 `MaterialPass.Target`，维护 `MaterialBin`、SlotBuffer、material binding 和 scalar region
- `ClusterSlotBuffer`：Cluster pipeline 内部派生数据，用于把 mesh-local material slot 映射到 raster/shade/deform bin
- `ShaderAsset.Metadata.MaterialBindings` + `EntryPointAttributes` 已接入资源布局和 pass/component authoring 路径
- 通用的 ECS authoring 设计见 [`docs/core/ecs_authoring.md`](core/ecs_authoring.md)
- 材质作为其中一个实例，见 [`docs/materials/authoring_system.md`](materials/authoring_system.md)

### 3.4 GPU 数据结构

| 结构 | 当前状态 |
|---|---|
| `GPUCluster` | 88B |
| `GpuInstanceHeader` | 通过 `InstanceHeaderLayout` 注册字段写入，不硬编码 header 字段 |
| `ClusterShadeStage` | 统一编排 ShadeBin + MaterialShade |

### 3.5 资源绑定与 PipelineState

- PipelineState 由 `PipelineCache` 通过完整 `ComputeState` / `GraphicsState` key 统一创建、预热和释放
- 资源绑定由 `ShaderBindingTable` / `BindingKey` 显式描述，PipelineState key 覆盖 binding layout、shader entry、material state、render target state 等影响项
- PipelineState 生命周期归 `PipelineCache`；cluster 内部保存 `PipelineTicket`，执行时解析为 `PipelineHandle`
- Material binding 由 `MaterialItems` dirty/build 时解析成 `MaterialBindings`，pass 不反查 `AssetStore` / `Material`

---

## 4. Current Gaps And Next Priorities

当前基线工作已完成，剩余问题主要转为中长期维护与新系统补全：

- `src/SomeEngine.Runtime/RuntimeApp.cs` 仍然过大且无测试
- 光照 / page streaming / tessellation 仍待补全
- 编辑器 / 物理 / 动画 / UI 仍未真正落地

---

## 5. Evolution Principles

1. **以当前代码 + 当前测试为准**：文档与实现冲突时，优先同步到现状
2. **Stage 无状态**：Stage 是纯组合函数，状态归 Pipeline 或 pass 持有
3. **Render Graph 管理资源状态**：避免手动 barrier
4. **Span API 优先**：避免 `unsafe`
5. **Hardening 优先于功能前推**：基线未稳时优先 corrective；当前可继续补全新系统

---

## 6. Implementation Areas

### Baseline & Documentation
- 状态：`DONE`
- 文档基线、tracker、debt、onboarding、batch artifacts 已接入

### Core Hardening
- 状态：`DONE`
- ECS 测试同步点、DeformCache 资源级镜像测试均已补齐
- PipelineState / binding 简化：生产路径使用 `PipelineTicket`、`ShaderBindings`、`PassBindings`，不再依赖旧全局 pipeline/binding owner

### Documentation Coverage
- 状态：`DONE`
- ECS / QVVS / Source Generator / VRB / 资产管线文档已补齐

### Rendering Features
- 状态：`CURRENT`
- material 主线：`TASK-304`（Material ECS 重构）已完成
- 光照系统强化
- Page 流式加载
- HW / SW 双路径继续稳定化

### Engine Systems
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
