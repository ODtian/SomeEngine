# 光栅化管线设计文档

> 合并自 `raster.md`、`raster_impl.md`、`plan/hw_sw_raster_merge.md`。
>
> **SW 光栅 + HW/SW 融合：✅ 已实施**

---

## 1. 管线架构

### 双路径光栅化

Cull 阶段按屏幕面积分流（`SW_THRESHOLD`），小三角形走 SW，大三角形走 HW：

```
Cull (SW/HW split) → RasterBin (BinSWCount/BinHWCount)
  → SW Raster CS (DispatchCompute per-VRB-batch, 写 VisBuffer + DepthUAV)
  → DepthMerge (SW DepthUAV → HW depth target)
  → HW Raster (DrawIndirect, 写同一 VisBuffer + HW depth)
  → HiZ Build → Phase 2 → Resolve
```

### VisBuffer + 原子深度

- SW raster：`InterlockedMax` 写 `R32_UINT` DepthUAV
- HW raster：传统 depth test + DepthStencil
- `DepthMergePass`：全屏三角形读 DepthUAV → 输出 `SV_Depth` 注入 HW depth target
- 两者共享同一 `R32_UINT` VisBuffer

---

## 2. SW 光栅核心

### 架构：32 线程 + VRB + WaveQueue

```
1 Wave = 1 VRB Batch (32 threads)
  Stage 1: DeduplicateVertIndexes (256-bit bitmask, ≤32 unique verts)
  Stage 2: Vertex Transform (寄存器, 零 LDS)
  Stage 3: Triangle Setup (WaveReadLaneAt 拉取顶点)
  Stage 4: WaveQueue 像素分发 + 原子深度写入
```

### VRB Build-time

- `ClusterBuilder.cs`：导入时对三角形排序，保留 meshopt 顶点顺序
- 贪心桶分组，每 batch ≤ 32 unique vertices（fast path）

### Binning 4-Pass

```
Init → Count (per-VRB-batch entries, InterlockedAdd BinSWCount/BinHWCount)
→ Reserve (前缀和 → 每 bin offset) → Scatter (写入 BinnedClusterIndexBuffer)
```

---

## 3. 可编程光栅化

### IVertexEvaluate 泛型系统（✅ 已实施）

```slang
interface IVertexEvaluate {
    void evaluate(inout VertexData data, ...);
}
struct StaticVertexEval : IVertexEvaluate { ... }
struct WaveVertexEval : IVertexEvaluate { ... }   // 波浪变形
```

- Shade pipeline 通过 `CSShade<TVE : IVertexEvaluate, TMat : ISurfaceEvaluate>` 泛型化
- `ClusterDeformBinPass`（4-pass）按 vertex eval 类型分 bin

### DeformCache 架构

在剔除后、光栅化前插入 `ClusterDeformPass`，将可变形顶点写入 `DeformCache`：

| 数据 | 压缩策略 | 大小 |
|------|---------|------|
| Position | Cluster AABB + uint16×3 量化 | 6B/v |
| Normal+Tangent | Octahedron SNORM16 + TangentSign | 6B/v |

---

## 4. 实施路线图

| 阶段 | 状态 | 内容 |
|------|------|------|
| Phase 1: 保守 Bounds | ✅ | 可变形 Instance 的 AABB 扩展 |
| Phase 2: DeformCache | ✅ | 原子分配 + 压缩编码 |
| Phase 3: DeformCache + HW 接入 | ✅ | 骨骼蒙皮（shader 泛型） |
| Phase 3.5: SW 光栅核心 | ✅ | VRB + WaveQueue + Binning |
| Phase 4: Bin 系统 + SW/HW 分流 | ✅ | BinSWCount/BinHWCount + DepthMerge |
| Phase 5: WPO DeformCache | 📋 | Per-material WPO 变体 |
| Phase 6: Tessellation | 📋 | Split→Dice 流式软光栅（见 `future/tessellation.md`） |
