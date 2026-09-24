# Cluster Pipeline Type Audit

依据：

- `docs/rendering/render_world_pipeline_refactor.md`
- `docs/rendering/cluster_pipeline.md`
- `docs/rendering/cluster_material_contract.md`

结论规则：

- `ClusterTypes.cs` 不在文档明确保留列表内，已删除。
- Stage 只保留 `Scene / Raster / Shade / Output` 这些 pass 编排器；单 pass wrapper 不允许保留。
- Pipeline 持有跨 pass graph resource 创建和 temporal history/state 更新；stage 只编排 pass 调用。
- 材质入口只走 `MaterialPass.Target`；asset loader 保留任意 renderer target 字符串，cluster 只在 `MaterialItems` 内解释 `cluster.*` target。
- `SlotBuffer`、bin、args、dirty range、mesh page、GPU buffer 是 cluster pipeline 内部派生数据，不进入 `RenderWorld`、`Material` 或 `MaterialPass`。
- PipelineState 生命周期属于全局 PipelineCache；cluster 内部状态只保存 `PipelineTicket`，执行时通过 RenderGraph context 解析 ready handle。
- 旧 `ClusterPipelineStateSet`、`ClusterPipelineStateSlice`、`BindRecipe`、`BindRole`、旧 slot/bin 公共框架、dispatch table、baker 私有组件和 cache/surface signature 协议均不允许保留。

## Removed

| 旧类型/文件 | 结论 | 处理 |
|---|---|---|
| `ClusterTypes.cs` | 文档未明确保留；聚合桶掩盖真实职责。 | 删除，拆成 enum/options/uniform/output/camera 语义文件。 |
| `ClusterPipelineStateSet` / `ClusterPipelineStateSlice` | 文档明确作废。 | 无生产类型。 |
| `PipelineOwner` | 文档未保留；PipelineState 生命周期归全局 store。 | 无生产类型。 |
| `BindRecipe` / `BindRole` | 文档明确作废。 | 无生产类型。 |
| `ClusterModes` / `ClusterRow` | 文档未保留。 | 无生产类型。 |
| `ClusterBatch` / `ClusterBatches` | 文档要求替换为 `MaterialBin` / `MaterialItems`。 | 无生产类型。 |
| `ClusterDispatch` / `ClusterDispatches` | 文档明确删除。 | 无生产类型。 |
| `ClusterRaster` / `ClusterShadeComponent` / `ClusterDeform` ECS component | cluster baker 私有组件作废。 | 无生产 component。 |
| `CacheSignature` / `SurfaceSignature` shader protocol | 旧 baker 校验协议残留。 | schema/importer/shader attribute 已删除。 |
| `ClusterUploadStage` / `ClusterCullStage` / `ClusterResolveStage` | 单 pass wrapper 或旧边界名，不满足 Stage 作为 pass 编排器。 | 删除，合并为 `ClusterSceneStage` / `ClusterOutputStage`。 |

## Type Table

