# RenderWorld 与 Material 重构决策

本文记录当前有效决策，也记录被覆盖的历史结论。实现以本文和 reference 的当前有效内容为准。

## 问题分类

本次重构实际分为九类问题：

1. 文档权威。
2. 序列化资产与运行时资产。
3. `RenderWorld` 职责。
4. `Material` 与 `MaterialPass`。
5. `Pass` / `Stage` / `Pipeline` 分级。
6. 全局 PipelineState / BindSet / RHI handle。
7. pipeline 派生数据与 dirty 生命周期。
8. cluster / forward 多入口与旧 baker / slot / bin。
9. shader / vertex 兼容性。

## 决策历史

当前有效：

- `RenderWorld` 就是 ECS world，没有 sidecar。
- 渲染对象权威 CPU 状态全部放在 `RenderWorld`。
- pipeline 可以保存派生 CPU 数据，例如 item、batch、dispatch args、bin、SlotBuffer，但这些不是 source state。
- pipeline 可以有多个 cluster。
- forward 可以有多种入口。
- 通用部分只有在生命周期、输入输出、失效规则一致时才复用。
- 不能复用时禁止为了统一接口再包一层。
- `Pass` 是可变执行单元，有状态，但大部分状态来自外部传入引用或已查到的 RHI handle copy。
- `Stage` 是持有 pass 和管理 pass 的 struct 集合包装。
- `Pipeline` 是整体管理协调者，持有 stage、item、batch、pipeline 级状态和资源访问入口。
- 当前所有旧 PipelineState cache、slot、bind set、dispatch 相关东西全部取消。
- 全局级 PipelineCache 和 BindSet owner 需要保留，负责 dirty 查找、创建和生命周期。
- 查找后相关位置直接持有 RHI handle。
- `SlotBuffer` 算法保留，旧 slot 体系删除。
- `VertexLayoutReq` 全部删除。
- `MaterialSlotBind` 全部删除并重新设计。
- cluster 相关 baker 全部重新设计，不沿用当前 baker。
- 运行时和序列化必须分开。
- fbs 可以只有一个统一格式。
- 运行时 asset 和序列化资产是多对多模型。
- 运行时 `Material` 可以是多类型 asset。
- `Material` 和 fbs 是两个不同东西。
- 所有 asset 运行时引用统一用 `Handle<T>`。
- `Material` 是 asset，`Shader` 也是 asset，引用方式必须一致。
- asset 不持有 resolver、私有 ECS world、GPU 资源生命周期或 pass 运行状态。
- 材质参数、资源引用、管线设置直接作为 material 字段。
- 使用注解和生成器生成 `ToBindSet(...)`。
- `MaterialPass` 保持最小，不再拆出薄包装类型。
- shader guid 只存在于序列化和导入层，运行时走 `Handle<Shader>`。
- live material resolver 取消。
- `fact` 这个命名拒绝使用。
- “resolver”不是 asset 方向需要的概念；asset 通过 `AssetStore` 和 `Handle<T>` 访问。
- 不新增缩写式薄包装名字；除非是已有领域通用缩写，否则不要用缩写造新类型。

被后续决策覆盖：

- “bin 和 slot 都是实例，服务于多个 Stage”只保留为历史语义，不作为当前实现目标。当前决定是取消现有 slot/bin 体系；如果以后还需要类似概念，必须按 pipeline 内部派生数据重新设计。
- 旧 `PipelineStateCache` 和旧 `BindCache` 作为旧类型和旧耦合路径取消。全局 PipelineCache / BindSet owner 是从头定义的新语义。
- `PassUse`、`Route`、`EntryRef`、`PassData` 这类新增协议类型全部取消。`MaterialPass` 直接表达入口记录。
- “new baker”没有定稿。不得在没有候选和确认的情况下写入新 baker 设计。

过程约束：

- 当设计存在多个候选时，候选展示和确认后再写入文档。
- 不在代码里落临时兼容层作为长期方向。
- 不用“留着以后再说”的方式保留旧抽象。
- 生产代码类名和方法名保持三个单词以内；超过三个单词时重新检查边界。

## 文档权威

当前有效文档分工：

- `render_world_pipeline_refactor.md` 记录决策和删除目标。
- `render_world_pipeline_reference.md` 记录当前有效架构参考。
- `cluster_pipeline.md` 只记录 cluster 管线的当前边界和旧概念作废清单。
- `shading_pipeline.md` 只记录 shading 管线的当前边界和旧概念作废清单。

