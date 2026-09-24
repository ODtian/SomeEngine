# Material Runtime Architecture

本文记录当前有效的材质运行时架构。设计决策和删除目标以
[`render_world_pipeline_refactor.md`](../rendering/render_world_pipeline_refactor.md)
为准；本文只展开材质系统在当前代码里的边界。

## Core Model

材质运行时模型只有三层：

```text
serialized asset
  -> loader
  -> runtime Material / Shader / Texture / Mesh
  -> Handle<T>
```

序列化资产可以保存 GUID、字符串字段和导入元数据。运行时热路径只使用
`AssetStore` 和 `Handle<T>`，不直接使用 fbs schema 类型，也不通过 GUID 做
pipeline 查询。

运行时引用统一是：

```csharp
Handle<Material>
Handle<Shader>
Handle<Texture>
Handle<Mesh>
```

不要为特定资产类型再引入 `ShaderHandle`、`MaterialHandle`、`TextureHandle`
这类薄包装。`Handle<T>` 的 id + generation 是运行时身份；GUID 是落盘和
导入身份。

## Material

`Material` 是运行时 asset，不是 ECS entity，也不是 fbs 表。

`Material` 可以保存：

- 参数字段。
- `Handle<Shader>`。
- `Handle<Texture>`。
- `MaterialState`。
- `MaterialPass[]`。

`Material` 禁止保存：

- `World` 或私有 ECS store。
- `BindingSetHandle`。
- `PipelineHandle`。
- render graph transient resource。
- slot offset。
- bin index。
- pass entity。
- GUID lookup service。

Material 只暴露三类版本事实：

- `ScalarVersion`：scalar payload 或 scalar layout 变化，驱动 material scalar buffer 上传。
- `PassVersion`：`MaterialPass`、shader entry 或 pipeline state 形状变化，驱动 material item / bin / PipelineState 输入重建。
- `BindingVersion`：texture 等 material binding 变化，驱动已有 bin 的 material binding 投影刷新，不重建 PipelineState。

## MaterialPass

`MaterialPass` 是运行时 material 暴露给 pipeline 的入口记录：

```csharp
public readonly record struct MaterialPass(
    string Target,
    Handle<Shader> Shader,
    string EntryPoint,
    MaterialState State);
```

字段含义：

- `Target`：pipeline 拥有的命名空间，例如 `cluster.shade`、`cluster.raster.sw`。
- `Shader`：运行时 shader asset handle。
- `EntryPoint`：shader 入口。
- `State`：材质状态，例如 surface、cull、bounds expansion、stencil 规则。

`MaterialPass` 不表达 ECS component，不表达 render graph pass，也不保存 GPU
handle。pipeline 只消费自己认识的 target；其它 target 对该 pipeline 不存在。

## Asset Loading

schema 到 runtime 的转换只发生在加载边界：

```text
MaterialAsset / ShaderAsset / MeshAsset
  -> MaterialAssetLoader / RuntimeAssetLoader
  -> AssetStore.Add(guid, runtimeAsset)
  -> Handle<T>
```

加载器可以读取 schema。runtime asset 类型、`RenderWorld`、pipeline 和 material
pass 热路径不能把 schema 类型当成自己的公共 API。

`AssetStore<T>` 管理单一 runtime asset 类型。`AssetStore` 是多类型聚合入口，
不是某个 asset 的专用 store。

## RenderWorld Boundary

`RenderWorld` 只保存权威 CPU scene state：

- source entity。
- transform。
- previous transform。
- instance index。
- `Handle<Mesh>`。
- `RenderMaterials.Materials[]` 中的 `Handle<Material>`。
- dirty flags。

`RenderWorld` 不保存：

- schema asset。
- material pass entity。
- shader entry record。
- binding set。
- PipelineState key。
- slot offset。
- bin index。
- submit order。

`RenderWorldExtractor` 从 authoring world 提取 mesh/material handle 和 dirty state。
pipeline 每帧读取当前 `RenderWorld`，但不长期持有它作为 source state。

## Pipeline Derived Data

pipeline 可以保存派生数据：

- material item。
- material bin。
- batch。
- dispatch args。
- bin。
- pipeline-owned `SlotBuffer`。
- instance header patch。
- GPU resource handle。

这些数据是 pipeline 内部提交数据，不写回 `RenderWorld`、`Material` 或
`MaterialPass`。

cluster 当前使用的关系是：

```text
RenderWorld instance
  -> mesh handle
  -> material handle table
  -> MaterialPass target selection
  -> shader handle + entry point
  -> PipelineState / BindSet owner lookup
  -> material bin
  -> SlotBuffer
  -> instance header SlotOffset
```

`SlotBuffer` 是 pipeline 内部 GPU index table。cluster 固定字段语义：

```text
field 0 = raster
field 1 = shade
field 2 = deform
```

GPU 通过 `InstanceHeaderLayout.SlotOffset + local material index + field` 查到
pipeline 写入的 bin/material group index。

## PipelineState And BindSet

PipelineState 生命周期属于全局 `PipelineCache`。dirty 输入变化时查找或创建 PipelineState，得到
`PipelineTicket`；pass、batch 或 material bin 只保存 ticket，执行时通过 render graph context 解析 ready `PipelineHandle`。

material PipelineState key 至少包含：

- `Handle<Shader>`。
- shader entry。
- shader content version。
- `MaterialState`。
- binding layout key。
- backend。
- graphics path 的 topology、target format、depth format、sample count。

BindSet 生命周期属于 RenderGraph 的 BindSet owner。稳定 material binding input
由 material 字段生成；涉及 transient graph resource 的 binding set 只能按当前帧
resource/view identity 查找，不能跨帧复用旧 transient view。

## Authoring

authoring world 可以保存用户编辑态组件，例如：

- `MeshInstance`。
- `MeshMaterialBindings`。
- editor/importer 生成的 asset GUID。

extract 后进入 runtime path 的是 `Handle<Mesh>` 和 `Handle<Material>`。材质系统
不拥有全引擎 authoring，也不把 pass entity 模型塞进 render submission。

## Current Invariants

- runtime material/shader/mesh/texture 都是 asset。
- runtime asset 引用统一使用 `Handle<T>`。
- `Material` 和 `Shader` 都不持有 GPU 生命周期。
- pipeline 生成 SlotBuffer/bin/batch，但不把这些派生数据写回 source state。
- schema 类型只出现在 loader/importer/asset database 边界。
- 旧 slot/bin 公共框架、pass entity 材质提交、GUID 热路径查询和旧 bind cache 语义均不作为当前设计存在。