| 文件 | 类型 | 文档归属 | 判定 | 说明 |
|---|---|---|---|---|
| `BvhPatchPass.cs` | `BvhPatchPass` | Upload 内部 pass | 通过 | 根据 mesh handle 派生 BVH root patch，属于 cluster GPU 上传派生数据。 |
| `BvhPatchPass.cs` | `PatchUniforms` | Upload uniform | 通过 | 固定 pass uniform，不进入 RenderWorld。 |
| `BindInput.cs` | `BindInput` | Binding | 通过 | 只组合 common binding、`MaterialBindings` 和 fallback；不持有 `AssetStore` / `Material`。 |
| `ClusterBinGpu.cs` | `ClusterBinGpu` | Bin | 通过 | bin count/offset/args GPU buffer helper，文档保留 bin 算法。 |
| `ClusterBvh.cs` | `ClusterBvhPatch` | Upload 派生数据 | 通过 | mesh handle 到 BVH root 的内部 patch 数据。 |
| `ClusterBvh.cs` | `ClusterBvh` | Upload 派生数据 | 通过 | 管理 BVH root upload 数据，不写回 RenderWorld。 |
| `ClusterCamera.cs` | `ClusterCameraData` | Frame input | 通过 | camera uniform source，边界间显式传递。 |
| `ClusterCamera.cs` | `ClusterCamera` | Frame input | 通过 | 从 runtime camera 生成 cluster camera 数据。 |
| `ClusterSceneStage.cs` | `ClusterCullPass` | Scene 内部 pass | 通过 | cull 的固定 compute pass，PipelineState 只保存 ticket。 |
| `ClusterSceneStage.cs` | `ClusterSceneStage` | Stage | 通过 | struct pass 编排器，持有 BVH patch、traverse、cull 三个 pass。 |
| `ClusterDebugFeature.cs` | `ClusterDebugFeature` | Debug feature | 通过 | debug graph feature，不参与 material/dispatch/slot 协议。 |
| `ClusterDeformPass.cs` | `ClusterDeformUniforms` | Raster/Deform uniform | 通过 | deform cache/material deform pass uniform。 |
| `ClusterDeformPass.cs` | `DeformCacheResources` | Raster/Deform output data | 通过 | pipeline 创建 deform cache graph resources，stage/pass 只显式传递和消费。 |
| `ClusterDeformPass.cs` | `DeformCacheFrame` | Raster/Deform frame output | 通过 | deform cache frame 输出记录，不是顶层边界。 |
| `ClusterDeformPass.cs` | `ClusterDeformPass` | Raster 内部 material pass | 通过 | deform bin 的消费者，直接消费 `MaterialBin` 的 PipelineState/binding/material binding，不持有 source state 或本地 PipelineCache。 |
| `ClusterDeformPass.cs` | `DeformDispatch` | Deform dispatch data | 通过 | 本 pass 的 dispatch 参数，不是旧 `ClusterDispatch` 聚合。 |
| `ClusterDrawPass.cs` | `ClusterDrawPass` | Raster 内部 pass | 通过 | HW raster draw pass，构造时解析固定 PipelineState，消费 raster bins 和 `MaterialBin`。 |
| `ClusterEnums.cs` | `ClusterDebugMode` | Config/debug | 通过 | public debug option，已从 `ClusterTypes.cs` 拆出。 |
| `ClusterEnums.cs` | `HiZMode` | Config/debug | 通过 | cull/HiZ runtime option，已从 `ClusterTypes.cs` 拆出。 |
| `ClusterLimits.cs` | `ClusterLimits` | Internal constants | 通过 | cluster 内部资源上限，不是协议层。 |
| `MaterialItem.cs` | `MaterialItem` | Material contract | 通过 | 文档明确保留：material handle、state、shader entries 的 CPU 投影。 |
| `MaterialItems.cs` | `SlotFrame` | SlotBuffer | 通过 | SlotBuffer graph output record，文档保留 SlotBuffer。 |
| `MaterialItems.cs` | `MaterialItems` | Material contract | 通过 | 文档明确保留：解释 `MaterialPass.Target`，维护 slots/bins/pass states。 |
| `MaterialItems.cs` | `RhiCreate` | Material runtime helper | 通过 | pass state 创建参数，局部 helper，不是旧 cache owner。 |
| `ClusterMeshes.cs` | `ClusterMeshes` | Mesh/page data | 通过 | 从 mesh handle 派生 GPU page/root 数据，RenderWorld 不存 BVH root；不是 dispatchtime asset store。 |
| `ClusterMeshes.cs` | `PendingPatch` | Mesh/page data | 通过 | mesh upload 后的内部 patch 队列项。 |
| `ClusterOptions.cs` | `ClusterConfig` | Runtime config | 通过 | pipeline config，不是 RenderWorld/material 协议。 |
| `ClusterOptions.cs` | `ClusterOptions` | Runtime config | 通过 | runtime options，不含旧 mode 类型。 |
| `ClusterOutputs.cs` | `ClusterTraverseOutput` | Traverse output | 通过 | 显式 graph handle output。 |
| `ClusterOutputs.cs` | `ClusterCullOutput` | Cull output | 通过 | 显式 graph handle output。 |
| `ClusterOutputs.cs` | `RasterBinFrame` | Raster bin output | 通过 | bin algorithm output，文档保留。 |
| `ClusterOutputs.cs` | `DeformBinFrame` | Deform bin output | 通过 | deform cache/material deform 的真实输入。 |
| `ClusterOutputs.cs` | `ClusterRasterOutput` | Raster output | 通过 | raster 边界输出 record。 |
| `ClusterOutputs.cs` | `ShadeBinFrame` | Shade bin output | 通过 | shade bin algorithm output。 |
| `ClusterOutputs.cs` | `ClusterShadeOutput` | Shade output | 通过 | shade 边界输出 record。 |
| `ClusterOutputStage.cs` | `ClusterOutputStage` | Stage | 通过 | struct pass 编排器，持有 resolve 和 temporal pass；temporal history/state 更新归 `ClusterPipeline`。 |
| `MaterialBin.cs` | `MaterialBin` | Material contract | 通过 | 文档明确保留；保存 PipelineTicket、binding table、material binding 投影、scalar graph handle。 |
| `ClusterPass.cs` | `ClusterPass` | Material contract | 通过 | cluster 私有 target enum，只在 pipeline 内解释 `MaterialPass.Target`。 |
| `MaterialItems.cs` | `ParseTarget` | Material contract | 通过 | `MaterialItems` 私有 target 解析；不再单独保留 target parser 类型。 |
| `ClusterPipeline.cs` | `ClusterPipeline` | Pipeline owner | 通过 | public pipeline facade。 |
| `ClusterPipeline.Runtime.cs` | `InstanceStats` | Runtime helper | 通过 | scan 结果 helper，不是 RenderWorld state。 |
| `ClusterPipeline.Runtime.cs` | `ClusterPipeline` | Pipeline owner | 通过 | 持有 Scene/Raster/Shade/Output stage、pipeline 资源 owner、temporal history 更新、item/page/material owner 和 pipeline 配置。 |
| `ClusterResolvePass.cs` | `ClusterResolvePass` | Output 内部 pass | 通过 | resolve compute pass，由 `ClusterOutputStage` 编排，不是 Stage wrapper。 |
| `ClusterResources.cs` | `ClusterBuffers` | Upload output | 通过 | cluster GPU buffer output record。 |
| `ClusterResources.cs` | `ClusterGpuResources` | Upload resource owner | 通过 | cluster 内部 GPU buffer owner。 |
| `ClusterShaders.cs` | `ClusterShaders` | Pipeline shader input | 通过 | pipeline 构造只接已解析 shader bundle，不直接接 `AssetDatabase` / `ClusterRenderAsset`。 |
| `ClusterSlotBuffer.cs` | `DirtyRange` | SlotBuffer dirty data | 通过 | dirty 时上传 slot range；不是 “dirty key”。 |
| `ClusterSlotBuffer.cs` | `SlotDirty` | SlotBuffer dirty tracker | 通过 | 管理 dirty ranges，符合 SlotBuffer 保留逻辑。 |
| `ClusterSlotBuffer.cs` | `ClusterSlotBuffer` | SlotBuffer | 通过 | 文档保留的 GPU index table。 |
| `ClusterSlotGpu.cs` | `ClusterSlotGpu` | SlotBuffer GPU upload | 通过 | 上传 SlotBuffer，内部派生数据。 |
| `ClusterSlotLayout.cs` | `ClusterSlotSpan` | SlotBuffer layout | 通过 | slot offset/local material span。 |
| `ClusterSlotLayout.cs` | `ClusterSlotLayout` | SlotBuffer layout | 通过 | 固定 field layout，不回写 Material/RenderWorld。 |
| `ClusterTraversePass.cs` | `ClusterTraversePass` | Scene 内部 pass | 通过 | BVH traverse pass，由 `ClusterSceneStage` 编排。 |
| `ClusterUniforms.cs` | `CullingUniforms` | Cull uniform | 通过 | fixed pass uniform。 |
| `ClusterUniforms.cs` | `DrawUniforms` | Raster uniform | 通过 | draw/raster uniform。 |
| `ClusterUniforms.cs` | `DrawDispatchUniforms` | Raster uniform | 通过 | indirect dispatch uniform。 |
| `ClusterUniforms.cs` | `SwRasterUniforms` | Raster uniform | 通过 | SW raster pass uniform。 |
| `ClusterUniforms.cs` | `BinningUniforms` | Bin uniform | 通过 | raster bin pass uniform。 |
| `ClusterUniforms.cs` | `ShadeBinUniforms` | Bin uniform | 通过 | shade bin pass uniform。 |
| `ClusterUniforms.cs` | `ShadeUniforms` | Shade uniform | 通过 | material shade pass uniform。 |
| `ClusterVariants.cs` | `ClusterVariants` | Shader helper | 通过 | backend/stage entry lookup helper。 |
| `DepthMergePass.cs` | `DepthMergePass` | Raster internal pass | 通过 | 构造时解析固定 PipelineState，SW depth merge into HW depth target。 |
| `HiZPass.cs` | `HiZOutput` | Cull/Raster output | 通过 | HiZ graph handle output record。 |
| `HiZPass.cs` | `HiZPass` | Cull/Raster internal pass | 通过 | HiZ build consumed by cull. |
| `MaterialShadePass.cs` | `MaterialShadePass` | Shade internal pass | 通过 | material compute shade pass，消费 shade bins/pass states。 |
| `MaterialShadePass.cs` | `MaterialShadeDispatch` | Shade dispatch data | 通过 | 本 pass 的 dispatch 参数，不是旧 dispatch table。 |
| `MaterialShadePass.cs` | `ShadeResources` | Shade resource view data | 通过 | pass 局部资源集合。 |
| `MaterialBindings.cs` | `MaterialBindings` | Material contract | 通过 | `MaterialItems` dirty/build 时解析出的 material binding 投影，pass 不反查 `AssetStore` / `Material`。 |
| `MeshPages.cs` | `PageMeta` | Mesh/page data | 通过 | mesh page metadata。 |
| `MeshPages.cs` | `MeshPages` | Mesh/page data | 通过 | mesh page builder/packer。 |
| `PageFaults.cs` | `PageFaults` | Mesh/page data | 通过 | page fault buffer owner。 |
| `PageHeap.cs` | `PageHeap` | Mesh/page data | 通过 | mesh page heap allocator。 |
| `PageHeap.cs` | `FreeBlock` | Mesh/page data | 通过 | heap allocator private block record。 |
| `PageStream.cs` | `PageStream` | Mesh/page data | 通过 | stream mesh pages into cluster resource store。 |
| `RasterBinPass.cs` | `RasterDeformBins` | Raster/deform bin output | 通过 | combined raster/deform bin output。 |
| `RasterBinPass.cs` | `CombinedBinningUniforms` | Bin uniform | 通过 | combined raster/deform bin uniform。 |
| `RasterBinPass.cs` | `RasterBinPass` | Raster internal pass | 通过 | count/reserve/scatter bins，文档保留 bin 算法。 |
| `PassShader.cs` | `PassShader` | Material contract | 通过 | runtime shader handle + entry point；schema asset 通过 `AssetStore` 解析，不进入 cluster material identity。 |
| `ShadeBinPass.cs` | `ShadeBinPass` | Shade internal pass | 通过 | visibility to shade bin pass。 |
| `ShadeBinPass.cs` | `ShadeBinRes` | Shade resource data | 通过 | pass 局部 resource grouping。 |
| `ShadeBinPass.cs` | `ShaderResourceUse` | Binding helper | 通过 | reflection/binding use helper，不是 bind recipe。 |
| `SwRasterPass.cs` | `SwRasterPass` | Raster internal pass | 通过 | SW raster pass，消费 raster bins and slot data。 |
| `SwRasterPass.cs` | `SwDispatch` | Raster dispatch data | 通过 | 本 pass 的 dispatch 参数，不是旧 dispatch table。 |

