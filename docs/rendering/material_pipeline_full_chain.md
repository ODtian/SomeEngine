# 材质管线完整链路设计

> ⚠️ **状态：SUPERSEDED**
> 本文档描述的 friflo Entity 材质 pass 模型的核心思想已被吸收进 `materials/architecture.md` (v9)。
> v9 在此基础上做了重大演化：Component 字段直接持有 variant 引用（消灭 key 查找）、IComponentAuthor 透传机制、声明式多 pass + stencil 等。
> 本文件保留为历史参考，新开发以 v9 为准。

从用户编写 Shader 到管线 Dispatch 的完整数据流。

---

## 核心数据模型

```
Material
├── Name, AssetGuid, Params(textures/scalars)
└── Passes: Entity[]               ← friflo entity handle 数组
     │
     ├── Entity 0 (primary pass)
     │    ├── Tags: Opaque, ...     ← ITag（纯标记）
     │    └── Components:           ← IComponent（有数据）
     │         ├── ClusterSWRaster { Inline: ShaderEntry, Cached: ShaderEntry }
     │         ├── ClusterHWRaster { InlineVS, CachedVS, PS: ShaderEntry }
     │         ├── VertexDeform    { Entry: ShaderEntry }
     │         └── ClusterShader   { Entry: ShaderEntry }
     │
     ├── Entity 1 (overlay shade pass)
     │    ├── Tags: Opaque
     │    └── Components:
     │         └── ClusterShader   { Entry: ShaderEntry }
     │
     └── Entity 2 (shadow caster)
          ├── Tags: ShadowCaster
          └── Components:
               └── ShadowPass     { Entry: ShaderEntry }
```

**一个 Entity = 一个 MaterialPass。**
一个材质可以有多个 pass（多个 entity）。
每个 entity 上叠多个 Tags 和 Components 描述该 pass 的角色和入口。
不同 entity 可以有不同的角色组合，也可以引用不同的 shader。

为什么需要多 entity：
- friflo 中同类型 Component 只能有一个 → overlay 需要自己的 ClusterShader
- ShadowCaster 有独立 shader → 独立 entity
- 简单材质可以只有 1 个 entity（所有角色叠一起）

---

## ShaderEntry：自描述的入口引用

```csharp
public struct ShaderEntry
{
    public string ShaderGuid;   // shader 资产的 AssetGuid（永久稳定）
    public string EntryPoint;   // 该 shader 中的入口名（用户命名）
}
```

- 不依赖任何数组位置
- 跨版本稳定
- 加载时通过 AssetResolver 解析 ShaderGuid → ShaderAsset

---

## Step 1：用户编写 Shader

用户写完整的 Slang 程序，#include 管线头文件，自由命名所有 entry point。
可以是一个 shader 文件，也可以分多个文件：

```slang
// my_pbr.slang — 所有入口在一个文件
#include "cluster_pipeline.h"

struct MyEval : IVertexEvaluate {
    float3 evaluate(ClusterVertex v) { return mul(WorldMatrix, v.position); }
};

// ─── SW Raster ───
[shader("compute")]
void CS_InlineRaster(uint3 tid : SV_DispatchThreadID) {
    MyEval eval;
    swRasterize(eval.evaluate(loadVertex(tid)), packId(tid));
}

[shader("compute")]
void CS_CachedRaster(uint3 tid : SV_DispatchThreadID) {
    swRasterize(readDeformCache(tid), packId(tid));
}

// ─── Deform ───
[shader("compute")]
void CS_Deform(uint3 tid : SV_DispatchThreadID) {
    MyEval eval;
    writeDeformCache(tid, eval.evaluate(loadVertex(tid)));
}

// ─── HW Raster ───
[shader("vertex")]
void VS_InlineHW(uint vid : SV_VertexID, out float4 pos : SV_Position, out uint id : PACKED_ID) {
    MyEval eval;
    pos = float4(eval.evaluate(loadVertex(vid)), 1.0);
    id = packId(vid);
}

[shader("vertex")]
void VS_CachedHW(uint vid : SV_VertexID, out float4 pos : SV_Position, out uint id : PACKED_ID) {
    pos = float4(readDeformCache(vid), 1.0);
    id = packId(vid);
}

[shader("pixel")]
void PS_HW(float4 pos : SV_Position, uint id : PACKED_ID) : SV_Target {
    writeVisBuffer(id);
}

// ─── Shade ───
[shader("compute")]
void CS_Shade(uint2 tid : SV_DispatchThreadID) {
    MyPBRShade shade;
    shadePixel(tid, shade);
}
```

