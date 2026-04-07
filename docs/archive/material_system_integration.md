# 材质系统引擎集成设计（MVP 阶段）

> 本文档描述 C# 引擎侧的材质系统集成方案，对应 [material_system.md](file:///f:/SomeEngine/docs/design/material_system.md) Step 1-3（MVP）阶段。
> Shader 侧的接口设计、泛型模板、Bindless 优化路径等已在上述文档中定义，本文不再重复。

---

## 1. 现状分析

### 当前数据流

```
MeshInstance.MaterialID (ECS Component)
    → InstanceSyncSystem
        → GpuInstanceHeader.MaterialID (GPU)
            → cluster_shade_binning.slang (Bin Key = MaterialID)
                → cluster_shade_material.slang (per-Bin Indirect Dispatch)
```

### 已有但不足的部分

| 已有 | 不足 |
|------|------|
| `MeshInstance.MaterialID` (uint) | MaterialID 只是裸数字，无对应的 Material 类 |
| `InstanceSyncSystem` 同步 MaterialID | 无 MaterialRegistry，不知道 MaterialID 对应什么 shader/资源 |
| `ClusterMaterialShadePass` per-MaterialID Dispatch | 所有 MaterialID 共用一个 PSO/SRB，无法区分 shader 类型 |
| `GpuInstanceHeader.MetadataOffset/Count` | C# 侧无 InstanceDataHeap，per-instance 参数覆盖不可用 |

---

## 2. 核心概念

### 2.1 Shader Type（着色器类型）

**定义：** 一组 Slang 代码，编译为一个 PSO。对应设计文档中 `CSShade<TMaterial>` 的一次特化。

**MVP 阶段只有 1 个 shader type：硬编码的 `StandardPBR`**。后续每个实现 `ISurfaceEvaluate` 的 struct 对应一个 shader type。

```
ShaderType = PSO + 入口函数
```

### 2.2 Material（材质）

**定义：** Shader Type + 一组参数值（纹理 + 标量）。每个 Material 对应一个 SRB（方案 A）。

```
Material = ShaderType + 参数绑定 → SRB
```

### 2.3 Material Instance（材质实例使用）

`MeshInstance` ECS 组件通过 `MaterialID` 引用一个 Material。

---

## 3. C# 类型设计

### 3.1 MaterialShaderType

代表一种着色器类型，管理 PSO 和 shader 编译产物。

```csharp
/// 一种材质着色器类型 = 一个 PSO
public class MaterialShaderType : IDisposable
{
    public string Name { get; }           // e.g. "StandardPBR"
    public IPipelineState PSO { get; }    // Compute PSO for shade pass
    
    // 创建一个新的 SRB（每个 Material 实例调用一次）
    public IShaderResourceBinding CreateSRB();
}
```

MVP 阶段只有一个 `MaterialShaderType`（硬编码 StandardPBR shader）。

### 3.2 Material

代表一个材质实例，持有参数绑定。

```csharp
/// 材质实例 = ShaderType 引用 + 参数绑定
public class Material : IDisposable
{
    public uint MaterialID { get; }              // 全局唯一，分配时确定
    public MaterialShaderType ShaderType { get; }
    public IShaderResourceBinding SRB { get; }   // 方案 A：独立 SRB
    
    // 参数 API
    public void SetTexture(string name, ITextureView texture);
    public void SetFloat4(string name, Vector4 value);
    // ... 可按需扩展
}
```

### 3.3 MaterialRegistry

全局材质管理器，负责 ID 分配和查询。

```csharp
/// 全局材质注册表
public class MaterialRegistry : IDisposable
{
    // Shader Type 管理
    public MaterialShaderType RegisterShaderType(string name, ShaderAsset asset, 
                                                  string entryPoint);
    
    // 材质实例管理
    public Material CreateMaterial(MaterialShaderType shaderType);
    public Material? GetMaterial(uint materialID);
    
    // 着色编排查询（供 ClusterMaterialShadePass 使用）
    public IReadOnlyList<MaterialShaderType> ShaderTypes { get; }
    public IReadOnlyList<Material> GetMaterialsByShaderType(MaterialShaderType type);
    public uint MaterialCount { get; }
}
```

---

## 4. 数据字典

### 4.1 数据流（完整链路）

```
                    ┌──────────────────┐
                    │ MaterialRegistry │
                    │  ShaderType[]    │
                    │  Material[]      │
                    └────────┬─────────┘
                             │ MaterialID
                             ▼
┌─────────┐     ┌──────────────────┐     ┌─────────────────┐
│ ECS     │────→│ InstanceSyncSystem│────→│ GpuInstanceHeader│
│MeshInst │     │  (每帧同步)       │     │  .MaterialID    │
└─────────┘     └──────────────────┘     └────────┬────────┘
                                                   │
                    ┌──── GPU Pipeline ────────────┤
                    ▼                              ▼
            ┌─────────────┐                ┌──────────────┐
            │ Shade Binning│                │ Material Shade│
            │ (Count/       │                │ 按 ShaderType │
            │  Reserve/     │                │ 分组 Dispatch  │
            │  Scatter)     │                │ 切换 SRB      │
            └─────────────┘                └──────────────┘
```