旧 reference 中 `DrawList / PipelineStateCache / BindCache / RenderWorld facts` 的描述已经过期。`RenderWorld` 不再保存 material pass facts 或 shader entry facts。

## 序列化与运行时

序列化和运行时必须分开。

fbs 可以只有一个统一格式。这个格式只是磁盘和编辑器序列化数据，不代表运行时 `Material`。

运行时 `Material` 是 asset，而且可以是多类型的。`StandardMaterial`、`UnlitMaterial`、`HairMaterial` 这类运行时材质可以共存。fbs 的 `type + fields` 只负责构造对应运行时材质。

运行时 asset 和序列化资产是多对多模型：

```text
一个 fbs 可以导入多个运行时 asset
多个序列化来源可以合成一个运行时 asset
一个运行时 asset 可以由多个序列化来源生成
```

禁止让 pipeline、`RenderWorld` 或运行时材质核心协议直接依赖 fbs 类型。

边界如下：

```text
fbs
  保存 guid、type、version、字段值

AssetStore
  把 guid 解析成运行时 handle
  管理运行时 asset 生命周期

Material
  运行时 asset
  多类型
  强字段
  由生成器生成绑定函数
```

fbs 里可以保存 guid。运行时不能用 guid 做热路径索引。

## AssetStore

所有 asset 统一用 `AssetStore<T>` 的 id + generation 管理；`AssetStore` 只是多类型聚合入口，不表示某个具体 asset 类型的专用 store。

asset 是运行时数据，不是服务对象。asset 本身不负责 resolve，不挂私有 world，不拥有 GPU 资源生命周期，不保存 pass 运行状态。

运行时 asset 引用统一使用：

```csharp
Handle<T>
```

不要引入 `ShaderHandle`、`TextureHandle`、`MaterialHandle` 这类特例名字。`Material` 是 asset，`Shader` 也是 asset，`Texture`、`Mesh` 也是 asset。

运行时引用统一是：

```csharp
Handle<Material>
Handle<Shader>
Handle<Texture>
Handle<Mesh>
```

序列化身份和运行时引用的关系：

```text
guid       文件身份，适合落盘、manifest、导入、编辑器引用
Handle<T> 运行时引用，适合 RenderWorld、Material、pipeline 使用
```

`AssetStore` 内部可以维护 `guid -> Handle<T>` 映射，但这个映射只用于加载、导入、重载和编辑器修改引用，不进入 draw/prepare 热路径。

## RenderWorld

`RenderWorld` 只存权威 CPU 对象状态。

允许存：

- source entity。
- transform。
- previous transform。
- instance index。
- mesh handle。
- material handle。
- per instance data offset。
- dirty flags。

禁止存：

- fbs 对象。
- guid resolver。
- pipeline handle。
- binding handle。
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

pipeline 可以保存派生 CPU 数据：

- visible item。
- batch。
- dispatch args。
- sort result。
- cluster bin。
- SlotBuffer。
- pipeline instance header。

这些派生数据不是权威 scene state，不能写回 `RenderWorld` 当 source state。

## Material

`Material` 是运行时 asset，不是 fbs 表。

材质参数、资源引用和管线设置直接作为 material 字段，不再使用字符串参数包承载运行时数据。

示意：

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

`ToBindSet(...)` 是 material 类型自己的绑定写入函数。它只把 material 字段转换成 BindSet 查询或写入需要的输入，不拥有 GPU descriptor 生命周期。

`Material` 禁止保存：

- `World`。
- `Entity`。
- `PassWorld`。
- `PassEntities`。
- `PipelineHandle`。
- `BindingSetHandle`。
- `MaterialSlotBind`。
- BVH 数据。
- slot offset。
- pass index。
- render target 格式。
- transient graph resource。

`Material` 可以保存：

- 参数字段。
- `Handle<Shader>`。
- `Handle<Texture>`。
- `Handle<Sampler>` 或等价 asset handle。
- `MaterialState`。
- 其它纯运行时 asset 数据。

## MaterialPass

`MaterialPass` 是运行时 material 暴露给 pipeline 的入口记录，不是 fbs 表，不是 ECS entity，不是 render graph pass。

`MaterialPass` 只保留最小字段：

```csharp
public readonly struct MaterialPass
{
    public string Target;
    public Handle<Shader> Shader;
    public string EntryPoint;
    public MaterialState State;
}
```

这里不再引入 `PassUse`、`Route`、`EntryRef`、`PassData` 等额外协议类型。

