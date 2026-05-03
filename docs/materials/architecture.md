# Material System 架构设计 (v11)

> **Status:** 当前代码已从 `1 Material = 1 Entity` 继续演化到 `Material root params + PassEntities[] + GameWorld authoring bindings + RenderWorld extract` 路线。本文大部分原则仍有效，但涉及“材质唯一实体”“多 pass 通过多个 Material 组合”的旧描述已不再准确，应以下面的更新段落为准。
>
> **Authoring split:** 通用 ECS authoring 设计见 [../core/ecs_authoring.md](../core/ecs_authoring.md)；材质作为具体实例的说明见 [authoring_system.md](authoring_system.md)。
>
> **Genericity note:** 本文中的 `ClusterRaster` / `ClusterShade` / `ClusterRender` 示例应视为“当前 feature adapter”，不是 authoring 核心层对外暴露的唯一模型。

## 设计原则

1. **Pull 模型**：管线 Stage 通过 ECS Query 主动拉取所需材质，材质不知道谁消费自己
2. **材质只描述能力**：材质声明实现了哪些 evaluate 接口，不关心管线内部的 mode（inline/cached/SW/HW）
3. **管线定义 Component**：每条管线定义自己的 IComponent 类型，材质系统核心不包含管线语义
4. **Importer 管线无关**：Importer 只做 Slang attribute → 注册 Author 的透传调度，不解释管线含义
5. **Material 是参数容器，不是提交实体**：`Material` 持有 root params 和 `PassEntities[]`
6. **pass 直接是实体**：component/tag 直接挂在 pass entity 上，不经过额外 feature 容器
7. **组合靠 authoring + extract**：当前 `MeshMaterialBindings` 表达 mesh-local material table，RenderWorld 再把 `(source, local slot, pass)` 展开为提交单元
8. **Bin key = 一次 dispatch 的完整工作单元**：只要 shader/固定状态/绑定集合不同就分 bin
9. **localMaterialIndex < 128**（per-mesh-asset 局部上限，7 bit）
10. **BinGroup 动态排序**：BinQueue 按 Entity 上的 orderKey 自动产出有序 region，管线不做拓扑排序

---

## 核心概念

### 概念总览

```text
用户 Shader (.slang)
  │  用户写 evaluate 函数，展开管线宏产出带 attribute 的 entry point
  ▼
ShaderAsset (编译产物)
  │  包含 N 个 variant + 每个 variant 的 pipeline attribute metadata
  ▼
Material (.mat 资产)
  │  声明共享参数 + 匿名 pass entity snapshots
  ▼
Material (运行时资产容器)
  │  持有 ShaderParamBag + PassEntities[]
  │
  └──▶ Pass Entities[]
         Tags: [Opaque] / [Masked] / ...
         Components: [ClusterRaster, ClusterShade, OverlayShade, MaterialRef, ...]
         Params: material.Params (共享) + pass-local overrides

GameWorld Entity
  │  MeshAssetGuid + MeshMaterialBindings
  └──▶ Extract
       RenderWorld pass entities:
       (source entity, material pass)
```

### Material — 资产/参数容器

```csharp
public class Material
{
    public string Name;
    public AssetGuid AssetGuid;
    public ShaderParamBag Params;          // 贴图、Buffer、标量
    public Entity[] PassEntities;          // 匿名 pass entities

    public void SetTexture(string name, ITextureView view) => Params.Set(name, view);
    public Material Instantiate();         // clone Params + clone PassEntities
}
```

Material 不参与 dispatch 路径。dispatch 只读 pass entities 上的 component/tag。

### Entity — 渲染意图

当前模型不是 `1 Material = 1 Entity`，而是：

- `1 Material = 1 root param bag + N pass entities`
- pass entity 才是渲染身份
- GameWorld entity 只持有 authoring 材质绑定数据
- RenderWorld 里才出现真正参与提交的 pass entity 实例

### Tag vs Component

