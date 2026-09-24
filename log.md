# Development Log

## [2026-06-21] BATCH-31: RenderGraph Contract Retrofit
- **Explicit Pass Access Contract**: `RenderGraphAccess` 枚举改为 `ReadOnly`/`WriteOnly`/`ReadWrite`；旧的 `Access(...)` 方法从公共 builder 表面删除，`Use(...)` 降级为 `internal`；反射 `*ReadWrite` 绑定不再塌缩为纯写。
- **Dependency, Culling, And Queue Closure**: 编译输出最终化重连 resolves、queue batches、queue waits、frame sync；culling 确定性地跟踪保留/裁剪原因（`PassKeepReasons`/`PassCullReasons`）；WriteOnly 声明正确消费被覆写资源的需求。
- **Resource Class, Lifetime, And History Semantics**: Imported/Transient/Extracted 资源合约完整；Imported 写保护 + 防别名；Exported 防别名；State trust 按来源/复用场景细粒度设置；跨帧历史走 Extract→Import 模式。
- **Alias Allocation And Handoff Safety**: `CanAlias()` 检查 Live/Exported/Reusable/Imported/DeviceLocal/BufferInitialData；贪心 first-fit 分配按 FirstPass 排序；alias handoff 插入 `AliasingBarrier` 使前内容 undefined；独立 async queue batch 间 alias 生命周期已 padding。
- **Barrier, Queue, And Execution Backend**: WriteOnly UAV 跳过 false prior dependency（`RequiresUavDependency` 中 `!IsWriteOnly(currentAccess)`）；执行时验证覆盖声明/access/state/range 四重检查；`PassBindings`/`PassParameters` 跨帧拒绝；新增 `IDevice.TryGetTextureViewOwner/BufferViewOwner`。
- **In-Repo Migration**: `ShadeBinPass` 显式分类 `BinIndirectArgs`/`PixelCoordBuffer` 为 `WriteOnly`；`ClusterDeformPass` 的 DeformCache/CacheOffsets 使用 `builder.Write()`；行为测试覆盖 binding-step access 分类。
- **验证**: RenderGraph 专项 198 通过；广泛渲染特性 169 通过；构建 0 错误。

## [2026-05-10] BATCH-18: Temporal Validation Hardening
- **Temporal validation is now automatable**: Runtime accepts `--temporal-validation`, runs shared presets, warms up each preset, captures backbuffer artifacts, and writes `summary.json`.
- **Shared presets**: Runtime UI and automation now use the same Off / Resolve Only / Jitter + Resolve / Stable History / High Rejection definitions.
- **Deterministic motion**: validation captures use a deterministic non-static camera path so motion-vector/history failures are harder to hide behind a static shot.
- **Artifacts and metrics**: captures are written as uncompressed `.tga`; JSON records preset settings, frame metadata, jitter, and image-diff metrics against the temporal-off baseline.
- **Verification**: focused temporal tests passed 29 / 29; `dotnet build SomeEngine.slnx --no-restore -v minimal -m:1` passed with only the existing `tools/DagVisualizer` NU1903 warning.

## [2026-03-28] BATCH-01: 文档同步与清理
- **Workstream 转换**：完成 Full Conversion，产出 BASELINE-REVIEW / DESIGN / TASK-DETAIL / TASK-TRACKER / DEBT-TRACKER / ONBOARDING / BATCH-01-INSTRUCTIONS
- **文档修正 8 处 DRIFT**：cluster_pipeline.md (GPUCluster 64B / MaterialSlotOffset / 9 Stage)、materials/architecture.md (SOA)、overview.md (BVH 双架构)
- **标注待实现文档**：material_pipeline_full_chain.md、gpu_pipeline_tag_integration_plan.md（合并自 gpu_pipeline_remaining_plan + material_tag_migration_plan）
- **删除废弃代码**：ClusterRenderFeature.cs (91KB) → 类型提取至 ClusterPipelineTypes.cs；ClusterRenderPass.cs.bak (80KB)
- **发现 Pre-existing 错误**：AssetResolverTests.cs / MaterialAssetPipelineTests.cs 有 3 个编译错误（与本次修改无关）


## [2026-03-26] Dual-Signature Shade Pipeline Binding
- **新建 `ShadeSignatures.cs`**：Sig0 全局缓存（12 管线 Dynamic 资源），Sig1 按 shader 反射懒创建（从 `ShaderParamBag.EnumerateResources()` 推导材质资源）。
- **重写 `ShadePSOBuilder.cs`**：PSO 使用 `ResourceSignatures = [Sig0, Sig1]`；SRBs 从 Sig1 创建（仅含材质纹理 + Uniforms）。
- **重写 `ClusterMaterialShadePass.cs`**：分层 Commit — Sig0 per-pass SRB 绑 1 次/PSO group，Sig1 material SRB 绑 N 次/bin。
- **`ShaderParamBag`**：新增 `EnumerateResources()` 方法。
- **`ClusterShadePipelineParams`**：移除 `Uniforms`（已迁移至 Sig1）。

## [2026-03-25] ClusterPipeline 无状态 Stage 重构
- **静态 `ClusterShade`**：合并 ShadeBin + MaterialShade 为无状态 static class。
- **`ShadePSOBuilder`**：抽取 PSO 组构建为纯工具类。
- **`ClusterStageUtils`**：抽取 `AddDynamicUniformPass<T>` 共享逻辑。
- **删除 `ClusterShadeStage.cs`、`ClusterShadeBinStage.cs`**。
- **消除 `useDeformCache` 布尔开关**：全部使用 `RenderGraphHandle.IsValid` 驱动路径选择。

## [2026-03-24] 修复 ShaderParamBag.GetSignatureHash 确定性
- `GetSignatureHash()` 遍历 `Dictionary<string, Entry>` 时顺序不确定，导致相同绑定集的两个 bag 可能产出不同签名 hash → BinQueue 无法正确合并 bin。
- 修复：先按 key 排序（`StringComparer.Ordinal`）再做 FNV-1a 哈希。

## [2026-03-24] IVertexEvaluate 可编程顶点变形接口
- **新建 `vertex_evaluate.slang`**：定义 `IVertexEvaluate`（`associatedtype DeformedVertex` + 实例方法 `evaluate`/`getPosition`）、`IVertexSource`、`InlineSource<T>`、`StaticVertexEval`、`VertexEvalContext`（含 PageHeap/StreamCursor 属性访问）。
- **泛型化 `sw_raster.slang`**：提取 `SWRasterKernel<VS : IVertexSource>` 泛型函数，`CSSWRaster` 入口通过 `InlineSource<StaticVertexEval>` 调用。`LoadClusterGeometry` 扩展输出 `attrOffset` 和 `totalVertexCount`。旧 `FetchAndTransformVertex` 标记 deprecated 保留。
- **编译测试**：新建 `VertexEvaluateCompilationTest.cs`，包含 (1) 默认 `InlineSource<StaticVertexEval>` 路径 (2) 自定义 `WPOVertexEval`（含 `StructuredBuffer<float4>` 资源绑定）路径。3/3 测试通过（含原有 `SWRasterCompilationTest` 回归）。

## [2026-03-23] 修复 ParameterBlock 材质变量绑定问题
- **资源名称扁平化映射**: 修改 `ShaderParamBag.cs`，在匹配不到直接参数名时，自动降级去匹配 Slang 编译器对 `ParameterBlock` 扁平化生成的 `Material_{name}_0` 和 `Material_{name}`，使得 C# 端材质对象（Textures/Samplers）无需关心底层的 Name Mangling 即可正确绑定。
- **Dummy ConstantBuffer 静默验证**: 对于 `ParameterBlock` 中的标量属性所自动生成的 `Material_0` 常量缓冲区，在 `ClusterPipeline` 中引入了一个全局 256 字节 `_dummyMaterialBuffer`。在创建 SRB 时自动以 `AllowOverwrite` 绑定到 `Material_0`，并在析构时安全释放，完美消除了 Diligent Engine 关于该缓冲必须被绑定的报错。
- **无需侵入式修改**: 成功保留了 Slang 相关的 `ParameterBlock` 声明语义与原结构体形式，全面满足用户关于声明方式的要求。

## [2026-03-23] MeshAsset 接入 Manifest 资产管线
- **自动 GUID + Meta**: `MeshAssetSerializer.Save()` 在保存时若 `AssetGuid` 为空则自动生成，并通过 `AssetMetaManager` 自动创建 `.mesh.asset.meta` 文件。
- **后缀统一**: `Program.cs` 中 mesh 文件枚举模式和导入输出路径从 `.mesh` 统一为 `.mesh.asset`，使 `AssetManifestScanner` 的 `LooksLikeMeshPath` 能正确识别。
- 现有 `IcoSphere.mesh` 重命名为 `IcoSphere.mesh.asset`。
## [2026-03-22] 软光栅解除 Dispatch 上限与支持间接分发（Indirect Dispatch）
- **动态派发消除闪烁**: 根除了 `ClusterSWRasterPass.cs` 中 `MaxClustersPerBin = 4096` 的硬编码上限。该限制曾导致高密度丛集环境下（如100lod0球）超额簇被意外截断从而产生严重的成块闪烁。
- **软硬分箱隔离**: 在 `cluster_binning.slang` 借由 GroupShared Memory 对软硬件 `swCount`/`hwCount` 分别执行并行前缀和（Prefix Scan），精准获取各类几何专属的偏移量。
- **突破 D3D11 尺寸限制**: 在分箱阶段统计全局 SW 总请求量并输出至 `BinnedSWDispatchArgs`，由于 `totalSW` 极易超出 65535，统一压平折算为 `DispatchX = min(totalSW, 65535), DispatchY = totalSW / 65535` 的二维按需派发大小空间。
- **着色器维度解包**: 改造 `sw_raster.slang` 线性映射 `GroupID`，配合查表快速定位当前线程所属的 `binIdx`，保证 C# 侧彻底解耦，改用 `DispatchComputeIndirect` 高效流转渲染负载。

## [2026-03-22] Slang 强类型重构 — 消除手动 Offset
- `cluster_structures.slang`：新增 5 个 GPU 结构体（`DispatchArgs`、`DrawInstancedArgs`、`CullDrawArgs`、`BVHDispatchArgs`、`RasterBinEntry`），为 typed `Load<T>/Store<T>` 和 `RWStructuredBuffer<T>` 提供基础。
- `cluster_cull.slang`：`CandidateCount`/`Phase2CandidateCount` → `RWStructuredBuffer<uint>`（支持 `InterlockedAdd(buf[0], ...)`）；`DrawArgs`/`Phase2DrawArgs`/`CandidateArgs` 保持 `RWByteAddressBuffer`（IndirectArgs 绑定要求 Raw），但使用 typed `Load<CullDrawArgs>(0)` / `Store<DispatchArgs>(0, ...)` 替代手动 `Load(4)/Store(8, ...)` 偏移。
- `cluster_binning.slang`：`RasterBinMeta` → `RWStructuredBuffer<RasterBinEntry>`，通过 `InterlockedAdd(RasterBinMeta[binKey].BinSWCount, ...)` 替代 `InterlockedAdd(binKey*24+8, ...)`；`DrawArgs`/`ClusterReadOffsetArgs` 保持 Raw + typed `Load<CullDrawArgs>(0)`；`BinningDispatchArgs`/`BinnedDrawArgs`/`BinnedHWDrawArgs` 保持 Raw + typed `Store<DispatchArgs/DrawInstancedArgs>()`。
- `cluster_bvh_traverse.slang`：`CandidateCount` → `RWStructuredBuffer<uint>`；`NextDispatchArgs`/`CurrentDispatchArgs` 保持 Raw + typed `Load/Store<BVHDispatchArgs>()`。
- `cluster_draw.slang`：`VisibleClusterMeta` 保持 `ByteAddressBuffer` + typed `Load<CullDrawArgs>(0).SWCount`；增加 `#include "cluster_structures.slang"`。
- `sw_raster.slang`：`RasterBinMeta` → `StructuredBuffer<RasterBinEntry>`，通过 `.ClusterOffset`/`.BinSWCount` 语义访问替代 `Load(addr+0/addr+8)`。
- `cluster_shade_binning.slang`：`BinIndirectArgs.Store<DispatchArgs>(tid*12, ...)` 替代 3 行手动 Store。
- C# 侧同步：`ClusterTraverseStage.cs`（CandidateCount）、`ClusterCullStage.cs`（Phase2CandidateCount）、`ClusterRasterBinStage.cs`（RasterBinMeta 24B stride）、`ClusterRenderFeature.cs`（CandidateCount/Phase2CandidateCount/RasterBinMeta/P2 全部 → `BufferMode.Structured`；RasterBinMeta Size 从 `MaxBins*16` 修正为 `MaxBins*24`）。
- 编译测试通过（`ClusterCullCompilationTest` + `ClusterBinningCompilationTest`，0 errors）。

