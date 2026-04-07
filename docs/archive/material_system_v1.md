# 材质系统设计：基于 Slang 泛型与接口的 Visibility Pipeline 材质架构

## 1. 设计目标与核心决策

### 1.1 架构选择

| 决策 | 选择 | 理由 |
|------|------|------|
| 着色路径 | **统一 Forward + Thin Aux Buffer** | VisBuffer 已解耦几何与着色，GBuffer roundtrip 带宽浪费大（4K ~120MB/帧）；后处理属性通过辅助 Buffer 按需导出 |
| 材质编程模型 | **Struct + Interface** | struct 字段 = 可绑定参数（Slang 反射可发现），interface 提供编译期类型检查；通过继承/组合/泛型复用 |
| 参数传递 | **两级方案：SRB 切换（MVP）→ Bindless（优化）** | MVP 阶段用 per-材质实例 SRB 切换（简单直接），后续切 Bindless + 全局堆（最小 Bin 数） |
| 输出方式 | **`PixelContext` 封装输出方法** | 避免固定 `MaterialOutput` 结构，PBR/Toon/Unlit 等均为一等公民 |
| 顶点布局 | **零运行时布局元数据** | Runtime 不存储任何顶点属性布局描述；Page Header 仅提供 `AttributesOffset` + `TotalVertexCount`；Shader 端（用户态）自行决定如何解读 attribute 字节流（stream 顺序、编码格式、字节宽度全由 shader 作者 hardcode），为极限顶点压缩和自定义编码留出最大空间 |

---

## 2. 四阶段求值接口

管线在四个阶段提供可编程求值点：

```
管线阶段              接口                   Context → Output
──────────────────────────────────────────────────────────────
Pre-Deform CS    →  IVertexEvaluate    →  VertexContext → VertexOutput
SW Raster / PS   →  IPixelEvaluate     →  PixelRasterContext → PixelRasterOutput
Dice CS          →  IDomainEvaluate    →  DomainContext → DomainOutput
Shade CS         →  ISurfaceEvaluate   →  PixelContext → void (通过 ctx 写出)
```

### 2.1 `IVertexEvaluate` — 顶点变形（Pre-Deform CS）

对应传统 Vertex Shader 的可编程部分。用于骨骼蒙皮之外的程序化顶点动画。

```slang
struct VertexContext
{
    float3   objectPos;      // 物体空间位置
    float3   worldPos;       // Instance Transform 后的世界位置
    float3   normal;         // 物体空间法线
    float3   worldNormal;    // 世界空间法线
    float2   uv0;            // UV（纹理驱动动画）
    float4   vertexColor;    // 顶点色（权重/遮罩）
    uint     instanceID;
    float4x4 localToWorld;
    float    time;
    float    deltaTime;
};

struct VertexOutput
{
    float3 positionOffset;   // 世界空间位移
    float3 normalOverride;   // 覆盖法线，float3(0) = 不覆盖
};

interface IVertexEvaluate
{
    VertexOutput evaluateVertex(VertexContext ctx);
}
```

### 2.2 `IPixelEvaluate` — 可编程光栅化（SW Raster / HW PS）

在光栅化像素循环中调用，用于 Masked（Alpha Test）和 PDO（Pixel Depth Offset）。

```slang
struct PixelRasterContext
{
    float3 barycentrics;
    float  depth;
    float2 uv0;
    bool   isFrontFace;
};

struct PixelRasterOutput
{
    bool  discard;           // Alpha Test: true = 丢弃
    float depthOffset;       // PDO: 深度偏移, 0 = 无
};

interface IPixelEvaluate
{
    PixelRasterOutput evaluatePixel(PixelRasterContext ctx);
}
```

### 2.3 `IDomainEvaluate` — Tessellation Displacement（Dice CS）

在 Dice 阶段对每个微顶点调用。

```slang
struct DomainContext
{
    float3 worldPos;         // 微顶点插值后世界位置
    float3 normal;           // 微顶点法线
    float2 uv0;
    float2 patchUV;          // Patch 内参数坐标 (u,v)
};

struct DomainOutput
{
    float displacement;      // 沿法线位移量
    bool  discard;
};

interface IDomainEvaluate
{
    DomainOutput evaluateDomain(DomainContext ctx);
}
```

### 2.4 `ISurfaceEvaluate` — 表面着色（Shade CS）

