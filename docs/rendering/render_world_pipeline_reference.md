# RenderWorld 与 Pipeline Reference

本文是当前有效架构参考。决策依据见 [RenderWorld 与 Material 重构决策](render_world_pipeline_refactor.md)。

## 核心边界

```text
RenderWorld
  权威 CPU 对象状态

AssetStore
  运行时 asset
  Handle<T>

Pipeline
  整体管理协调
  stage / item / batch / dirty / GPU 派生资源

Stage
  pass struct 集合包装
  pass 管理方法

Pass
  可变执行单元
  持有外部引用、PipelineTicket 和必要的 RHI handle copy

全局 PipelineCache
  dirty 输入 -> PipelineTicket

全局 BindSet owner
  dirty 输入 -> BindingSetHandle
```

`RenderWorld` 不持有 GPU pipeline，不持有 BindSet，不持有提交顺序，不持有 material pass entity，不持有 shader entry fact。

pipeline 可以保存 item、batch、dispatch args、SlotBuffer、cluster bin 等派生 CPU 数据。这些数据不是权威 scene state。

## RenderWorld

`RenderWorld` 只存权威 CPU 对象状态：

- source entity。
- transform。
- previous transform。
- instance index。
- mesh handle。
- per instance data offset。
- dirty flags。

材质列表不在 `RenderInstance` 上重复保存。实例到材质的权威运行时组件是 `RenderMaterials`，pipeline 读取 `RenderInstance + RenderMaterials` 组合生成自己的 slot/header 派生数据。

`RenderWorld` 禁止存：

- fbs 对象。
- guid resolver。
- `PipelineHandle`。
- `BindingSetHandle`。
- PipelineState key。
- BindSet key。
- material pass entity。
- shader entry fact。
- material pass fact。
- slot。
- dispatch。
- bin。
- pass submit order。
- pipeline-specific cluster component。

普通 instance 没有 pass 维度。同一个 instance 可以同时出现在 depth、shadow、forward、velocity、picking、cluster shade 等路径里。pass 相关数据必须由 pipeline 派生，不写回 `RenderWorld`。

## AssetStore

`AssetStore<T>` 管单个运行时 asset 类型的 id + generation 生命周期，不代表 fbs。
`AssetStore` 是多类型聚合入口，只负责把 `Add<T>` / `TryGet<T>` / `TryFind<T>` 分发到对应的 `AssetStore<T>`。

运行时 asset 和序列化资产是多对多模型：

```text
一个 fbs 可以导入多个运行时 asset
多个序列化来源可以合成一个运行时 asset
一个运行时 asset 可以由多个序列化来源生成
```

运行时引用统一使用：

```csharp
Handle<T>
```

示例：

```csharp
Handle<Material>
Handle<Shader>
Handle<Texture>
Handle<Mesh>
```

禁止引入 `ShaderHandle`、`TextureHandle`、`MaterialHandle` 这类特例名字。

guid 只用于落盘、manifest、导入和编辑器引用。运行时热路径不通过 guid resolver 查询 asset。

## Material

`Material` 是运行时 asset，不是 fbs 表。

`Material` 使用强字段保存参数、资源引用和管线设置：

```csharp
public partial struct StandardMaterial
{
    public Handle<Shader> Shader;
    public Handle<Texture> Albedo;
    public float Roughness;
    public float Metallic;
    public MaterialState State;
}
```

生成器根据注解生成：

```csharp
ToBindSet(...)
Passes(...)
```

`ToBindSet(...)` 只生成 binding input，不拥有 `BindingSetHandle` 生命周期。

`Material` 禁止保存：

- `World`。
- `Entity`。
- `PassWorld`。
- `PassEntities`。
- `PipelineHandle`。
- `BindingSetHandle`。
- slot offset。
- BVH 数据。
- pass index。
- fbs node。
- resolver。

## MaterialPass

`MaterialPass` 是运行时 material 暴露给 pipeline 的入口记录。

```csharp
public readonly struct MaterialPass
{
    public string Target;
    public Handle<Shader> Shader;
    public string EntryPoint;
    public MaterialState State;
}
```

`MaterialPass` 不是 fbs 表，不是 ECS entity，不是 render graph pass，不是 PipelineState key。

如果一个 pipeline 路径需要多个 shader entry，就暴露多条 `MaterialPass`。pipeline 按 `Target` 和 `EntryPoint` 收集自己需要的入口。

`MaterialPass.Target` 是名字字段。名字由 pipeline 拥有命名空间：

