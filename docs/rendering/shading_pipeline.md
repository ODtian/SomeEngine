# 着色管线

> 当前有效方向：旧 shade bin、旧 material slot、旧 PipelineState slice、`ClusterPipelineStateSet` 设计全部作废。保留 SlotBuffer 和 binning 算法，但归 pipeline 内部派生数据所有。

## 作废内容

Material shade 不再通过：

- shade bin 旧公共框架。
- material slot buffer 旧协议。
- slot offset 写回 `RenderWorld`。
- PipelineState slice。
- `ClusterPipelineStateSet`。
- 旧 binding set cache。
- reflected bind recipe。
- cluster-specific baker output。

这些概念不能改名保留。

## 输入

Shade pass 从 pipeline 派生 item 直接驱动。item 来源是：

```text
RenderWorld instance state
AssetStore runtime asset
Material
MaterialPass
Shader
pipeline visibility result
pipeline SlotBuffer
```

`RenderWorld` 只提供 CPU 对象状态，不提供 material pass entity、shader entry fact、slot/bin、PipelineState 或 BindSet。

## SlotBuffer

Shade pass 可以消费 pipeline-owned SlotBuffer。

SlotBuffer 是 GPU index table：

```text
slot offset + local index + shade field -> shade bin / material group
```

SlotBuffer 不再叫 material slot buffer。slot offset 进入 pipeline instance header，不写回 `RenderWorld` source state。

## Bin

Shade binning 算法保留：

- count。
- reserve。
- scatter。
- bin counts。
- bin offsets。
- indirect args。
- pixel coord buffer。

输入从旧 material slot buffer 改为 pipeline SlotBuffer 和 pipeline material/bin table。

逻辑仍然是：

```text
vis buffer / visible clusters
  -> 查 instance
  -> 查 local material index
  -> 查 SlotBuffer
  -> 得到 shade bin
  -> 写入对应 bin 的 count / offset / args
```

## PipelineState

Shade pass 不使用 `ClusterPipelineStateSet` 或 dispatch table。

Shade pass 通过全局 PipelineCache 在 dirty 时声明 PipelineState 需求，得到 `PipelineTicket`。pass、material bin 或 pipeline 派生数据只保存 ticket；执行路径通过 `RenderGraphContext.GetPipeline` 解析 ready `PipelineHandle`。

PipelineState 生命周期归全局 PipelineCache。

## Binding

Shade pass 不使用旧 `BindRecipe`、`BindRole` 或旧 binding set cache。

Material 字段由生成器生成 binding input。Shade pass 用 binding input 查全局 BindSet owner，取得 `BindingSetHandle`。

frame、pass、material、transient binding handle 放在使用位置，不包成聚合引用。

## Vertex

着色管线不消费 `VertexLayoutReq`。

mesh/shader 基础兼容性只由 vertex stride check 提供：

```text
mesh vertex stride == shader entry required stride
```

attribute 语义由 shader/eval 自己解释，不进入通用 material pass。

## Baker

着色管线不消费 cluster-specific baker output。

`ClusterShaderBaker`、`ClusterComponentBaker`、`ClusterPassRules` 这类把材质直接烘成 cluster shade component 的设计作废。

shade pass 从运行时 material、shader metadata、pipeline item、SlotBuffer 和 GPU buffer 构建提交数据。

## 重建参考

- [RenderWorld 与 Pipeline Reference](render_world_pipeline_reference.md)
- [RenderWorld 与 Material 重构文档](render_world_pipeline_refactor.md)