字段含义：

- `Target`：给哪个 pipeline 或 pipeline 内路径使用。
- `Shader`：运行时 shader asset handle。
- `EntryPoint`：shader 入口。
- `State`：material asset 解析出的材质状态。

如果一个 pipeline 路径需要多个 shader entry，就暴露多条 `MaterialPass`。pipeline 自己按 `Target` 和 `EntryPoint` 收集。

`MaterialPass.Target` 使用名字字段，不造全局 role enum。名字由 pipeline 拥有命名空间，material 只声明字符串，pipeline 只消费自己认识的名字。cluster target 和 cluster runtime 命名结论见 [Cluster Material Contract](cluster_material_contract.md)。

示例：

```text
forward.color
forward.depth
forward.velocity
cluster.main.shade
cluster.main.raster
cluster.debug.shade
shadow.depth
```

`MaterialPass` 禁止出现：

- `World`。
- `Entity`。
- tag。
- component。
- json。
- slot。
- bin。
- BVH。
- bounds。
- instance index。
- local material slot。
- `MaterialSlotBind`。
- `BindSet`。
- `BindingSetHandle`。
- `PipelineHandle`。
- PipelineState cache 字段。
- `VertexLayoutReq`。
- cache signature。
- surface signature。

## Pass / Stage / Pipeline

`Pass` 要设计成可变执行单元。

`Pass` 可以有状态，但状态边界要窄。`Pass` 可以持有：

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
- `Material` 对象所有权。
- 全局 PipelineCache / BindSet 生命周期。
- pipeline item 主数组。
- pipeline 配置源状态。

`Stage` 是持有 pass 和管理 pass 的 struct。`Stage` 负责：

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

如果 `Stage` 是 struct，必须避免 pass 被 copy 后状态丢失。pass 管理方法需要明确使用 `ref` 语义，或者禁止复制式访问。

`Pipeline` 是整体管理协调者。`Pipeline` 持有：

- `Stage`。
- item 数组。
- batch 数组。
- dispatch args。
- pipeline 级 dirty / version。
- pipeline 级 GPU buffer / texture。
- pipeline 配置。
- `AssetStore` 访问入口。
- 全局 PipelineCache / BindSet owner 访问入口。

`Pipeline` 不持有：

- fbs。
- 序列化 asset 对象。
- `RenderWorld` source state。
- resolver。
- material 私有 world。

`Pipeline` 通过 `Handle<T>` 引用 `AssetStore` 里的运行时资产，不和序列化层耦合。

## 全局 PipelineState

旧 `PipelineStateCache` 类型和旧耦合路径取消。新的全局 PipelineCache 需要保留，负责 dirty 查找、创建和生命周期。

规则：

```text
dirty 输入 -> collect/warmup -> PipelineTicket
```

查到后，相关位置直接持有 `PipelineTicket`：

- pass field。
- batch field。
- pipeline 派生数据。

生命周期归全局 PipelineCache，不归持有 ticket 的对象。执行时通过 render graph context 解析 ready `PipelineHandle`；RHI backend 继续拥有 native object。

PipelineState lookup 输入不进 `RenderWorld`，不进 `Material`。

dirty 时生成的 PipelineState lookup 输入至少包含：

- `Handle<Shader>`。
- shader version。
- shader entry。
- `MaterialState`。
- render target format。
- depth format。
- sample count。
- layout。
- backend。
- graphics 路径需要的 vertex input / topology 等输入。

PipelineState lookup 只在 dirty 输入变化时发生。没有 dirty 时 pass、batch 或 pipeline 派生数据只持有 `PipelineTicket`，执行路径按 need 解析 ready `PipelineHandle`。

## 全局 BindSet

旧 bind set cache 类型和旧耦合路径取消。新的全局 BindSet owner 需要保留，负责 dirty 查找、创建、复用和延迟释放。

规则：

```text
dirty 输入 -> lookup/create -> BindingSetHandle
```

查到后，相关位置直接持有 `BindingSetHandle`：

- frame 固定 binding 放 frame 或 pass frame 字段。
- pass 固定 binding 放 pass 字段。
- material batch binding 放 batch 或 pass material 表。
- transient binding 放本帧 pass 数据。

禁止重新发明 `BindRef(Frame, Pass, Material, Object)` 这种聚合。

BindSet key 不能只按 material。至少包含：

- binding layout。
- texture view handle。
- buffer view handle。
- sampler handle。
- array element。
- frame / transient generation，如果资源来自 render graph transient。