## [2026-03-22] VRB + Material 溢出管线改造
- **SW 微三角闪烁修复**: 调整 `sw_raster.slang` 中 `SetupTriangle` 的像素包围盒取整公式，去掉额外的 `-1` 偏移，并对像素级插值深度做 `saturate(depth)` 钳制。之前极小三角在屏幕边界附近会因为 bbox/深度量化抖动产生时序性破片闪烁。
- **BVH / HiZ Readback 修复**: `ClusterDebugReadbackPass` 与 `ClusterBVHTraversePass` 的 staging readback 先前都是“本帧先 map 再 copy”，导致调试 UI 永远读到旧值或空值。现在统一改成读取上一次 copy 结果、随后拷贝本帧数据供下一帧消费；同时去掉了 `ClusterBVHTraversePass` 内已失效的 `_readbackOffset` 字段。
- **HW Draw 接线起步**: `ClusterHiZStage.cs` 在 `UseSWRaster=true` 时不再是纯 SW 路径，而是同 phase 先跑 `ClusterSWDraw` 再追加一条 `ClusterDraw`（`UseHWDrawArgs=true`）消费 `BinnedHWDrawArgs`，两者共享同一 `VisBuffer`。目前 HW 仍写真实 `depthTarget`，SW 继续写 `RasterDepth`(`R32_UINT`)；这还不是最终统一深度方案，但已把 HW 调度骨架接上。
- **Raster 输出扩展**: `ClusterRasterOutput` 新增 `RasterDepth`，用于在 hybrid 路径里让 SW phase2 复用 phase1 的 `DepthUAV`；`ClusterSWDrawStage.cs` 已支持复用已有 `VisBuffer/DepthUAV`。
- **重新开启分类**: `cluster_cull.slang` 的 `ENABLE_SW_HW_SPLIT` 已重新打开；`sw_raster.slang` 读取 `RasterBinMeta` 的 bin count 偏移同步改为新的 24B layout（`BinSWCount` at +8）。
- **Binning SRB 修复**: 修正 `ClusterBinningPass.cs` 中 `BindSRB(...)` 调用参数错位。`Count/Scatter` 漏传了 `dispatchArgs` 槽位的占位 `null`，导致 `MaterialSlotBuffer` 被错误绑定到 shader 的 `PageHeap` 位置、`PageHeap` 反而未绑定，运行时触发 Diligent 关于 structured/raw view 不匹配和缺失资源绑定的错误。
- **SW/HW Binning 起步**: `cluster_binning.slang` 现支持同时读取 SW/HW 可见区间：`DrawArgs[4]` 作为 swCount、`DrawArgs[8]` 作为 hwCount，按 `ClusterReadOffsetArgs` 解码 Phase1/Phase2 的全局区间。SW 仍按 per-VRB-batch 拆 entry，HW 先按每 cluster 1 entry 进入同一 `BinnedClusterIndexBuffer`。
- **RasterBinMeta 扩展**: per-bin 元数据从 16B 扩到 24B，现保存 `ClusterOffset / BinCapacity / BinSWCount / BinHWCount / SWCursor / HWCursor`，并在 `CSBinningReserve` 同时生成 `BinnedDrawArgs`（SW）与 `BinnedHWDrawArgs`（HW，StartInstance 指向 bin 内 HW 起始）。
- **C# 管线结构同步**: `ClusterRasterBinOutput` 新增 `BinnedHWDrawArgs`；`ClusterRasterBinStage.cs` / `ClusterBinningPass.cs` / 旧 `ClusterRenderFeature.cs` helper 已同步资源创建与传递，便于下一步真正接通 HW draw 调度。
- **测试**: 新增 `ClusterBinningCompilationTest`，当前与 `ClusterCullCompilationTest` / `SWRasterCompilationTest` / `WaveQueueCompilationTest` 共 4 项定向测试通过。
- **Cull Debug**: 临时关闭 `cluster_cull.slang` 的 SW/HW split 分类（`ENABLE_SW_HW_SPLIT = 0`）。当前 binning/draw 仍只消费 SW 区间，分类开启会把大多数 cluster 写到 HW 尾部导致整帧无物体渲染；等第 3 步 SW/HW 双路径 binning 接通后再删掉该硬编码。
- **GPUCluster.cs**: `Pad0` → `MaterialTableOffset`, `Pad1` → `VRBBatchInfo`，保持 64B 布局不变。
- **ClusterBuilder.cs**: 删除贪心 O(n²) `ReorderForVRB` + degenerate padding。新增 `BuildVRBBatches` 线性扫描（ulong bitmask，unique>32 关 batch），输出 packed uint（5 batch tri-counts + batchCount）。重写 `EmitSplitMeshlet` 直接使用原始三角形顺序（保持 meshopt 空间局部性），调用 `BuildVRBBatches` 编码 `VRBBatchInfo`。
- **cluster_structures.slang**: 新增 `VRBBatch` struct + `DecodeVRBBatchCount` / `DecodeVRBBatch` / `DecodeVRBTotalFastTris` / `DecodeTriangleCount` / `IsMaterialSlowPath` 解码函数。
- **Binning 管线**: `BinnedClusterIndexBuffer` 从 `StructuredBuffer<uint>` → `StructuredBuffer<uint2>`（.x=visibleIndex, .y=rangeStart<<16|rangeEnd）。`cluster_binning.slang`、`sw_raster.slang`、`cluster_draw.slang`、`ClusterRenderFeature.cs`、`ClusterRasterBinStage.cs` 全部适配。
- 编译 0 errors。ClusterBuilderTests 通过。SWRasterCompilationTest SPIR-V 通过。

## [2026-03-21] 资产系统扁平 facade API 起步
- 新增 `src/SomeEngine.Assets/AssetCatalog.cs`：落地 `AssetNode`、`IAssetCatalog`、`ManifestAssetCatalog`，把 manifest/source/dependency/referencer 装配成更扁平的编辑器查询视图。
- 新增 `src/SomeEngine.Assets/AssetWorkspace.cs`：落地 `AssetProblem`、`AssetValidationReport`、`IAssetWorkspace`、`ManifestAssetWorkspace`，统一提供 `Validate()`、`PreDeleteCheck()`、`FindImpact()`、`RebuildIndex()` 四类上层入口。
- 继续收敛入口：`IAssetWorkspace` 现同时继承 `IAssetResolver`，`ManifestAssetWorkspace` 直接支持 `Load<T>()` / `ListAssets<T>()`，上层可只拿一个 workspace 服务同时完成查询、校验、删除前检查与资产解析。
- 调整 `src/SomeEngine.Assets/ManifestAssetResolver.cs`：抽出 `ManifestAssetSupport` 共享 manifest 解析逻辑，避免 resolver/workspace 各自复制一套 asset load/list 行为。
- 继续完善 facade 数据模型：在 `src/SomeEngine.Assets/AssetGuid.cs` 新增 `AssetId` 作为上层正式资产 ID 别名；`AssetNode`/`AssetProblem` 改为面向 `AssetId`，并补充 `HasSource`、`HasAsset`、`HasRelatedAsset` 等便捷属性。
- 扩展 `src/SomeEngine.Assets/AssetManifest.cs`：新增 `GetDependencyClosure()` 与 `GetReferencerClosure()`，为后续 impact 分析、递归删除检查、引用树 UI 提供统一底层图遍历能力。
- 扩展 `src/SomeEngine.Assets/AssetCatalog.cs`：新增 `AssetCatalogExtensions.List<TAsset>()`，并保留 `TryGetPath(AssetGuid, ...)` 兼容重载，方便旧 resolver 路径逐步过渡。
- `ManifestAssetWorkspace.FindImpact()` 现改为直接复用 manifest 的 referencer closure，而不再自己维护一套 BFS。
- 新增 `src/SomeEngine.Assets/AssetAnalysisReports.cs`：落地 `AssetImpactReport`、`AssetDeleteReport`，作为更稳定的底层/上层通用分析结果对象，不依赖编辑器存在。
- 继续增强底层索引能力：`AssetManifest` 新增 `TryGetAssetByPath()`、`TryGetSourceGuid()`、`GetTransitiveDependencies()`、`GetTransitiveReferencers()`；`IAssetCatalog` 新增 `TryGetId(assetPath, out AssetId)`。
- `ManifestAssetWorkspace` 新增 `AnalyzeImpact()` 与 `AnalyzeDelete()`，把 direct/transitive 影响与删除阻塞者显式区分，后续 CLI / 调试输出 / UI 都可以直接复用。
- 继续补齐一源多产物：`AssetManifest` 新增 `AssetsBySource`、`GetAssetsBySource()`、`GetAssetsBySourcePath()`、`TryGetAssetBySourceAndSubAssetKey()`；可直接通过 `(SourceGuid, sub_asset_key)` 做稳定反查。
- `IAssetCatalog` / `ManifestAssetCatalog` 新增 source 维度查询：`TryGetSourcePath()`、`TryGetSourceGuid()`、`GetAssetsBySource()`、`GetAssetsBySourcePath()`。
- `AssetAnalysisReports.cs` 新增 `SourceAssetsReport`；`ManifestAssetWorkspace` 新增 `AnalyzeSource(SourceGuid|string)`，把 source 对应的全部产物汇总为统一报告对象。
- 继续补 workflow 真正需要的摘要：`AssetAnalysisReports.cs` 新增 `SourceImportSummary` 与 `AssetDeleteConsequenceReport`，前者汇总 importer / fingerprint / importerVersion / dependency 摘要，后者汇总删除目标的 dangling/orphan 风险。
- `ManifestAssetWorkspace` 新增 `AnalyzeSourceImport(SourceGuid|string)` 与 `AnalyzeDeleteConsequences(AssetId)`；source 摘要当前优先从 `SourceMeta` + `AssetMeta` 读取，不依赖编辑器或额外工具层。
- 继续精细化删除后果分析：`AssetDeleteConsequenceReport` 新增 `DirectDanglingRiskAssets`、`TransitiveDanglingRiskAssets`、`RemovalSet`、`HasDanglingRisk`、`HasOrphanRisk`，把“谁会立即断引用”和“谁在级联删除链上受影响”明确拆开。
- `ManifestAssetWorkspace.AnalyzeDeleteConsequences()` 现会同时输出 direct/transitive dangling 集合、完整 removal set，以及 orphan risk 集合，便于后续 CLI / 调试流程进一步做 force delete 或风险确认。
- 本轮继续补测试：新增 `AnalyzeSourceImport()` 的缺失 source 路径覆盖，以及 `AnalyzeDeleteConsequences()` 的 orphan risk 覆盖，避免当前分析逻辑只在正向 happy path 上有断言。
- 扩展 `src/SomeEngine.Assets/AssetProjectValidator.cs`：新增 `ValidateFlat()`，把既有 `AssetValidationIssue` 映射为上层 facade 使用的 `AssetProblem`，保持旧校验 API 兼容。
- 扩展 `tests/SomeEngine.Tests/Assets/AssetManifestTests.cs` 与 `tests/SomeEngine.Tests/Assets/AssetProjectValidatorTests.cs`：覆盖 catalog 节点投影视图、workspace 影响分析、workspace 直接解析资产、flat validation report 映射。
- 新增 source→assets / sub-asset 反查测试覆盖；定向测试 `AssetManifestTests` + `AssetProjectValidatorTests` + `AssetResolverTests` 共 18 项全部通过。
- 新增 source import 摘要与删除后果分析测试覆盖；定向测试 `AssetManifestTests` + `AssetProjectValidatorTests` + `AssetResolverTests` 共 19 项全部通过。
- 当前定向测试 `AssetManifestTests` + `AssetProjectValidatorTests` + `AssetResolverTests` 共 21 项全部通过。

## [2026-03-21] Manifest 基础设施与运行时 resolver 起步
- 新增 `src/SomeEngine.Assets/AssetManifest.cs`：落地 `source_index.json`、`asset_index.json`、`dependency_graph.json` 三份 manifest 文件的 JSON 读写；补充 `AssetManifestRecord`，统一承载 `AssetGuid`、`SourceGuid`、`SubAssetKey`、路径与类型信息。
- 新增 `src/SomeEngine.Assets/AssetManifestBuilder.cs`：提供最小 manifest 构建器，可登记 source、asset 与依赖关系，并自动去重 dependency graph，作为后续全量扫描 / 增量构建的装配入口。
- 新增 `src/SomeEngine.Assets/ManifestAssetResolver.cs`：基于 manifest 实现 `IAssetResolver`，支持按 `AssetGuid` 加载 `ShaderAsset` / `MaterialAsset` / `MaterialInstanceAsset` / `MeshAsset`，并支持 `ListAssets<T>` 与 `TryGetPath`。
- 新增 `tests/SomeEngine.Tests/Assets/AssetManifestTests.cs`：覆盖 manifest 三索引 roundtrip、`ManifestAssetResolver` 的 shader 加载与列表查询、以及 `AssetManifestBuilder` 依赖去重行为。
- 定向测试 `AssetResolverTests` + `AssetManifestTests` 共 7 项全部通过。

## [2026-03-21] Manifest 全量扫描生成器起步
- 新增 `src/SomeEngine.Assets/AssetManifestScanner.cs`：支持全项目扫描 source `.meta` 与 `*.asset`，自动构建 `AssetManifest`，并可直接输出到 `Library/AssetManifest/`。
- 扫描器当前可识别 `ShaderAsset`、`MaterialAsset`、`MaterialInstanceAsset`、`MeshAsset`，并自动提取 guid 依赖边：
  - `MaterialAsset.PassEntry.shader_guid`
  - `MaterialInstanceAsset.parent_guid`
  - `MeshAsset.default_material_guids`
- 新增 `tests/SomeEngine.Tests/Assets/AssetManifestScannerTests.cs`：覆盖 source/asset 扫描、依赖图生成，以及 `ScanAndSave()` 的 manifest 文件写出。
- 通过 `dotnet test --filter "FullyQualifiedName~AssetManifestTests|FullyQualifiedName~AssetManifestScannerTests|FullyQualifiedName~MemoryAssetResolverTests|FullyQualifiedName~ResolverIntegrationTests"` 验证，9 项定向测试全部通过。

## [2026-03-21] 项目校验器 / orphan / dangling 检测起步
- 新增 `src/SomeEngine.Assets/AssetProjectValidator.cs`：基于 `AssetManifestScanner` 提供最小项目校验入口，当前可产出三类 issue：`OrphanSourceMeta`、`DanglingReference`、`OrphanAsset`。
- 校验逻辑已覆盖：
  - 源 `.meta` 存在但源文件缺失
  - manifest 依赖图中的引用 guid 不存在
  - 没有任何入边的资产（当前按设计先仅报告，不自动删除）
- 新增 `tests/SomeEngine.Tests/Assets/AssetProjectValidatorTests.cs`：覆盖 orphan source meta、dangling shader guid、orphan asset，以及“被引用资产不应误报 orphan”的行为。
- 通过 `dotnet test --filter "FullyQualifiedName~AssetProjectValidatorTests|FullyQualifiedName~AssetManifestScannerTests|FullyQualifiedName~AssetManifestTests"` 验证，7 项定向测试全部通过。