| 类型 | 用途 | 数据 | ECS 映射 |
|---|---|---|---|
| 语义分类 | Opaque / Masked / Translucent / TwoSided | 无数据 | `ITag`（零大小标记） |
| 管线入口 | ShadowCaster / Outline | 无数据 | `ITag` |
| 管线 capability | ClusterRaster / ClusterShade / ClusterDeform / ForwardShade / ShadowRaster | 携带 ShaderVariantRef 字段 | `IComponent`（管线定义） |
| overlay 角色 | OverlayShade | 携带 Layer 字段 | `IComponent`（管线定义） |
| stencil 配置 | StencilState | 携带 Ref/Compare/PassOp 字段 | `IComponent`（管线定义） |
| 材质引用 | MaterialRef | 回指 Material 对象 | `IComponent` |

跨 Entity 引用不进入材质提交模型。

---

## Shader 变体系统

### 三层模型

```text
第一层：用户创作（Evaluate 接口实现）
  用户只写 ISurfaceEvaluate / IVertexEvaluate / IPixelEvaluate / IDomainEvaluate 的实现
  不需要知道 inline/cached、SW/HW

第二层：管线 Wrapper（entry point 模板）
  管线提供 .slang 头文件，包含泛型入口模板和展开宏
  用户 #include 后展开所有入口

第三层：ShaderAsset（编译产物）
  一个 ShaderAsset 包含 N 个 variant
  每个 variant 携带管线 attribute metadata
```

### 用户 Shader 示例

```slang
#include "cluster_pipeline.slang"

struct MyPBR : ISurfaceEvaluate {
    Texture2D albedo;
    SamplerState s;
    void evaluateSurface(PixelContext ctx) { /* ... */ }
};

struct MyWave : IVertexEvaluate {
    float amplitude;
    VertexOutput evaluateVertex(VertexContext ctx) { /* ... */ }
};

// 展开管线宏 → 产出所有带 attribute 的 entry point
CLUSTER_SHADE_ENTRIES(MyPBR)
CLUSTER_RASTER_ENTRIES(MyWave)
CLUSTER_DEFORM_ENTRIES(MyWave)
```

展开后产出带 Slang custom attribute 的 entry point：

```slang
[ClusterShade("default")]
[shader("compute")]
void CS_Shade(uniform MyPBR material, uint3 tid : SV_DispatchThreadID) { /* wrapper */ }

[ClusterRaster("sw_inline")]
[shader("compute")]
void CS_SWRaster_Inline(uniform MyWave material, uint3 tid : SV_DispatchThreadID) { /* wrapper */ }

[ClusterRaster("hw_vs_inline")]
[shader("vertex")]
void VS_HW_Inline(uniform MyWave material, /* ... */) { /* wrapper */ }

[ClusterRaster("hw_ps")]
[shader("pixel")]
void PS_HW(/* ... */) { /* wrapper */ }

[ClusterDeform("default")]
[shader("compute")]
void CS_Deform(uniform MyWave material, uint3 tid : SV_DispatchThreadID) { /* wrapper */ }
```

### Cluster 管线的入口分布

| 管线角色 | Inline 模式 | Cache 模式 |
|---|---|---|
| **SW Raster** | CS (内联 VertexEval) | CS (读 deform cache) |
| **HW Raster** | VS (内联 VertexEval) + PS | VS (读 deform cache) + PS |
| **Deform** | — | CS (写 deform cache) |
| **Shade** | CS | CS |

用户写 4 个 evaluate 函数。管线宏展开后产出 7+ 个 entry point。

### Component 字段 = 变体引用

管线 Component 的字段直接持有 mode variant 引用，消灭运行时 key 查找：