编译产出 ShaderAsset（AssetGuid = "abc-123"），Variants 数组：
```
Variant[0]: { Stage: Compute, EntryPoint: "CS_InlineRaster",  ContentHash: 0xA1 }
Variant[1]: { Stage: Compute, EntryPoint: "CS_CachedRaster",  ContentHash: 0xA2 }
Variant[2]: { Stage: Compute, EntryPoint: "CS_Deform",        ContentHash: 0xA3 }
Variant[3]: { Stage: Vertex,  EntryPoint: "VS_InlineHW",      ContentHash: 0xB1 }
Variant[4]: { Stage: Vertex,  EntryPoint: "VS_CachedHW",      ContentHash: 0xB2 }
Variant[5]: { Stage: Pixel,   EntryPoint: "PS_HW",            ContentHash: 0xC1 }
Variant[6]: { Stage: Compute, EntryPoint: "CS_Shade",         ContentHash: 0xD1 }
```

---

## Step 2：MaterialAsset Schema

```fbs
table PassEntry {
    shader_guid: string;
    shader: string;
    // 无 entry_point
    // 无 tags
}

table MaterialAsset (fs_serializer) {
    asset_guid: string;
    name: string;
    passes: [PassEntry] (fs_vector:"IList");       // shader 引用列表（仅用于加载 ShaderAsset）
    textures: [TextureBinding] (fs_vector:"IList");
    scalars: [ScalarParam] (fs_vector:"IList");
    tag_data: [ubyte] (fs_vector:"IList");         // [NEW] 自定义序列化的 entity 数据
}
```

**PassEntry 只作为 shader 加载清单**。entry_point 和 tag 全在 `tag_data` 中。

> `tag_data` 使用自定义二进制/JSON 格式（不用 friflo 的 StoreToJson，因为需要增量加载到共享 store）。

---

## Step 3：friflo 数据结构

### ShaderEntry

```csharp
/// <summary>自描述的 shader 入口引用。</summary>
public struct ShaderEntry
{
    public string ShaderGuid;   // AssetGuid，永久稳定
    public string EntryPoint;   // 用户命名的入口
}
```

### IComponent（角色 + 入口数据）

```csharp
/// <summary>SW Raster 角色。</summary>
public struct ClusterSWRaster : IComponent
{
    public ShaderEntry Inline;   // CS entry — 内联模式
    public ShaderEntry Cached;   // CS entry — 缓存模式
}

/// <summary>HW Raster 角色。</summary>
public struct ClusterHWRaster : IComponent
{
    public ShaderEntry InlineVS; // VS — 内联模式
    public ShaderEntry CachedVS; // VS — 缓存模式
    public ShaderEntry PS;       // PS — 两种模式共用
}

/// <summary>Deform 角色（仅 Cache 模式使用）。</summary>
public struct VertexDeform : IComponent
{
    public ShaderEntry Entry;    // CS entry
}

/// <summary>Shade 角色。</summary>
public struct ClusterShader : IComponent
{
    public ShaderEntry Entry;    // CS entry
}

/// <summary>Forward 管线角色（未来）。</summary>
public struct ForwardShader : IComponent
{
    public ShaderEntry VS;
    public ShaderEntry PS;
}
```

### ITag（纯标记）

```csharp
public struct Opaque       : ITag { }
public struct Masked       : ITag { }
public struct TwoSided     : ITag { }
public struct Translucent  : ITag { }
public struct ShadowCaster : ITag { }
```

---

## Step 4：导入器（MaterialAssetImporter）

```csharp
public static MaterialAsset Import(MaterialDefinition def)
{
    var store = new EntityStore();  // 导入阶段的临时 store
    string shaderGuid = def.ShaderAssetGuid;  // "abc-123"

    // ─── 简单材质：1 个 entity 承担所有角色 ───
    var primary = store.CreateEntity();
    primary.AddComponent(new ClusterSWRaster
    {
        Inline = new ShaderEntry { ShaderGuid = shaderGuid, EntryPoint = "CS_InlineRaster" },
        Cached = new ShaderEntry { ShaderGuid = shaderGuid, EntryPoint = "CS_CachedRaster" },
    });
    primary.AddComponent(new ClusterHWRaster
    {
        InlineVS = new ShaderEntry { ShaderGuid = shaderGuid, EntryPoint = "VS_InlineHW" },
        CachedVS = new ShaderEntry { ShaderGuid = shaderGuid, EntryPoint = "VS_CachedHW" },
        PS       = new ShaderEntry { ShaderGuid = shaderGuid, EntryPoint = "PS_HW" },
    });
    primary.AddComponent(new VertexDeform
    {
        Entry = new ShaderEntry { ShaderGuid = shaderGuid, EntryPoint = "CS_Deform" },
    });
    primary.AddComponent(new ClusterShader
    {
        Entry = new ShaderEntry { ShaderGuid = shaderGuid, EntryPoint = "CS_Shade" },
    });
    primary.AddTag<Opaque>();

    // ─── 多 pass 材质示例：overlay 独立 entity ───
    // var overlay = store.CreateEntity();
    // overlay.AddComponent(new ClusterShader { Entry = new ShaderEntry { ... } });
    // overlay.AddTag<Opaque>();

    // ─── ITagDeriver 链 ───
    foreach (var deriver in _derivers)
        deriver.Derive(store, shaderAssets);

    // ─── 序列化 ───
    byte[] tagData = MaterialTagSerializer.Serialize(store);  // 序列化所有 entity

    return new MaterialAsset
    {
        Passes = [new PassEntry { ShaderGuid = shaderGuid, Shader = "my_pbr" }],
        TagData = tagData,
    };
}
```

