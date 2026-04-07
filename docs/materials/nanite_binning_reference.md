# Nanite Material Range 与 Raster Binning 机制分析

## 1. Cluster 的材质 Range 编码

每个 Cluster 的材质信息编码为 32 位，有 **Fast Path** 和 **Slow Path** 两条路径。

### Fast Path（≤3 种材质）

直接内联在 cluster 数据中，**无需查表**：

```
// Material Packed Range - Fast Path (32 bits)
// Material0Index  : 6  // (0 : Material0Length)
// Material1Index  : 6  // (Material0Length : Material1Length)
// Material2Index  : 6  // (剩余三角形)
// Material0Length : 7  // num minus one
// Material1Length : 7  // 最多 64 个三角形
```

判断方式：`Material0Length > 0` 即为 fast path（`IsMaterialFastPath`）。

三角形按连续排列，前 `Material0Length` 个属于 Material0，接下来 `Material1Length` 个属于 Material1，剩余属于 Material2。

### Slow Path（>3 种材质）

```
// Material Packed Range - Slow Path (32 bits)
// BufferIndex     : 19  // 材质表在页面中的偏移
// BufferLength    :  6  // range 条目数 (num-1)
// Padding         :  7  // 固定为 127，用于区分 fast/slow
```

每个 range 条目为 32 位：

```
// TriStart        :  8  // 起始三角形
// TriLength       :  8  // 三角形数量
// MaterialIndex   :  6  // 材质索引 (最多64种)
// Padding         : 10
```

判断 fast/slow path：`MaterialEncoding < 0xFE000000` 为 fast path（slow path 高 7 位是 127 = `0xFE`）。

---

## 2. Cluster 材质查表方式

### 单个三角形查材质：`GetRelativeMaterialIndex`

给定三角形索引，返回其 **相对材质索引**：

- **Fast Path**：直接比较 `TriIndex` 与 `Material0Length` / `Material1Length` 边界
- **Slow Path**：遍历材质表，逐条目检查 `[TriStart, TriStart + TriLength)` 是否包含该三角形

### 相对材质索引 → Raster Bin

```
RelativeMaterialIndex + PrimitiveId + MeshPassIndex
  → LoadMaterialSlot() 从 PrimitiveMaterialData buffer 加载 FNaniteMaterialSlot
  → 返回 MaterialSlot.RasterBin (或 FallbackRasterBin)
```

`FNaniteMaterialSlot` 结构：

| 字段 | 说明 |
|------|------|
| `TriangleShadingBin` | 三角形 shading bin |
| `VoxelShadingBin` | 体素 shading bin |
| `RasterBin` | 光栅化 bin |
| `FallbackRasterBin` | 降级光栅化 bin |

### Raster Bin 重映射：`RemapRasterBin`

最终 bin 可能被 remap 为 **fixed function bin**（简单材质 / voxel / 禁用可编程光栅时）：

```
FixedFunctionBin = NANITE_FIXED_FUNCTION_BIN
                 | TwoSided | SplineMesh | Skinned | CastShadow | Voxel
```

---

## 3. 光栅化 Binning 粒度

**Bin 的粒度是 Material Range 级别（per material range per cluster），而非整个 cluster 也非单个三角形。**

### `RasterBinBuild` 核心逻辑

每个线程处理一个 visible cluster：

#### Fast Path（≤3 材质）

```
对 3 个 material range 分别查 RasterBin：
  RasterBin0 = GetRemappedRasterBinFromIndex(Material0Index, ...)
  RasterBin1 = GetRemappedRasterBinFromIndex(Material1Index, ...)
  RasterBin2 = GetRemappedRasterBinFromIndex(Material2Index, ...)

如果相邻 range 有相同 RasterBin → 合并 range

对每个非空 range 调用 ExportRasterBin(RasterBin, ClusterIndex, RangeStart, RangeEnd, ...)
```

#### Slow Path（>3 材质）

```
遍历材质表的每个条目：
  DecodeMaterialRange(EncodedRange) → (TriStart, TriLength, MaterialIndex)
  RasterBin = GetRemappedRasterBinFromIndex(MaterialIndex, ...)

  如果与当前 run 的 RasterBin 相同 → 合并（扩展 range）
  否则 → flush 当前 run，开始新 run

最后 flush 剩余 run → ExportRasterBin(...)
```

### `ExportRasterBin` 写出数据

每个 bin 条目写入 `OutRasterBinData`：

```hlsl
OutRasterBinData[offset].x = ClusterIndex;                   // 所属 cluster
OutRasterBinData[offset].y = (RangeStart << 16) | RangeEnd;  // 三角形范围
```

一个 cluster 可能在 **多个 bin** 中出现（多种材质），每个 bin 条目记录该 cluster 中属于该 bin 的三角形范围 `[RangeStart, RangeEnd)`。

### SW/HW 分流

在 `ExportRasterBin` 中，基于材质标志决定走 SW 还是 HW 光栅化：

| 条件 | 路径 |
|------|------|
| `bPixelProgrammable` 且 wave<32 | 强制 HW |
| `!bNoDerivativeOps`（需要导数） | 强制 HW |
| `bDisplacement`（位移） | 强制 SW + batching |
| `bVoxel` | 强制 SW，无 batching |

SW 和 HW 计数分开存储在同一个 bin 内（`BinSWCount` / `BinHWCount`），最终由 `RasterBinFinalize` 生成 indirect dispatch 参数。

---

## 4. 关键结论

1. **材质信息存储在 cluster 级别**，每个 cluster 内的三角形按材质连续排列形成 material range
2. **查表方式**：`RelativeMaterialIndex` → `PrimitiveMaterialData` → `MaterialSlot`（含 RasterBin、ShadingBin）
3. **Binning 粒度是 material range**：一个 cluster 可以拆成多个 bin 条目（每种材质一个），但不是单三角形级别
4. **相同 RasterBin 的相邻 range 会被合并**，减少原子操作和元数据开销
5. **每个 bin 条目 = (ClusterIndex, TriangleRange)**，光栅化时只处理该 cluster 中指定范围的三角形