## Adjacent Protocol Files

| 文件 | 类型 | 文档归属 | 判定 | 说明 |
|---|---|---|---|---|
| `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs` | `MaterialAssetLoader` | MaterialPass creation | 通过 | shader attribute 只生成 `MaterialPass.Target` 字符串；不引用 cluster pipeline 类型，不验证 cluster cache/deform 规则。 |
| `src/SomeEngine.Render/Assets/ClusterPipelineAssets.cs` | `ClusterPipelineAssets` | Pipeline asset loading | 通过 | 负责从 `ClusterRenderAsset` / `AssetDatabase` 解析 shader set 并创建 cluster pipeline，序列化解析不进入 pipeline constructor。 |
| `src/SomeEngine.Assets/Importers/SlangEntryMeta.cs` | `SlangEntryMeta` | Shader metadata import | 通过 | 只保留 entry metadata 读取；旧 cache/surface signature 字段删除。 |
| `src/SomeEngine.Assets/Importers/SlangEntryMeta.cs` | `Attr` | Shader metadata import | 通过 | Slang attribute raw record。 |
| `src/SomeEngine.Render/Components/MeshInstanceComponent.cs` | `MeshInstance` | RenderWorld input | 通过 | RenderWorld 输入 mesh handle，不输入 BVH root。 |
| `src/SomeEngine.Render/Systems/RenderWorldExtractor.cs` | `RenderWorldExtractor` | RenderWorld input | 通过 | 从 ECS 提取 mesh/material handle 和 dirty state，不生成 cluster 私有组件。 |