## [2026-03-21] 反向引用图与 PreDeleteCheck 基础能力
- 扩展 `src/SomeEngine.Assets/AssetManifest.cs`：在 manifest 构造阶段自动建立 reverse reference map，新增 `Referencers`、`GetReferencers(AssetGuid)` 与 `PreDeleteCheck(AssetGuid)`。
- `PreDeleteCheck` 当前返回直接引用方列表，作为未来 `IAssetDatabase.PreDeleteCheck` 的最小底层实现。
- 扩展 `tests/SomeEngine.Tests/Assets/AssetManifestTests.cs` 与 `tests/SomeEngine.Tests/Assets/AssetProjectValidatorTests.cs`：覆盖反向引用图构建与 `PreDeleteCheck` 行为。
- 通过 `dotnet test --filter "FullyQualifiedName~AssetManifestTests|FullyQualifiedName~AssetProjectValidatorTests"` 验证，7 项定向测试全部通过。

## [2026-03-21] 资产系统扁平 API 重构方向草案
- 新增 `docs/design/asset_flat_api_direction_20260321.md`。
- 说明了当前资产系统的实际进度、为何复杂感主要来自上层暴露面过多，而不是底层 identity 设计错误。
- 提出保持底层 `source` / `asset` 分离，同时在上层新增 `AssetNode`、`IAssetCatalog`、`IAssetWorkspace` 等扁平 facade 的重构方向。

## [2026-03-21] 资产标识阶段总结文件
- 新增 `docs/design/asset_identity_and_source_tracking_session_summary_20260321.md`。
- 汇总了本轮围绕 `asset_identity_and_source_tracking.md` 已完成的修改、尚未完成的修改，以及从阅读设计稿到逐步落地 `SlangShaderImporter` 依赖追踪的对话推进经历。

## [2026-03-21] WaveQueue — Wave-Level 泛型任务分发
- 新建 `assets/Shaders/wave_queue.slang`：`IWaveTask` 接口（`[mutating] GetTaskCount()` / `[mutating] ExecuteTask(srcLane, localIdx)`）+ `WaveQueue::Distribute<T>` 静态泛型分发器。纯 wave intrinsics（`WavePrefixSum`/`WaveActiveBitOr`/`countbits`/`WaveReadLaneAt`），零 LDS/barrier，boundary bitmask O(1) producer 查找。
- 新建 `tests/SomeEngine.Tests/WaveQueueCompilationTest.cs`：内联 Slang source 实现 trivial `IWaveTask`，通过 `SlangShaderImporter.Import()` 验证编译。SPIR-V 编译通过，DXIL 跳过（测试环境无 DXC）。

## [2026-03-21] SW Raster — 软光栅化 Compute Shader
- 新建 `assets/Shaders/sw_raster.slang`（~320 行）：采用 Nanite `VERT_REUSE_BATCH` 模式，32 threads/group，零 LDS。
  - **Stage 1**: `DeduplicateVertIndexes` 通过 `WaveActiveBitOr`/`FindNthSetBit`/`MaskedBitCount` wave 去重顶点，只变换唯一顶点，`WaveReadLaneAt` 广播 clip-space 位置。
  - **Stage 2**: `SetupTriangle` 8-bit 子像素边方程 + top-left fill rule（匹配 Nanite `NaniteRasterizer.ush`）。
  - **Stage 3**: `PixelRasterTask : IWaveTask` + `WaveQueue::Distribute` 像素分发，InterlockedMax 原子深度测试 + VisBuffer 写入。
- 新建 `tests/SomeEngine.Tests/SWRasterCompilationTest.cs`：SPIR-V 编译通过。
- 新建 `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterSWRasterPass.cs`：compute PSO（static 缓存）+SRB pool，绑定 VisBuffer/DepthUAV 为 UAV，DispatchCompute(MaxClustersPerBin × MaxBins)。
- 新建 `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterSWDrawStage.cs`：无状态 Stage，创建 VisBuffer + DepthUAV (R32_UINT) 纹理、ClearRenderTarget、上传 SWRasterUniforms、调度 ClusterSWRasterPass。返回 `ClusterRasterOutput` 与 HW draw 接口一致。
- 修改 `ClusterHiZStage.cs`：`HiZConfig.UseSWRaster` 开关，Phase1/Phase2 draw 分支路由到 `ClusterSWDraw.AddPasses` 或 `ClusterDraw.AddPasses`。
- 修改 `ClusterPipeline.cs`：新增 `UseSWRaster` 属性，传入 `HiZConfig`（含 `QuantStep`/`QuantOrigin`）。
- `ClusterBuilder.cs`：新增 VRB 断言（每 32 三角形窗口顶点跨度 <64）。

## [2026-03-20] 资产GUID最小闭环（Phase 0 + 1 + 2 起步）
- 新增 `AssetGuid`、`SourceGuid`、`AssetRef<T>`、`IAssetRecord`、`IImportedAsset`、`IShaderAssetRecord` 与 `ImportTraceData`，作为资产标识与导入追踪基础类型。
- 新增 `SourceMetaManager` / `AssetMetaManager`，支持源文件 `.meta` 与 `.asset.meta` 的最小读写闭环。
- 扩展 `shader_asset.fbs`：新增 `asset_guid`、`ImportTrace`、`DependencyEntry`；扩展 `material_asset.fbs`：新增 `asset_guid` 与 `PassEntry.shader_guid`，保留旧字符串字段兼容。
- 改造 `SlangShaderImporter`：旧 `Import(path)` 继续可用，内部自动创建/读取 `.meta`、计算主文件 fingerprint、复用已有 `AssetGuid`、写出 `ImportTrace` 与 `.asset.meta`。
- 改造 `Material` / `MaterialPass` / `MaterialRegistry` / `MaterialAssetLoader`：新增 `Material.AssetGuid`、`ShaderRef`、`ResolvedShader`，`ComputeSignature()` 优先使用 GUID，`MaterialRegistry` 支持按 `AssetGuid` 查找，loader 支持 `shader_guid` 优先、字符串回退。
- 扩展 `material_instance_asset.fbs` 与 `mesh_asset.fbs`：新增 `asset_guid`、`parent_guid`、`default_material_guids`；`MaterialInstanceLoader` 新增按父材质 GUID 解析入口，`MeshMaterialResolver` 支持 material guid 优先、旧字符串槽位回退。
- 新增 `IAssetResolver`、`AssetEntry` 与 `MemoryAssetResolver`，并为 `ShaderAsset` / `MaterialAsset` / `MaterialInstanceAsset` / `MeshAsset` 生成类型补充 `IAssetRecord` 实现；`Material` 也实现 `IAssetRecord`，可被统一注册到内存 resolver。
- `MaterialAssetLoader` / `MaterialInstanceLoader` / `MeshMaterialResolver` 新增可选 `IAssetResolver` 入口，GUID 解析不再只能依赖零散委托。
- 新增测试覆盖：shader 导入 GUID 稳定性、material loader 的 guid 优先 / legacy name 回退、material instantiate 保留 guid/ref、registry 按 guid 查询。
- 新增测试覆盖：`MaterialInstanceAsset` 新字段 roundtrip、按 `ParentGuid` 解析实例父材质、`MeshMaterialResolver` 的 GUID 优先与 legacy fallback；相关定向测试全部通过。
- 新增测试覆盖：`MemoryAssetResolver` 的 register/load/list/path 行为，以及通过 resolver 加载 material、material instance、mesh 默认材质的集成路径；定向测试通过。
- `SlangShaderImporter` 继续按设计稿推进：重导入前优先使用历史 `Dependencies` 重新计算 fingerprint 做快速跳过；真正编译后改为从 `IModule.GetDependencyFileCount/GetDependencyFilePath` 提取精确依赖列表，覆盖 `#include` / `import` 的实际解析结果，并写回 `ImportTrace` 与 `.asset.meta`。
- 新增 include 依赖跟踪测试：公共 include 文件变更会导致 `ContentFingerprint` 更新，同时 `AssetGuid` 保持稳定。
- 相关定向测试通过；仍存在一个旧的 `SlangIntegrationTests.TestSlangCompilation` 失败，原因是本机缺少 DXC，当前只生成到 2 个 SPIR-V 变体，不属于本轮改动引入。

## [2026-03-20] 资产标识与源文件追踪设计草案
- 新增 `docs/design/asset_identity_and_source_tracking.md`，整理 `AssetGuid` / `SourceGuid` / `AssetRef<T>` 的分层语义。
- 明确 `ShaderAsset` 应保存 `SourceGuid + SourcePath + Hash + ImporterVersion` 作为导入来源追踪，而 `Material` 等正式资产只依赖 `AssetGuid`。
- 给出 schema 调整方向、`.meta` 与 manifest 职责划分，以及一版最小 C# 参考实现草案。

## [2026-03-20] Phase 5: 材质资产打包与解析流程强化
- **FlatBuffer Schema**: 新增 `common_types.fbs`（ParamValue union: float/int/bool/Vec2/Vec3/Vec4），`material_asset.fbs` 新增 ScalarParam，`material_instance_asset.fbs` 新增 ScalarOverride + TagOverride。FlatSharp 通过 `include` + `IncludePath` 机制引用共享类型。
- **ShaderParamBag**: 新增 `SetScalar`/`GetScalar` 支持标量参数存储（float, int, Vector4）。
- **TagStore**: 新增 `CopyAllTags(from, to)` 和 `RemoveAllTags(item)` 方法。
- **MaterialAssetLoader**: 新增 `ApplyScalarParam` 方法，从 FlatBuffer 解析标量参数到 ShaderParamBag。
- **MaterialInstanceLoader**: 重写为通用 `CopyAllTags` 替代硬编码的 6 个 tag 复制，新增 scalar/tag override 支持。
- **MeshMaterialResolver**: 新增静态类，消费 `MeshAsset.default_material_slots` → `MaterialPass?[]`。
- 14 个测试全部通过。
- **Runtime 接入**: `Program.cs` 中 PBR/Unlit 材质创建改为 `MaterialAssetLoader.LoadFromAsset()`，通过 `MaterialAsset` 对象描述 shader 名称、纹理绑定、tags，走完整加载管线。提供 `LoadTexture` / `LoadShader` 回调复用现有 Diligent 纹理和 SlangShaderImporter。

## [2026-03-20] 材质 Mock 从管线层提取到应用层
- `ClusterMaterials.RegisterDefaults` 删除（创建 mat0、PSO、默认纹理、Sampler）。`ClusterMaterials` 仅保留 `SetupDefaultSlots` 和 `CreateDefault1x1Texture` 两个静态工具方法。
- `ClusterPipeline` 删除 `_defaultShadePSO`、`_defaultTextures`、`_defaultSampler`、`SetupMaterialWithDefaults`。`FindOrCreatePSO` 不再有 `shader == null` 的 fallback 分支（要求所有 MaterialPass 必须有 ShaderAsset）。
- `Program.cs` 显式创建默认纹理 + Sampler、PBR 材质、Unlit 材质，分别注册 + 打标签 + 分配 BinSpace slots。

## [2026-03-20] 材质全变 Unlit 的真正根因剖析与修复
- 现象：即使 PSO 分组正确且 `FindOrCreatePSO` 工作正常，所有球体仍然全部显示为 Unlit 材质。
- 根因：`Program.cs` 在加载网格为其分配默认材质时，错误地请求了名为 `"Default"` 的材质，但 `ClusterMaterials.RegisterDefaults` 注册的名称是 `"DefaultPBR"`。这导致 `mat0` 查找失败，返回空 pass 列表。
- 连锁反应：空 pass 列表传递给 `BinSpace.AllocateSlots` 会“分配”长度为 0 的块（假设返回 `offset = 0`）。之后 `mat1` 也被分配（返回 `offset = 0`），并正确地写入了它的 BinIndex（1，也就是 Unlit Bin）。由于 `defaultSlotOffset` 和 `mat1SlotOffset` 都指向 0，它们在着色时读取了相同的 Slot 数据，**导致所有球体都读取到 BinIndex = 1，并因此被派发给 Unlit Shader 进行求值**！
- 修复 1：将 `Program.cs` 中请求的默认材质名字修正为 `"DefaultPBR"`。
- 修复 2（隐含的安全漏洞）：发现在 `cluster_shade_material.slang` 和 `cluster_shade_unlit.slang` 中，基于 ThreadID (`tid.x`) 的 Dispatch **完全没有进行 Bounds Check（越界检查）**。这会导致 Dispatch 中多余的线程（为了凑够 64 的整数倍）去错误地着色相邻 Bin 里的像素（或者内存垃圾）。
  - 为此，将 `BinCounts` Buffer 引入了 `ClusterMaterialShadePass`、`ClusterShadePipelineParams` 并在着色器代码中补上了 `if (tid.x >= BinCounts[Uniforms.ShadingBin]) return;`。这彻底避免了相邻材质之间的越界覆盖问题。

- `ClusterPipeline.FindOrCreatePSO` 此前始终返回 `_defaultShadePSO`（硬编码），导致即使材质携带不同的 `ShaderAsset`，实际 Dispatch 时仍使用默认 PBR 着色器。
- 修改为：当 Shader 不为 null 时，使用 `shader.CreateShader(_context, "CSMaterialShade")` 编译真正的 Compute Shader 并创建独立的 PSO；抽取了共享的 `ShadePSOLayout` 静态字段。
- 修复后，BinSpace 中不同 ShaderAsset 的材质将各自拥有独立的 PSO，Dispatch 时执行对应的着色逻辑。

## [2026-03-20] Unlit Shading Model (第二个材质)
- 为 `mat1` 编写并分配了一个自定义的 Shader：`cluster_shade_unlit.slang`，复用管线数据布局，但剥离了法线贴图和 PBR 光照，实现了直观的无光泽基础色 (Unlit) 渲染。
- `Program.cs` 在加载时动态提取并应用该全新着色器资产，并在小球阵列中混合使用 PBR 和 Unlit。这直观展示了基于 ShaderAsset 的不同着色管线的运行时无缝集成能力。