```csharp
[PipelineComponent("ClusterRaster")]
public struct ClusterRaster : IComponent
{
    public ShaderVariantRef SWInline;
    public ShaderVariantRef SWCached;
    public ShaderVariantRef HWVSInline;
    public ShaderVariantRef HWVSCached;
    public ShaderVariantRef HWPS;
}

[PipelineComponent("ClusterShade")]
public struct ClusterShade : IComponent
{
    public ShaderVariantRef Default;
}

[PipelineComponent("ClusterDeform")]
public struct ClusterDeform : IComponent
{
    public ShaderVariantRef Default;
}
```

```csharp
/// <summary>轻量变体寻址，不持有 bytecode。</summary>
public readonly record struct ShaderVariantRef(AssetGuid ShaderAsset, int VariantIndex);
```

管线 stage 消费时按当前 mode 选字段：

```csharp
var raster = entity.GetComponent<ClusterRaster>();
var cs = isInlineMode ? raster.SWInline : raster.SWCached;
// 用 cs 构建 PSO
```

mode（inline/cached、SW/HW）是管线全局配置，不是 per-material 选择。材质不知道 mode 的存在。

---

## 导入管线

### Importer 透传模型

Importer 零管线知识。链路：

```text
Slang 编译 + 反射
  ↓ 对每个 entry point
  entryPointRefl.Function.GetAttribute(i)
  → 得到 AttributeReflection { Name, Args... }
  ↓ 按 Name 查注册的 IComponentAuthor
  author.Author(entity, attr, variantIndex)
  ↓ Author 自己解释 attr 参数，填充 Component 字段
```

### IComponentAuthor 接口

```csharp
/// <summary>
/// 管线实现。接收 Slang attribute 原始数据，负责在 Entity 上创建/填充 Component。
/// 单阶段：只操作当前 Entity，不引用其他 Entity。
/// </summary>
public interface IComponentAuthor
{
    /// <summary>匹配的 Slang attribute 名。</summary>
    string AttributeName { get; }

    /// <summary>
    /// 接收原始 attr，按管线自定义的协议解释参数并填充 Component。
    /// </summary>
    void Author(Entity entity, AttributeReflection attr, int variantIndex);
}
```

### 管线实现示例

```csharp
public class ClusterRasterAuthor : IComponentAuthor
{
    public string AttributeName => "ClusterRaster";

    public void Author(Entity entity, AttributeReflection attr, int variantIndex)
    {
        string key = attr.GetArgumentValueString(0);
        ref var comp = ref entity.GetOrAddComponent<ClusterRaster>();
        var varRef = new ShaderVariantRef(currentAssetGuid, variantIndex);
        switch (key)
        {
            case "sw_inline":    comp.SWInline   = varRef; break;
            case "sw_cached":    comp.SWCached   = varRef; break;
            case "hw_vs_inline": comp.HWVSInline = varRef; break;
            case "hw_vs_cached": comp.HWVSCached = varRef; break;
            case "hw_ps":        comp.HWPS       = varRef; break;
        }
    }
}

public class ClusterShadeAuthor : IComponentAuthor
{
    public string AttributeName => "ClusterShade";

    public void Author(Entity entity, AttributeReflection attr, int variantIndex)
    {
        string role = attr.GetArgumentValueString(0);  // "default" or "overlay"
        ref var comp = ref entity.GetOrAddComponent<ClusterShade>();
        comp.Default = new ShaderVariantRef(currentAssetGuid, variantIndex);

        if (role == "overlay")
        {
            int layer = attr.GetArgumentValueInt("layer");
            entity.AddComponent(new OverlayShade { Layer = (byte)layer });
        }
    }
}

public class StencilConfigAuthor : IComponentAuthor
{
    public string AttributeName => "StencilConfig";

    public void Author(Entity entity, AttributeReflection attr, int variantIndex)
    {
        entity.AddComponent(new StencilState
        {
            Ref = (byte)attr.GetArgumentValueInt("ref"),
            Compare = ParseComparisonFunc(attr.GetArgumentValueString("compare")),
            PassOp = ParseStencilOp(attr.GetArgumentValueString("passOp")),
        });
    }
}
```