```text
forward.color
forward.depth
forward.velocity
cluster.main.shade
cluster.main.raster
cluster.debug.shade
shadow.depth
```

## Pass

`Pass` 是可变执行单元。

`Pass` 可以持有：

- resolved `PipelineHandle`。
- resolved `BindingSetHandle`。
- pass-local dirty stamp。
- pass-local fixed resource handle。
- 外部传入资源引用。
- 少量执行暂存。

`Pass` 不持有：

- `RenderWorld` source state。
- `AssetStore` source data。
- fbs。
- `Material` 所有权。
- 全局 PipelineCache / BindSet 生命周期。
- pipeline item 主数组。
- pipeline 配置源状态。

`Pass` 可以持有普通 RHI handle copy，但不拥有 handle 生命周期。PipelineState 例外：pass 或 material bin 只保存 PipelineTicket，执行时解析 ready PipelineHandle。

## Stage

`Stage` 是持有 pass 和管理 pass 的 struct。

`Stage` 负责：

- pass 字段集合。
- pass 初始化顺序。
- pass 更新顺序。
- pass 执行顺序。
- pass 间输出传递。
- 把 pipeline 输入分发给 pass。

`Stage` 不负责：

- `RenderWorld` 存储。
- `AssetStore` 存储。
- item / batch 主数组。
- PipelineState / BindSet 全局生命周期。
- pipeline 配置源状态。

如果 `Stage` 是 struct，pass 管理方法必须避免复制 pass 状态。需要使用 `ref` 语义，或者禁止复制式访问。

## Pipeline

`Pipeline` 是整体管理协调者。

`Pipeline` 持有：

- stage。
- item 数组。
- batch 数组。
- dispatch args。
- pipeline 级 dirty / version。
- pipeline 级 GPU buffer / texture。
- pipeline 配置。
- `AssetStore` 访问入口。
- 全局 PipelineCache 访问入口。
- 全局 BindSet owner 访问入口。

`Pipeline` 不持有：

- fbs。
- 序列化 asset 对象。
- `RenderWorld` source state。
- resolver。
- material 私有 world。

pipeline 通过 `Handle<T>` 引用 `AssetStore` 里的运行时 asset，不和序列化层耦合。

## Item 与 Batch

旧 `DrawList` 不作为通用核心类型继承。

pipeline 可以有自己的 item 和 batch。它们是派生提交数据，不是 `RenderWorld` source state。

item 表达：

- 哪个 instance。
- 哪个 mesh section。
- 哪个 material。
- 哪个 `MaterialPass`。
- 哪个可见结果。

batch 表达：

- 提交分组。
- `PipelineHandle` copy。
- `BindingSetHandle` copy。
- item range 或 dispatch range。

batch 不是 dispatch，不拥有 RHI handle 生命周期。

graphics pipeline 可以有 draw item / draw batch。compute shade、cluster dispatch 不强制叫 draw。

## 全局 PipelineCache

全局 PipelineCache 负责 dirty 查找、创建、复用和延迟释放：

```text
dirty 输入 -> lookup/create -> PipelineHandle
```

PipelineState lookup 输入不进 `RenderWorld`，不进 `Material`。

dirty 时生成的 PipelineState lookup 输入至少包含：

- shader version。
- shader entry。
- `MaterialState`。
- render target format。
- depth format。
- sample count。
- layout。
- backend。
- graphics 路径需要的 vertex input / topology 等输入。

没有 dirty 时提交热路径只使用已经查到的 `PipelineHandle`。

RHI backend 继续拥有 native object。持有 `PipelineHandle` copy 的 pass、batch、pipeline 派生数据不负责销毁。

## 全局 BindSet Owner

全局 BindSet owner 负责 dirty 查找、创建、复用和延迟释放：

```text
dirty 输入 -> lookup/create -> BindingSetHandle
```

BindSet key 至少包含：

- binding layout。
- texture view handle。
- buffer view handle。
- sampler handle。
- array element。
- frame / transient generation，如果资源来自 render graph transient。

Material 不持有最终 BindSet 生命周期。Material 通过 `ToBindSet(...)` 生成 binding input，pipeline/pass 用这个输入查全局 BindSet owner。

不同层级的 binding handle 放在使用位置：

```text
frame 固定 binding -> frame 或 pass frame 字段
pass 固定 binding -> pass 字段
material batch binding -> batch 或 pass material 表
transient binding -> 本帧 pass 数据
```