## [2026-03-20] 第二个材质集成与验证
- 验证并通过了 Cluster Pipeline 对多材质的支持：在 `Program.cs` 运行时通过 `BinSpace.AllocateSlots(mat1.Passes)` 将新创建的 `mat1` 材质成功注入。
- 在网格实体创建循环中，为不同索引的 Sphere 实例关联了不同的 `MaterialSlotOffset`（`mat1SlotOffset` 和 `defaultSlotOffset`），验证了通过材质管线添加并使用第二个材质的完整工作流。

## [2026-03-20] Shade Pass PSOGroup 重构
- 新建 `ShadePSOGroup` struct（PSO + SRBs + Passes + BinStart/BinCount），Feature 持有。
- `BinQueue.Rebuild()` 按 ShaderAsset 排序，保证同 shader bins 连续。
- `ClusterMaterialShadePass.Execute()` 改为分组 dispatch：外层循环 PSOGroup（set PSO），内层循环 bins（commit SRB + dispatch），消除 per-bin PSO 比较。
- `MaterialPass` 移除 `SRB` 属性和 `CommitBindings()` 方法，SRB 归 Feature 管理。
- `ClusterPipeline` 新增 `RebuildShadePSOGroups()`：从 BinSpace 扫描 bins，break-on-change 分组，flat list PSO 缓存（引用比较），per-bin SRB 创建 + 材质参数绑定。
- 移除 `CreateSRBForMaterial()` 公开 API，SRB 生命周期完全内聚。
- 编译 0 错误，46 个材质相关测试全通过。

## [2026-03-19] ShaderAsset 元数据扩展与材质管线集成 (Phase A + C)
- **ShaderAsset 元数据抽取**：升级 Slang 编译器至 v2026.4.2，利用全新的 module reflection 在 `SlangShaderImporter` 中提取基于 `[PipelineTag("xxx")]` 的 user-defined attributes，同时自动抽取 `ParameterBlock<T>` 中定义的资源作为 `ShaderMaterialBinding`，完全解耦底层反射与高层材质绑定硬编码逻辑。
- **自动 Tag 推导与签名**：在 `MaterialRegistry.Register` 中，从 `ShaderAsset.Metadata.PipelineTags` 自动为材质通道推导并注册如 `OpaqueTag` 等兼容性 Tag；更新 `BinQueue` 处的特征签名函数 `ComputeSignature`，将其由仅计算 Params 改为 `ShaderAsset Hash ^ Params Hash`，实现按着色器资产维度的精确渲染状态装箱 (Binning)。
- **资产层小修与默认槽位集成**：扩展 `MaterialAssetLoader` 以支持多 pass 独立 ShaderAsset 的加载；在 Runtime `Program.cs` 层，为 `MeshAsset` 及其 `default_material_slots` 集成了 `BinSpace.AllocateSlots`，通过提取并将 `MaterialSlotOffset` 挂接到 `MeshInstance`，完成了实例化网格与多材质状态的正确映射。

## [2026-03-19] ClusterPipeline 瘦身重构（498→299 行）
- `RenderGraph` 新增非泛型 `AddPass(name, setup, execute)` 重载 + `LambdaRenderGraphPass`。
- 新增 `ClusterCameraData` record 打包相机参数（View/Proj/Pos/LOD/Screen/PrevHistory）。
- 新增 `ClusterMaterials` 静态类，从 `ClusterRenderFeature` 提取材质注册/默认纹理/PSO 创建，断绝依赖。
- `ClusterTraverse.AddPasses` 重构：内化 CullingUniforms 构建和上传，接受 `ClusterCameraData`（8 参数），输出 `CullingUniforms` handle。
- 新增 `ClusterHiZ.Add2PhasePipeline` 静态 Stage：封装 HiZ PingPong + Cull + RasterBin + Draw + HiZ Build 全部 2-phase 编排。
- `ClusterPipeline.cs` 重写：`AddPasses` 方法体 ~60 行纯 Stage 组装，Camera 状态用 `ClusterCameraData`，`FreezeCullingCamera` 直接存快照。
- 新增 `TestAddPassNonGeneric` 单元测试。

## [2026-03-18] ClusterPipeline 功能拆分瘦身（615→519 行）
- `BinSpaceExtensions.AddUploadPass` 扩展方法：MaterialSlotBuffer 上传逻辑从 Pipeline 移至 Pipelines 层扩展方法，BinSpace (Materials) 不依赖 RenderGraph。
- `CullingUniforms.Create` 工厂方法：30 行字段填充移入 struct 自身，Pipeline 只需一行调用。
- `ClusterShade` facade：合并 `ClusterShadeBinStage` + `ClusterShadeStage` 为统一入口，Pipeline 不再知道 ShadeBin+MaterialShade 两步过程。
- `ClusterDebugReadbackPass.AddPasses`：buffer 创建 + handle wiring 封装入 pass 内部。
- `AddDynamicUniformPass<T>` helper：CullingUniforms/DrawUniforms buffer 创建+upload 泛化为一行调用。

## [2026-03-15] Cluster Draw Meta Handle 修复
- 保持现有新架构与 Draw metadata 设计不变，为 `ClusterDrawConfig` 增加 `VisibleClusterMeta` 显式传递入口，避免 `ClusterDrawStage` 私有 zero-buffer 截断后续 phase/deform/shade 依赖的 draw meta 协议。
- `ClusterPipeline` 现显式把 `traverse.ZeroOffsetBuffer` 传给 Phase1 / Phase2 / Transparent Draw stage，回退路径仍保留本地 0 偏移缓冲，避免 draw request 膨胀。
- 清理 `ClusterRender` 路径中残留的 `ResourceStateTransitionMode.Transition`，统一改为 `Verify`。
- 新增 `ClusterPipelineTypesTests` 覆盖 `VisibleClusterMeta` 配置传递语义。

## [2026-03-03] HiZ Culling Pipeline Debug Mode Implementation
- Replaced `bool UseHiZ` with `HiZDebugMode` enum (`Legacy`, `Phase1Only`, `Phase1OnlyPassAll`, `Phase1ThenHiZ`, `Full2Phase`) to isolate and test the 2-phase culling behavior.
- Updated `ClusterPipeline.cs` to dynamically inject render passes based on the selected debug mode.
- Created robust bindings between legacy variables (`_useHiZ`) and standard evaluation to facilitate graceful testing degradation.
- Extended `ClusterDebugReadbackPass` inside `ClusterGraphPasses.cs` (augmented buffer sizes up to 80 bytes) to fetch rendering counts dynamically specifically targeting `HPhase2IndirectDrawArgs`.
- Implemented ImGui Enum switch UI interface (`HiZ Mode`) within `SomeEngine.Runtime/Program.cs` providing real-time `Phase2 Draw Vertex Count` and `Phase2 Draw Instance Count` metrics reflection.

## [2026-02-20] Slot-Based Binding Implementation
- Updated `ShaderResourceVariableDesc` to include `ResourceType` for D3D12 disambiguation.
- Added `GetShaderResourceRegisterClass` to group resource types into register classes (SRV, UAV, CBV, Sampler).
- Updated `PipelineResourceSignatureBase::FindResource` and `GetResourceAttribution` to support slot-based matching with `ResourceType` filtering.
- Implemented platform-specific logic:
    - **Vulkan/D3D12/WebGPU**: Match `Binding` and `Set` (and `ResourceType` for D3D12).
    - **D3D11/OpenGL**: Ignore `Set` (pass `~0u`) and match by `Binding` and `ResourceType` class.
- Updated `IShaderResourceBinding::GetVariableByBinding` to support the new matching logic.
- Updated `IPipelineState::GetStaticVariableByBinding` and `IPipelineResourceSignature::GetStaticVariableByBinding` for consistency.
- Updated `Archiver` module (`SerializedPipelineStateImpl` and `SerializedResourceSignatureImpl`) to match the new interface signatures.
- Updated `Mapping.xml` to provide C# default values for `ResourceType` parameters.
- Refactored `ShaderAsset` schema and importer to support generic per-backend reflection data instead of hardcoded fields.
- Updated C# `ShaderExtensions` and `TriangleRenderPass` to pass `ResourceType` during binding.
- Re-aligned `ShaderResourceVariableDesc` members to maintain 24-byte size and updated serialization/hashing logic.
- Updated all backend `ShaderVariableManager` implementations.

## [2026-02-20] Logging and PSO Ambiguity Fix
- Set `DebugMessageCallback` in `RenderContext.InitializeD3D12` to ensure Diligent logs are routed to C# console.
- Updated `FindPipelineResourceLayoutVariable` calls in D3D, WebGPU, and OpenGL backends to pass the explicit `ResourceType`. This fixes the "Ambiguous slot-based match" error when multiple resources of different types (e.g., CBV and SRV) share the same binding slot.
- Identified that `LOG_ERROR_MESSAGE` missing is due to Diligent's multi-module architecture on Windows; different DLLs (like `GraphicsEngine.dll`) may have their own `DebugMessageCallback` pointer which remains uninitialized unless `SetDebugMessageCallback` is called within that module.
## [2026-02-20] Instance Culling Implementation
- Updated `ClusterRenderPass.cs` to pass `InstanceCount` to culling shader and dispatch compute shader with Y-dimension corresponding to instance count.
- Updated `cluster_cull.slang` to support instance culling:
    - Added `InstanceCount` and `InstanceData` (StructuredBuffer) to shader resources.
    - Implemented logic to transform cluster bounds (Center, Radius) using instance transform matrix.
    - Updated `IsVisible` (Frustum Culling) and `IsLodSelected` to operate on world-space bounds.
    - Correctly populated `DrawRequest` with global instance ID for the draw pass.