### Importer 代码

```csharp
// SlangShaderImporter 中，对每个 entry point：
var funcRefl = entryPointRefl.Function;
for (uint a = 0; a < funcRefl.AttributeCount; a++)
{
    var attr = funcRefl.GetAttribute(a);
    if (authorRegistry.TryGet(attr.Name, out var author))
    {
        author.Author(entity, attr, variantIndex);
    }
}
```

Importer 不知道 `ClusterRaster` 是什么，不解析 attr 参数。attr 协议是 Slang shader ↔ C# Author 之间的私有契约。

### 注册机制

管线在初始化时注册 Author：

```csharp
materialSystem.RegisterAuthor(new ClusterRasterAuthor());
materialSystem.RegisterAuthor(new ClusterShadeAuthor());
materialSystem.RegisterAuthor(new ClusterDeformAuthor());
materialSystem.RegisterAuthor(new StencilConfigAuthor());
```

可选通过源生成器从 `[PipelineComponent]` 注解自动生成注册代码。

---

## 材质资产 (.mat)

### 设计推导

每个字段的来源和消费者：

| 字段 | 数据从哪来 | 被谁消费 | 为什么在 .mat |
|---|---|---|---|
| **shader** | 用户在编辑器选择 | 加载器 → 加载 ShaderAsset → Authors 创建 Component | Material 的核心身份 |
| **tags** | 用户在编辑器设置 | 加载器 → `entity.AddTag<Opaque>()` → 管线 query 过滤 | 同一 PBR shader 可做 opaque 或 masked，per-material 选择 |
| **textures** | 用户在编辑器拖入 | 加载器 → resolve 到 ShaderParamBag | shader 声明 slot，材质填 value |
| **scalars** | 用户在编辑器设置 | 同上 | 同上 |
| **samplers** | 用户在编辑器或代码设置 | 同上 | 同上 |

不在 .mat 的字段：

| 不在 .mat | 为什么 |
|---|---|
| **passes** | `MaterialAsset` 直接持有匿名 pass entity snapshots |
| **reuse / stencil** | 管线概念，来自 shader attribute → Author |
| **管线角色** | 来自 shader attribute → Author |

### Schema

```
material "BrickWall":
  shader: "standard_pbr.shader"
  tags: [opaque]
  textures:
    albedo: "textures/brick_albedo.png"
    normal: "textures/brick_normal.png"
  scalars:
    roughness: 0.8
    metallic: 0.0
```

### Shader 侧声明示例（管线概念在 shader attribute 上）

```slang
// outline_raster.slang
// shader attribute 声明 stencil（管线概念）
[ClusterRaster("sw_inline")]
[StencilConfig(ref = 1, compare = "always", passOp = "replace")]
[shader("compute")]
void CS_OutlineRaster(...) { ... }

// shadow_depth.slang
[ShadowRaster]
[shader("compute")]
void CS_ShadowRaster(...) { ... }
```

注意：overlay 角色不在 shader attribute 上声明（同一 shader 可能在不同上下文做 primary 或 overlay）。
overlay 通过运行时给 Entity 挂 `OverlayShade` Component 表达。

### 加载流程

```text
MaterialAssetLoader.LoadFromAsset(matAsset)
  │
  ├── 创建 Material 对象，设 Params
  │
  ├── 对每个 pass snapshot：
  │     ├── 在传入的 materialStore 创建 pass entity
  │     ├── 挂 MaterialRef { Owner = material }
  │     ├── 反序列化显式 tags/components
  │     └── 加载 shader 并应用 entry-point authoring
  │
  └── material.PassEntities = 所有创建的 pass entities
```

---

## 多 Pass 组合

多 pass 现在是 `MaterialAsset` 的一等能力。每个 pass 都直接是实体快照，component/tag 直接挂在 pass 上，没有 feature 容器层。

### 角色来源

