# SomeEngine 文档索引

## 总览

- [引擎目标](goal.md) — 引擎总目标与功能规划
- [项目结构](project_structure.md) — 代码目录结构

## 核心模块 (`core/`)

- [Job System](core/job_system.md) — 高性能零 GC Job 调度系统
- [ECS 设计](core/ecs_design.md) — friflo ECS 集成、Component/System 设计
- [ECS Authoring](core/ecs_authoring.md) — 通用的 `authoring entity -> runtime entity -> extractor` 模型
- [QVVS 坐标系统](core/qvvs.md) — Quaternion-Vector-Vector-Scalar 变换表示

## 渲染管线 (`rendering/`)

- [架构总览](rendering/overview.md) — GPU-Driven Cluster Rendering 整体架构
- [Render Graph](rendering/render_graph.md) — 类型体系、自动 barrier、DCE、拓扑排序、Placed aliasing
- [Cluster Pipeline](rendering/cluster_pipeline.md) — BVH 遍历、2-Phase HiZ、Stage 编排
- [HiZ 剔除](rendering/hiz_culling.md) — 2-Phase Occlusion Culling 详细设计
- [光栅化](rendering/rasterization.md) — SW/HW 双路径、WaveQueue、可编程光栅
- [VRB](rendering/vrb.md) — Variable Rate Binning 顶点去重批处理
- [着色管线](rendering/shading_pipeline.md) — Compute Shade Binning、Material Dispatch
- [Feature 用例](rendering/feature_use_cases.md) — Stage 复用、BinSpace/BinGroup 组织与 Feature 模式参考
- [降级策略](rendering/degradation_strategies.md) — SW/HW 深度、变形缓存、动画 BVH、Binning 耦合分析
- [BVH 剔除设计](rendering/cluster_bvh_culling.md) — Cluster BVH 结构设计
- SW 光栅子文档：[`rendering/sw_raster/`](rendering/sw_raster/)

## 材质系统 (`materials/`)

- [材质架构 v10](materials/architecture.md) — Material ECS、BinGroup 动态 region、ClusterRender authoring
- [Material In ECS Authoring](materials/authoring_system.md) — 材质如何作为 ECS 通用 authoring 的一个具体实例
- [Nanite Binning 参考](materials/nanite_binning_reference.md) — Nanite Material Range 机制分析
- [Nanite Shade 参考](materials/nanite_shade_reference.md) — UE5 Compute Shade Binning 分析

## 资产管线 (`assets/`)

- [Shader 集成](assets/shader_integration.md) — Slang 编译管线
- [资产身份追踪](assets/asset_identity.md) — GUID、Meta、Manifest 与运行时 authoring 的事实来源
- [资产管线总览](assets/pipeline_overview.md) — Importer/Meta/Manifest/Workspace 全流程

## RHI (`rhi/`)

- [SRB 绑定策略](rhi/srb_binding_strategy.md) — SRB 管理方案

## 远期计划 (`future/`)

- [动画系统](future/animation_system.md)
- [物理系统](future/physics_system.md)
- [Tessellation](future/tessellation.md) — Split→Dice 细分方案
- [Stencil/ClipMask](future/stencil.md) — 软光栅下的几何集合操作
- [Page 流式加载](future/page_streaming.md)
- UI 系统 — *待编写*

## 归档 (`archive/`)

已被新版替代的旧文档，保留供参考。