## [2026-02-20] ImGui Debug UI Implementation
- Integrated `ImGui.NET` into `SomeEngine.Render` and `SomeEngine.Runtime`.
- Implemented `ImGuiRenderer` for Diligent (C#):
    - Handled font atlas texture creation and uploading.
    - Implemented PSO with alpha blending and dynamic vertex/index buffers.
    - Added support for Slang shaders (`imgui.slang`).
- Implemented `ImGuiInputHandler` using Silk.NET Input to handle mouse, keyboard, and scroll events.
- Added a debug UI in `SomeEngine.Runtime` providing:
    - Rendering toggles (Wireframe, Overdraw, Debug Spheres, Cluster ID).
    - Manual LOD selection slider.
    - Entity Inspector: View and edit `TransformQvvs` (Position, Scale) for all entities in the `GameWorld`.
    - "Add Entity" button for quick scene population.
## [2026-02-21] Compilation Fixes
- Fixed `ShaderAsset` reflection access in `SlangIntegrationTests.cs` (switched to `Reflections` array).
- Fixed `TestContext.WriteLine` analyzer warnings in several test files by switching to `TestContext.Out.WriteLine`.
- Resolved multiple nullability warnings (`CS8602`, `CS8600`, etc.) in `RenderContext.cs`, `SimpleMeshRenderPass.cs`, and `Program.cs`.
- Initialized `ParallelJob.Data` in `JobSystemTests.cs` to fix uninitialized field warning.
- Added explicit null checks and safe access for `MeshAsset.Payload` in `ClusterLodLevelTests.cs`.
- Fixed broken braces in `ClusterLodAutoCutTests.cs`.
## [2026-02-23] BVH Debug View Enhancements
- Added DebugBVHDepth to CullingUniforms and ClusterRenderPass to allow filtering BVH visualization by depth.
- Updated ExecuteBVH loop to track and pass CurrentDepth to the traversal shader.
- Enhanced cluster_bvh_traverse.slang to color-code BVH nodes based on culling status:
    - Green: Accepted/Traversed.
    - Blue: Culled by LOD.
- Enabled Alpha Blending and disabled Depth Write for BVH debug PSO to improve visibility of overlapping nodes.
- Added 'BVH Depth' slider to the ImGui debug panel.
- Fixed a crash caused by using UpdateBuffer on a Usage.Dynamic buffer for CullingUniforms.

## [2026-02-23] RenderGraph Refactoring & 3A Features
- Implemented **Lambda-based AddPass API**: Supports generic data passing between Setup and Execute phases, improving code modularity and clarity.
- Implemented **Topological Sort (Kahn's Algorithm)**: Automatically determines the correct execution order of render passes based on resource dependencies.
- Implemented **Dead Pass Stripping**: Automatically culls render passes that do not contribute to any output (imported resources or marked as output).
- Implemented **Automatic Resource State Barriers**: Automatically inserts `TransitionResourceStates` before each pass based on declared read/write requirements.
- Implemented **Transient Resource Allocation (Memory Aliasing)**: Introduced `RGResourcePool` to reuse physical textures and buffers between non-overlapping resource lifetimes, reducing VRAM footprint.
- Added comprehensive unit tests for RenderGraph features.
- Integrated `LambdaRenderPass` and updated `RenderGraph` to use a more robust compilation process.

## [2026-02-23] DiligentCore Review Fixes
- Fixed `GetResourceAttribution` slot matching: changed `Binding != ~0u && Set != ~0u` to `Binding != ~0u`, enabling D3D11/GL backends to use slot-based matching.
- Fixed placed resource lifetime: added `IDeviceMemory` reference holding (`AddRef`/`Release`) in D3D12 and Vulkan Buffer/Texture placed constructors/destructors.
- Removed incorrect hardcoded `m_MemoryProperties = MEMORY_PROPERTY_HOST_COHERENT` from D3D12 placed buffer constructor.
- Removed dead `InitSparseProperties()` call from Vulkan placed texture constructor.
- Added slot-based binding tests: ResourceType disambiguation, Set wildcard, GetStaticVariableByBinding, Binding=0/Set=0 distinction.- Refactored `GetResourceAttribution` multi-signature binding priority: changed from per-signature interleaved to two-pass global priority (Pass 1: slot-based across all signatures, Pass 2: name-based fallback).
- Renamed `MemoryRequirements.MemoryTypeIndex` to `MemoryTypeBits` to accurately reflect Vulkan bitmask semantics.
- Renamed `DEVICE_MEMORY_TYPE_DEFAULT` to `DEVICE_MEMORY_TYPE_PLACED` for clarity.
- Added null-pointer input validation (`DEV_CHECK_ERR`) to `CreatePlacedBuffer`/`CreatePlacedTexture` in D3D12 and Vulkan backends.

## [2026-02-24] Slang NoMangle HLSL Export
- Implemented `SlangNoMangleTests.cs` to demonstrate Slang compilation with the `NoMangle` option.
- Verified HLSL export via `GetEntryPointCode`, ensuring that entry point and resource names are preserved without standard Slang mangling.
- Enabled `AllowUnsafeBlocks` in `SomeEngine.Tests.csproj` to support `SlangShaderSharp`'s pointers.

## [2026-02-25] Dynamic BVH Patching and PageTable Removal
- Removed the implicit dependency on the `PageTable` Buffer across all Render pipeline phases (`ClusterCullPass`, `ClusterDrawPass`, `ClusterBVHTraversePass`).
- Designed a direct bit-packing scheme for `ClusterBVHNode` to store local page offsets directly in leaf nodes, removing the indirection gap.
- Added Compute Shader (`bvh_patch.slang`) based asynchronous patching using indirect CPU-tracking mappings in `ClusterResourceManager` upon mesh allocations.
- Re-architected multi-instance culling and BVH distribution: 
  - Adjusted traversal queued buffers (`_queueA`, `_queueB`) element stride from `uint` to `uint2` to pack `InstanceID`.
  - Upgraded Culling inputs in `cluster_cull.slang` and candidate representations from `uint2` to `uint3` (`pageOffset, clusterID, instanceID`).
  - Patched bounding spheres and transformations using corresponding `Instances` transformations inside the occlusion culling pass logic.
  - Linked `TransformSyncSystem` to correctly distribute world matrices arrays to BVH shaders iteratively covering all generated meshes.

## [2026-02-25] Instance Data Re-Architecture (Phase 1)
- Resolved the `roots[0]` hard-code issue in `ClusterBVHTraversePass.cs` causing identical mesh rendering across all instances.
- Introduced `GpuInstanceHeader` struct (16 bytes, holds `BVHRootIndex` and reserved `MaterialID`) in C# and Slang.
- Introduced `MeshInstance` ECS component.
- Refactored `TransformSyncSystem` into `InstanceSyncSystem` using robust multiple component query (`TransformQvvs` and `MeshInstance`). Concurrently uploads `GlobalTransformBuffer` and `GlobalInstanceHeaderBuffer`.
- Removed CPU-side queue initialization in traverse pass. Implemented `InitQueue` GPU compute kernel dispatching parallel root fetching per instance, eliminating host-side array allocations and buffer uploading overhead.
- Updated `ClusterCullPass`, `ClusterDrawPass`, `ClusterPipeline`, `TriangleRenderPass` and dependent test environments.

## [2026-02-28] Render Pass Fine-Grained Refactoring
- **栅格化切换算法优化**：重构了 `ShouldUseSWRaster` 方法，通过从 `BuildScreenBoundsAndNearDepth` 预计算的屏幕空间 AABB 信息中提取当前 instance 对应的像素面积信息，准确决定是进入软光栅（微小三角形）还是硬件光栅（常规三角形），避免了之前的重复计算，显著提高了算法精度和运行性能。
- **资产重构**：重构了导入和构建流程，将原有生成的 `.slang.asset` 和 `.mat.asset` 分别更改为更加明确清晰的 `.shader.asset` 与 `.material.asset`！同时去除了所有原先累赘的 `.asset.meta` 双重后缀，直接映射为 `.shader.meta` 等简洁统一的 `.meta` 格式（符合资产与元数据的分离规范），更新了所有的 Scanner 以及 Test 验证框架。
- Refactored `HiZBuildPass` and `ClusterDebugPass` into multiple fine-grained passes to eliminate manual resource state transitions.
- Implemented `HiZMip0Pass` and `HiZDownsamplePass` for iterative HiZ pyramid construction.
- Implemented `ClusterDebugBVHPass`, `ClusterDebugSphereCopyPass`, and `ClusterDebugSphereDrawPass`.
- Updated `ClusterBVHTraversePass` to support granular setup and execute methods for different traversal stages.
- Moved `ClusterBVHReadbackPass` to the end of the BVH traversal sequence to correctly handle transient readback buffers.
- Replaced all occurrences of `ResourceStateTransitionMode.Transition` and `ResourceStateTransitionMode.None` with `Verify` delegating all barrier management to the `RenderGraph`.
- Temporarily disabled HiZ logic in `ClusterPipeline` to address rendering issues (triangles missing).
- Fixed `ImGui Font Texture` and `SimpleMesh` buffer initialization states by adding explicit transitions in `Init` methods.
- Refactored `ClusterClearBuffersPass` and `ClusterBVHClearArgsPass` for discrete clear operations.
- Split BVH traversal loop into separate depth passes in `ClusterPipeline.AddToRenderGraph`.

## [2026-02-27] RenderGraph Compilation and History Resource Tracking
- Re-architected `RenderGraph` `Compile` and `Execute` phases to generate structured `_compiledPasses` and explicit execution order.
- Implemented topological sorting (Kahn's algorithm) ensuring deterministic execution via original index tie-breakers.
- Implemented **Dead Pass Stripping** by collecting sink resources (`MarkAsOutput`, `QueueTextureExtraction`) and analyzing backward producer reachability.
- Upgraded **Automatic Barrier System**: computes `PreBarriers` per pass and tracks dynamic `ResourceState`, automatically injecting `TransitionResourceStates`.
- Implemented safe extraction pipelines using `QueueTextureExtraction` and `QueueBufferExtraction` to establish definitive lifecycle ends and external ownership.
- Refactored `ClusterPipeline` HiZ history loop:
  - Registers `_prevHiZTexture` with `RegisterExternalTexture` when resolution and format validity passes (`IsHiZHistoryCompatible`).
  - Correctly configures extraction queue for `CurrHiZ` to safely promote history variables across frames.
  - Linked correct `_hasPrevHistory` uniform states and propagated it across cull components.

## [2026-02-26] Winding Order Fix
- Set `FrontCounterClockwise = true` in `RasterizerStateDesc` under `ClusterDrawPass.cs` and `SimpleMeshRenderPass.cs` to correctly handle standard CCW models like the monkey head.
- Reversed the index generation order in `PrimitiveMeshGenerator.CreateIcoSphere` so procedurally generated IcoSpheres conform to the CCW standard.

## [2026-02-26] Cluster BVH Buffer Capacity and Bounds Checking
- Fixed a major memory corruption issue (grid flickering) when rendering a high number of instances (~3600 monkey heads, exceeding former 100K cluster limits).
- Increased `_maxDraws` from 100K to 2.5M in `ClusterPipeline.cs` and updated Traverse queue buffers from 262K to 4M capacity in `ClusterBVHTraversePass.cs`.
- Introduced `MaxQueueNodes` and `MaxCandidates` limits in `CullingUniforms`.
- Added strict bounds checking across async compute kernel writes in `cluster_bvh_traverse.slang` and `cluster_cull.slang` guaranteeing memory safety during extreme clustering limits.

## [2026-02-28] RenderGraph Auto Barrier Fix
- Fixed an issue in `RenderGraph` where multiple reads/writes to the same resource in a pass would only track the last defined state. Combined required states using bitwise OR (e.g., `DepthRead | DepthWrite`) to properly support multiple usage scenarios.
- Fixed Diligent Engine debug assertion error by using `ResourceState.Unknown` instead of the tracked old state for `OldState` in `StateTransitionDesc` when automatically injecting `TransitionResourceStates` via `RenderGraph.Compile()`.
- Fixed a bug where UnorderedAccess (UAV) to UnorderedAccess transitions were missing. Updated `RenderGraph` to explicitly emit a barrier with `StateTransitionFlags.None` when `oldState == newState == ResourceState.UnorderedAccess` to ensure correct execution order between compute passes (e.g., `ClusterCullPass` to `ClusterDrawPass` args sync).
- Temporarily removed `SimpleMeshRenderPass` from `SomeEngine.Runtime/Program.cs` as requested.
- [2026-03-02] 完成 RenderGraph Refactoring Phase 1 & 2，将 ClusterPipeline 的瞬态 Uniform 改造为 RenderGraph 资源并新增 UploadUniformsPass；拆解了 ClusterResourceManager 的 BVH Patch 逻辑，移入 RenderGraph 的 ClusterBVHPatchPass 中执行。
- [2026-03-02] 完成 RenderGraph Refactoring Phase 3 & 4，创建 InstanceDataManager 彻底隔离 ECS 与渲染图后端的资源依赖，且全面引入 RGPooledBuffer 进行全局资源（如 PageHeap、GlobalBVHBuffer）的历史状态自动化跨帧闭环。

## [2026-03-10] RenderGraph 重构续作（4 阶段）
- **阶段 0**: 所有 Pass 从 `RenderPass` 基类迁移到 `IRenderGraphPass` 接口，`Execute` 签名统一为 `Execute(RenderGraphContext)`。API 重命名：`Reset`→`BeginFrame`, `ImportTexture`→`Import`, `WriteTexture`→`Write`, `ReadTexture`→`Read`, 移除 `MarkAsOutput`。
- **阶段 1**: `RenderGraphTests.cs` API 对齐修复。
- **阶段 2**: `RenderGraph.cs` 添加 frame-count-based deferred release queue（3 帧延迟），防止 GPU 仍在使用的资源被立即 Dispose。
- **阶段 3**: GPU 资源全面迁移到 RG：`ClusterResourceManager` 3 个 buffer 改为 desc-only + `graph.CreateBuffer`；`RenderContext` depth buffer 改为 desc-only + `graph.CreateTexture`；所有 DSV 引用更新。
- **阶段 4**: `ClusterUploadInstanceDataPass` 和 `ClusterResourceManager.ExecutePendingUploads` 的 `unsafe`/`fixed` 代码全部替换为 Span API `UpdateBuffer<T>`。

## [2026-03-10] RenderGraph Per-Subresource Barrier & Lifetime Aliasing
- 新增 `SubResourceRange` 结构（mip + array slice 范围），扩展 `RenderGraphBuilder.Read/Write/ReadWrite` 支持 per-mip/slice 声明。
- 重写 `BuildAutomaticBarriersAndTrackedStates`：状态追踪粒度从 whole-resource 改为 `(resourceId, mip, slice)`，支持 per-subresource barrier 生成与合并优化。
- `CompiledBarrier` 和 `Execute` barrier 发射使用 `FirstMipLevel/MipLevelCount/FirstArraySlice/ArraySliceCount`。
- `HiZBuildPass.SetupMip0` 声明写 mip 0，`SetupDownsample` 声明读 mip N-1 写 mip N。
- 修复 `RGMemoryHeap.TryAllocate` 从保守模式改为 lifetime-aware aliasing：只把 lifetime 重叠的 allocation 加入冲突集，启用 placed resource 内存复用。

## [2026-03-11] Debug Freeze Culling Camera
- 在 `ClusterRenderFeature` 中新增 `FreezeCullingCamera` 属性，勾选后将剔除相机（CullingUniforms + BVH Traverse）锁定为冻结时刻的快照，渲染相机（DrawUniforms）仍跟随自由相机。
- 在 Runtime ImGui 面板的 Rendering 分组下新增 "Freeze Culling Camera" Checkbox。

## [2026-03-11] Debug Overdraw View and Readback Buffer Fixes
- Fixed an issue where the Overdraw view failed to reflect HiZ culling and flickered. Restored `DepthEnable = true` and `DepthWriteEnable = true` through a new `Cluster Draw Depth Only PSO` pre-pass, ensuring the depth buffer is correctly populated for the next frame's HiZ pyramid generation. Then, an additive `Cluster Draw Overdraw PSO` pass with `DepthEnable = false` is run to stably accumulate the overdraw color of all submitted fragments, independent of draw order.
- Fixed the `ClusterDebugReadbackPass` returning all zeros (readback failure). Moved the `MapBuffer` (with `MapFlags.DoNotWait`) invocation to *before* the `CopyBuffer` operations. This correctly utilizes the RenderGraph's asynchronous execution pattern to map the staging buffer from the previous frame's copy, instead of incorrectly mapping immediately after issuing a new copy command.
- Marked `HDebugReadbackBuffer` as an output in `ClusterRenderFeature` to ensure the RenderGraph does not strip or optimize away the readback pass.

## [2026-03-11] HiZ Culling Accuracy Fix
- Fixed an issue in `cluster_cull.slang` where totally occluded objects were not being culled, which was especially noticeable when the camera is stationary.
- Changed the Mip Level calculation for `SampleHiZMax` from `floor(log2(pixelDiameter))` to `ceil(log2(pixelDiameter))`. This ensures the sampled bounding box footprint spans at most 2x2 texels at the selected mip level, preventing "holes" during the 4-corner maximum depth sampling lookup that caused false visibility results.
- Changed `IsOccludedByHiZ` to use `cluster.Center`/`cluster.Radius` (render bounds) instead of `cluster.LODCenter`/`cluster.LODRadius` (LOD hierarchy bounds) for tighter occlusion testing.

## [2026-03-12] HiZ Debug Frame Dump
- Added `ClusterDebugDumper.cs` — one-shot GPU data dump triggered by F5. Exports HiZ mip chain and depth buffer as binary R32_Float files with JSON metadata (ViewProj, camera, HiZ params).
- Added `tools/analyze_hiz.py` — Python analysis script (numpy/matplotlib) to load and visualize the dumped data.
- Improved HiZ occlusion rate: Phase 1 now tests against previous frame's **complete** depth buffer (Phase 1+2) instead of Phase 1-only depth. Added second HiZ build pass after Phase 2 draw, reusing same ping-pong `HiZ_A`/`HiZ_B` textures (written twice per frame).
- Fixed `nearDepth` calculation: replaced linearized `centerDepth - depthRadius` with direct projection of the sphere's nearest world-space point to clip space, fixing gross overestimation under perspective.
- Improved Culling Stats display: now shows per-stage breakdown (BVH Output → LOD Rejected → Phase1 HiZ Cull / Drawn → Phase2 HiZ Cull / Drawn → Total Drawn).
- Fixed HiZ false-visibility near silhouettes: replaced the derivative-based screen bounds approximation (`ComputeNdcDelta`) with exact 8-corner AABB projection. Under strong perspective, the linear derivative overestimated the footprint, causing clusters to sample the `1.0` background sky at high mip levels and fail culling.

## [2026-03-12] Exact Sphere Projection for HiZ Culling
- Replaced the 8-corner AABB projection in `cluster_cull.slang`'s `BuildScreenBoundsAndNearDepth` with mathematical exact bounding sphere projection, eliminating AABB-induced whitespace padding and minimizing projected footprint bounds.
- Added `View`, `P00`, and `P11` matrix parameters to `CullingUniforms` in C# and slang to feed view-space coordinates directly to the exact projection formula.

## [2026-03-12] View 内存泄漏修复
- `CachedTexture`/`CachedBuffer` 实现 `IDisposable`，增加 `Views` 字典缓存所有通过 `CreateView` 创建的视图，Dispose 时统一释放。
- 删除 `GetPhysicalTextureMipView` 和 `GetMipView`，新增通用 `GetOrCreateTextureView`/`GetOrCreateView`，按 `viewDesc.Name` 在 `CachedTexture.Views` 中查找或创建。
- `HiZBuildPass` 改用 `GetOrCreateView` 构造 `TextureViewDesc`。
- `ClusterDebugAABBPass` 改用 `GetDefaultView` 替代每帧 `CreateView`。
- RenderGraph 的资源替换、空闲淘汰和 Dispose 统一走 `CachedTexture.Dispose()`。
- `ClusterRenderFeature.Dispose` 补充遗漏的 `_debugAABBPass?.Dispose()`。
- `ClusterBVHTraversePass.Dispose` 补充遗漏的 `_clearArgsSRB_A/B` 和 `_clearArgsPSO` 释放。
- `ImGuiRenderer` 中 `CreateDefaultShaderSourceStreamFactory` 改为 `using var` 确保释放。

## [2026-03-12] 深入排查与修复 HiZ Culling 剔除精度漏洞
- 修复 `hiz_build.slang` 中 `DownsampleMip` 的边缘奇数截断问题：通过在渲染最后一行/列时增加 `+ 1` texel 采样跨度，完美保证奇数源维度下降采样时的全覆盖，杜绝屏幕右下边缘深度的丢失（解决边缘 False Occlusion），且避免对齐 2 的幂次带来的 4096 极高内存开销。
- 解决 `cluster_cull.slang` 投影跨越 Texel 引发背景露缝、集群规律低频闪烁的问题（Center Flashing Bug）：
  - 移除了包围盒 Mip 计算公式中的 `- 1.0` 以达成严格的 `pixelDiameter <= divisor` 数学条件。通过确保包围盒**最多只横跨 2 个格子**，使得 4 角 (`SampleHiZMax`) 能够毫无死角地闭环包围所有空间缝隙。
  - 将 `mipSize` `float` 乘法带来的非对齐映射舍弃，更换为等价于 GPU 内部硬件 `floor(pxMin / divisor)`（`>> L` 整数截断）的逻辑，解决 texel 采样的边界滑动问题。 
- 重构并清理调试阶段添加的高内存开销功能，移除硬编码 `Pad4` 并在 C# 构建了基于 `DumpHiZData` 标志位的条件资源分配流程（正常态分配 `16 byte` Buffer）。将 F5 Dump 数据脚本作为 Python 分析工程 (`analyze_bounds.py`) 保留于 `tools/`。

## [2026-03-13] Visibility Buffer + Compute Resolve 管线
- 在 `cluster_draw.slang` 中新增 `VSVisBuffer` / `PSVisBuffer` 入口点，输出 `R32_UINT` 编码 `(VisibleClusterIndex << 7) | TriangleID`。
- 新建 `cluster_resolve.slang`：全屏 Compute Shader (`CSResolve`, 8×8 线程组)，读取 VisBuffer 反查 PageHeap 重建三角形，计算面法线，支持 ClusterID/LOD/Normal 三种 Debug 可视化。
- 在 `ClusterDrawPass.cs` 中新增 VisBuffer PSO（`R32_UINT` RT + `D32_Float` DS），通过 `_useVisBuffer` 标志在 VisBuffer 和 Forward 路径间切换。
- 新建 `ClusterResolvePass.cs`：Compute Pass 读取 VisBuffer + VisibleClusters + PageHeap，写入 ColorTarget UAV。
- `ClusterRenderFeature.cs`：`DrawUniforms` 增加 `ScreenWidth`/`ScreenHeight`；新增 `UseVisBuffer` 属性（默认 true）；创建 VisBuffer 纹理和 Clear pass；在 Legacy 和 2-Phase 分支末尾均挂载 Resolve pass。

## [2026-03-13] 修复 LOD 选择闪烁 Bug
- 修复 `ClusterBuilder.cs` 中 BVH 叶节点 `LODError`（float）与 GPUCluster `LODError`（f16）精度不一致：量化重构将 cluster 的 LODError 从 float 压缩为 f16，但 BVH 节点仍用 float 值。当 f16 四舍五入略大于 float 原值时，LOD 切换边界会出现父级被拒绝同时子级被剔除的间隙（一帧闪烁）。修复：`LODError = (float)(Half)m.ParentError` 确保 BVH 与 cluster 使用相同 f16 精度。
- 需要重建 .mesh 资产使改动生效。

## [2026-03-13] 合并 Phase 1/Phase 2 Visible Clusters
- `ClusterRenderFeature.cs`：Phase 2 cull/draw 共享 `hVisibleClusters` 和 `hIndirectDrawArgs`，删除独立的 `Phase2VisibleClusters`/`Phase2IndirectDrawArgs` 资源。Phase 2 draw 重画全部 N1+N2 instances（early-Z 拒绝 Phase 1 重叠），Resolve Pass 自动看到所有 cluster。零 shader 改动。

## [2026-03-14] SoA 顶点属性打包重构
- 将 `ClusterBuilder.cs` 中顶点属性打包方式从 AoS（interleaved）改为 SoA（per-stream 顺序排列）。
- `VertexAttributeDescriptor.Offset` 重命名为 `StreamIndex`，反映 SoA 语义。
- Page Header 结构不变，`AttributesOffset` 指向首个 stream 起始。
- 材质 shader 可通过 `attrBase + sum(precedingStreamSizes) + (vStart + vi) * elementSize` 直接寻址。
- 新增 `TestSoAStreamLayout` 测试验证回读 Normal/UV 数据正确性和 stream 物理连续性。

## [2026-03-14] Shader 公共 Helper 提取 + 顶点属性 Fetch 工具
- 新建 `cluster_common.slang`：提取 `LoadClusterInfo`、`FetchVertexPosition`、`FetchVertexIndex`、`ProjectToScreen`、`ComputeBarycentric`、`PCGHash`、`ColorFromHash`、`DrawRequest` 等共享代码。
- 添加 SoA 属性解码工具：`DecodeSnorm8x4`、`DecodeFloat16x2`、`DecodeUnorm8x4`、`FetchNormal`、`FetchTangent`、`FetchUV`、`FetchVertexColor`。
- 重构 `cluster_resolve.slang`（227→107 行）和 `cluster_shade_material.slang`（205→113 行），删除重复代码，统一使用 `#include "cluster_common.slang"`。
- 所有 helper 函数改为显式参数传递（不依赖全局资源声明），提高复用性。

## [2026-03-14] Shade Material Shader — 属性插值着色
- `cluster_shade_binning.slang`：删除内联 `DrawRequest`，改用 `#include "cluster_common.slang"`。
- `cluster_shade_material.slang`：从面法线着色改造为重心坐标插值着色管线（`ProjectToScreen` + `ComputeBarycentric` + `FetchNormal3` + 四元数旋转 + 方向光）。
- `cluster_common.slang`：新增 `LoadStreamBytes`（字节级非对齐加载）、`DecodeSnorm8x3`、`FetchNormal3`（3B/vertex 法线读取）。
- `ClusterBuilder.cs`：属性排序为确定性顺序（NORMAL→TANGENT→TEXCOORD→COLOR→...），不做 padding。

## [2026-03-14] BSDF PBR 材质系统 MVP
- 新建 `brdf.slang`：Cook-Torrance 微表面 BRDF 库（GGX NDF、Smith-Schlick 几何遮蔽、Schlick Fresnel、`EvaluateDirectionalLight` 完整直接光计算）。
- 新建 `material_interfaces.slang`：定义 `PixelContext`（几何数据 + 输出）和 `ISurfaceEvaluate` 接口。PixelContext 仅包含几何信息，光照/Decal/IBL 等系统通过独立 Provider 接口按需注入（MVP 阶段不实现）。
- 新建 `standard_pbr.slang`：`StandardPBRMaterial : ISurfaceEvaluate`，固定 PBR 参数（baseColor=0.8, metallic=0, roughness=0.5），通过 `DirectionalLightParams` 读取光照 Uniform。
- 重构 `cluster_shade_material.slang`：提取 `buildPixelContext()` 封装 VisBuffer 解码 + 三角形重建 + 重心坐标插值 + 属性 fetch；`CSMaterialShade` 入口改为构建 PixelContext → 创建 StandardPBRMaterial → 调用 `evaluateSurface()`。所有 Debug 模式保持不变。
- `ShadeUniforms` 增加 `CameraPos`（`Vector3` + padding），`ClusterRenderFeature.cs` 传入 `_cameraPos`。

## [2026-03-14] 材质系统核心类型（Phase 1）
- 新建 `Materials/` 目录，添加 6 个核心文件：`ShaderResourceAttribute.cs`、`MaterialSlots.cs`（TextureSlot/BufferSlot/SamplerSlot）、`MaterialBase.cs`（抽象基类）、`MaterialShaderType.cs`（PSO 封装）、`MaterialRegistry.cs`（泛型注册表）、`StandardPBRMaterial.cs`。
- 设计：材质类型 = C# 类定义，材质实例 = C# 对象。`[ShaderResource]` 特性标记字段 → Phase 2 源生成器自动生成 SRB 绑定代码。
- 重写 `ClusterMaterialShadePass.cs`：移除自有 PSO/SRB，改为 `MaterialRegistry` 驱动的双层循环（外层 ShaderType 切 PSO，内层 Material 切 SRB + DispatchIndirect）。
- `ClusterRenderFeature.cs` 新增 `RegisterDefaultMaterials()` 方法，PSO 创建搬入，`activeMaterialCount` 从 registry 读取。
- `Program.cs` DI 注册 `MaterialRegistry`。
- 9 个单元测试通过。

## [2026-03-14] 多材质验证 + UV Fetch（Phase 2A）
- `PrimitiveMeshGenerator.cs`：ico 球新增 TANGENT 属性生成（cross(up, normal)），SoA 流顺序改为 NORMAL(3B) → TANGENT(4B) → UV(4B)。
- `cluster_shade_material.slang`：`buildPixelContext()` 添加 `cursor.advance(4)` 跳过 TANGENT、`FetchUV` + 重心坐标插值 UV；`CSMaterialShade` 用 `PCGHash(MaterialID)` 算 baseColor 实现每材质颜色区分。
- 新增 UV debug 可视化模式（mode 10，棋盘格 + UV 色彩映射）。
- `ClusterDebugMode` 枚举扩展：MaterialID=7, Barycentric=8, Normal=9, UV=10。
- `Program.cs`：`SpawnEntity` 新增 `materialId` 参数；DI 初始化后创建第 2 个 `StandardPBRMaterial`(ID=1)。
- ⚠️ 需要重新生成 .mesh 资产（旧资产无 TANGENT 流）。
- **Bug fixes**:
  - `cluster_common.slang`: `FetchUV` 改用 `LoadStreamBytes`（非对齐读取），修复 3B normal 导致 UV streamBase 非 4 字节对齐问题。
  - `cluster_resolve.slang`: mode≥3 时 `return` 不写颜色，避免覆盖 shade pass debug 输出。
  - `ClusterRenderFeature.cs`: resolve-only 提前返回只对 mode 1/2 生效，mode 7-10 走完整 shade pipeline。
  - `Program.cs`: Debug Cluster ID checkbox → Shade Debug Combo 下拉框；Add Entity 交替分配 MaterialID。
  - `IcoSphereTest.cs`: 新增 3 属性（NORMAL/TANGENT/UV）断言验证。

## [2026-03-14] SRB 优化 + Source Generator + 纹理（Phase 2B）
- **SRB 优化 (S0→S1)**:
  - Shade PSO `DefaultVariableType` → `Mutable`，仅 `Uniforms` 保留 `Dynamic`。
  - `BindPipelineResources` 所有 `Set()` 改用 `AllowOverwrite`（Mutable + RG placed resource 每帧重绑）。
- **Source Generator**:
  - 新建 `SomeEngine.Generators` 项目（netstandard2.0 ISG）。
  - `MaterialBindingGenerator`：扫描 `[ShaderResource]` 字段，按类型（TextureSlot/BufferSlot/SamplerSlot）生成 `CommitBindings()`。
- **纹理支持**:
  - `StandardPBRMaterial`：添加 `AlbedoMap`/`NormalMap`/`ARMMap`/`MaterialSampler` 字段。
  - `ClusterRenderFeature`：`RegisterDefaultMaterials()` 创建 1×1 默认纹理（白色 albedo、平坦法线、默认 ARM）+ linear sampler。
  - `cluster_shade_material.slang`：声明 `Texture2D AlbedoMap/NormalMap/ARMMap` + `SamplerState MaterialSampler`；baseColor 改用 `AlbedoMap.Sample()`。
  - 新增 `SetupMaterialWithDefaults()` 公共 API 供外部配置新材质。

## [2026-03-14] 材质系统重新设计 v4.2
- **IShaderParams 统一体系**：新建 `IShaderParams` 接口 + `ShaderParamAttribute`（含 `Dynamic` / `Stage` 属性，默认 Mutable）；`MaterialBase` 实现 `IShaderParams`。
- **Params 组合**：材质通过持有 `IShaderParams` 字段组合可复用参数块（`PBRParams`、`NoiseParams` 等），源生成器自动在 `ApplyToSRB()` 中调用 `field.ApplyToSRB(srb)`；材质自身也可直接放 `[ShaderParam]` 字段。
- **语义标签**：`IMaterialTag` + `MaterialTagSet`（Dictionary-based），核心标签 `OpaqueTag`/`MaskedTag`/`TwoSidedTag`/`StencilRefTag` 等。
- **多 Pass PSO**：`IPassKey` + `MaterialShaderType`，每个 ShaderType 可注册多个 Pass（如 ShadowCaster）。
- **源生成器重写**：`MaterialBindingGenerator` → `ShaderParamsGenerator`。统一扫描 `IShaderParams` 实现类和 `MaterialBase` 子类，处理资源字段（SRB Set）、IShaderParams 组合字段（委托调用）、base 链。
- **管线集成**：`ClusterShadePipelineParams : IShaderParams`（全部 Dynamic）替代手写 `BindPipelineResources()`；`ClusterMaterialShadePass` 改用 `pipelineParams.ApplyToSRB()` + `material.CommitBindings()`。
- **BufferSlot 增强**：增加 `IBuffer?` 字段支持 ConstantBuffer 直接绑定。
- 删除旧 `ShaderResourceAttribute.cs`。9 个测试通过。

## [2026-03-15] 修复源生成器重复生成 ApplyToSRB Bug
- 移除 `PBRParams` 和 `ClusterShadePipelineParams` 中手动编写的 `ApplyToSRB` 空方法，避免与源生成器发生 CS0111 冲突。
- 修复 `MaterialBindingGenerator.cs` 中未过滤隐式字段（如 auto-property 的 `<Field>k__BackingField`），导致其生成非法 C# 语法的漏洞：在 `GetMembers()` 遍历时前置 `IsImplicitlyDeclared` 检查。
- 修复 `PBRParams` 中 ShaderParam 资源绑定名称不匹配导致的 `No resource is bound to variable` 错误：移除与属性名完全一致的冗余字符串参数（改为 `[ShaderParam]`），仅在绑定名与属性名不同时（如 `MaterialSampler`）使用字符串指定名称，通过源生成器的默认规则保持精准匹配。

## [2026-03-15] 光栅化 Binning 管线（Step 1-2）
- **Step 1 回退 + RasterBinKey**：回退 `cluster_cull.slang`、`ClusterCullPass.cs`、`ClusterRenderFeature.cs`、`ClusterGraphPasses.cs` 中错误的 DeformFlags/DeformedBuffer 代码；`GpuInstanceHeader`（C# + Slang）字段改为 `RasterBinKey`（uint16 = VertexEvalProgram:8 | RasterFlags:8）+ padding；`InstanceSyncSystem.cs` 同步更新。
- **Step 2 Binning CS**：新建 `cluster_binning.slang`（`CSBinningInit` 初始化 per-bin metadata + DrawArgs，`CSBinning` 按 InstanceHeaders.RasterBinKey 将 VisibleClusters 原子散射到 BinnedClusterBuffer 对应 bin 区域，count 从 DrawArgs GPU 读取）。新建 `ClusterBinningPass.cs`（双 PSO/SRB 分 Init + Scatter 内核，绑定 7 资源）。`ClusterRenderFeature.cs` 集成：4 新 buffer（RasterBinMeta、BinnedClusterBuffer、BinnedDrawArgs、BinningUniforms），uniform upload lambda 增加 BinningUniforms，legacy path cull→binning→draw 路由（draw 改读 BinnedClusterBuffer + BinnedDrawArgs）。

- [Render] Cluster Render Pass Pipeline: Refactored PSOs and SRB Pools to 100% Static caching, making all Passes stateless and thread-safe.

## [2026-03-16] 材质架构文档更新 (ShaderAsset + PSO + Pull 模型)
- **ShaderAsset**：复用已有的预编译 `ShaderAsset` 类（FlatBuffers: name + variants + reflections）。统一三种生成方式：1) 编辑器下拉组装；2) `[ClusterShade]` 等特性驱动 Importer 自动模板实例化（推荐）；3) 手写入口函数 + Stage Wrapper。
- **PSO 所有权**：PSO 归 Stage 缓存。同一 ShaderAsset 可被多个管线使用（Cluster Compute vs Forward Graphics），不同管线产出不同 PSO。
- **PSO 缓存策略**：bin key = PSO 索引。Stage 维护扁平数组 `_psoByBin[binKey]`，Dictionary 仅在低频 `RebuildDispatchTable` 中做 ShaderAsset 去重，热路径零 hash 开销。
- **Pull 模型**：去除 `OnMaterialRegistered` 回调。兼容性 Tag 由 `MaterialRegistry.Register()` 根据 ShaderAsset 元数据自动打标；bin key 和 PSO 由 Stage 在 Setup 阶段 Pull 查询后按需构建。
- **底部对照表修正**：`ShaderAsset` 标记为已有类，去除过时的 `ModulePath + StructName` 描述。