Material 不持有最终 BindSet 生命周期。Material 产生 binding 输入，pipeline/pass 用这个输入查全局 BindSet owner。

涉及 render graph transient resource 的 BindSet 不能长期复用。key 必须带 frame/generation，或者由 render graph 在本帧创建/查找，防止跨帧拿错资源。

## RHI Handle 生命周期

查找后直接持有 RHI handle，不再包运行时 wrapper。

可直接保存在相关位置的 handle：

- `PipelineHandle`。
- `BindingSetHandle`。
- buffer handle。
- texture handle。
- view handle。

生命周期归 owner，不归持有 handle copy 的 pass、batch 或 frame 数据。

PipelineState / BindSet 创建必须在明确的 render 线程或 render 准备阶段收口。pipeline 可以并行构建 key 和 binding input，但 RHI 对象创建不能散落在任意 job。

全局 PipelineState / BindSet owner 必须支持延迟销毁。dirty 后旧 handle 可能仍被 frame-in-flight 使用，不能立刻 destroy native object。

## Pipeline 派生数据与 Dirty

pipeline 可以持有派生数据：

- item。
- batch。
- dispatch args。
- visible list。
- sort result。
- cluster bin。
- SlotBuffer。
- pipeline instance header。

这些数据不进 `RenderWorld`。

按输入决定生命周期：

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

dirty 来源必须拆开：

- `RenderWorld` object dirty。
- asset reload dirty。
- material field dirty。
- shader reload dirty。
- mesh layout dirty。
- pipeline config dirty。
- target format dirty。
- frame resource dirty。

不同 dirty 触发不同更新：

```text
transform dirty -> instance buffer
material field dirty -> binding input / material buffer
shader dirty -> PipelineState / layout / pass selection
mesh dirty -> item / vertex stride check
visibility dirty -> frame item / batch
```

禁止把正式概念命名为 planning suffix。

## SlotBuffer

旧 slot 体系删除，不代表 `SlotBuffer` 算法删除。

`SlotBuffer` 是 pipeline 内部 GPU index table。

本质：

```text
slot offset + local index + field -> bin / shader / material group
```

当前 SOA 布局思想保留：

```text
field0: slot0, slot1, slot2...
field1: slot0, slot1, slot2...
field2: slot0, slot1, slot2...
```

GPU 查询：

```text
fieldIndex * capacity + slotOffset + localIndex
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
- `SlotPreparer` 扫 ECS pass entity。
- `SlotTable` 作为公共 slot owner。
- slot 依赖 cluster baker component。
- slot offset 写回 `RenderWorld` source state。
- field 来自跨 pipeline 动态注册框架。
- slot 和 BVH、bounds、pass index 耦合。

新来源：

```text
RenderWorld
  instance
  mesh
  material handles
  local material index

AssetStore
  Material
  MaterialPass
  Shader

Pipeline
  选择自己需要的 MaterialPass
  查 PipelineState / BindSet
  生成 bin / group index
  写 SlotBuffer
```

slot offset 进入 pipeline 自己上传的 instance header，不写回 `RenderWorld` component。

field 布局由持有它的 pipeline 固定。例如 cluster 内部可以固定为：

```text
field 0 = raster
field 1 = shade
field 2 = deform
```

不要做跨 pipeline 的动态 field 注册框架。

SlotBuffer 存紧凑 index，不存 entity、material guid 或 shader object。具体值由 pipeline 决定：

- bin index。
- shader index。
- material group index。

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

旧 signature 来源取消：

- shader asset guid 字符串。
- entry point 字符串加旧 Params 签名。
- pass entity component。

新 signature 来自已解析运行时状态：

- `PipelineHandle`。
- `BindingSetHandle`。
- `MaterialPass.Target`。
- `MaterialPass.EntryPoint`。
- `MaterialState`。
- material table index 或 material data index。
- order key。

hash 只能用于 bucket，不能作为唯一身份。命中必须走完整 equality。

GPU binning 算法保留：

```text
vis buffer / visible clusters
  -> 查 instance
  -> 查 local material index
  -> 查 SlotBuffer
  -> 得到 material bin
  -> 写入对应 bin 的 count / offset / args
