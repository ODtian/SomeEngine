# Cluster Rendering Pipeline 设计文档

> 合并自 `plan_cluster_rendering.md`、`instance_plan.md`、`cluster_bvh_culling.md`。
>
> **核心管线状态：✅ 已实施**

---

## 1. 架构总览

GPU-Driven Cluster Rendering，核心流程：

```
Upload Globals → BVH Traverse → 2-Phase HiZ Cull → DeformBin → RasterBin → SW/HW Draw → DepthMerge → HiZ Build → ShadeBin → MaterialShade
```

### 三层架构

| 层级 | 实例 | 职责 |
|------|------|------|
| **RenderPass** | `ClusterCullPass`, `ClusterSWRasterPass` 等 | GPU 命令，barrier 由 RG 管理 |
| **Stage（静态函数）** | `ClusterCull.AddPasses`, `ClusterSWDraw.AddPasses` 等 | 编排 Pass + 创建 RG 资源 |
| **Feature / Pipeline** | `ClusterPipeline` | 组合 Stage，管理跨帧资源 |

### 已实现的 9 个 Stage

| Stage 类 | 职责 |
|---------|------|
| `ClusterUploadStage` | 上传实例变换、Uniform buffer |
| `ClusterTraverseStage` | BVH 遍历，输出 Candidates |
| `ClusterCullStage` | 视锥+遮挡剔除，SW/HW 分流 |
| `ClusterDeformBinStage` | 可变形 Cluster 的 Vertex Eval Binning |
| `ClusterRasterBinStage` | 光栅化 Binning（4-pass: Init/Count/Reserve/Scatter） |
| `ClusterDrawStage` | HW 光栅化（VS/PS，DrawIndirect） |
| `ClusterSWDrawStage` | SW 光栅化（CS，VRB+WaveQueue） |
| `ClusterHiZStage` | 2-Phase HiZ 全流程编排 |
| `ClusterShade` | 着色 Binning (Count/Reserve/Scatter) + 材质着色 Dispatch，静态编排器 |

---

## 2. 数据结构

### GPU Page Layout

```
[ Page Header ] → ClusterCount, 各 Stream 字节偏移
[ Cluster List ] → GPUCluster (64B): IntBase/PackedCenterXY/LODCenter/LODRadius/PackedCenterZRadius/LODErrorHalf/VertexStart/TriangleStart/GroupId/PackedCounts/PackedMaterials/PackedRanges/MaterialTableOffset/VRBBatchInfo
[ Position Stream ] → uint16×3 量化
[ Attribute Stream ] → OctNormal + UV
[ Index Stream ] → u8/u16
```

### 实例数据（✅ 已实施）

| Buffer | 内容 |
|--------|------|
| `StructuredBuffer<GpuTransform>` | QVVS 变换矩阵（per-instance） |
| `StructuredBuffer<GpuInstanceHeader>` | BVHRootIndex + MaterialSlotOffset + MetadataOffset + MetadataCount + BoundsExpansion |

### VisibleClusters 双端布局

```
← swCount →              ← hwCount →
[ SW₀ SW₁ ... SWₙ |  ... gap ...  | HWₘ ... HW₁ HW₀ ]
```

---

## 3. BVH 遍历与剔除

- **BVH Traverse**：当前实现为队列驱动（双缓冲 queueA/queueB 交替 dispatch），计划支持 Persistent Thread 模型作为双架构
- **LOD 选择**：`Error_self ≤ Threshold && Error_parent > Threshold`
- **Page Fault 反馈**：`ChildPointer == 0xFFFFFFFF` 时通过 `InterlockedAdd` 写入 `PageFaultBuffer`
- **Frustum Culling**：在 BVH 节点和 Cluster 两级执行
- **2-Phase HiZ Occlusion Culling**：
  - Phase 1：用上帧 HiZ 保守剔除
  - Phase 1 后 HiZ Build
  - Phase 2：用当前帧 HiZ 补测

---

## 4. 待完成

- [ ] Instance-level sparse grid 空间索引（加速实例级剔除）
- [ ] 运行时 Page 流式加载管理（`ClusterStreamer` 框架已有，Page 管理待完善）
- [ ] Phase 3-4 of `instance_plan.md`：流式加载 + 泛型 Metadata 属性解耦