## [2026-03-18] 材质系统 Phase 2+3 直接替换 + 资产链路
- **Phase 2**：删除 `MaterialBase`/`MaterialShaderType`/`StandardPBRMaterial`/`PBRParams`，新建 `ShaderParamBag`/`MaterialPass`/`Material`/`TagStore`/`BinQueue`。重写 `MaterialRegistry`/`MaterialTag`/`ClusterMaterialShadePass`。适配 `ClusterRenderFeature`/`ClusterShadeStage`/`ClusterPipeline`/`MaterialBindingGenerator`/`Runtime Program`。
- **Phase 3**：新增 `.mat`/`.matinst` FlatBuffer schema + Serializer/Loader。`mesh_asset.fbs` 添加 `default_material_slots`。新建 `MaterialSlot`/`MaterialSlotBuffer`/`MaterialTagAttribute` + 源生成器。
- 编译 0 错误，26 个 Material 单元测试全通过，Runtime 渲染不变。

## [2026-03-18] 材质系统 Phase 4 GPU 路径改造
- `GpuInstanceHeader.MaterialID` / `MeshInstance.MaterialID` → `MaterialSlotOffset`（C# + Slang）。
- Slang 新增 `MaterialSlot` struct（PackedBins/PackedExtra 各 uint）+ decode helpers（`GetShadingBin`/`GetRasterBin`/`GetShadowBin`）。
- `cluster_shade_binning.slang`：Count/Scatter 3 个 pass 绑定 `MaterialSlotBuffer`，bin key 改为 `MaterialSlotBuffer[slotOffset].ShadingBin` 间接查找。
- `cluster_shade_material.slang`：`ShadeUniforms.MaterialID` → `ShadingBin`，dispatch 按 binKey 索引。
- CPU 端：`ClusterRenderFeature` 创建 Dynamic StructuredBuffer + mock 上传 pass（`ShadingBin = pass.MaterialID`）；`ClusterShadeBinCountPass`/`ScatterPass` 添加 `HMaterialSlotBuffer` SRB 绑定。
- `ClusterMaterialShadePass` dispatch 改用 `uniformData.ShadingBin`。`ClusterShadeStage` / `Program.cs` 适配。
- 编译 0 错误，26 个 Material 单元测试全通过。