### 4.2 SRB 绑定内容（方案 A：StandardPBR 示例）

| 绑定名 | 类型 | 来源 | 生命周期 |
|--------|------|------|---------|
| `VisBuffer` | Texture2D\<uint\> | RenderGraph | 每帧 |
| `VisibleClusters` | StructuredBuffer | RenderGraph | 每帧 |
| `PageHeap` | ByteAddressBuffer | RenderGraph | 每帧 |
| `Instances` | StructuredBuffer | RenderGraph | 每帧 |
| `PixelCoordBuffer` | StructuredBuffer | RenderGraph | 每帧 |
| `BinOffsets` | StructuredBuffer | RenderGraph | 每帧 |
| `Uniforms` | ConstantBuffer | RenderGraph | 每帧 |
| `OutputColor` | RWTexture2D | RenderGraph | 每帧 |
| `AlbedoMap` | Texture2D | Material 参数 | 材质生命周期 |
| `NormalMap` | Texture2D | Material 参数 | 材质生命周期 |
| `ARMMap` | Texture2D | Material 参数 | 材质生命周期 |
| `MaterialSampler` | SamplerState | Material 参数 | 材质生命周期 |

> [!IMPORTANT]
> 前 8 个资源 (管线资源) 每个 Material 的 SRB 中都需要绑定，这与 Diligent 的 SRB 模型一致：一个 SRB 必须包含 PSO 所需的全部资源。所以 `ClusterMaterialShadePass` 需要在 Execute 时为每个 Material 的 SRB 重新绑定这些管线资源。

---

## 5. Shade Dispatch 编排改造

### 5.1 当前 `ClusterMaterialShadePass` 的循环

```csharp
// 现有：单 PSO，遍历所有 MaterialID
for (uint matID = 0; matID < ActiveMaterialCount; matID++)
{
    uniformData.MaterialID = matID;
    // Map/Unmap uniforms...
    ctx.CommitShaderResources(srb, ...);
    ctx.DispatchComputeIndirect(binIndirectArgs, matID * 12);
}
```

### 5.2 改造后的编排

```csharp
// 改造后：按 ShaderType 分组，类型内遍历 Material 切 SRB
foreach (var shaderType in registry.ShaderTypes)
{
    ctx.SetPipelineState(shaderType.PSO);
    
    foreach (var material in registry.GetMaterialsByShaderType(shaderType))
    {
        // 绑定管线资源（每帧变化的 RenderGraph 资源）
        BindPipelineResources(material.SRB, visBufferSRV, visibleClusters,
                              pageHeap, instances, pixelCoordBuffer,
                              binOffsets, uniformBuf, outputColorUAV);
        
        // 更新 Uniforms 中的 MaterialID
        uniformData.MaterialID = material.MaterialID;
        MapUpdateUniforms(uniformBuf, uniformData);
        
        ctx.CommitShaderResources(material.SRB, ...);
        ctx.DispatchComputeIndirect(binIndirectArgs, material.MaterialID * 12);
    }
}
```

> [!NOTE]
> **MVP 阶段只有 1 个 ShaderType + 1 个 Material**，外层循环只执行一次。但结构已为多材质就绪。

---

## 6. Shader 侧改动（MVP 最小集）

### 6.1 `cluster_shade_material.slang` 改造

**当前：** 硬编码面法线着色，无属性插值。

**改造为：**

```slang
// 利用 cluster_common.slang 中已有的 SoA Fetch 工具
void CSMaterialShade(uint3 tid : SV_DispatchThreadID)
{
    // ... 现有的 Bin 查找和 VisBuffer 解码 ...
    
    // 使用 StreamCursor 读取属性
    uint4 header1 = PageHeap.Load4(pageOffset + 16);
    uint attrOffset = header1.z;  // AttributesOffset
    
    StreamCursor cursor = { pageOffset + attrOffset, totalVertexCount };
    uint normalBase = cursor.advance(4);
    uint tangentBase = cursor.advance(4);
    uint uv0Base = cursor.advance(4);
    
    // 插值法线
    float3 n0 = FetchNormal(PageHeap, normalBase, vStart, vi0);
    float3 n1 = FetchNormal(PageHeap, normalBase, vStart, vi1);
    float3 n2 = FetchNormal(PageHeap, normalBase, vStart, vi2);
    float3 interpNormal = normalize(bary.x * n0 + bary.y * n1 + bary.z * n2);
    
    // 插值 UV
    float2 uv0_0 = FetchUV(PageHeap, uv0Base, vStart, vi0);
    float2 uv0_1 = FetchUV(PageHeap, uv0Base, vStart, vi1);
    float2 uv0_2 = FetchUV(PageHeap, uv0Base, vStart, vi2);
    float2 uv = bary.x * uv0_0 + bary.y * uv0_1 + bary.z * uv0_2;
    
    // MVP: 采样 albedo 纹理 + 方向光
    float4 albedo = AlbedoMap.Sample(MaterialSampler, uv);
    float NdotL = saturate(dot(worldNormal, Uniforms.LightDir));
    float3 color = albedo.rgb * (NdotL * Uniforms.LightIntensity + Uniforms.AmbientColor);
    
    OutputColor[pixelCoord] = float4(color, 1.0);
}
```