| 角色 | Component | 谁设的 | 场景 |
|---|---|---|---|
| primary shade | `ClusterShade`（无 `OverlayShade`） | Author（shader attr） | 默认 |
| overlay shade | `ClusterShade` + `OverlayShade { Layer }` | 运行时代码 / 编辑器 | 同一 shader 在不同上下文可做 primary 或 overlay |
| stencil | `StencilState { Ref, Compare, PassOp }` | Author（shader attr `[StencilConfig]`） | shader 固有 |

overlay 关系依然不是材质系统核心语义；它只是在 pass entity 上表现为 `OverlayShade` 等 component，由具体管线解释。

### BinQueue 直接 query

管线不做拓扑排序。BinQueue 根据 Entity 的 Component 数据自动产出有序执行区间。

---

## Bin 系统

### BinGroup

管线通过 `RegisterGroup(group)` 注册提交分组：

```csharp
public struct BinGroup
{
    /// <summary>查询此组包含的 Entity。</summary>
    public Func<Entity[]> Query;

    /// <summary>
    /// 从 Entity 计算 orderKey。相同 key 的 Entity 归入同一提交段。
    /// 提交段按 key 升序排列。key 间插入 barrier。
    /// </summary>
    public Func<Entity, int> OrderKey;

    /// <summary>签名函数。相同签名的 Entity 合入同一 bin。</summary>
    public Func<Entity, ulong> SignatureFunc;
}
```

### 管线注册示例

```csharp
// Shade BinQueue：管线注册 3 个 group，一次性搞定
shadeBinQueue.RegisterGroup(new BinGroup
{
    Query = () => store.Query<ClusterShade, Opaque>()
                       .Without<OverlayShade>().ToArray(),
    OrderKey = _ => 0,                // primary opaque 永远排最前
    SignatureFunc = shadeSignatureFunc,
});

shadeBinQueue.RegisterGroup(new BinGroup
{
    Query = () => store.Query<ClusterShade, OverlayShade>().ToArray(),
    OrderKey = e => e.GetComponent<OverlayShade>().Layer + 1,  // 按层排
    SignatureFunc = shadeSignatureFunc,
});

shadeBinQueue.RegisterGroup(new BinGroup
{
    Query = () => store.Query<ClusterShade, Masked>()
                       .Without<OverlayShade>().ToArray(),
    OrderKey = _ => 10000,            // masked 在所有 overlay 之后
    SignatureFunc = shadeSignatureFunc,
});
```

### Rebuild 逻辑

```text
Rebuild():
  1. 执行所有 group 的 Query，收集 (entity, orderKey) 对
  2. 按 orderKey 分组 → 相同 key 归入同一 region
  3. 每个 region 内按 SignatureFunc 去重 → 产出 bin
  4. region 按 key 升序排列
  5. 输出: 有序 BinRange[]
```

示例：3 个 overlay 层自动产出 6 个 region：

```
BinRange[0]: orderKey=0      primary opaque     [bin0..bin2]
BinRange[1]: orderKey=1      overlay layer 0    [bin3]
BinRange[2]: orderKey=2      overlay layer 1    [bin4]
BinRange[3]: orderKey=3      overlay layer 2    [bin5]
BinRange[4]: orderKey=10000  masked             [bin6..bin7]
```

材质增删 overlay 层时，Rebuild 自动调整 region 数量。管线代码零改动。

### 管线 Dispatch

```csharp
shadeBinQueue.Rebuild();

// 管线不知道有几层 overlay，不知道 overlay 是什么
// 只知道：按 region 顺序 dispatch，region 间插 barrier
foreach (var range in shadeBinQueue.GetRanges())
{
    for (int i = range.Start; i < range.Start + range.Count; i++)
        DispatchBin(i);

    ctx.UAVBarrier();
}
```

### Overlay Dispatch 的 IndirectArgs 复用

overlay bin 着色的是 primary 的像素。overlay dispatch 使用 primary bin 的 IndirectArgs：