## [2026-03-18] 材质系统多 Pass / Overlay 支持 + Slot 共享缓存
- `MultiPassTag` 添加 `OverlayCount` 字段，`OverlayTag` 添加 `LayerIndex` + `PrimaryPass` 引用。两者均为自动推导不序列化。
- `MaterialRegistry.Register()` 自动推导：多 pass Material 的 primary pass 打 `MultiPassTag`，后续 pass 打 `OverlayTag`。
- 新建 `OverlayMapping.cs`：静态工具类，从 OverlayTag 查询 + BinQueue 映射构建排序后的 `OverlayEntry` 列表，供 Feature dispatch 时遍历。
- 新建 `MaterialSlotCache.cs`：hash + refcount 共享缓存，相同 pass 组合的 instance 共享同一段 `MaterialSlotBuffer` 区间。
- `Material.AddPass()` 方法：支持运行时追加 overlay pass。
- 编译 0 错误，37 个 Material 单元测试全通过（新增 11 个）。

## [2026-03-18] BinSpace 重构 — 动态字段 SlotBuffer + 统一入口
- 删除 `MaterialSlot.cs` 固定 struct。
- `MaterialSlotBuffer.cs` 重写为 `ushort[]` + 动态 stride，通用 `SetField`/`GetField` 替代具名方法。
- `MaterialSlotCache.cs` 重写：存储 `MaterialPass[]` 列表，新增 `RebuildField(fieldIndex, binQueue)` 支持 bin rebuild 后 patch。
- 新建 `BinSpace.cs`：统一入口，内部持有多 BinQueue（per-field）、SlotBuffer、SlotCache。纯 CPU 数据层，不管 GPU dispatch。
- 适配 `ClusterRenderFeature.cs` / `ClusterPipeline.cs`：GPU 上传从 `MaterialSlot[]` 改为 `ushort[]`。
- 新增 BinSpace + RebuildField 测试。编译 0 错误，全部测试通过。

## [2026-03-18] GPU 侧 MaterialSlot → SOA SlotBuffer
- 删除 GPU `MaterialSlot` struct 和 `GetShadingBin/GetRasterBin/GetShadowBin` helpers。
- 新增通用 SOA 访问函数 `GetSlotField(slotBuffer, slotOffset, fieldIndex, slotCapacity)`。
- `MaterialSlotBuffer.cs` 改为 SOA 布局（`_data[fieldIndex * capacity + slotOffset]`），capacity 保证偶数，扩容逐段搬运。
- GPU `StructuredBuffer<MaterialSlot>` → `StructuredBuffer<uint>`，shade/raster binning 均通过 SOA 读。
- 删除 `GpuInstanceHeader.RasterBinKey`，raster binning 改为从 SlotBuffer 读。
- `ShadeBinUniforms` 增加 `SlotCapacity`/`ShadingBinFieldIndex`；`BinningUniforms` 增加 `SlotCapacity`/`RasterBinFieldIndex`。
- `ClusterBinningScatterPass` 新增 `HMaterialSlotBuffer` 绑定。
- 编译 0 错误，全部测试通过。

## [2026-03-18] BinSpace 接入渲染管线，移除 Mock Upload
- 将全局 `BinSpace` 实例作为属性集成到 `MaterialRegistry` (`Bins`)，并管理生命周期。
- 新增 `MaterialRegistry.FreezeBinLayout()`。在被调用时或之后的 `Register` 操作中，自动为 `MaterialPass` 分配真实的 `SlotOffset`。
- 修改 `MaterialPass` 和 `Material`，增加 `SlotOffset` 供 `MeshInstance` 组件使用（替代原有的 MaterialID 假映射）。
- 升级 `ClusterUploadConfig` 和 `BinningUniforms`，补充 `SlotCapacity` 和 `RasterBinFieldIndex`，修复了 Raster Binning 期间因缺少 SOA 配置导致 offset 错乱。
- 在 `ClusterPipeline.Initialize` 中统一注册 `"RasterBin"` 和 `"ShadingBin"` 字段，并冻结 BinSpace。
- 在 `ClusterPipeline.AddPasses` 中移除了遍历 registry 生成 `MaterialSlotBuffer` mock 数据的逻辑，改为调用 `_registry.Bins.RebuildIfDirty()` 获取真实的 `GetData()` 数组成果并上传到 GPU。
- 编译通过且 43 个涉及 Material 相关的核心单元测试维持全数绿灯。

## [2026-03] Cluster 多材质支持 (Phase B)
- `GPUCluster` 结构体扩容至 64 字节，新增 `PackedMaterials` (支持最多3种材质的 ID) 和 `PackedRanges` (支持2个分界点)。
- `ClusterBuilder` 重构 `Process` 逻辑，合并输入 mesh 的 primitives 并生成内部的 `_MATERIAL_INDEX` 属性。
- `Clusterize` 内部按材质对三角形进行排序，强制拆分材质数量超过 3 种的 cluster。
- 增加 HLSL `GetLocalMaterialIndex` 方法，通过 `triIdx` 从 `PackedRanges` 解码材质 ID。
- `ClusterShadeBinCountPass` / `ClusterShadeBinScatterPass` 结合 `ClusterBuffer` / `HPageHeap` 使用 `GetLocalMaterialIndex` 确定材质 Bin。
- `Program.cs` / AssetLoader 模型加载时正确使用 `DefaultMaterialSlots` 构建 MaterialSlotBuffer。
- 修复并更新单元测试，全数绿灯（排除原有的 5 个环境与无关报错）。

## Phase 5: Final Optimization and Polish
- **全局 PSO Cache**: 实现了 `GlobalPsoCache` 根据 `ShaderAsset` 字典与状态参数生成哈希缓存 Compute PSO，并移除了 ClusterPipeline 和 RenderFeature 中冗余的死代码和自身 _psoCache。
- **动态 Bin 数**: 去除了 `ClusterLimits.MaxBins` (16) 限制。管线的 BinnedDrawArgs 和 Meta 等预分配内存现在全数由 `BinSpace` 生成的准确 `TotalBinCount` 作为参数去构造，实现了显存占用的随需扩展与上限解除。
- **MaterialSlotBuffer 增量 Patch**: 依靠 CPU 内部的 DirtyTracker 对象针对修改动作精确圈出最小操作范围。结合 Diligent Engine 的 UpdateBuffer 和 RenderGraph，彻底屏蔽了 MaterialSlotCache 对没发生改变的项引起的反复重建；现由脏范围直接推算出字节偏移做局部更新。

