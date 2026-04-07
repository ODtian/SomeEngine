# Compute Shade Pipeline 设计文档

> **状态：✅ 已实施并接入 Entity-based material 路线**

---

## 1. 概要

VisBuffer Resolve + Compute Material Shading，参考 UE5 Nanite Compute Shade Binning。

当前管线：

`VisBuffer → ShadeBin (Count/Reserve/Scatter) → ShadePSOGroup 分组 → Per-Bin MaterialShade Dispatch → Color`

---

## 2. Shade Binning（3-Pass Counting Sort）

### Pass 1: Count
- 全屏 CS，每线程读 VisBuffer 像素
- 查询 `MaterialSlotBuffer[offset + localMaterialIndex].ShadingBin`
- 对每个 Bin `InterlockedAdd` 累计像素数 → `BinCounts[]`

### Pass 2: Reserve
- 对 `BinCounts[]` 做前缀和
- 计算每 Bin 在全局输出数组中的起始偏移 → `BinOffsets[]`

### Pass 3: Scatter
- 再次全屏 CS，每像素按 Bin ID + `BinOffsets[]` 写入像素坐标
- 输出：紧凑的 `PixelCoordBuffer[]`，按 Bin 分段排列

### 实现文件

| 文件 | 职责 |
|------|------|
| `ClusterShadeBinningPass.cs` | ShadeBinningResources + Count/Reserve/Scatter 三个 pass 类 |
| `ClusterShade.cs` | 静态编排器，组合 ShadeBin + MaterialShade |
| `cluster_shade_binning.slang` | GPU shader |

---

## 3. Material Shade Dispatch

对每个 Shade Bin dispatch 一次 CS：

1. 读 `PixelCoordBuffer` → `(x, y)`
2. 读 VisBuffer → `VisibleClusterIndex + TriangleID`
3. 反查 PageHeap 顶点，计算重心坐标
4. 插值 UV/Normal/Tangent
5. 材质求值（PBR: BaseColor/Normal/Metallic/Roughness）
6. 写入 `OutputColor` UAV

### Dual-Signature 绑定

- **Sig0**：每个 PSO group 共用的全局资源（VisBuffer / VisibleClusters / PageHeap / OutputColor 等）
- **Sig1**：由 `MaterialRef.Owner.Params` + shader metadata / entry-point variant 推导出的材质资源，按 cache key 缓存在 `ClusterShade` 静态字典中
- `ClusterMaterialShadePass` 执行阶段按 group 绑定 Sig0，再按 bin 提交 Sig1 SRB

### 泛型着色管线

```slang
void CSShade<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate>(
    uniform TVE vertexEval, uniform TMaterial material, uint3 tid)
```

- `IVertexEvaluate`：顶点变形（Static/Wave/Skinned）
- `ISurfaceEvaluate`：材质求值（StandardPBR/Unlit/Custom）

### 实现文件

| 文件 | 职责 |
|------|------|
| `ClusterMaterialShadePass.cs` | Per-bin dispatch |
| `ClusterShade.cs` | Sig0/Sig1、PSO 分组构建、pass 编排 |
| `ShadePSOGroup.cs` | 纯 CPU break-on-change 分组逻辑 |
| `cluster_shade_material.slang` | 标准 PBR 着色 |
| `cluster_shade_unlit.slang` | Unlit 着色 |
| `cluster_shade_pipeline.slang` | 泛型着色管线共享代码 |
| `vertex_evaluate.slang` | IVertexEvaluate 接口 + 实现 |

---

## 4. 当前状态

- [x] ShadeBin 3-pass（Count / Reserve / Scatter）
- [x] `ClusterShade` 静态编排器替代原 `ClusterShadeBinStage` / `ClusterShadeStage`
- [x] `ShadePSOGroup.ComputeShaderGroups()` 已提取，按 shader break-on-change 进行分组
- [x] Sig0 / Sig1 双签名绑定已接入实际管线
- [x] `ClusterShadeSig1Tests` 已覆盖 Sig1 cache key、resource layout 与 cache reuse
- [ ] 1x1 vs 2x2 Quad 双模式（当前统一 1x1）
- [ ] 更多 `ISurfaceEvaluate` 实现（SSS、Refraction 等）
