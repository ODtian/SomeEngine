# TASK-DEFINITIONS

> 详细任务定义以 `docs/TASK-DETAIL.md` 为准。  
> 本文件用于 batch / report / review 快速交叉引用当前活跃任务。

## Phase 3

| ID | Title | Summary | Detail |
|---|---|---|---|
| TASK-301 | 光照系统强化 | 扩展当前 compute shading 到更完整的灯光能力 | `docs/TASK-DETAIL.md#task-301-光照系统强化` |
| TASK-302 | Page 流式加载 | 为 `ClusterStreamer` 补预估、优先级、预取等实际 streaming 能力 | `docs/TASK-DETAIL.md#task-302-page-流式加载` |
| TASK-303 | Tessellation | 推进 split/dice 动态细分路径 | `docs/TASK-DETAIL.md#task-303-tessellation` |
| TASK-304 | Material ECS 重构 | 删除 MaterialPass/TagStore/MaterialRegistry，迁移到 friflo EntityStore + 瘦 Material (1:1 Entity) + IComponentAuthor + BinGroup 动态 region。设计见 architecture.md v10 | `docs/materials/architecture.md` |
| TASK-307 | 资产管线破坏性重构 | 100% 破坏性重写 asset pipeline 框架层：删 AssetId、4 接口、5 实现类、9 Report 类，替换为 IAsset + AssetDatabase + AssetTypeRegistry，≤1000 行。全项目消费处迁移 | `.dev-workstream/batches/BATCH-07-INSTRUCTIONS.md` |