## [2026-03-23] 优化软硬件光栅化切换算法
- 废弃了 `EstimateScreenArea` 中使用 `LODCenter` 及其投影面积的粗略计算逻辑。
- 提取并复用 `BuildScreenBoundsAndNearDepth`，使软硬件光栅化路径切换的预估与剔除操作相统一，完全建立在对 Cluster 高精度紧凑空间几何包围盒投影的计算上。
- 在 Shader 侧增加和应用了通过精确屏幕像素长宽求得的 2D 包围盒面积阈值（2000 px²），大幅消除原先因包围球体积高估导致的判断失准。
- 修正了 C# 端向 RenderGraph 提供 CullingUniforms 时发生的成员越界截断问题，并正确补充了主框架传递下来的屏幕尺寸属性。

## [2026-03-24] 材质 Shader 泛型重构与运行时资产集成
- **Slang 泛型着色管线**: 抽离核心的 cluster shading 逻辑至全新的 `cluster_shade_pipeline.slang`。利用 `CSShade<TMaterial : ISurfaceEvaluate>` 泛型函数将光照计算、调试可视化、PixelContext 重构与具体材质属性解耦。
- **PBR 与 Unlit 材质打通**: 
  - `standard_pbr.slang` 实现完整 `ISurfaceEvaluate`，内部按需绑定 `Texture2D` (`AlbedoMap`, `NormalMap`, `ARMMap`) 和 `MaterialSampler`，取代了之前的硬编码固定色。 
  - `cluster_shade_unlit.slang` 与 `cluster_shade_material.slang` 均改写为简单的入口声明，调用统一泛型流水线。
- **实例访问规范化**: 将 `getInstancePropertyFloat4` 以及 Instance 数据相关的 Buffer 集中到 `cluster_common.slang` 中，以解决在分离不同 Material 特化模块时的声明缺失及重复声明冲突。
- **资产序列化测试**: 添加 `MaterialCreationTests.cs` 通过 FlatSharp 正确生成真实的 `ShaderAsset` (`.slang.asset`) 和 `MaterialAsset` (`.mat.asset`) 以持久化到文件系统，为 `ManifestAssetScanner` 扫描出真实的 GUID 提供数据源。
- **运行时 Manifest 接入**: 移除 `Program.cs` 中的 Mock API 注册流程（原先直接通过代码注册 PBR/Unlit Defaults）。改为调用 `AssetManifestScanner.ScanAndSave` 构建本地缓存后实例化 `ManifestAssetDatabase`，并配合 `MaterialAssetLoader.LoadFromAsset` 使用真实 GUID/Name 在运行时动态抽取和装配管线资源。
- 修复了 `ManifestAssetDatabase` 的保护可见性问题，利用标准 `assetDb.List()` 扁平索引接口查阅并注册材质。

## [2026-03-24] 文档整理
- 按领域重组 docs/：core/ rendering/ materials/ assets/ rhi/ future/ archive/
- 归档 18 份旧版/被替代文档到 archive/
- 新增合并文档：render_graph.md, cluster_pipeline.md, rasterization.md, shading_pipeline.md
- 新增 README.md 文档索引
- 更新 project_structure.md 反映实际代码结构
- 新增 rendering/degradation_strategies.md：SW/HW 深度降级、Tess 架构（Nanite 风格 + HW DrawInstancedIndirect）、DeformCache/Inline、动画 BVH、Binning 耦合、SlotBuffer 3-field 设计

## [2026-03-25] DeformCache 预变形缓存接入
- **Shader 层**：
  - `vertex_evaluate.slang`：`IVertexEvaluate` 接口新增 `getCacheStride()`/`writeCache()`/`readCache()` 三个 cache 序列化方法（half3 packed = 8B/顶点），`VertexFetchArgs` 新增 `visibleClusterIndex` 字段，新增 `CachedSource<TVE : IVertexEvaluate>` 泛型结构体实现 `IVertexSource`（从 DeformCache + 单独的 CacheOffsets 寻址 buffer 读取）。
  - `cluster_deform.slang`（新建）：DeformCS kernel，32 threads/group = 1 cluster。Lane 0 通过 `CacheAllocCounter` 原子分配 cache 空间写入 `CacheOffsets[visibleIdx]`，所有 lane 执行 `evaluate()` → `writeCache()` 写压缩数据。入口：`CSDeformStatic`/`CSDeformWave`。
  - `sw_raster.slang`：新增 `CSSWRasterCached` 入口点 + `DeformCache`/`CacheOffsets` 资源声明。
  - `cluster_draw.slang`：新增 `VSVisBufferCached` 入口点 + `DeformCache`/`CacheOffsets` 资源声明。
  - `cluster_shade_pipeline.slang`：新增 `buildPixelContextCached<TVE>()` 从 cache 读取 3 个顶点的 DeformedVertex 替代 3× `evaluate()` 调用。
- **C# 层**：
  - `ClusterDeformPass.cs`（新建）：PSO 缓存（CSDeformStatic/CSDeformWave）+ SRB pool + `IRenderGraphPass` 实现。
  - `ClusterHiZStage.cs`：`HiZConfig` 新增 `UseDeformCache`；Phase1 中 RasterBin 后插入 DeformCache buffer 创建（RWByteAddressBuffer 48MB + CacheOffsets + CacheAllocCounter）+ `ClusterDeformPass`。
  - `ClusterPipeline.cs`：新增 `UseDeformCache` 属性。
- **测试**：`DeformCacheCompilationTest.cs`（3 tests: CSSWRasterCached/CSDeformStatic+Wave/VSVisBufferCached），全部通过。修复 `VertexEvaluateCompilationTest` 中自定义 evaluator 缺失 cache 方法的回归。
- **Shade API 统一重构**：
  - 删除 `buildPixelContextCached`，引入 `TriangleInfo` struct + `decodeTriangle()` + `evaluateTrianglePositions<TVE>()` + `fetchCachedTrianglePositions<TVE>()` 共享解码/位置获取 helper。
  - `buildPixelContext()` 改为接收 `TriangleInfo + 3 float3 worldPos`，由调用方决定位置来源（inline evaluate 或 cache read），消除 ~80 行重复代码。
- `handleDebugMode` 的 barycentric/normal/UV/deformation debug 模式全部改用 `decodeTriangle` + `evaluateTrianglePositions`，不再内联手动构造 `VertexEvalContext`。
- Phase2 DeformPass 已挂入 `ClusterHiZStage.cs`，与 Phase1 共用 `DeformCache`/`CacheOffsets` buffer。

## [2026-04-03] BATCH-07 Asset Pipeline Destructive Rework
- 删除 `AssetId`、`IAssetRecord`、`IAssetResolver`、`IAssetDatabase`、`IAssetWorkspace`、`AssetNode`、`ManifestAssetDatabase` 等 legacy facade，顶层统一为 `IAsset + AssetManifest + AssetDatabase + AssetTypeRegistry + MetaManagers`。
- `AssetManifestScanner` 改为 registry 分发；`AssetDatabase` 统一承担 load/import/resolve/list/validate/watch/rebuild。
- `MaterialAssetLoader` / `MaterialInstanceLoader` / `MeshMaterialResolver` 删除 resolver 重载，Runtime 默认资产查找改走 `AssetDatabase`。
- 验证结果：`dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj -- RunConfiguration.MaxCpuCount=1` → `124 passed, 0 failed, 1 skipped`；`dotnet build SomeEngine.slnx` 通过。

## [2026-04-03] BATCH-07 Task 5 Corrective Follow-up
- `AssetTypeRegistry` 改为宿主显式注册 builtin handlers/importers；`SlangSourceImporter` 从 registry 内部拆出。
- `AssetDatabase.Load<T>(path)` 增加 `IsUpToDate()`，只在 source 过期时触发导入；watcher 只发 `SourceChanged`，不再内建自动 reimport。
- `AssetManifestScanner` 删除 `is ShaderAsset` 分支，仅依赖 `.asset.meta`；`ShouldSkip` / registry 配置检查 / path helper / JSON options 去重。
- `Validate()` 不再输出根资产 `OrphanAsset`；验证结果更新为 `127 passed, 0 failed, 1 skipped`。

## [2026-04-03] BATCH-07 Task 5i-5j Root-Cause Fix
- 扫描策略从“项目根全盘递归 + skip”改为 `assetRoots` 定向扫描；`AssetDatabase` 新增 `assetRoots:` 构造参数，默认只扫 `assets/`。
- `ShouldSkip` 相关路径排除逻辑已删除；manifest 输出目录不再靠硬编码前缀规避，而是天然不在扫描根内。
- watcher 暂时降级为空实现：`AssetDatabase` 不再实现 `IDisposable`，`StartWatching()` / `StopWatching()` 为 no-op。
- 验证结果更新为 `129 passed, 0 failed, 1 skipped`；`dotnet build SomeEngine.slnx` 通过。

## [2026-04-07] BATCH-07b DI 驱动的注册模式 + Mesh Importer
- 删除 `AssetTypeRegistry.cs` 和 `AssetTypeRegistration.cs`，消灭静态全局可变状态。
- 4 个 handler 从 `AssetTypeRegistration` 内部 nested class 提取为 `Pipeline/` 下独立 public 类（`ShaderAssetTypeHandler`、`MaterialAssetTypeHandler`、`MaterialInstanceAssetTypeHandler`、`MeshAssetTypeHandler`）。
- `AssetDatabase` 构造函数改为接受 `IEnumerable<IAssetTypeHandler>` + `IEnumerable<IAssetImporter>`，替代静态 registry 查找。
- 新增 `GltfSourceImporter : IAssetImporter`，走统一 importer 路径导入 `.gltf/.glb` → `.mesh.asset` + `.material.asset`。
- `ClusterBuilder.Process()` 的 `materialGuidResolver` 回调已删除。
- Runtime `TryImportModelToMesh()` 简化为 `assetDb.Import(resolvedPath)`。
- Source Generator (`AssetPipelineCatalogGenerator`) 自动发现 handler/importer 实现类，生成 `GeneratedAssetPipelineCatalog`。

## [2026-04-10] BATCH-07c 泛型 AssetProvider + TextureAsset Pipeline
- 用泛型 `AssetProvider<T>` + 非泛型 `IAssetProvider` 桥接替换 `IAssetTypeHandler`，接口彻底删除。
- `AssetDatabase` 引入 `TypedStore<T>` 按 `typeof(T)` 分桶缓存，运行时零装箱。`Load<T>` 约束放宽为 `where T : class`（支持 `ITexture` 等非 IAsset 类型）。`AssetDatabase` 实现 `IDisposable`。
- 5 个 `AssetProvider<T>`：Shader / Material / MaterialInstance / Mesh / TextureData（Assets 层）；1 个 GPU Provider：`TextureAssetProvider : AssetProvider<ITexture>`（Render 层，StbImageSharp 解码 + 1x1 raw RGBA 快路）。
- 新增 `texture_asset.fbs` FlatBuffer schema + `TextureAssetSerializer`。
- 新增 `WellKnownAssets` 常量类（3 个默认纹理稳定 GUID）。`tools/GenerateDefaultAssets` 更新，生成对应 `.texture.asset` 文件。
- `GltfSourceImporter` 更新：提取 GLTF 图片 → `.texture.asset`，`MaterialAsset.TextureBinding.Path` 改为存 `AssetGuid` 字符串。
- `MaterialAssetLoader` / `MaterialInstanceLoader` 纹理引用从路径改为 `AssetGuid.TryParse()`。
- 删除 `RenderGraph.GetOrCreatePersistentTexture`、`TextureFileLoader.CreateTexture`、`ClusterMaterials.cs`（死代码）。
- `AssetPipelineCatalogGenerator` 更新：自动发现 `IAssetProvider` 替代 `IAssetTypeHandler`。
- `docs/assets/pipeline_overview.md` 重写：删除过期的 `IAssetTypeHandler` / `AssetTypeRegistry` 段落，替换为 `AssetProvider<T>` 模型、Providers 表、Default Textures 段落。
- 验证结果：`140 passed, 0 failed, 1 skipped`；`dotnet build` 0 errors, 17 warnings (pre-existing)。

## [2026-05-05] BATCH-16 Temporal Quality And Runtime Validation
- Added deterministic temporal jitter via `TemporalJitter`: 8-sample Halton pixel offsets, pixel-to-NDC conversion, and projection application without mutating camera state.
- Extended temporal resolve settings from a single history weight to explicit bounded controls for history weight, neighborhood clamp scale/minimum, and motion rejection.
- Updated `temporal_resolve.hlsl` to clamp previous HDR history against a small current-frame neighborhood and reduce history under high motion.
- Runtime and Editor now apply temporal jitter when temporal resolve is enabled. Runtime exposes F7 reset, F8 jitter toggle, F9 resolve toggle, and an ImGui temporal validation panel.
- Verification: focused temporal tests `20 passed`; full solution build `0 errors` with existing DagVisualizer NU1903 warning; full tests `225 passed, 0 failed`.

## [2026-05-09] BATCH-17 Runtime Debug State And Validation Console Rework
- Replaced scattered Runtime debug locals with `RuntimeDebugState`, `RuntimeDebugInputState`, `RuntimeHiZPreviewCache`, and `RuntimeFrameTimings`.
- Reimplemented the monolithic `Engine Debug` ImGui block as a bounded `Runtime Validation Console` with Frame, Rendering, Temporal, Scene, and Assets tabs.
- Added `RuntimeDebugCommand` routing so reset/dump/load/import/spawn/evict actions are executed by `Program.cs`, not directly by UI drawing code.
- Removed the old generic per-entity transform inspector from Runtime UI; scene controls remain validation helpers rather than editor features.
- Verification: `dotnet build SomeEngine.slnx --no-restore -v minimal -m:1` passed with 0 errors and the existing DagVisualizer NU1903 warning; `dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet` passed `243/243`.