**多 shader 场景**：不同 entry 的 ShaderGuid 可以不同：
```csharp
entity.AddComponent(new ClusterSWRaster
{
    Inline = new ShaderEntry { ShaderGuid = "raster-shader-guid", EntryPoint = "CS_Raster" },
    Cached = new ShaderEntry { ShaderGuid = "raster-shader-guid", EntryPoint = "CS_CachedRaster" },
});
entity.AddComponent(new ClusterShader
{
    Entry = new ShaderEntry { ShaderGuid = "shade-shader-guid", EntryPoint = "CS_Shade" },
});
```
Passes 清单中列出所有用到的 shader：
```csharp
Passes = [
    new PassEntry { ShaderGuid = "raster-shader-guid", Shader = "my_raster" },
    new PassEntry { ShaderGuid = "shade-shader-guid",  Shader = "my_shade" },
]
```

---

## Step 5：运行时加载

```csharp
public static Material LoadFromAsset(
    MaterialAsset asset,
    MaterialStore materialStore,   // 共享 friflo EntityStore 的 wrapper
    IAssetResolver resolver,
    TextureLoadFunc? textureLoader)
{
    var material = new Material { Name = asset.Name, AssetGuid = ... };

    // 1. 从 tag_data 反序列化 → 直接在共享 store 中创建 entity（可能多个）
    //    MaterialTagSerializer.DeserializeAll 内部：
    //      - 解析 tag_data 中每个 entity 的 component/tag 数据
    //      - 对每个 entity 调用 materialStore.Store.CreateEntity()
    //      - 添加 Components 和 Tags
    //      - 返回所有创建好的 Entity handle
    material.Passes = MaterialTagSerializer.DeserializeAll(asset.TagData, materialStore.Store);

    // 2. 加载贴图
    if (asset.Textures != null)
        foreach (var binding in asset.Textures)
            material.SetTexture(binding.Name, textureLoader?.Invoke(binding.Path));

    // 3. 加载标量参数
    if (asset.Scalars != null)
        foreach (var scalar in asset.Scalars)
            ApplyScalarParam(material.Params, scalar.Name, scalar.Value);

    // 4. 注册（零推导，仅分配 MaterialID）
    materialStore.Register(material);

    return material;
}
```

**加载流程**：
```
tag_data → MaterialTagSerializer.DeserializeAll → 共享 store 中创建 entity[]
         → material.Passes = Entity[]
         → materialStore.Register() → 分配 MaterialID
```

无临时 store，无复制。反序列化直接在共享 store 中创建。

---

## Step 6：BinSpace 注册

```csharp
public void RegisterInBinSpace(Material material, IAssetResolver resolver, bool isInlineMode)
{
    uint materialId = material.MaterialID;

    foreach (var entity in material.Passes)
    {
        // ─── RasterBin ───
        if (entity.HasComponent<ClusterSWRaster>())
        {
            var raster = entity.GetComponent<ClusterSWRaster>();
            ShaderEntry se = isInlineMode ? raster.Inline : raster.Cached;
            var shader = resolver.Load<ShaderAsset>(se.ShaderGuid);
            ulong hash = shader.GetVariantContentHash(ShaderStage.Compute, se.EntryPoint);
            _binSpace.SetField(materialId, _rasterFieldIndex, hash);
        }

        // ─── ShadingBin ───
        if (entity.HasComponent<ClusterShader>())
        {
            var shade = entity.GetComponent<ClusterShader>();
            var shader = resolver.Load<ShaderAsset>(shade.Entry.ShaderGuid);
            ulong hash = shader.GetVariantContentHash(ShaderStage.Compute, shade.Entry.EntryPoint);
            _binSpace.SetField(materialId, _shadingFieldIndex, hash);
        }

        // ─── VertexEval（仅 Cache 模式）───
        if (!isInlineMode && entity.HasComponent<VertexDeform>())
        {
            var deform = entity.GetComponent<VertexDeform>();
            var shader = resolver.Load<ShaderAsset>(deform.Entry.ShaderGuid);
            ulong hash = shader.GetVariantContentHash(ShaderStage.Compute, deform.Entry.EntryPoint);
            _binSpace.SetField(materialId, _vertexEvalFieldIndex, hash);
        }
    }
}
```

