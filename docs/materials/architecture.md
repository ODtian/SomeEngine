# Material System 架构设计 (v10)

> 前版见 [architecture_v8_archived.md](architecture_v8_archived.md)

## 设计原则

1. **Pull 模型**：管线 Stage 通过 ECS Query 主动拉取所需材质，材质不知道谁消费自己
2. **材质只描述能力**：材质声明实现了哪些 evaluate 接口，不关心管线内部的 mode（inline/cached/SW/HW）
3. **管线定义 Component**：每条管线定义自己的 IComponent 类型，材质系统核心不包含管线语义
4. **Importer 管线无关**：Importer 只做 Slang attribute → 注册 Author 的透传调度，不解释管线含义
5. **瘦 Material**：1 Material = 1 shader + 1 params + tags = 1 Entity。Material 是原子的资产单元
6. **组合靠 tag/component**：overlay 等多 pass 角色通过 Entity 上的 Component 表达，BinQueue 直接 query。无容器层级
7. **Material 是资产容器**：Material 对象持有 shader 引用、参数、tags，不参与 dispatch 逻辑
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
  │  引用 1 个 ShaderAsset + 声明贴图/标量参数 + tags
  │  1 Material = 1 shader = 1 Entity
  ▼
Material (运行时资产容器)
  │  持有 ShaderParamBag + 1 Entity
  │
  └──▶ Entity
         Tags: [Opaque]
         Components: [ClusterRaster, ClusterShade, ClusterDeform, MaterialRef, ...]
         Params: material.Params (引用)

多 pass 组合 (MaterialStack 或实例级)
  │  [skin.mat, stocking.mat, outline.mat]
  │  多个 Material → 多个 Entity
  └──▶ Author 单阶段：每个 Entity 独立挂 Component
       管线通过 BinGroup 动态排序消费
```

### Material — 资产/参数容器

```csharp
public class Material
{
    public string Name;
    public AssetGuid AssetGuid;
    public string ShaderAssetName;         // 引用的 ShaderAsset
    public ShaderParamBag Params;          // 贴图、采样器、标量
    public Entity Entity;                  // 此材质的唯一 Entity

    public void SetTexture(string name, ITextureView view) => Params.Set(name, view);
    public void SetSampler(string name, ISampler sampler) => Params.Set(name, sampler);
    public Material Instantiate();         // clone Params + 创建新 Entity
}
```

Material 不参与 dispatch 路径。dispatch 只读 Entity Component。

### Entity — 渲染意图

**1 Material = 1 Entity**。Entity 代表一个材质的渲染身份。

所有使用同一 Material 的物体实例共享同一 Entity。Entity 描述材质级策略，不描述实例级状态。

### Tag vs Component

| 类型 | 用途 | 数据 | ECS 映射 |
|---|---|---|---|
| 语义分类 | Opaque / Masked / Translucent / TwoSided | 无数据 | `ITag`（零大小标记） |
| 管线入口 | ShadowCaster / Outline | 无数据 | `ITag` |
| 管线 capability | ClusterRaster / ClusterShade / ClusterDeform / ForwardShade / ShadowRaster | 携带 ShaderVariantRef 字段 | `IComponent`（管线定义） |
| overlay 角色 | OverlayShade | 携带 Layer 字段 | `IComponent`（管线定义） |
| stencil 配置 | StencilState | 携带 Ref/Compare/PassOp 字段 | `IComponent`（管线定义） |
| 材质引用 | MaterialRef | 回指 Material 对象 | `IComponent` |

不再有 ReusePixels / ReuseVisibleSet — 跨 Entity 引用已消除。

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
| **passes** | 1 Material = 1 shader。多 pass 通过多个 Material + tag/component 组合 |
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
  ├── 创建 Material 对象，设 ShaderAssetName + Params
  │
  ├── 加载对应 ShaderAsset
  │
  ├── 在 MaterialSystem.Store（独立 static EntityStore）创建 Entity
  │     ├── 挂 MaterialRef { Owner = material }
  │     ├── 从 .mat tags 设置 ITag（Opaque / Masked / ...）
  │     └── 遍历 ShaderAsset variant 的 entry point attributes
  │           └── 对每个 attribute，调用注册的 Author
  │               Author 解释 attr → 挂管线 Component + 角色 Component
  │               (单阶段，不引用其他 Entity)
  │
  └── material.Entity = 创建的 Entity
```

---

## 多 Pass 组合

多 pass 角色（overlay、outline 等）通过 Entity 上的 tag/Component 表达。没有容器层级。

### 角色来源

| 角色 | Component | 谁设的 | 场景 |
|---|---|---|---|
| primary shade | `ClusterShade`（无 `OverlayShade`） | Author（shader attr） | 默认 |
| overlay shade | `ClusterShade` + `OverlayShade { Layer }` | 运行时代码 / 编辑器 | 同一 shader 在不同上下文可做 primary 或 overlay |
| stencil | `StencilState { Ref, Compare, PassOp }` | Author（shader attr `[StencilConfig]`） | shader 固有 |

overlay 角色**不来自 shader attribute**（同一个 PBR shader 可以在 mesh A 上做 primary，在 mesh B 上做 overlay）。
由使用侧直接设在 Entity 上：

```csharp
// 皮肤做 primary（默认，无需额外操作）
var skin = materialSystem.Load("skin.mat");
// skin.Entity 有 ClusterShade，没有 OverlayShade → BinQueue 归入 primary region

// 丝袜做 overlay —— 直接改 Entity，不复制
var stocking = materialSystem.Load("stocking.mat");
stocking.Entity.AddComponent(new OverlayShade { Layer = 1 });
// BinQueue query 看到 OverlayShade → 归入 overlay region
```

### BinQueue 直接 query