禁止创建 `BindRef(Frame, Pass, Material, Object)` 这种聚合。

## RHI 创建与释放

PipelineState / BindSet 创建必须在明确的 render 线程或 render 准备阶段收口。pipeline 可以并行构建 key 和 binding input，但 RHI 对象创建不能散落在任意 job。

全局 PipelineCache / BindSet owner 必须支持延迟销毁。dirty 后旧 handle 可能仍被 frame-in-flight 使用，不能立刻 destroy native object。

涉及 render graph transient resource 的 BindSet 不能长期复用。key 必须带 frame/generation，或者由 render graph 在本帧创建/查找。

## Dirty 生命周期

按输入决定重建时机：

```text
包含 camera / visibility / frame resource / render graph transient
  每帧覆盖或生成

只包含 material / shader / mesh / MaterialState / target format
  dirty 时重建

GPU resource
  由对应 owner 持有，handle copy 放到 pass/batch/pipeline；PipelineState 例外，只保存 PipelineTicket
```

不每帧重建：

- `guid -> Handle<T>`。
- `MaterialPass`。
- shader metadata scan。
- PipelineState lookup/create。
- binding layout。
- 稳定 material binding input。

每帧可以生成：

- culling result。
- visible item。
- batch range。
- dispatch args。
- transient binding。

dirty 来源需要拆开：

- `RenderWorld` object dirty。
- asset reload dirty。
- material field dirty。
- shader reload dirty。
- mesh layout dirty。
- pipeline config dirty。
- target format dirty。
- frame resource dirty。

## SlotBuffer

旧 slot 体系删除，`SlotBuffer` 算法保留。

`SlotBuffer` 是 pipeline 内部 GPU index table：

```text
slot offset + local index + field -> bin / shader / material group
```

保留：

- slot offset。
- slot allocation。
- slot dirty range。
- SlotBuffer upload。
- free list。
- capacity growth。
- SOA layout。

取消：

- `MaterialSlotBind`。
- slot 依赖 ECS pass entity。
- slot 依赖 cluster baker component。
- slot offset 写回 `RenderWorld` source state。
- field 来自跨 pipeline 动态注册框架。
- slot 和 BVH、bounds、pass index 耦合。

slot offset 进入 pipeline 自己上传的 instance header，不写回 `RenderWorld`。

field 布局由持有它的 pipeline 固定。SlotBuffer 存紧凑 index，不存 entity、material guid 或 shader object。

## Bin

旧 bin 公共框架删除，bin 算法逻辑保留。

bin 是 pipeline 内部分组结果。SlotBuffer 是 GPU 查分组结果的表。

关系：

```text
bin 产生每个 material/pass/group 的 index
SlotBuffer 把 instance local material index 映射到这个 index
GPU pass 用 SlotBuffer 查 index 后 scatter 到对应 bin
```

保留逻辑：

- 按 order key 排序。
- 按完整 signature 分组。
- 生成 bin index。
- 生成 args bin map。
- count / reserve / scatter。
- bin counts。
- bin offsets。
- indirect args。

signature 来自已解析运行时状态，例如 `PipelineHandle`、`BindingSetHandle`、`MaterialPass`、`MaterialState`、material table index、order key。

hash 只能用于 bucket，不能作为唯一身份。命中必须走完整 equality。

## Shader 与 Vertex

`VertexLayoutReq` 删除。

旧的 `ImmutableArray<byte>` 顶点布局描述符删除。

通用检查只做：

```text
mesh vertex stride == shader entry required stride
```

顶点语义名不参与通用检查。某个 pipeline 需要更强协议时，由该 pipeline 自己定义，不回灌到 `MaterialPass` 或 `RenderWorld`。

## 禁止新增

- 把 `PipelineHandle` 存到普通 `RenderWorld` instance。
- 把 `BindingSetHandle` 存到普通 `RenderWorld` instance。
- 把不同层级 binding 包成 `BindRef`。
- 使用 `PassGraph`、`DrawBatch`、`FrameGraph`、`ExecutionGraph` 作为正式类型名。
- 在全局 PipelineCache 里保存 instance、item、batch range。
- 在 material 里保存 `PipelineHandle`。
- 在 material 里保存 `BindingSetHandle`。
- 在 instance header 的 source state 中写入 PipelineState、BindSet 或 slot offset。
- 用 asset path、entry point 名称、binding 名称做热路径 resolver。
- 提交热路径重新构造完整 PipelineState desc 并查找。

任何启发式算法必须报告后再做。
