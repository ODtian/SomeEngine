# SomeEngine 文档索引

## 总览

- [引擎目标](goal.md) — 引擎总目标与功能规划
- [项目结构](project_structure.md) — 代码目录结构

## 核心模块 (`core/`)

- [Job System](core/job_system.md) — 外部 SomeJob 集成契约
- [ECS 设计](core/ecs_design.md) — friflo ECS 集成、Component/System 设计
- [ECS Authoring](core/ecs_authoring.md) — 通用的 `authoring entity -> runtime entity -> extractor` 模型
- [QVVS 坐标系统](core/qvvs.md) — Quaternion-Vector-Vector-Scalar 变换表示

## 渲染管线 (`rendering/`)

- [架构总览](rendering/overview.md) — GPU-Driven Cluster Rendering 整体架构
- [Render Graph](rendering/render_graph.md) — 类型体系、自动 barrier、DCE、拓扑排序、Placed aliasing
- [Cluster Pipeline](rendering/cluster_pipeline.md) — 删除旧 cluster slot/bin/run/baker 后保留 SlotBuffer 与 binning 算法的边界
- [Cluster Material Contract](rendering/cluster_material_contract.md) — `MaterialPass.Target`、cluster target、PipelineState 和 material/bin runtime 命名结论
- [RenderWorld 与 Pipeline Reference](rendering/render_world_pipeline_reference.md) — RenderWorld、AssetStore、Pipeline/Stage/Pass、PipelineCache/BindSet owner 的当前参考
- [RenderWorld 与 Pipeline 重构文档](rendering/render_world_pipeline_refactor.md) — 运行时资产、Material、MaterialPass、SlotBuffer、dirty 生命周期的决策记录
- [HiZ 剔除](rendering/hiz_culling.md) — 2-Phase Occlusion Culling 详细设计
- [光栅化](rendering/rasterization.md) — SW/HW 双路径、WaveQueue、可编程光栅
- [VRB](rendering/vrb.md) — Variable Rate Binning 顶点去重批处理
- [着色管线](rendering/shading_pipeline.md) — 删除旧 shade slot/run 后保留 pipeline SlotBuffer 与 binning 的边界
- [Feature 用例](rendering/feature_use_cases.md) — Frame output、graph resource sharing 与 Feature 集成边界
- [降级策略](rendering/degradation_strategies.md) — SW/HW 深度、变形缓存、动画 BVH、Binning 耦合分析
- [BVH 剔除设计](rendering/cluster_bvh_culling.md) — Cluster BVH 结构设计
- SW 光栅子文档：[`rendering/sw_raster/`](rendering/sw_raster/)

## RHI (`rhi/`)

- [RHI Design Baseline](rhi/design_baseline.md) — 独立 RHI 的语料筛选、架构结论、API 冻结前置条件
- [RHI API Spec](rhi/api_spec.md) — 独立 RHI core API 的实现目标与冻结门槛
- [RHI Advanced Capability API Decisions](rhi/advanced_capability_api_decisions.md) — indirect、bindless、dynamic offset、RT、mesh shader、pipeline cache、HDR 等现代能力的候选方案与最终 API 形状

## 材质系统 (`materials/`)

- [材质架构 v10](materials/architecture.md) — Material ECS、BinGroup 动态 region、ClusterRender authoring
- [Material In ECS Authoring](materials/authoring_system.md) — 材质如何作为 ECS 通用 authoring 的一个具体实例
- [Nanite Binning 参考](materials/nanite_binning_reference.md) — Nanite Material Range 机制分析
- [Nanite Shade 参考](materials/nanite_shade_reference.md) — UE5 Compute Shade Binning 分析

## 资产管线 (`assets/`)

- [Shader 集成](assets/shader_integration.md) — Slang 编译管线
- [资产身份追踪](assets/asset_identity.md) — GUID、Meta、Manifest 与运行时 authoring 的事实来源
- [资产管线总览](assets/pipeline_overview.md) — Importer/Meta/Manifest/Workspace 全流程

## 远期计划 (`future/`)

- [动画系统](future/animation_system.md)
- [物理系统](future/physics_system.md)
- [Tessellation](future/tessellation.md) — Split→Dice 细分方案
- [Stencil/ClipMask](future/stencil.md) — 软光栅下的几何集合操作
- [Page 流式加载](future/page_streaming.md)
- UI 系统 — *待编写*

## 归档 (`archive/`)

已被新版替代的旧文档，保留供参考。
