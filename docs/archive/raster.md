# 预计算变形 Cluster 可行性与管线架构计划

## 1. 核心思路

在剔除结束后、光栅化前，插入 **Pre-Deform CS** 阶段，将存活的可变形 Cluster 顶点一次性变换完毕。后续光栅化和 Resolve 直接读取结果。

```
Conservative Cull (扩展 Bounds)
    ↓ 可见 Cluster 列表 + 原子分配 Offset
Pre-Deform CS  →  TransientDeformedBuffer
    ↓ UAV Barrier
Rasterize (读 DeformedBuffer)
    ↓
Geometry Resolve (读 DeformedBuffer)
```

---

## 2. Cluster 级顶点压缩

利用 Cluster 内顶点的空间局部性，显著压缩 Pre-Deform 输出：

| 数据 | 压缩策略 | 大小 |
|------|---------|------|
| **Position** | Cluster AABB + uint16×3 量化（Pre-Deform CS 用 WaveActiveMin/Max 求 AABB） | **6B/v** |
| **Normal+Tangent** | Octahedron SNORM16 + TangentSign | **6B/v** |
| **UV** | 不蒙皮，直接从 PageHeap 读 | **0B** |

> [!TIP]
> Rasterize 只需 Position（6B），Normal/Tangent（6B）仅 Resolve 阶段读取。可分两个 sub-buffer 优化带宽。
> 总计 **12B/vertex**，对比 Full FP32 的 40B/vertex，压缩比 **3.3×**。

### 占用估算

| 场景 | 蒙皮 Cluster 数 | 压缩后 |
|------|----------------|--------|
| 中等 | 2,000 | **3 MB** |
| 密集 | 10,000 | **15 MB** |
| 极限 | 30,000 | **46 MB** |

---

## 3. 两次 Binning 架构

### 3.1 为什么是 2 次而非 3 次

管线中有 3 个需要材质特定代码的阶段：Pre-Deform、Rasterize、Material Shade。但 **Pre-Deform Bin 和 Raster Bin 可以合并为一次 Binning**：

- 一个 Cluster 的 Pre-Deform 类型（蒙皮/WPO/无）和 Raster 类型（不透明/Masked/Tess+Disp）在 Cull 阶段就完全确定
- 组合成一个**复合 Bin Key**，一次 Binning 同时决定两条路径
- 执行时串行：先 Dispatch Pre-Deform CS → Barrier → Dispatch Rasterize

### 3.2 Bin 1: Vertex+Raster Bin（per-Cluster）

```
Bin V0: 静态不透明        → 跳过 PreDeform, 纯几何光栅
Bin V1: 蒙皮不透明        → PreDeform(skin), 纯几何光栅
Bin V2: WPO-材质A          → PreDeform(WPO-A), 纯几何光栅
Bin V3: WPO-材质A+Masked   → PreDeform(WPO-A), Masked 光栅(材质A)
Bin V4: Tess+Disp-材质B    → 无 PreDeform, Tess 光栅(材质B)¹
Bin V5: 蒙皮+Tess-材质B    → PreDeform(skin), Tess 光栅(材质B)¹
Bin V6: 静态 Masked        → 跳过 PreDeform, Masked 光栅
...
```

> ¹ Tessellation 不走 Pre-Deform 预计算（见 §5）。

**关键：骨骼蒙皮不需要 per-material**（统一 Shader，参数化 BoneMatrices）。WPO 和 Displacement 需要 per-material（不同材质图函数）。

### 3.3 Bin 2: Shade Bin（per-Pixel）

```
VisBuffer → 像素级材质 ID 分类 → per-material Dispatch → 着色
```

与标准 Deferred Shade Binning 相同，不展开。

### 3.4 Vertex Bin 与 Fragment Bin 的解耦收益

Pre-Deform 将"顶点变形"从光栅化中剥离后：

| | 无 Pre-Deform | 有 Pre-Deform |
|--|--------------|---------------|
| 光栅化 Shader 变体 | 蒙皮×WPO×Masked×PDO×TwoSided | Masked×PDO×TwoSided（+Tess+Disp） |
| 变体爆炸 | ❌ 严重 | ✅ 大幅削减 |