**模式切换时**：重新注册所有材质的 BinSpace（entry 变 → hash 变 → bin 重排 → PSO 重建）。

---

## Step 7：PSO 构建

### 通用辅助

```csharp
// 从 ShaderEntry 解析到 ShaderAsset + Variant
private (ShaderAsset shader, ShaderBytecode variant) ResolveEntry(
    ShaderEntry se, ShaderStage stage, IAssetResolver resolver)
{
    var shader = resolver.Load<ShaderAsset>(se.ShaderGuid);
    var variant = shader.FindVariant(stage, se.EntryPoint)
        ?? throw new InvalidOperationException(
            $"No {stage} entry '{se.EntryPoint}' in shader '{se.ShaderGuid}'");
    return (shader, variant);
}
```

### SW Raster PSO

```csharp
// 遍历 RasterBin 的 bins
var raster = entity.GetComponent<ClusterSWRaster>();
ShaderEntry se = isInlineMode ? raster.Inline : raster.Cached;
var (shader, variant) = ResolveEntry(se, ShaderStage.Compute, resolver);
var cs = shader.CreateShader(context, variant.EntryPoint);
var pso = psoCache.GetOrCreateComputePSO(device, cs, ...);
```

### HW Raster PSO

```csharp
var hw = entity.GetComponent<ClusterHWRaster>();
ShaderEntry vsEntry = isInlineMode ? hw.InlineVS : hw.CachedVS;
ShaderEntry psEntry = hw.PS;

var (vsShader, vsVariant) = ResolveEntry(vsEntry, ShaderStage.Vertex, resolver);
var (psShader, psVariant) = ResolveEntry(psEntry, ShaderStage.Pixel, resolver);

var vs = vsShader.CreateShader(context, vsVariant.EntryPoint);
var ps = psShader.CreateShader(context, psVariant.EntryPoint);
var pso = psoCache.GetOrCreateGraphicsPSO(device, vs, ps, ...);
```

### Deform PSO（仅 Cache 模式）

```csharp
if (!isInlineMode && entity.HasComponent<VertexDeform>())
{
    var deform = entity.GetComponent<VertexDeform>();
    var (shader, variant) = ResolveEntry(deform.Entry, ShaderStage.Compute, resolver);
    var cs = shader.CreateShader(context, variant.EntryPoint);
    var pso = psoCache.GetOrCreateComputePSO(device, cs, ...);
}
```

### Shade PSO

```csharp
var shade = entity.GetComponent<ClusterShader>();
var (shader, variant) = ResolveEntry(shade.Entry, ShaderStage.Compute, resolver);
var cs = shader.CreateShader(context, variant.EntryPoint);
var pso = psoCache.GetOrCreateComputePSO(device, cs, ...);
```

---

## 完整示例

### 单 Shader 简单材质（1 entity）

```
MaterialAsset "DefaultPBR":
  passes: [{ shader_guid: "abc-123", shader: "my_pbr" }]
  tag_data: [Entity 0]
    Entity 0:
      ClusterSWRaster { Inline: ("abc-123","CS_InlineRaster"), Cached: ("abc-123","CS_CachedRaster") }
      ClusterHWRaster { InlineVS: ("abc-123","VS_InlineHW"), CachedVS: ("abc-123","VS_CachedHW"), PS: ("abc-123","PS_HW") }
      VertexDeform    { Entry: ("abc-123","CS_Deform") }
      ClusterShader   { Entry: ("abc-123","CS_Shade") }
      Tags: [Opaque]
```

Material.Passes.Length == 1。管线从 Passes[0] 读所有角色。

### 多 Shader 材质（1 entity，不同 entry 引用不同 shader）