`PixelContext` 定义见上方，其中顶点属性访问通过原始数据引用字段实现（零运行时布局元数据）。

```slang
interface ISurfaceEvaluate
{
    void evaluateSurface(PixelContext ctx);
}
```

---

## 3. 材质参数模型（两级方案）

### 3.1 问题

Shade Binning（Count → Reserve → Scatter）按 MaterialID 分组像素。如果每个材质实例对应一个独立 Bin，材质实例数多时 Dispatch 次数爆炸。

### 3.2 方案 A：SRB 切换（MVP，推荐先实现）

**同一 PSO 下，每个材质实例切换 SRB（不切换 Pipeline State）。**

材质 struct 直接持有纹理资源作为字段，C# 端每个材质实例创建独立 SRB：

```slang
struct StandardPBRMaterial : ISurfaceEvaluate {
    Texture2D    albedoMap;        // 直接持有纹理
    Texture2D    normalMap;
    Texture2D    armMap;
    SamplerState sampler;
    float4       baseColorTint;

    void evaluateSurface(PixelContext ctx) {
        float2 uv = ctx.getUV(0);
        float4 albedo = this.albedoMap.SampleGrad(this.sampler, uv, ...);
        // ... 光照计算 ...
        ctx.writeColor(float4(finalColor, 1.0));
    }
}
```

C# 端编排：

```csharp
// 同一 shader 类型共享 PSO，每个材质实例有独立 SRB
ctx.SetPipelineState(standardPBR_PSO);  // 只设一次

for (int matInst = 0; matInst < pbrMaterials.Count; matInst++)
{
    ctx.CommitShaderResources(pbrMaterials[matInst].SRB, ...);  // 切 SRB（轻量）
    ctx.DispatchComputeIndirect(pbrMaterials[matInst].IndirectArgs);
}
```

**特点：**
- ✅ 最简单直接，Slang struct 字段就是资源绑定
- ✅ 不需要 Bindless 基础设施
- ✅ 调试友好，每个 SRB 对应明确的资源集
- ⚠️ N 个材质实例 = N 次 SRB 切换 + N 次 Dispatch
- ⚠️ 100 个材质的额外开销约 0.1-0.5ms（可接受）

**Shade Bin 策略（方案 A）：**
Bin Key = `(ShaderType, MaterialInstanceID)`。同一 shader 类型内不切换 PSO，只切换 SRB。

### 3.3 方案 B：Bindless + 全局堆（性能优化路径）

将所有纹理放入全局 Bindless Descriptor Heap，所有材质参数放入全局 `MaterialParamsBuffer`。像素在 CS 内通过间接寻址查询参数，同一 shader 类型只需 1 次 Dispatch。

```slang
Texture2D GlobalTextures[];
SamplerState GlobalSamplers[];

struct PBRMaterialParams {
    uint   albedoTexIndex;
    uint   normalTexIndex;
    uint   armTexIndex;
    uint   samplerIndex;
    float4 baseColorTint;
    float  roughnessScale;
    float  metallicScale;
};
StructuredBuffer<PBRMaterialParams> MaterialParamsBuffer;

struct StandardPBRMaterial : ISurfaceEvaluate {
    void evaluateSurface(PixelContext ctx) {
        uint matID = ctx.getMaterialID();
        PBRMaterialParams p = MaterialParamsBuffer[matID];
        float4 albedo = GlobalTextures[p.albedoTexIndex].SampleGrad(
            GlobalSamplers[p.samplerIndex], uv, ddx, ddy);
        // ...
    }
}
```

C# 端：

```csharp
// 每个 shader 类型只有 1 个 SRB，绑定全局堆
ctx.SetPipelineState(standardPBR_PSO);
ctx.CommitShaderResources(globalSRB, ...);   // 只绑一次
ctx.DispatchComputeIndirect(pbrBin.IndirectArgs);  // 1 次 Dispatch
```

**特点：**
- ✅ N 个材质实例 = 1 次 Dispatch（最优）
- ✅ Bin 数 = Shader 类型数（3-5 个）
- ⚠️ 需要 Bindless Descriptor Heap 基础设施
- ⚠️ 材质 struct 不直接持有纹理，需通过索引间接访问

**Shade Bin 策略（方案 B）：**
Bin Key = `ShaderType`。100 种 PBR 材质 → 1 个 Bin → 1 次 Dispatch。

### 3.4 对比

