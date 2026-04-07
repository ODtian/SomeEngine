# UE5 Nanite：Visibility Buffer Resolve 与 Compute Shade Binning 管线总结

## 1. Visibility Buffer 编码

Nanite 使用一个 **64-bit Visibility Buffer** 作为光栅化目标，每像素存储：

| 字段 | 位宽 | 说明 |
|------|------|------|
| Depth | 32-bit (高位) | 用于深度测试（硬件光栅写 HW depth，软光栅用 `InterlockedMax` 写入 64-bit 原子操作的高 32 位） |
| VisibleClusterIndex | ~25 bit | 指向当前帧的可见 cluster 列表的索引 |
| TriangleID | ~7 bit | cluster 内三角形的局部索引（128 triangles → 7 bit 足够） |

> [!NOTE]
> 硬件光栅走正常的 VS/PS 流程写 `R32_UINT` RT + HW Depth；软光栅走 Compute Shader，将 `(Depth << 32 | Payload)` 用 `InterlockedMax` 原子写入 `R64_UINT` UAV，利用深度在高位天然保证最近片元胜出。

通过 VisibleClusterIndex 可以反查 **InstanceID** 和 **ClusterID**（存在一个 Compact Visible Cluster 列表中），进而访问所有几何与材质数据。

---

## 2. Shade Bin 分类（Count → Reserve → Scatter）

UE 5.4 将所有 Nanite 材质着色从 Pixel Shader 迁移到了 **Compute Shader**。核心思路是对 VisBuffer 中的像素按**材质/Shading Bin ID** 进行分类排序（本质上是一个 GPU Counting Sort），然后为每种材质 Dispatch 一次 Compute Shader。

### 三阶段流程

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│   1. Count   │ ──→ │  2. Reserve  │ ──→ │  3. Scatter  │
└──────────────┘     └──────────────┘     └──────────────┘
```

#### Phase 1: Count（计数）
- 全屏 Compute Dispatch，每线程读取一个 VisBuffer 像素
- 查询该像素对应的 **Shading Bin ID**（由 VisibleClusterIndex → MaterialSlot → ShadeBinID 映射得到）
- 对每个 Bin 用 `InterlockedAdd` 累计像素数量 → 写入 `BinCounts[]`

#### Phase 2: Reserve（分配偏移）
- 对 `BinCounts[]` 做 **Prefix Sum**，计算每个 Bin 在全局输出数组中的起始偏移
- 输出 `BinOffsets[]`，这样所有 Bin 共享同一块连续内存，**只需一次内存分配**

#### Phase 3: Scatter（分散写入）
- 再次全屏 Dispatch，每个像素根据自己的 Bin ID 和 `BinOffsets[]`，用原子操作在该 Bin 的区域写入 **像素坐标（packed x,y）**
- 最终得到一个紧凑的 **Pixel Coordinate List**，按 Bin ID 分段排列

> [!IMPORTANT]
> 三阶段设计的关键优势：避免了 `NumBins × ScreenPixels` 的内存开销，只需 `TotalVisiblePixels` 大小的单一缓冲区。

---

## 3. Compute Material Evaluation（材质着色 Dispatch）

对每个 Shade Bin，Dispatch 一个 Compute Shader：

```
for each ShadeBin:
    Dispatch( binPixelCount / 64, 1, 1 )  // 每 workgroup 64 线程
```

每个线程的工作流：

1. **读取像素坐标** → 从 Scatter 阶段的输出缓冲读 `(x, y)`
2. **读 VisBuffer** → 取出 `VisibleClusterIndex + TriangleID`
3. **反查几何数据**：
   - `VisibleClusterIndex` → 查 Compact Cluster 列表 → 得到 `InstanceID`, `ClusterID`, `PageOffset`
   - 从 PageHeap 加载 3 个顶点的位置、UV、法线
4. **计算重心坐标**：
   - 用 3 个顶点投影到屏幕空间，由像素坐标 `(x, y)` 解出重心坐标 `(λ0, λ1, λ2)`
   - 这是在 Compute Shader 中手动完成的（没有硬件插值器）
5. **插值顶点属性**：用重心坐标对 UV、Normal、Tangent 等做插值
6. **计算屏幕空间导数**（用于 Mip 选择）：
   - **1×1 模式**：如果材质不需要 `ddx/ddy`，直接跳过，使用分析方法或预计算 Mip level
   - **2×2 Quad 模式**：如果材质依赖 `ddx/ddy` quad 操作，Nanite 会将 4 个相邻像素打包到同一 wave 的连续 lane 中，用 `QuadReadLaneAt` 模拟硬件 quad
7. **材质求值**：执行 PBR 材质图，采样纹理，计算 BaseColor / Metallic / Roughness / Normal 等
8. **写入 GBuffer**：将结果写入 GBuffer UAV（不走 ROP，走 Compute Store）

---

## 4. 1×1 vs Quad Shading 模式

| 模式 | 条件 | 特点 |
|------|------|------|
| **1×1 Shade Bin** | 材质中无 `ddx/ddy`、无 quad 操作 | 每像素独立着色，零 quad overshading，最高性能 |
| **2×2 Quad Shade Bin** | 材质使用了 `ddx/ddy`（如各向异性过滤、视差贴图） | 4 像素打包为一组，使用 wave intrinsics 模拟 quad 操作 |

> [!TIP]
> UE5 在编译材质时做静态分析，自动判断每个材质该走哪条路径。大部分简单材质走 1×1，性能更好。

---

## 5. Raster Bin vs Shade Bin

Nanite 实际上有**两套 Bin 系统**：

| 概念 | 作用阶段 | 分类依据 |
|------|----------|----------|
| **Raster Bin** | 光栅化阶段 | WPO / Masked / Two-Sided 等光栅化相关状态 |
| **Shade Bin** | 着色阶段 | 材质着色器变体（BaseColor / Normal map 组合等） |

- **非变形材质**（无 WPO）可以合并到同一个 Raster Bin，光栅化只需一次 Dispatch
- **变形材质** 需要单独的 Raster Bin，增加光栅化 Pass 数量
- Shade Bin 的数量取决于材质变体数量，每个 Bin 一次 Compute Dispatch

---

## 6. 对我们管线的参考意义

我们当前管线已有的基础设施：
- ✅ GPU-Driven Culling（BVH 遍历 + 2 阶段 HiZ）
- ✅ Indirect Draw（Cluster 级别）
- ✅ PageHeap 中存储了完整的几何数据（位置、索引）

下一步要做的：

### Step 1: VisBuffer 输出
将 `ClusterDrawPass` 输出从颜色改为 `R32_UINT`，存储 `VisibleClusterIndex | TriangleID`

### Step 2: Shade Bin 分类（3-pass Counting Sort）
1. **Count Pass** — 全屏扫描 VisBuffer，统计每种材质的像素数
2. **Prefix Sum Pass** — 对 Count 做 Prefix Sum，得到每个 Bin 的偏移
3. **Scatter Pass** — 把像素坐标写入对应 Bin 的区间

### Step 3: Compute Material Resolve
- 对每个 Bin Dispatch compute shader
- 从 VisBuffer 反查顶点数据 → 计算重心坐标 → 插值属性 → 材质求值 → 写 GBuffer/Color

### Step 4（可选）: 1×1 / Quad 双模式
- 简单材质走 1×1，带导数的材质走 Quad 模式