```csharp
// BinQueue 在 Rebuild 时建立映射：overlay bin → primary bin
private ushort[] _argsBinMap;  // [binIndex] → IndirectArgs 来自哪个 bin

// dispatch 时
int bin = range.Start + i;
int argsBin = shadeBinQueue.GetArgsBin(bin);  // overlay → primary bin
ctx.DispatchComputeIndirect(new DispatchComputeIndirectAttribs
{
    AttribsBuffer = binIndirectArgs,
    DispatchArgsByteOffset = (ulong)(argsBin * 12),
});
```

映射规则：
- primary bin → 指向自己
- overlay bin → 指向同 instance 上 localMaterialIndex 0 对应的 primary bin（instance.Materials[0] 的 Entity 在 primary region 里的 bin）

### 签名计算

从 Entity Component 读取：

```csharp
ulong ShadeSignatureFunc(Entity entity)
{
    var shade = entity.GetComponent<ClusterShade>();
    var matRef = entity.GetComponent<MaterialRef>();
    // hash(shader identity + param layout)
    return HashCombine(shade.Default.GetHashCode(),
                       matRef.Owner.Params.GetSignatureHash());
}
```

### BinSpace

当前代码中，BinSpace 作为 Cluster pipeline 的 bridge，管理：

- 多个 BinQueue field（per stage）
- MaterialSlotBuffer SOA 布局
- MaterialSlotCache（hash + refcount 共享）
- per-field slot entity 选择（同一个 local material slot 可对不同 field 指向不同的 render pass entity）
- 脏检测与 rebuild

```csharp
public sealed class BinSpace
{
    public int RegisterField(string name, BinQueue queue);
    public void FreezeLayout();
    public void RegisterSlots(ReadOnlySpan<Entity> entities);
    public bool RebuildIfDirty();
    public ReadOnlySpan<ushort> GetSlotData();  // GPU upload
    public uint Version { get; }
}
```

### MaterialSlotBuffer — GPU 侧 SOA 布局

不变。`ushort[]` SOA 布局，per-field 分段存储 bin key。

### MaterialSlotCache

不变。相同 Entity 组合的实例共享同一段 SlotBuffer 区间。

---

## PSO/SRB 管理

### 全 Dynamic 隐式签名（BATCH-08）

所有 PSO 使用 `PipelineResourceLayoutDesc { DefaultVariableType = Dynamic }` + Diligent 隐式反射。不再有显式 `IPipelineResourceSignature`。

- 每个 shader group 持有 1 个 SRB，per-dispatch 绑定所有资源（per-pass + per-material）
- `MaterialSampler` 作为 `ImmutableSamplerDesc` 烘入 layout
- material 纹理通过 `ShaderParamBag.ApplyTo(srb)` 绑定

### PSO 构建

管线从 Entity Component 读 ShaderVariantRef → 加载 bytecode → 构建 PSO。PSO 按 shader identity + render state 缓存。

Stencil state 从 StencilState Component 读取合入 PSO descriptor：

```csharp
void BuildPSOForBin(Entity entity, ref PipelineStateDesc desc)
{
    if (entity.TryGetComponent<StencilState>(out var stencil))
    {
        desc.DepthStencilDesc.StencilEnable = true;
        desc.DepthStencilDesc.FrontFace.StencilFunc = stencil.Compare;
        desc.DepthStencilDesc.FrontFace.StencilPassOp = stencil.PassOp;
    }
}
```

---

## 材质系统核心边界