| | 方案 A：SRB 切换 | 方案 B：Bindless |
|--|--|--|
| **100 种 PBR** | 100 次 Dispatch（~0.3ms） | 1 次 Dispatch |
| **PSO 切换** | 0 | 0 |
| **SRB 切换** | N 次（轻量） | 0 |
| **实现复杂度** | 低 | 中（需全局堆管理） |
| **Shader 写法** | 自然（直接持有纹理） | 间接（索引查询） |
| **调试** | 容易 | 较难 |

> [!TIP]
> **两种方案对 interface 层完全透明** —— ISurfaceEvaluate 等接口不变，只是 struct 内部的资源获取方式不同。可先用方案 A 快速迭代，材质数量成为瓶颈后无缝切换方案 B。

### 3.5 per-instance 参数覆盖（两方案通用）

现有 `GpuInstanceHeader.MetadataOffset/MetadataCount` 预留了 per-instance 属性堆寻址（类 Unity BRG）。

- **per-material 参数**（纹理、标量默认值） → struct 字段 或 `MaterialParamsBuffer`
- **per-instance 覆盖**（颜色 tint、自定义 float） → `InstanceDataHeap[MetadataOffset]`

`PixelContext` 的 `getInstanceProperty_*` 方法从 `InstanceDataHeap` 读取覆盖值。

---

## 4. 接口组合

| 材质类型 | `ISurfaceEvaluate` | `IPixelEvaluate` | `IVertexEvaluate` | `IDomainEvaluate` |
|---------|:---:|:---:|:---:|:---:|
| 纯不透明 PBR | ✅ | — | — | — |
| Masked PBR | ✅ | ✅ | — | — |
| 程序化顶点动画 | ✅ | — | ✅ | — |
| 植被 (动画+Masked) | ✅ | ✅ | ✅ | — |
| 地形 Tessellation | ✅ | — | — | ✅ |
| 自定义 Toon | ✅ | — | — | — |

纯不透明材质不实现 `IPixelEvaluate`，管线跳过光栅化求值走快速路径。

---

## 5. 复用模式

### 5.1 Struct 继承

```slang
struct PBRBase {
    Texture2D albedoMap;
    Texture2D normalMap;
    Texture2D armMap;
    SamplerState sampler;
    float4 tint;

    void samplePBR(float2 uv, float2 ddx, float2 ddy,
                   out float3 color, out float3 normalTS, out float3 arm) {
        color    = this.albedoMap.SampleGrad(this.sampler, uv, ddx, ddy).rgb * this.tint.rgb;
        normalTS = this.normalMap.SampleGrad(this.sampler, uv, ddx, ddy).rgb * 2.0 - 1.0;
        arm      = this.armMap.SampleGrad(this.sampler, uv, ddx, ddy).rgb;
    }
};

struct FoliageMaterial : PBRBase, ISurfaceEvaluate, IVertexEvaluate, IPixelEvaluate {
    Texture2D windNoise;
    float windStrength;
    float alphaClip;

    void evaluateSurface(PixelContext ctx)  { /* 复用 samplePBR */ }
    VertexOutput evaluateVertex(VertexContext ctx) { /* 风动画 */ }
    PixelRasterOutput evaluatePixel(PixelRasterContext ctx) { /* Alpha Test */ }
};
```

### 5.2 组合嵌套

```slang
struct SharedNoise {
    Texture3D volume;
    SamplerState sampler;
    float scale;
    float sampleNoise(float3 pos) { return volume.Sample(sampler, pos * scale).r; }
};

struct WaveMaterial : ISurfaceEvaluate, IVertexEvaluate {
    PBRBase     pbr;
    SharedNoise noise;
    float       amplitude;
    /* ... */
};
```

### 5.3 泛型组合

```slang
interface INoiseProvider {
    float sampleNoise(float3 pos);
}

struct PerlinNoise3D : INoiseProvider {
    Texture3D tex; SamplerState s;
    float sampleNoise(float3 pos) { return tex.Sample(s, pos).r; }
};

struct SimplexNoise : INoiseProvider {
    float sampleNoise(float3 pos) { /* 纯数学 */ }
};

struct WaveVertex<TNoise : INoiseProvider> : IVertexEvaluate {
    TNoise noise;
    float amplitude;
    VertexOutput evaluateVertex(VertexContext ctx) {
        VertexOutput o;
        o.positionOffset = float3(0, noise.sampleNoise(ctx.worldPos + ctx.time) * amplitude, 0);
        o.normalOverride = float3(0, 0, 0);
        return o;
    }
};
```

