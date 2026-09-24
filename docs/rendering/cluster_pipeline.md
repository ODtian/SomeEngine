# Cluster Pipeline 设计指南

> 当前有效方向：删除旧 `ClusterPipelineStateSet`、旧 slot/bin 公共框架、旧 bind set cache、dispatch table 和 cluster baker。保留全局 PipelineCache / BindSet owner，保留 SlotBuffer 和 binning 算法，但它们归 cluster pipeline 内部派生数据所有。

## 作废内容

以下旧 cluster pipeline 内容不可作为实现依据：

- `ClusterPipelineStateSet` / `ClusterPipelineStateSlice` / PipelineState slice lifecycle。
- 旧 `PipelineStateCache` 耦合路径。
- `BindRecipe` / `BindRole` / 旧 binding set cache / SRB pool。
- `SlotStage` / `SlotTable` / `SlotPreparer` / `SlotCache` 作为公共 slot 体系。
- `BinSpace` / `BinQueue` / `ClusterBinTable` 作为跨 pipeline 公共框架。
- `MaterialSlotBind`。
- `MaterialSlotOffset` 写回 `RenderWorld` source state 的协议。
- `VertexLayoutReq` / opaque vertex layout blob。
- `ClusterBakers` / `ClusterShaderBaker` / `ClusterComponentBaker` / `ClusterPassRules` / `ClusterPassCopy`。

这些概念不能改名保留。

## 保留逻辑

旧类删除不等于算法删除。cluster 仍需要这些逻辑：

- SlotBuffer 作为 GPU index table。
- slot offset 分配。
- slot dirty range。
- SlotBuffer upload。
- field 布局。
- 按 signature 分组。
- bin index。
- args bin map。
- count / reserve / scatter。
- bin counts。
- bin offsets。
- indirect args。

这些逻辑全部归 cluster pipeline 内部，不进入 `RenderWorld`，不进入 `Material`，不进入 `MaterialPass`。

## 新边界

Cluster pipeline 只保留真实 GPU 算法边界：

```text
Upload
Traverse
Cull
Raster
Shade
Resolve
Temporal
```

边界之间通过显式 output record 传递 graph handle。材质、shader entry、render state 从 `RenderWorld + AssetStore + MaterialPass` 读取，不再经旧 slot/bin/dispatch，也不经 cluster baker 私有组件。

## RenderWorld 输入

`RenderWorld` 提供权威 CPU 对象状态：

- instance。
- transform。
- mesh handle。
- material handle。
- dirty flags。

`RenderWorld` 不提供：

- PipelineState key。
- BindSet key。
- `PipelineHandle`。
- `BindingSetHandle`。
- slot/bin。
- material slot offset。
- dispatch table。
- `VertexLayoutReq`。
- `MaterialSlotBind`。
- cluster-specific baker output。
- material pass entity。
- shader entry fact。

## Asset 输入

Cluster pipeline 通过 `Handle<T>` 从 `AssetStore` 聚合入口读取运行时 asset；实际每个 runtime asset 类型由同一个泛型实现 `AssetStore<T>` 管 id + generation 生命周期：

- `Handle<Material>`。
- `MaterialPass`。
- `Handle<Shader>`。
- mesh data。
- shader metadata。

fbs 和 guid 不进入 cluster 热路径。

## Material 契约

Material / cluster target / RHI state 的命名结论见 [Cluster Material Contract](cluster_material_contract.md)。

当前有效结论：

- `MaterialPass.Pipeline` 改为 `MaterialPass.Target`。
- `MaterialPass.Entry` 改为 `MaterialPass.EntryPoint`。
- `PipelineState` 改为 `MaterialState`。
- `ClusterPipes` 删除，target parsing 收回 `MaterialItems`。
- `ShaderVariantRef` 改为 runtime `PassShader`。
- `ClusterItem` 改为 `MaterialItem`。
- `ClusterBatch` 改为 `MaterialBin`。
- `ClusterBatches` 改为 `MaterialItems`。
- `ClusterDispatch` / `ClusterDispatches` 删除。

`MaterialPass.Target` 是通用 material 字符串目标，不是 cluster enum。cluster 只在 `MaterialItems` 的私有 target 解析中解释 `cluster.*` target。forward、shadow、deferred 等路径可以有自己的 target 命名空间，不回灌到 `MaterialPass`。

## SlotBuffer

Cluster 可以持有自己的 SlotBuffer。SlotBuffer 是 pipeline 内部 GPU 查询表：

```text
slot offset + local index + field -> bin / shader / material group
```

field 布局由 cluster pipeline 固定。例如：

```text
field 0 = raster
field 1 = shade
field 2 = deform
```

slot offset 进入 cluster 自己上传的 instance header，不写回 `RenderWorld` component。

SlotBuffer 存紧凑 index，不存 entity、material guid 或 shader object。

## Bin

Cluster bin 是 pipeline 内部分组结果，不是跨 pipeline API。

bin 逻辑：

```text
MaterialPass / PipelineState / BindSet / order
  -> 完整 signature
  -> bin index
  -> SlotBuffer field value
  -> GPU count/reserve/scatter
```

hash 只能用于 bucket，命中必须走完整 equality。

## PipelineState

Cluster pass 不使用旧 `ClusterPipelineStateSet` 或 dispatch table。

Cluster pass 通过全局 PipelineCache 在初始化或 dirty 时声明 PipelineState 需求，得到 `PipelineTicket`。pass、material bin 或 cluster 派生数据只保存 ticket；执行路径通过 `RenderGraphContext.GetPipeline` 解析 ready `PipelineHandle`。

PipelineState 生命周期归全局 PipelineCache，不归 pass/batch。

## Binding

Cluster pass 不使用旧 `BindRecipe`、`BindRole`、旧 binding set cache。

Material 字段通过生成器生成 binding input。Cluster pass 用 binding input 查全局 BindSet owner，取得 `BindingSetHandle`。

frame/pass/transient binding handle 放在使用位置，不包成聚合引用。

## Baker

cluster pipeline 不再依赖 cluster-specific baker。

禁止 baker 直接写：

- cluster raster/shade/deform component。
- BVH root。
- material slot offset。
- material slot bind。
- vertex layout opaque blob。
- cache/surface signature blob。

Cluster pipeline 自己从运行时 material、shader metadata、mesh data 和 `RenderWorld` instance state 构建内部 item、batch、bin、SlotBuffer 和 GPU buffer。

## Pass

每个 pass 直接拥有或接收自己需要的引用：

- fixed pass 通过全局 PipelineCache 声明 fixed PipelineState，并在初始化路径显式 warmup。
- material-dependent pass 从 cluster item 选择 material/pass/shader。
- pass 自己写 binding 映射。
- pass 接收 SlotBuffer graph/RHI handle。
- pass 不消费旧 material slot buffer。
- pass 不消费 dispatch table。

## 重建参考

- [RenderWorld 与 Pipeline Reference](render_world_pipeline_reference.md)
- [RenderWorld 与 Material 重构文档](render_world_pipeline_refactor.md)
