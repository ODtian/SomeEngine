# DEBT-TRACKER

## P1 — 阻断型

| ID | Area | Source | Description | Status |
|---|---|---|---|---|
| DEBT-013 | ECS Transform | test failure | `TransformSystemTests.TestRotation` 失败，根因是测试未等待 job completion | DONE (TASK-103) |
| DEBT-007 | 资源绑定 | BATCH-08 | ~~Sig1 cache key / descriptor 构建~~ dual-sig 已删除（BATCH-08），原测试随架构删除 | DONE (BATCH-08) |

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
| DEBT-016 | pipeline_overview.md | 文档过期 | `IAssetTypeHandler` / `AssetTypeRegistry` 段落残留 | DONE (BATCH-07c review) |
| DEBT-017 | ShaderParamBag | BATCH-08 review | Dynamic 变量要求非 null 绑定。当 texture view 为 null 时需绑 fallback（`default_white/default_normal/default_arm`）。BATCH-11 增加 renderer-owned fallback resources 与 `ShaderParamBag.ApplyFallbacks`。 | DONE (TASK-311d) |
| DEBT-018 | RenderGraph tests | rendergraph review | `RenderWorldRefactorTests` / `RenderGraphRhiTests` 仍通过 source-string 断言和私有反射钉死 executor forwarding 细节，后续 owner flatten / cleanup 容易被测试噪声阻塞。 | OPEN |
| DEBT-019 | BindingIndex | rendergraph review | `BindingIndex` 的 `Layout/Texture/Buffer` 返回值已改为稳定数组快照，不再依赖共享 `_matches` scratch list 和调用方即时消费的隐含契约。 | DONE |
| DEBT-020 | RenderGraph features | rendergraph review | `IRenderFeature` 自动录制在 `BeginFrame` record 回调前执行，和 `RenderGraphBlackboard` producer/consumer 顺序不匹配；消费型 feature 需要显式录制顺序或 producer/consumer 生命周期。 | OPEN |
| DEBT-021 | RenderGraph setup replay | rendergraph review | pass setup 在 declaration rebuild 时会重放用户 delegate；应改为初次 setup 生成不可变 declaration record，或用运行时校验确保 setup 可重入且声明稳定。 | OPEN |
| DEBT-022 | Cluster binding contract | rendergraph review | shader binding name 同时承担 layout 过滤、资源解析、访问分类和业务角色分类，setup 声明与 execute binding 双写；需要单一 pass binding contract 生成 graph edge 与 binding set。 | OPEN |
| DEBT-023 | Compute chain visibility | rendergraph review | `RasterBinPass` / `ShadeBinPass` 等在单个 graph pass 内实现多 pipeline chain、手工 barrier 和 use-union，图无法看到真实 step 依赖；需要 graph-visible compute sequence/subpass contract 或拆成明确 graph pass。 | OPEN |
| DEBT-024 | ResourceLifetime | rendergraph review | `ResourceLifetime.Transient` 是公开概念但目前与 `Pooled` 基本同池复用；texture creation 已补对等 lifetime overload，但仍需要删除、改名或实现独立 transient 语义。 | PARTIAL |
| DEBT-025 | RenderGraph ownership | rendergraph review | `RenderGraph` 仍通过 partial 聚合 pass/resource registry、view/binding cache、alias/pool、pending frame retirement 和 executor forwarding；需要把这些生命周期不变量收口到职责明确的内部组件。 | OPEN |
| DEBT-026 | Binding taxonomy | rendergraph review | Slang importer、schema enum、RHI `BindingType`、`BindRules`、`PassBindings` 和 validation 重复维护 binding 分类，且 schema 到 runtime 依赖 ordinal cast；需要单一转换/校验入口。 | OPEN |
| DEBT-027 | Material external bindings | rendergraph review | material/fallback binding paths can pass long-lived raw RHI texture/buffer views into `BindingResourceDesc`; these resources need an explicit graph import/registration contract or graph-owned handles before they can be validated uniformly. | OPEN |
| DEBT-028 | CompiledGraph boundary | rendergraph review | `CompiledGraph` still carries declaration replay output, compile result, and device-preparation mutation points; declaration graph, compiled graph, device-dependent preparation, and frame submission state need harder owner boundaries. | OPEN |
| DEBT-029 | Queue execution policy | rendergraph review | queue batches are compiled, but parallel command recording is disabled in code while docs describe it as possible; thread-safety of command-list creation, view/binding caches, timestamps, and context state must be proven before enabling or documented as disabled. | OPEN |

## 统计

- **DONE**: 16
- **OPEN / PARTIAL**: 13
- **当前测试基线（2026-05-05）**: `225 passed, 0 failed, 0 skipped`