---

## 6. 管线泛型入口

### 6.1 Shade 入口

```slang
[shader("compute")]
[numthreads(64, 1, 1)]
void CSShade<TMaterial : ISurfaceEvaluate>(
    uniform TMaterial material,
    /* system resources */
    uint3 tid : SV_DispatchThreadID)
{
    PixelContext ctx = buildPixelContext(tid.x, ...);
    if (!ctx.valid) return;
    material.evaluateSurface(ctx);
}
```

### 6.2 可编程光栅入口

```slang
bool EvaluateRasterPixel<TMaterial : IPixelEvaluate>(
    TMaterial material, PixelRasterContext ctx, inout float adjustedDepth)
{
    PixelRasterOutput result = material.evaluatePixel(ctx);
    adjustedDepth = ctx.depth + result.depthOffset;
    return !result.discard;
}
```

### 6.3 Pre-Deform / Dice 入口

```slang
float3 EvaluateVertex<TMaterial : IVertexEvaluate>(TMaterial material, VertexContext ctx)
{
    VertexOutput o = material.evaluateVertex(ctx);
    return ctx.worldPos + o.positionOffset;
}

float3 EvaluateDomain<TMaterial : IDomainEvaluate>(TMaterial material, DomainContext ctx)
{
    DomainOutput o = material.evaluateDomain(ctx);
    return ctx.worldPos + ctx.normal * o.displacement;
}
```

---

## 7. C# 端编译与编排

### 7.1 编译特化

```csharp
// 为每种 shader 类型编译特化变体
var linked = session.Link(entryModule, materialModule);
var specialized = linked.Specialize("CSShade", new[] { "StandardPBRMaterial" });
var dxil = specialized.Compile(Target.DXIL);
```

### 7.2 Dispatch 编排（方案 A — SRB 切换）

```csharp
// 按 shader 类型分组，类型内遍历材质实例切 SRB
foreach (var shaderType in shaderTypes)
{
    ctx.SetPipelineState(shaderType.PSO);  // 每种 shader 类型只设一次
    foreach (var matInst in shaderType.MaterialInstances)
    {
        ctx.CommitShaderResources(matInst.SRB, ...);  // 切 SRB（轻量）
        ctx.DispatchComputeIndirect(matInst.IndirectArgs);
    }
}
```

### 7.3 Dispatch 编排（方案 B — Bindless）

```csharp
// 每个 shader 类型只需 1 次 Dispatch
foreach (var shaderType in shaderTypes)
{
    ctx.SetPipelineState(shaderType.PSO);
    ctx.CommitShaderResources(globalBindlessSRB, ...);  // 绑全局堆
    ctx.DispatchComputeIndirect(shaderType.IndirectArgs);
}
```

---

## 8. 专题系统

### 8.1 透明渲染

Visibility Buffer 不支持 alpha blending。透明物体走两条路径：

| 类型 | 方案 |
|------|------|
| **半透明表面**（玻璃、水体等） | Stochastic Transparency + TAA：光栅阶段用随机阈值 discard，TAA 累积恢复半透明效果 |
| **特殊透明物体**（粒子、UI 叠加等） | 独立 Forward 透明 Pass：走单独管线或 Cluster 管线混合后合成 |

### 8.2 Decal 系统

无 GBuffer → Decal 不能传统叠加。采用三条路径：

**静态网格 Decal：** 前向渲染路线。

**Cluster-Decal 系统（类 Clustered Lighting）：**
- 复用 Clustered Light Culling 框架，光源替换为 Decal（投影盒 + 纹理 + 混合参数）
- 材质在着色阶段通过 `PixelContext.getDecalCount()` / `getDecal()` 自行查询和混合
- 优势：甚至可以在光栅化之前就求值（Pre-Deform 阶段）

**骨骼网格 Decal：**
- 表面渲染路径：投影盒子绑定到骨骼，前向渲染时读取范围内顶点，在 Rest 姿态下计算投影采样后渲染 Overlay
- 光栅前求值路径：Decal 绑定到影响的骨骼，运行时遍历骨骼去求值

### 8.3 材质 LOD