```

## Cluster 与 Forward

cluster baker 全部删除，不重命名保留。

删除目标：

- `ClusterBakers`。
- `ClusterShaderBaker`。
- `ClusterComponentBaker`。
- `ClusterPassRules`。
- `ClusterPassCopy`。
- `MaterialSlotBind`。
- `VertexLayoutReq`。
- 旧 slot/bin/dispatch。

cluster pipeline 自己读取：

- `RenderWorld` instance state。
- `Handle<Material>`。
- `MaterialPass`。
- `Handle<Shader>`。
- shader metadata。
- mesh data。

cluster pipeline 自己生成：

- cluster item。
- batch。
- bin。
- page。
- BVH/page mapping。
- raster / shade / deform data。
- GPU buffer。
- SlotBuffer。

这些内容不能塞回 `RenderWorld`、`Material` 或 `MaterialPass`。

Forward 多入口用 `MaterialPass.Target` 名字区分：

```text
forward.color
forward.depth
forward.velocity
```

多个 cluster 也通过名字区分：

```text
cluster.main.shade
cluster.main.raster
cluster.debug.shade
```

多个 cluster pipeline 不共享隐式全局状态。能共享的只有明确传入的全局 PipelineCache、全局 BindSet owner、`AssetStore`、`RenderWorld` 输入和公共 GPU 资源引用。每个 cluster pipeline 自己持有 item、batch、bin、page 和 SlotBuffer 状态。

## Shader 与 Vertex

`VertexLayoutReq` 全部删除。

顶点布局在 GPU pipeline 里只作为 mesh 和 shader 是否对得上的检查输入，不是通用 ECS component。

旧的 `ImmutableArray<byte>` 顶点布局描述符不进入新设计。这里不需要一套复杂的可序列化布局协议。

检查规则收敛为 stride 对齐：

```text
mesh vertex stride == shader entry required stride
```

顶点语义名可以不同。只要运行时协议允许，语义不参与通用 material pass 的等值。

如果某个 pipeline 需要更强协议，那是该 pipeline 自己的 shader/mesh 协议，不回灌到通用 `MaterialPass` 或 `RenderWorld`。

shader 是 asset，运行时引用使用：

```csharp
Handle<Shader>
```

shader guid 只存在于序列化和导入层。material 和 pipeline 热路径不通过 guid resolver 找 shader。

## 命名

禁止使用已经否定的泛化名字：

- fact。
- resolver。
- old slot 体系。
- dispatch。
- old bind set 体系。
- old PipelineState cache 体系。
- planning suffix。

允许使用清晰的现有名字：

- `Material`。
- `MaterialPass`。
- `MaterialState`。
- `Handle<T>`。
- `AssetStore`。
- `RenderWorld`。
- `Pipeline`。
- `Stage`。
- `Pass`。
- `SlotBuffer`。

不要为了描述边界连续增加薄包装类型。

## 删除目标

这些旧概念没有继续存在的基础：

- `MaterialPassBaker`。
- `PassBakers`。
- `IBaker<TSource>` 用于 material pass 的路径。
- `ShaderBakeSource`。
- `MaterialTagBaker`。
- `BakerName`。
- `MaterialSlotBind`。
- `VertexLayoutReq`。
- `ClusterBakers`。
- `ClusterComponentBaker`。
- `ClusterShaderBaker`。
- `ClusterPassRules`。
- `ClusterPassCopy`。
- 旧 `PipelineStateCache`。
- 旧 `BindCache`。
- 旧 slot 体系。
- 旧 bin 公共框架。
- 旧 bind set cache。
- 旧 dispatch table。

删除时按最终结构一次收敛，不保留兼容层作为长期设计。

## 最终数据流

```text
fbs bytes
  -> 序列化数据
  -> AssetStore 导入
  -> 运行时 Material
  -> Handle<Material>

RenderWorld entity
  -> Handle<Mesh>
  -> Handle<Material>
  -> transform / instance data

pipeline
  -> 读取 RenderWorld
  -> 通过 AssetStore 读取 Material / Shader / Mesh
  -> 读取 MaterialPass
  -> 调用 ToBindSet 生成 binding input
  -> 检查 vertex stride
  -> dirty 查全局 PipelineCache 得到 PipelineTicket
  -> dirty 查全局 BindSet owner 得到 BindingSetHandle
  -> 更新 pipeline item / batch / SlotBuffer
  -> pass 持有 PipelineTicket 和必要的 RHI handle copy
```

这条路径里没有 fbs 直连 pipeline，没有 material 私有 ECS world，没有 live resolver，没有 MaterialSlotBind，没有 VertexLayoutReq，没有旧 slot/bin/dispatch/cache。