```text
┌──────────────────────────────────┐     ┌────────────────────────────────┐
│  材质系统核心                      │     │  管线代码（各自定义）            │
│                                  │     │                                │
│  EntityStore (friflo)            │     │  Component 类型定义             │
│  IComponentAuthor 注册表          │◄────│  ClusterRaster : IComponent    │
│  Material 资产容器                │     │  ClusterShade  : IComponent    │
│  ShaderParamBag                  │     │  OverlayShade  : IComponent    │
│  MaterialSlotBuffer / SlotCache  │     │  StencilState  : IComponent    │
│  BinQueue / BinSpace             │     │  ForwardShade  : IComponent    │
│  序列化 / 生命周期               │     │  ShadowRaster  : IComponent    │
│                                  │     │                                │
│  不含管线语义                     │     │  IComponentAuthor 实现          │
│                                  │     │  ClusterRasterAuthor           │
│                                  │     │  ClusterShadeAuthor            │
│                                  │     │  StencilConfigAuthor           │
└──────────────────────────────────┘     └────────────────────────────────┘
```

材质系统核心提供：
- EntityStore 管理
- Author 注册和调度
- Material 生命周期（load/unload/instantiate）
- BinQueue/BinSpace 基础设施（BinGroup 动态 region）
- MaterialSlotBuffer GPU 数据管理
- 序列化框架

管线提供：
- Component 类型定义（ClusterRaster、ClusterShade、OverlayShade、StencilState 等）
- Author 实现（解释 Slang attribute，填充 Component）
- BinGroup 注册（query + orderKey + signatureFunc）
- variant 选择逻辑（mode → 字段映射）
- PSO/SRB 构建和 dispatch 编排
- RenderGraph pass 编排

---

## Cluster 多材质（未来）

GPU 侧材质 slot 查找的完整路径：

```text
ClusterHeader.materialRanges (烘焙, 最多 3 range, per-cluster)
   │
   ├── GetLocalMaterialIndex(header, triangleID) → localMaterialIndex
   │
   ├── InstanceHeader.materialSlotOffset (per-instance, 上传时确定)
   │
   ▼
MaterialSlotBuffer[offset + localIndex] → MaterialSlot { RasterBin, ShadingBin, ... }
   │
   └── 一次 Load 得到所有 stage 的 bin key
```

此部分设计不变，数据源来自 RenderWorld pass entity。

---

## 设计决策记录

| 决策 | 选项 | 选择 | 理由 |
|---|---|---|---|
| Material 胖瘦 | 胖（N shader N pass）/ 瘦（1 shader 1 Entity） | **瘦** | 组合不绑死，同一材质可在不同上下文复用 |
| 多 pass 组合位置 | Material 内部 / 外部（Stack/Instance） | **外部** | 组合关系不是材质固有属性 |
| 管线 mode 透明性 | mode 暴露到材质 / 对材质不透明 | **不透明** | mode 是管线全局配置，不应污染材质模型 |
| variant 寻址 | string key 查找 / Component typed 字段 | **typed 字段** | 编译时安全，无运行时查找开销 |
| Entity 粒度 | 1 Material = 1 Entity / 1 Material = N pass Entities | **1 Material = N pass Entities** | pass 才是渲染身份；材质是参数容器 |
| Author 阶段 | 单阶段（只操作当前 Entity）/ 二阶段（需跨 Entity） | **单阶段** | 消除 Phase 2，简化加载流程 |
| BinQueue region | 固定命名 / 动态 orderKey | **动态 orderKey** | 支持任意层数 overlay，管线不做拓扑排序 |
| 入口点展开 | importer 知道管线 / 用户展开 + importer 透传 | **用户展开 + 透传** | importer 管线无关 |
| Author 协议 | importer 解析 attr / 透传给 Author | **透传** | attr 协议是 Slang shader ↔ C# Author 的私有契约 |
| overlay/stencil 来源 | .mat 声明 / shader attribute + Author | **shader attribute + Author** | 材质不知道管线概念 |
| L2 多 view | 材质建模 / 管线基础设施 | **管线** | multi-view 不是材质的关注点 |
| Material 保留 | 保留为容器 / 全部 ECS 化 | **保留** | 承担资产生命周期 + 参数容器 + 用户 API |
| 历史 .mat 迁移 | 保留 / 破坏性变更 | **破坏性变更** | 架构改动收敛到当前 schema |