| 方案 | 实现 |
|------|------|
| **用户手动** | 材质在 `evaluateSurface` 中根据距离/Mip 简化采样 |
| **自动切换 MaterialID** | Visibility 结束后遍历 Cluster，根据距离和 LOD 级别自动切换到简化材质 |
| **烘焙时分层** | 高 LOD 层级在资产烘焙阶段直接指定不同材质 |

### 8.4 多 Pass 兼容

| Pass | 调用的接口 | 说明 |
|------|-----------|------|
| **主着色** | `ISurfaceEvaluate` | 完整着色 |
| **Shadow Depth** | 不透明无需材质；Masked 仅调用 `IPixelEvaluate` | Alpha Test only |
| **Motion Vector** | `IVertexEvaluate` 提供前后帧位移差 | `VertexOutput` 可扩展 `prevPositionOffset` |
| **Stochastic 透明** | `ISurfaceEvaluate` + 随机阈值 | 复用主着色路径 |

---

## 9. 与 Raster Bin 系统的集成

| Bin 环节 | 材质系统提供 |
|---------|------------|
| **Binning CS** | `MaterialID` → 查 Registry 得到接口实现组合 → 确定 Bin Key |
| **Pre-Deform CS** | 对实现了 `IVertexEvaluate` 的 Bin：dispatch 特化入口 |
| **SW Raster CS** | 对实现了 `IPixelEvaluate` 的 Bin：扫描线中调用特化 `EvaluateRasterPixel` |
| **HW Raster PS** | 对实现了 `IPixelEvaluate` 的大三角形 Bin：特化 PS |
| **Shade CS** | 对每个 Shader 类型：dispatch `CSShade<ConcreteMaterial>` |
| **Dice CS** | 对实现了 `IDomainEvaluate` 的 Bin：调用特化 `EvaluateDomain` |

---

## 10. 渐进式实施

| 阶段 | 内容 | 前置 |
|------|------|------|
| **Step 1** | 定义四个接口 + Context/Output 结构 + `PixelContext` | — |
| **Step 2** | `StandardPBRMaterial : ISurfaceEvaluate` + 泛型 `CSShade` | Step 1 + 现有 Shade Binning |
| **Step 3** | SRB 切换编排（方案 A） + Slang 反射绑定 | Step 2 |
| **Step 4** | `InstanceDataHeap` per-instance 属性读取 | Step 3 + instance_plan Phase 4 |
| **Step 5** | `MaskedPBRMaterial : IPixelEvaluate` + SW Raster 集成 | Step 3 + raster_impl Phase 3.5 |
| **Step 6** | `IVertexEvaluate` + Pre-Deform 集成 | Step 1 + raster_impl Phase 4 |
| **Step 7** | `IDomainEvaluate` + Dice 集成 | Step 1 + raster_impl Phase 6 |
| **Step 8** | Cluster-Decal 系统 | Step 3 |
| **Step 9** | Stochastic Transparency + TAA | Step 5 |
| **Step 10** | Bindless 纹理堆 + `MaterialParamsBuffer`（方案 B 优化） | Step 3 成为瓶颈后 |

> [!NOTE]
> **Step 1-4 形成 MVP**：泛型材质着色 + SRB 切换 + per-instance 覆盖。
> **Step 10** 在材质实例数量成为瓶颈后切换到 Bindless 方案，接口层无需改动。

---

## 11. 设计决策记录

| 决策 | 理由 |
|------|------|
| 统一 Forward，不走 GBuffer | VisBuffer 已解耦几何与着色，GBuffer roundtrip 带宽浪费大 |
| Struct + Interface | struct 字段 = 可绑定参数，interface 提供编译期检查 |
| SRB 切换为 MVP，Bindless 为优化路径 | SRB 切换简单直接（不切 PSO，只切描述符表）；Bindless 最优但需基础设施 |
| `PixelContext` 封装输出 | 避免固定 `MaterialOutput`，PBR/Toon/Unlit 都是一等公民 |
| per-instance 参数走 `InstanceDataHeap` | 颜色 tint、自定义覆盖，复用现有 `MetadataOffset`（类 Unity BRG） |
| Decal 走 Cluster-Decal 系统 | 无 GBuffer 无法传统叠加，材质自行查询混合最灵活 |
| 透明走 Stochastic + TAA | VisBuffer 不支持 alpha blending，随机透明度 + 时序累积 |
| 命名统一 `I___Evaluate` | 对应管线四个可编程求值点 |