---

## 4. 可编程光栅化模式总览

Pre-Deform 后，光栅化阶段残留的"可编程"能力：

| 模式 | 影响 | Pre-Deform 可吸收？ | 留给光栅化？ |
|------|------|---------------------|-------------|
| **Skinning** | 顶点位置+法线 | ✅ | ❌ |
| **WPO** | 顶点世界位置 | ✅ | ❌ |
| **Displacement** | 微顶点位置（Tess） | ❌ 拓扑不同 | ✅ Tess 路径 |
| **Alpha Test** | 像素 discard | ❌ | ✅ |
| **PDO** | 像素深度偏移 | ❌ | ✅ |
| **Two-Sided** | 背面剔除开关 | ❌ 不涉及 | ✅ |
| **Customized UVs** | UV 变换 | ⚠️ 可选 | ✅ |

---

## 5. Tessellation 不纳入 Pre-Deform

### 5.1 原因

- **数量爆炸**：128 三角形 × TessFactor 8² ≈ 8000+ 微三角形/Cluster，同屏数千 Tess Cluster → GB 级写入
- **拓扑不兼容**：微三角形不再是固定 128 tri/cluster 结构，无法复用 VisBuffer 编码和 PageHeap 读取
- **Nanite 的做法**：Split→Dice 阶段边算边光栅，不存中间结果

### 5.2 Nanite Tessellation 架构（基于源码）

```
Cluster Triangles
    ↓ 每三角形估算 TessFactor
Split 阶段 (NaniteSplit.usf)
    ├── 层级化 Patch 细分（查表法，保证水密）
    ├── 保守 Displacement Bounds 剔除（Nießner/Loop 2021 Cone Bounds）
    └── 递归直到 TessFactor ≤ TableSize
    ↓ VisiblePatches
Dice 阶段 (NaniteDice.ush)
    ├── 查表获取微三角形拓扑
    ├── 每微顶点 EvaluateDomain() → Displacement + Alpha Test
    ├── LDS 缓存角顶点 ClipPos/NormalClip
    └── 直接软光栅化微三角形 → VisBuffer
```

### 5.3 WPO 与 Displacement 的分工

来自 `NaniteRasterizer.usf` L854 和 `NaniteRasterizationCommon.ush` L496：

| 对象 | 执行内容 | 代码位置 |
|------|---------|---------|
| **角顶点**（3个/三角形） | Skinning + **WPO** | `FetchTransformedNaniteVertex(bEvaluateWPO)` |
| **微顶点**（细分点） | 插值角顶点 + **Displacement** + Alpha Test | `EvaluateDomain()` → `GetMaterialDisplacement()` |

WPO 和 Displacement 来自**同一材质图的不同输出引脚**，编译为同一 Shader 变体中的不同函数。一个"Tess+WPO+Masked"材质只需**一个 RasterBin**。

### 5.4 RasterBin 驱动 Tessellation

每个 RasterBin 通过 Root Constant 指定，对应一个编译好的材质 Shader 变体。`PatchRasterize` 从 `RasterBinMeta[GetRasterBin()].BinSWCount` 读取该 Bin 的 Patch 数量，从 `MaterialDisplacementParams` 读取 Displacement 参数。不同材质的程序化 Displacement 完全隔离在不同 Bin 中。

---

## 6. 实施路线图

| 阶段 | 内容 | 复杂度 |
|------|------|--------|
| **Phase 1** | Cull 阶段 — 保守 Bounds（Skinned AABB expansion） | ★★ |
| **Phase 2** | `TransientDeformedBuffer` 原子分配 + 压缩编码 | ★★★ |
| **Phase 3** | Pre-Deform CS（骨骼蒙皮）+ Rasterize/Resolve 接入 | ★★★★ |
| **Phase 4** | 复合 Bin 系统（Vertex+Raster Bin 合并分类） | ★★★ |
| **Phase 5** | WPO 材质 Pre-Deform 支持 | ★★★★ |
| **Phase 6** | Tessellation/Displacement 光栅化路径（Split→Dice） | ★★★★★ |