### 6.2 Shader 新增资源绑定

```slang
// 新增：材质纹理（SRB 切换提供）
Texture2D AlbedoMap;
Texture2D NormalMap;
Texture2D ARMMap;
SamplerState MaterialSampler;
```

### 6.3 `cluster_shade_binning.slang` 清理

删除内联的 `DrawRequest` 定义，改为 `#include "cluster_common.slang"`。

---

## 7. MaterialID 分配策略

### 7.1 Bin Key 含义

设计文档方案 A 规定：`Bin Key = (ShaderType, MaterialInstanceID)`。

但当前 Shade Binning shader 的 Bin Key 是裸 `MaterialID`。这意味着：

**每个 Material 有一个唯一的 MaterialID = 一个 Bin**。

Shade 阶段的 Dispatch 按 MaterialID 索引 `BinIndirectArgs`。所以：

```
MaterialRegistry 分配 MaterialID = 顺序递增的 uint
BinCounts[materialID] ← CSBinCount 按 MaterialID 原子累加
BinIndirectArgs[materialID] ← CSBinReserve 写入 DispatchArgs
CSMaterialShade 按 MaterialID Dispatch → 使用对应的 SRB
```

### 7.2 MAX_MATERIALS 限制

Binning shader 的 `sPrefix` 共享数组限制为 `MAX_MATERIALS = 256`。

**MVP 阶段足够。** 如果未来需要更多，可将 prefix sum 改为多 pass 或使用 Bindless（方案 B）。

---

## 8. 代码变更清单

| 模块 | 文件 | 变更 |
|------|------|------|
| `SomeEngine.Render` | **[NEW]** `Systems/MaterialRegistry.cs` | MaterialShaderType + Material + MaterialRegistry 类 |
| `SomeEngine.Render` | **[MODIFY]** `ClusterMaterialShadePass.cs` | 接收 MaterialRegistry，按 ShaderType 分组 Dispatch |
| `SomeEngine.Render` | **[MODIFY]** `ClusterRenderFeature.cs` | 构造时接收 MaterialRegistry，AddShadingPasses 使用 registry |
| `SomeEngine.Render` | **[MODIFY]** `ClusterShadeBinningPass.cs` | activeMaterialCount 改从 registry 读取 |
| Shaders | **[MODIFY]** `cluster_shade_material.slang` | 添加属性插值 + 纹理采样 + 材质资源绑定 |
| Shaders | **[MODIFY]** `cluster_shade_binning.slang` | `#include "cluster_common.slang"` 消除 DrawRequest 重复 |
| App 层 | **[MODIFY]** 初始化代码 | 创建 MaterialRegistry，注册默认 PBR 材质 |

---

## 9. 渐进式实施阶段

### Phase 1：最小可见（~0.5 天）
- 创建 `MaterialRegistry` + `Material` + `MaterialShaderType` 骨架
- 注册 1 个默认 StandardPBR（无纹理，纯色参数）
- shade shader 添加重心坐标插值 + 法线插值着色

### Phase 2：纹理采样（~1 天）
- Material 实现 `SetTexture` / SRB 绑定
- shade shader 添加 albedo 纹理采样
- 引入测试纹理验证 UV 正确性

### Phase 3：多材质（~0.5 天）
- 应用层创建多个 Material，分配不同 MaterialID
- 验证 Shade Binning 按 MaterialID 正确分桶
- 验证 SRB 切换编排正确

---

## 10. 设计决策记录

| 决策 | 理由 |
|------|------|
| MaterialID 由引擎分配，不由用户指定 | 避免冲突，与 Binning 数组索引对齐 |
| 每个 Material 持有独立 SRB | 方案 A 最简路径；Diligent SRB 包含完整资源集，不支持部分绑定 |
| 管线资源每帧重新绑定到每个 SRB | Diligent Dynamic 变量允许每帧更新 Set；SRB 切换时资源引用可能失效 |
| MVP 阶段不拆分 PixelContext | 直接在 shade shader 中内联属性读取，Step 4 泛型化时再提取 |
| MaterialRegistry 放在 Render 层 | 材质是渲染概念，不属于 Core/ECS 层 |