## Verification Gates

必须继续保持这些搜索只命中测试 gate 或文档：

```powershell
rg -n "ClusterTypes|ClusterPipelineStateSet|ClusterPipelineStateSlice|PipelineOwner|BindRecipe|BindRole|ClusterModes|ClusterRow" src\SomeEngine.Render\Pipelines\ClusterPipeline src\SomeEngine.Render\Assets tests\SomeEngine.Tests
rg -n "CacheSignature|SurfaceSignature|CanonicalDescriptor" src assets\Shaders assets\Schema tests\SomeEngine.Tests
rg -n "ClusterShade\(|ClusterRaster\(|ClusterDeform\(|cluster\.main" src assets\Shaders tests\SomeEngine.Tests
rg -n "ClusterUploadStage|ClusterCullStage|ClusterResolveStage|ClusterTraverseStage" src\SomeEngine.Render\Pipelines\ClusterPipeline
rg -n "private .*Pass|private readonly .*Pass" src\SomeEngine.Render\Pipelines\ClusterPipeline\ClusterPipeline.Runtime.cs
rg -n "AssetStore|MaterialGpu|_materialShaders" src\SomeEngine.Render\Pipelines\ClusterPipeline\ClusterDrawPass.cs src\SomeEngine.Render\Pipelines\ClusterPipeline\SwRasterPass.cs src\SomeEngine.Render\Pipelines\ClusterPipeline\MaterialShadePass.cs src\SomeEngine.Render\Pipelines\ClusterPipeline\ClusterDeformPass.cs
```
