# TASK-DEFINITIONS

> 详细任务定义以 `docs/TASK-DETAIL.md` 为准。  
> 本文件用于 batch / report / review 快速交叉引用当前活跃任务。

## Phase 3

| ID | Title | Summary | Detail |
|---|---|---|---|
| TASK-301 | 光照系统强化 | 扩展当前 compute shading 到更完整的灯光能力 | `docs/TASK-DETAIL.md#task-301-光照系统强化` |
| TASK-302 | Page 流式加载 | 为 `ClusterStreamer` 补预估、优先级、预取等实际 streaming 能力 | `docs/TASK-DETAIL.md#task-302-page-流式加载` |
| TASK-303 | Tessellation | 推进 split/dice 动态细分路径 | `docs/TASK-DETAIL.md#task-303-tessellation` |
| TASK-311 | 统一 FrameTarget 与 History 资源系统 | 标准 target 与用户 target 共用同一个 registry、extraction 与 history 生命周期 | `docs/TASK-DETAIL.md#task-311-统一-frametarget-与-history-资源系统` |
| TASK-311a | Unified FrameTargetRegistry | 通过完全相同的 API 声明标准和自定义 texture/buffer frame target | `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md#task-1---task-311a-unified-frametargetregistry` |
| TASK-311b | RenderGraph 资源提取原语 | 增加 explicit extraction，使 graph-created resource 可成为 external/history resource | `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md#task-2---task-311b-rendergraph-resource-extraction-primitives` |
| TASK-311c | History 生命周期集成与 HiZ 迁移 | 把 HiZ 与自定义 history target 迁到统一 target/history 模型上 | `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md#task-3---task-311c-history-lifetime-integration-and-hiz-migration` |
| TASK-311d | Material fallback 资源绑定 | 为缺失 material 资源槽绑定 deterministic renderer-owned fallback，关闭 DEBT-017 | `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md#task-4---task-311d-material-fallback-resource-binding` |
| TASK-311e | Runtime / Pipeline 采用统一 FrameTargetRegistry | Runtime frame setup 与 ClusterPipeline 通过统一 registry 解析 SceneColor/Depth/HiZ/custom targets | `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md#task-5---task-311e-runtime-and-pipeline-adoption` |
| TASK-304 | Material ECS 重构 | 删除 MaterialPass/TagStore/MaterialRegistry，迁移到 friflo EntityStore + 瘦 Material (1:1 Entity) + IComponentAuthor + BinGroup 动态 region。设计见 architecture.md v10 | `docs/materials/architecture.md` |
| TASK-307 | 资产管线破坏性重构 | 100% 破坏性重写 asset pipeline 框架层：删 AssetId、4 接口、5 实现类、9 Report 类，替换为 IAsset + AssetDatabase + AssetTypeRegistry，≤1000 行。全项目消费处迁移 | `.dev-workstream/batches/BATCH-07-INSTRUCTIONS.md` |
| TASK-309 | Asset + Material RenderWorld 完整改造 | BATCH-09 与 BATCH-09b 已共同完成 RenderWorld 改造收敛：mesh-local material linear table 语义落地，region-centric bridge 删除，frame-path 约束提升为 zero-GC + no-Dictionary | `.dev-workstream/batches/BATCH-09b-INSTRUCTIONS.md` |
| TASK-309f | Mesh local material 线性表语义收敛 | 把 `region` 从 cluster runtime 语义中移除，收敛到稳定 mesh-local material linear table / local material slot 模型 | `docs/TASK-DETAIL.md#task-309f-mesh-local-material-table-semantics-cleanup` |
| TASK-309g | RenderWorld extract local-slot 重构 | RenderWorld 只展开 `(source entity, local material slot, material pass)` 提交事实，不再为 region 映射保留桥接结构 | `docs/TASK-DETAIL.md#task-309g-renderworld-extract-local-slot-rework` |
| TASK-309h | Cluster prepare zero-GC slot folding | 删除 `RenderWorldMaterialSlotSynchronizer` / `ClusterPipelineSlotBindingBuilder`，把 slot folding 收口到 cluster prepare，并要求每帧 0 GC、禁止 Dictionary | `docs/TASK-DETAIL.md#task-309h-cluster-prepare-zero-gc-slot-folding` |
| TASK-309i | Host/Test/Docs/Legacy 清理与守护 | 同步 Runtime/Editor/Tests/Docs 到新语义，并补上 allocation regression / hot-path 守护，形成 batch9b 完成态 | `docs/TASK-DETAIL.md#task-309i-host-test-docs-legacy-cleanup-and-guardrails` |
