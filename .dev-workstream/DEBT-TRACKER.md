# DEBT-TRACKER

## P1 — 阻断型

| ID | Area | Source | Description | Status |
|---|---|---|---|---|
| DEBT-013 | ECS Transform | test failure | `TransformSystemTests.TestRotation` 失败，根因是测试未等待 job completion | DONE (TASK-103) |
| DEBT-007 | Dual-Signature | review 发现 | Sig1 cache key / descriptor 构建 / cache reuse 已补直接测试 | DONE (TASK-101) |

## P2 — 近期修

| ID | Area | Source | Description | Status |
|---|---|---|---|---|
| DEBT-002 | Program.cs | 架构问题 | 53KB 单文件，引擎启动/场景/输入/Debug 全在一处，零测试 | OPEN |
| DEBT-008 | DeformCache | 测试缺失 | `CacheAllocCounter` / `CacheOffsets` / cached-path 边界已补镜像测试 | DONE (TASK-102) |
| DEBT-004 | cluster_pipeline.md | 文档过时 | GPUCluster 48B→64B / MaterialID→MaterialSlotOffset / Stage 10→9 | DONE (TASK-001) |
| DEBT-005 | materials/architecture.md | 文档过时 | `struct MaterialSlot` 残留，已改为 SOA `ushort[]` | DONE (TASK-002) |
| DEBT-006 | material_pipeline_full_chain.md | 文档归档 | 未实现的 friflo Entity 材质模型 | DONE (TASK-003) |
| DEBT-010 | shading_pipeline.md | 文档过时 | `ClusterShade` 编排与 PSO 分组现状未同步 | DONE (TASK-004) |

## P3 — 触及时修

| ID | Area | Source | Description | Status |
|---|---|---|---|---|
| DEBT-003 | 残留文件 | 代码卫生 | `ClusterRenderPass.cs.bak` 80KB | DONE (TASK-007) |
| DEBT-009 | 文档缺失 | 文档缺失 | ECS/QVVS/SourceGen/VRB 无独立文档 | DONE (TASK-201) |
| DEBT-011 | rasterization.md | 文档过时 | DeformedBuffer→DeformCache 名称偏差 | DONE (TASK-005) |
| DEBT-012 | overview.md | 文档过时 | 当前 Queue-Driven 与未来 Persistent Threads 未区分 | DONE (TASK-006) |
| DEBT-014 | sw_raster docs | 文档漂移 | `docs/rendering/sw_raster/sw_raster.md` 旧的 `DeformedBuffer` 命名已同步 | DONE |
| DEBT-001 | ClusterRenderFeature | 废弃代码 | 91KB 废弃文件 | DONE (TASK-009) |
| DEBT-015 | asset_identity.md | 文档漂移 | `docs/assets/asset_identity.md` 仍保留 pre-ECS `MaterialPass / MaterialRegistry` material identity 叙述 | OPEN |

## 统计

- **DONE**: 13
- **OPEN / PARTIAL**: 2
- **当前测试基线（2026-04-03）**: `124 passed, 0 failed, 1 skipped`