```
MaterialAsset "ComplexPBR":
  passes: [
    { shader_guid: "raster-001", shader: "my_raster" },
    { shader_guid: "shade-002",  shader: "my_shade" },
  ]
  tag_data: [Entity 0]
    Entity 0:
      ClusterSWRaster { Inline: ("raster-001","CS_Raster"), Cached: ("raster-001","CS_CachedRaster") }
      ClusterHWRaster { InlineVS: ("raster-001","VS_HW"), CachedVS: ("raster-001","VS_CachedHW"), PS: ("raster-001","PS_HW") }
      VertexDeform    { Entry: ("raster-001","CS_Deform") }
      ClusterShader   { Entry: ("shade-002","CS_Shade") }
      Tags: [Opaque]
```

Material.Passes.Length == 1。虽然用了两个 shader，但只有 1 个 entity（角色不冲突）。

### Overlay 材质（2 entity，两个 ClusterShader 需要拆分）

```
MaterialAsset "OverlayPBR":
  passes: [
    { shader_guid: "abc-123", shader: "my_pbr" },
    { shader_guid: "overlay-456", shader: "my_overlay" },
  ]
  tag_data: [Entity 0, Entity 1]
    Entity 0 (primary):
      ClusterSWRaster { Inline: ("abc-123","CS_InlineRaster"), Cached: ("abc-123","CS_CachedRaster") }
      ClusterHWRaster { InlineVS: ("abc-123","VS_InlineHW"), CachedVS: ("abc-123","VS_CachedHW"), PS: ("abc-123","PS_HW") }
      VertexDeform    { Entry: ("abc-123","CS_Deform") }
      ClusterShader   { Entry: ("abc-123","CS_Shade") }
      Tags: [Opaque]
    Entity 1 (overlay):
      ClusterShader   { Entry: ("overlay-456","CS_OverlayShade") }
      Tags: [Opaque]
```

Material.Passes.Length == 2。primary 和 overlay 各有自己的 ClusterShader。

### 无 HW Draw 支持的材质

```
MaterialAsset "SWOnly":
  passes: [{ shader_guid: "sw-001", shader: "sw_only_raster" }]
  tag_data: [Entity 0]
    Entity 0:
      ClusterSWRaster { Inline: ("sw-001","CSRaster"), Cached: ("sw-001","CSCachedRaster") }
      VertexDeform    { Entry: ("sw-001","CSDeform") }
      ClusterShader   { Entry: ("sw-001","CSShade") }
      Tags: [Opaque]
      // 无 ClusterHWRaster → 管线知道此材质不支持 HW path
```

---

## 模式切换流程

```
用户切换 Inline ↔ DeformCache:
  1. pipelineConfig.IsInlineMode = !pipelineConfig.IsInlineMode
  2. 遍历所有已注册材质 → 重新计算 BinSpace hash
     （Inline 选 raster.Inline / hw.InlineVS）
     （Cached 选 raster.Cached / hw.CachedVS + dispatch VertexDeform）
  3. 重建 PSO groups（新 entry → 新 bytecode → 新 PSO）
  4. Cache 模式下额外 dispatch Deform CS
     Inline 模式下跳过 Deform dispatch
```

---

## 序列化格式（tag_data）

不使用 friflo 的 StoreToJson（它是全量替换，无法增量加载到共享 store）。
使用自定义格式：

```csharp
public static class MaterialTagSerializer
{
    // 导入时：EntityStore（可能包含多个 entity）→ byte[]
    public static byte[] Serialize(EntityStore store) { ... }

    // 加载时：byte[] → 在共享 store 中创建 entity[] → 返回所有 Entity handle
    public static Entity[] DeserializeAll(byte[] data, EntityStore sharedStore) { ... }
}
```

序列化内容（每个 entity）：
1. Component type 列表 + 每个 Component 的字段值
2. Tag type 列表
3. 格式可以是 JSON、MessagePack 或自定义二进制

---

## 遗留代码处置

| 当前代码 | 处置 |
|---------|------|
| `TagStore<T>` | 删除，由 friflo EntityStore 替代 |
| `IMaterialTag` 接口 | 删除，由 `ITag` / `IComponent` 替代 |
| `[MaterialTag]` attribute | 删除 |
| `MaterialTagResolver` source gen | 删除 |
| `MaterialPass` class | 重构：Material 持有 Entity[] 而非 MaterialPass[] |
| `MaterialPass.EntryPointName` | 删除，入口在 Component 的 ShaderEntry 中 |
| `MaterialPass.Shader` | 删除，shader 引用在 ShaderEntry.ShaderGuid 中 |
| `PassEntry.entry_point` | 从 Schema 移除 |
| `PassEntry.tags` | 从 Schema 移除 |
| `TagEntry` table | 从 Schema 移除 |
| `ClusterSWRasterPSOs` | 删除 |
| `RasterPSOBuilder.FindOrCreateComputePSO` | 改造：接收 ShaderEntry 参数 |
