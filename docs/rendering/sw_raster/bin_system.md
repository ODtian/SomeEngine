# Bin 系统设计

## 1. RasterBin / ShadeBin 解耦

### 当前问题

当前 `_rasterBinFieldIndex` 和 `_shadingBinFieldIndex` 共用同一个 `BinSpace`实例和同一个 `SlotBuffer`。这是设计错误——修改 shade 材质不应影响 raster binning，反之亦然。

### 修正方案

两个独立的 `BinSpace`：

```csharp
private readonly BinSpace _rasterBinSpace = new();  // 光栅阶段
private readonly BinSpace _shadeBinSpace  = new();   // 着色阶段
```

各自有独立的 `SlotBuffer`、`AddUploadPass`、`RebuildIfDirty`。GPU 侧两个独立的 `MaterialSlotBuffer`。

## 2. RasterBinSpace 的多 Field 设计

Raster BinSpace 内部有多个语义 Field：

| Field | 语义 | 分组依据 |
|-------|------|---------|
| `VertexEvalField` | 顶点变换类型 | `IVertexEvaluate` 变体（skinned-A, WPO-B, static, ...） |
| `RasterTypeField` | 光栅行为 | Opaque, Masked, TwoSided, PDO |
| `DomainEvalField` | Tess domain 类型 | `IDomainEvaluate` 变体（仅 Tess bin） |

每个 Field 独立注册 Region 和 signature，组合成复合 Bin key。

### 复合 Bin Key

```
RasterBinKey = (VertexEvalVariant, RasterType)
```

> Stride 不进 key——它是 VertexEvalVariant 的隐含属性，冗余。

示例：
```
Bin 0: (Static,     Opaque)      → 跳过 PreDeform, 纯几何光栅
Bin 1: (Skinned-A,  Opaque)      → PreDeform(skin), 纯几何光栅
Bin 2: (WPO-MatA,   Opaque)      → PreDeform(WPO-A), 纯几何光栅
Bin 3: (WPO-MatA,   Masked)      → PreDeform(WPO-A), Masked 光栅(采样纹理)
Bin 4: (Static,     Masked)      → 跳过 PreDeform, Masked 光栅
Bin 5: (Static,     Tess+Disp-B) → 跳过 PreDeform, Tess 光栅(材质B)
```

### 关键约束

- **骨骼蒙皮不需要 per-material**：统一 Shader，参数化 BoneMatrices
- **WPO 和 Displacement 需要 per-material**：不同材质图函数
- Bin 数量 = VertexEval 变体数 × RasterType 数量，通常 < 20

## 3. ShadeBinSpace 设计

与光栅完全独立：

| Field | 语义 |
|-------|------|
| `ShadingField` | 按材质 shader signature 分组 |

### 解耦收益

| | 耦合 | 解耦 |
|--|------|------|
| Raster 变更影响 Shade | ❌ 会 | ✅ 不会 |
| Shade 变更影响 Raster | ❌ 会 | ✅ 不会 |
| 独立 rebuild | ❌ 不能 | ✅ 可以 |
| SlotBuffer 大小 | 两者之和 | 各自最优 |

## 4. SW/HW 分流策略

在 RasterBinning CS 中，对每个 Cluster 估算屏幕面积后决定走 SW 或 HW：

```slang
float screenArea = EstimateClusterScreenArea(cluster, view);
bool useSW = (screenArea < SW_THRESHOLD_PIXELS)
          || (binMeta.MaterialFlags & RASTER_FLAG_MASKED)
          || (binMeta.MaterialFlags & RASTER_FLAG_PDO)
          || (binMeta.MaterialFlags & RASTER_FLAG_TESS);
```

- SW Cluster 写入 BinnedClusterBuffer **从头部开始**
- HW Cluster 写入 BinnedClusterBuffer **从尾部开始**
- 与 Nanite 一致的双端写入策略

### RasterBinMeta

```slang
struct RasterBinMeta
{
    uint ClusterOffset;     // BinnedClusterBuffer 起始偏移
    uint BinSWCount;        // SW 路径 Cluster 数
    uint BinHWCount;        // HW 路径 Cluster 数
    uint MaterialFlags;     // Masked / TwoSided / PDO / Tess 标志
};
```

## 5. 管线编排（Phase 4 完成后）

```
Cull → RasterBinning CS →
  for each Bin:
    PreDeform CS (if VertexEval != Static)
    → UAV Barrier
    → SW Raster CS (dispatch BinSWCount groups)
    → HW Draw (DrawIndirect BinHWCount instances)
  → (Depth Merge, if Phase 3.5 route)
  → HiZ Build → Phase 2 Cull → ...
  → ShadeBinning CS → per-Material Shade Dispatch → Output
```

## 6. 与 Vertex+Raster Bin 合并的原理

Pre-Deform Bin 和 Raster Bin 可以合并为一次 Binning：
- 一个 Cluster 的 PreDeform 类型和 Raster 类型在 Cull 阶段就完全确定
- 组合成复合 Bin Key，一次 Binning 同时决定两条路径
- 执行时串行：先 Dispatch PreDeform CS → Barrier → Dispatch Rasterize