管线不做拓扑排序。BinQueue 根据 Entity 的 Component 数据自动产出有序执行区间。

---

## Bin 系统

### BinGroup（替代固定 Region）

原 `RegisterRegion(name, query, sig)` 改为 `RegisterGroup(group)`：

```csharp
public struct BinGroup
{
    /// <summary>查询此组包含的 Entity。</summary>
    public Func<Entity[]> Query;

    /// <summary>
    /// 从 Entity 计算 orderKey。相同 key 的 Entity 归入同一 region。
    /// Region 按 key 升序排列。key 间插入 barrier。
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

BinSpace 作为 BinQueue 之上的统一层，管理：

- 多个 BinQueue field（per stage）
- MaterialSlotBuffer SOA 布局
- MaterialSlotCache（hash + refcount 共享）
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

### Sig0 / Sig1 双签名

已在 ClusterShade 中实现：

- **Sig0**：全局资源（VisBuffer、ClusterData、Lights 等），所有 bin 共享
- **Sig1**：per-material 资源（贴图、采样器），按 material 参数布局缓存

Sig1 cache key 基于 ShaderAsset metadata 过滤后的 binding layout（BATCH-05 已实现）。

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

## 与 v8 架构的迁移

| v8 概念 | v10 概念 | 变化 |
|---|---|---|
| `MaterialPass` | **删除** | 职责分散到 Entity Component + 管线代码 |
| `MaterialRegistry` | **删除** | 用 friflo `EntityStore` 替代 |
| `TagStore<MaterialPass>` | **删除** | 用 friflo archetype query 替代 |
| `IMaterialTag` | `ITag`（零大小）或 `IComponent`（带数据） | friflo 原生类型 |
| `MaterialPass.ShaderRef` | Component 字段 `ShaderVariantRef` | |
| `MaterialPass.MaterialID` | Entity 上的 `MaterialSlotId : IComponent` | |
| `MaterialPass.ComputeSignature()` | 管线 signatureFunc(Entity) | |
| `MaterialPass.ApplyToSRB()` | 管线 stage dispatch 代码 | |
| `MaterialPass.Params` | `MaterialRef` → `Material.Params` | 回指 Material 共享 Params |
| `Material.AddPass()` | **删除** | 多 pass 通过 MaterialStack 组合多个 Material |
| `MaterialTagDeserializerGenerator` | 更新为生成 `entity.AddTag<T>()` / `entity.AddComponent()` | |
| `FindSibling<TTag>` | **删除** | 无跨 Entity 引用 |
| `RegisterRegion` (固定) | `RegisterGroup` (动态 orderKey) | BinQueue 自动产出有序 region |
| `ReusePixels` / `ReuseVisibleSet` | **删除** | 用 OverlayShade.Layer + BinGroup orderKey 替代 |
| 旧 .mat FlatBuffer schema | **破坏性变更**，新 schema（1 shader + 1 params + tags） | |

### 不变的部分

- BinQueue 核心算法（分组 + 签名去重），API 从固定 region 改为动态 BinGroup
- BinSpace 统一层
- MaterialSlotBuffer SOA 布局
- MaterialSlotCache hash+refcount 共享
- ShaderParamBag（结构不变，位置在 Material 上）
- ShaderAsset 资产格式（扩展 variant metadata）

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

此部分设计不变，但数据源从 MaterialPass 切换到 Entity。

---

## 设计决策记录

| 决策 | 选项 | 选择 | 理由 |
|---|---|---|---|
| Material 胖瘦 | 胖（N shader N pass）/ 瘦（1 shader 1 Entity） | **瘦** | 组合不绑死，同一材质可在不同上下文复用 |
| 多 pass 组合位置 | Material 内部 / 外部（Stack/Instance） | **外部** | 组合关系不是材质固有属性 |
| 管线 mode 透明性 | mode 暴露到材质 / 对材质不透明 | **不透明** | mode 是管线全局配置，不应污染材质模型 |
| variant 寻址 | string key 查找 / Component typed 字段 | **typed 字段** | 编译时安全，无运行时查找开销 |
| Entity 粒度 | 1 Material = 1 Entity / 1 Material = N Entity | **1:1** | 资源归属无歧义，无跨 Entity 引用 |
| Author 阶段 | 单阶段（只操作当前 Entity）/ 二阶段（需跨 Entity） | **单阶段** | 消除 Phase 2，简化加载流程 |
| BinQueue region | 固定命名 / 动态 orderKey | **动态 orderKey** | 支持任意层数 overlay，管线不做拓扑排序 |
| 入口点展开 | importer 知道管线 / 用户展开 + importer 透传 | **用户展开 + 透传** | importer 管线无关 |
| Author 协议 | importer 解析 attr / 透传给 Author | **透传** | attr 协议是 Slang shader ↔ C# Author 的私有契约 |
| MaterialPass | 保留 / 删除 | **删除** | Entity + Component 替代全部职责 |
| TagStore | 保留 / 用 EntityStore 替代 | **替代** | friflo archetype query 更高效 |
| overlay/stencil 来源 | .mat 声明 / shader attribute + Author | **shader attribute + Author** | 材质不知道管线概念 |
| 跨 Entity 引用 | ReusePixels{Primary=Entity} / Entity 自描述 + BinGroup 排序 | **自描述 + BinGroup** | 消除图解析，管线按 region 顺序 dispatch |
| L2 多 view | 材质建模 / 管线基础设施 | **管线** | multi-view 不是材质的关注点 |
| Material 保留 | 保留为容器 / 全部 ECS 化 | **保留** | 承担资产生命周期 + 参数容器 + 用户 API |
| 旧 .mat 兼容 | 兼容 / 破坏性变更 | **破坏性变更** | 架构改动太大，无法兼容 |
