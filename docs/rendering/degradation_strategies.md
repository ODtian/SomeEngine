# 光栅化降级策略 · 变形缓存 · 动画 BVH 分析

---

## 一、SW/HW 深度策略（已定）

| Tier | 策略 | 角色 |
|------|------|------|
| **Default** | ① Shared + A64 | HW/SW 写同一 DepthUAV+VisBuffer，无 Merge pass |
| **Compat** | ③ Separate + A64 | HW 用 DSV，SW 用 A64，DepthMerge 合并 |
| **Fallback** | ⑤ HW-Only | 不支持 A64 时关闭 SW raster |

---

## 二、Tessellation 架构与降级

### 核心设计：Cluster-Based，只输出元数据

Tessellation 采用 Nanite 风格，**不产出独立顶点/索引缓冲**，仍基于 Cluster 数据：

- Split 阶段估算 TF，递归拆分到 TF ≤ max，输出 `SplitWorkItem`（32B 元数据）
- Dice / HW 阶段从 patch 元数据 + DiceTable + PageHeap 在线计算微顶点
- VisBuffer 编码统一：写原始 `ClusterIndex + TriID`，Shade 通过 depth 重建重心坐标

| 属性 | Cluster 管线 | Tess 管线 |
|------|------------|----------|
| 顶点来源 | PageHeap | PageHeap（插值原始 cluster 顶点） |
| 输出 | 直接光栅 | SplitWorkItem 元数据 |
| VisBuffer 编码 | `ClusterIndex << 7 \| TriID` | **相同** |
| 拓扑 | 固定 index stream | DiceTable 查表（64 种 TF 组合） |

### 双路径光栅化

#### SW 路径（主路径）

```
Tess cluster → Split → PatchBuffer[]
  → Dice CS: 1 group = 1 patch, LDS 查表生成微顶点 + IDomainEvaluate
  → 直接调用 SW Raster 核心 → A64 atomic → VisBuffer + DepthUAV
  → DispatchIndirect(totalPatchCount)，无需按 TF pattern 排序
```

#### HW 路径（可选）

```
Tess cluster → Split → PatchBuffer[]（按 TF pattern 分组）
  → Per-Pattern DrawInstancedIndirect:
      VertexCountPerInstance = microTriPerPattern × 3   // 编译期常量
      InstanceCount          = patternPatchCount         // Split 统计

  VS:
    patchIdx   = SV_InstanceID
    microTriID = SV_VertexID / 3
    cornerID   = SV_VertexID % 3

    patch = PatchBuffer[patternBase + patchIdx]
    bary  = DiceTable_VertexBary(TF, DiceTable_TriIndex(TF, microTriID)[cornerID])
    origBary = lerp(patch.BaryUV0/1/2, bary)

    // 从 PageHeap 读原始 cluster 顶点 + IDomainEvaluate
    pos, normal, uv = InterpolateFromCluster(patch, origBary)
    output.Pos = mul(ViewProj, IDomainEvaluate(pos, normal, uv))
    output.PackedID = pack(patch.ClusterIndex, patch.TriID)

  PS:
    A64 atomic → VisBuffer(PackedID) + DepthUAV
```

**零 buffer materialization**——VS 从 patch 元数据 + DiceTable + PageHeap 直接算微顶点位置，不写中间 buffer。

**Split 分组**：Split emit 时已知 TF pattern，直接 atomic append 到对应 pattern 的子区间（最多 64 种），不需要独立 binning pass。

#### SW vs HW 对比

| 维度 | SW Dice（CS 内联光栅） | HW DrawInstancedIndirect |
|------|---------------------|------------------------|
| 小三角形 (<1px) | ✅ 无 quad overshading | ❌ HW quad 浪费 75%+ |
| 大三角形 (>4px) | ⚠️ 逐像素 atomic | ✅ HW 原生高效 |
| Buffer 开销 | ✅ 零 materialization | ✅ 零 materialization |
| IDomainEvaluate | CS 内联 | VS 内联 |
| 排序要求 | 无 | 需按 TF pattern 分组 |

### 降级策略

| 级别 | 策略 | 效果 |
|------|------|------|
| **Full** | Split → Dice SW Raster | 完整细分 + displacement |
| **HW** | Split → DrawInstancedIndirect | HW 光栅，大三角形更优 |
| **Reduced** | 降低 `PIXELS_PER_EDGE` 或 clamp TF | 减少微三角形数 |
| **Minimal** | TF=1 | 退化为原始三角形 + displacement |
| **Off** | 不标记 HasTess | 无 displacement |

### 与 ClusterBin 的关系

Tess cluster 在 ClusterBin 中是独立 bin（`RasterFlags.HasTess`）。Tess bin 的 clusters 走 Split→Dice/HW 而非普通 SWRaster/HWDraw。

```
field[1]: RasterFlags (uint)
  bit 0: AlphaTest
  bit 1: TwoSided
  bit 2: HasTess
```

---

## 三、变形策略与 Bin 共享分析

### 核心问题

VRB-Bin 下 Deform 和 Raster 能否共享同一个 `BinnedClusterIndex[]`？

**答案：可以。** 以下详细推演。

### 关键观察：Material 同时决定 VertexEval 和 RasterPath

```
MaterialSlotBuffer[slot]:
  .RasterBinKey    → 决定 SW/HW 分流 + alpha test 等
  .VertexEvalKey   → 决定顶点变形函数 (Static/Skinned/WPO)
  .ShadeBinKey     → 决定着色 bin
```

一个 Material 完全确定了该 cluster 的变形方式和光栅路径。因此：
- **RasterBin 的 binKey 已经隐式包含 VertexEval 类型**
- 同一 bin 内的所有 cluster 使用相同的变形函数 + 相同的光栅路径
- **不需要独立的 DeformBin 阶段**

### 统一 Bin 布局

```
[CullOutput]
  VisibleClusters[0..C-1]         ← cull 产出的候选 cluster

[RasterBin 4-pass output]       （唯一的 binning）
  BinnedClusterIndex[0..C-1]     ← 按 binKey 重排的 cluster 索引
  BinMeta[0..B-1]                ← { offset, swCount, hwCount } per bin
  IndirectArgs[0..B-1]           ← per-bin DispatchIndirect / DrawIndirect args

  binKey = hash(MaterialSlot.RasterBinKey)
  // RasterBinKey 编码: { vertexEvalType, rasterPath(SW/HW), alphaTest, ... }
```

### 两阶段衔接：Deform → Raster

#### Cache 模式 (DeformCache + VRB-Bin)

```
阶段 1: Deform CS                   阶段 2: Raster
─────────────────────────────────    ─────────────────────────────────
for each bin b in [0..B-1]:          for each bin b in [0..B-1]:
  DispatchIndirect(DeformCS,           if bin.isSW:
    IndirectArgs[b])                     DispatchIndirect(SWRasterCS,
                                           IndirectArgs[b])
  // DeformCS 内部:                    else:
  //   idx = BinnedClusterIndex           DrawIndirect(HWRasterVS/PS,
  //         [BinMeta[b].offset + tid]       IndirectArgs[b])
  //   cluster = PageHeap[idx]
  //   vtx = ReadRawVertex(cluster)      // Raster 内部:
  //   transformed = VertexEval(vtx)     //   idx = BinnedClusterIndex[same]
  //   DeformCache[cacheAddr] = pack     //   cache = DeformCache[cacheAddr]
  //   (cacheAddr 由 cluster 的          //   pos = unpack(cache)
  //    VRB batch offset 直接映射)       //   → VRB → WaveQueue / HW draw
```

**关键布局**：

```
DeformCache 与 VRB Batch 的映射:

  Cluster[i] 在 Page 中有 N 个 VRB batch，每 batch 32 vertices
  DeformCache 按 cluster 的 VRB batch 线性排列:

  BinnedClusterIndex:  [ c₀  c₁  c₂ | c₃  c₄ | ... ]
                         ↑ bin 0     ↑ bin 1
  DeformCache:         [ c₀.batch₀ c₀.batch₁ | c₁.batch₀ c₁.batch₁ c₁.batch₂ | ... ]
                         ↑ 每 batch = 32 × PackedVertex (6B pos + 6B attr = 12B)

  CacheBaseOffset[i] = prefix_sum of (BinnedClusterIndex[i].batchCount × 32)
```

**Deform 的 dispatch 粒度**：
- IndirectArgs 中 `groupCount = sum(batchCount for clusters in this bin)`
- 每 thread group 处理一个 VRB batch (32 vertices)
- 固定负载，完美均衡

#### Inline 模式

```
阶段 1: 跳过                        阶段 2: Raster (含 inline 变形)
─────────────────────────────────    ─────────────────────────────────
(无 Deform pass)                     for each bin b in [0..B-1]:
                                       DispatchIndirect(SWRasterCS, ...)
                                       // Raster 内部:
                                       //   读 PageHeap raw vertex
                                       //   inline VertexEval(vtx)
                                       //   VRB → rasterize
```

两种模式差异仅在：Raster CS 的顶点来源（Cache vs PageHeap+inline eval）。
Shader 通过泛型 `IVertexSource` 抽象：

```slang
interface IVertexSource {
    float3 getPosition(uint clusterIdx, uint vertIdx);
    float3 getNormal(uint clusterIdx, uint vertIdx);
}
struct PageHeapSource<TVE : IVertexEvaluate> : IVertexSource { ... }   // Inline
struct CacheSource : IVertexSource { ... }                              // Cache
```

### MaterialRange 为什么不可行？

```
MaterialRange dispatch:
  Range 0 (MatID 0-3):  [c₀ c₁ c₅ c₈ ...]     ← 按材质连续段，不按 VRB batch
  Range 1 (MatID 4-7):  [c₂ c₃ c₆ ...]

  Deform CS 按 Range dispatch → 变换顶点 → 写 Cache

  问题：Cache 中顶点按 MaterialRange 排列
  但 Raster 需要按 VRB batch 排列（每 batch 32 vertices）
  → 需要重新 bin → 重新生成 IndirectArgs → 4-pass binning 再来一次
  → 总工作 = MaterialRange bin(4-pass) + Deform + VRB re-bin(4-pass) + Raster
  对比 VRB = VRB bin(4-pass) + Deform + Raster
  多了一整套 4-pass binning！
```

**结论：MaterialRange bin 产出的 draw entry 无法直接用于 VRB 光栅化，必须重新 emit。确认淘汰。**

### Inline vs Cache 的选择逻辑

两者都保留，是**性能 tradeoff**，不是能力区分：

| 维度 | Inline | Cache |
|------|--------|-------|
| 变形开销分摊 | ❌ 每次光栅化重复变形 | ✅ 变形一次，多次复用 |
| 带宽 | ✅ 无 cache 读写 | ❌ 写 cache + 读 cache (12B/vertex) |
| 多 Pass 复用 | ❌ Shadow/HiZ phase 各自 inline | ✅ Shadow/HiZ 复用 cache |
| 变形复杂度容限 | ⚠️ Raster CS 内做变形增加寄存器压力 | ✅ 独立 pass，无压力限制 |

选择逻辑（运行时 per-bin 决定）：
```csharp
if (material.VertexEvalComplexity <= Threshold && passCount == 1)
    → Inline
else
    → Cache
```

### 最终变形策略架构

```
                        ┌─ Inline: rasterCS 内联变形 ─────── SW/HW Raster
CullOutput → RasterBin ─┤
                        └─ Cache:  DeformCS → Cache ──────── SW/HW Raster
                              ↑                                  ↑
                        共享 BinnedClusterIndex           共享 BinnedClusterIndex
                        共享 IndirectArgs                 共享 IndirectArgs（复用）
```

**RasterBin 是唯一的 cluster-level binning pass**，Deform 和 Raster 共享其产出。DeformBin 作为独立阶段可以取消。

---

## 四、Binning 管线耦合分析与 SlotBuffer 设计

### 三个 Binning 阶段的粒度

| Bin | 粒度 | 输入 | 可否合并 |
|-----|------|------|---------|
| **ClusterBin** (= RasterBin + DeformBin) | Cluster | CullOutput | 已合并为一 |
| **ShadeBin** | **Pixel** | VisBuffer（光栅后） | ❌ 不同粒度，独立 |

最少 **2 个 binning pass**：ClusterBin（4-pass cluster 级）+ ShadeBin（3-pass pixel 级）。

### Shade ↔ VertexEval 的管线相关耦合

| 模式 | Shade 读顶点方式 | Shade ↔ VertexEval |
|------|-----------------|-------------------|
| **Cache** | 读 DeformCache | ✅ **解耦**——Shade 不需要知道 VertexEval |
| **Inline** | 读 PageHeap + 重新 eval | ❌ **耦合**——Shade PSO 泛型参数包含 VertexEval |

### VertexEval × RasterPath × ShadeGroup 自由度

**存在于 SlotBuffer 的（per-material，注册时确定）**：
- VertexEvalType（Static/Skinned/WPO）
- RasterFlags（AlphaTest, TwoSided, ...）
- ShadeGroupID

**不存在于 SlotBuffer 的（runtime per-cluster）**：
- SW vs HW：由屏幕面积决定

**跨维度耦合表**：

| 维度对 | 独立？ | 条件 |
|--------|--------|------|
| VertexEval ↔ RasterFlags | ✅ 独立 | |
| VertexEval ↔ SW/HW | ✅ 独立 | SW/HW 是 runtime per-cluster |
| RasterFlags ↔ ShadeGroup | ✅ 独立 | |
| RasterFlags ↔ SW/HW | ✅ 独立 | |
| **ShadeGroup ↔ VertexEval** | ⚠️ **管线相关** | Cache→独立；Inline→耦合 |
| ShadeGroup ↔ SW/HW | ✅ 独立 | |

### 实际自由度计算

假设 3 种 VertexEval × 2 种 RasterFlags × 5 种 ShadeGroup：

| 模式 | ClusterBin 数 | ShadeBin 数 | 总 dispatch |
|------|-------------|------------|------------|
| **Cache** | 3×2×2(runtime) = **12** | **5** | ≤12 + 5 = **17** |
| **Inline** | **12** | 5×3 = **15** | ≤12 + 15 = **27** |

### SlotBuffer 设计：3 个独立 raw field

**不存预组合的 composite key**——存原子属性值，binning shader 按管线模式组合：

```
MaterialSlotBuffer (SoA, 3 fields):
  field[0]: VertexEvalType   (uint: 0=Static, 1=Skinned, 2=WPO)
  field[1]: RasterFlags      (uint: bit0=AlphaTest, bit1=TwoSided, ...)
  field[2]: ShadeGroupID     (uint: 0=PBR, 1=Unlit, 2=SSS, ...)
```

```slang
// ClusterBin shader
uint ve = SlotBuffer.Load(slot, 0);
uint rf = SlotBuffer.Load(slot, 1);
uint hw = (screenArea > threshold) ? 1 : 0;    // runtime per-cluster!
uint clusterKey = ve * 4 + rf * 2 + hw;

// ShadeBin shader
uint sg = SlotBuffer.Load(slot, 2);
uint shadeKey;
if (USE_DEFORM_CACHE)
    shadeKey = sg;                               // 解耦
else {
    uint ve2 = SlotBuffer.Load(slot, 0);
    shadeKey = sg * VE_COUNT + ve2;              // 耦合
}
```

**好处**：
- 同一份 SlotBuffer 适用所有管线模式，无需重建
- 新增属性只加 field，不改已有 field 语义
- Bin key 组合方式全在 shader 端，CPU 零改动

---

## 五、BVH 降级策略

| Tier | 策略 | 说明 |
|------|------|------|
| 第一版 | γ Expand AABB | 最小改动 |
| 主路径 | β-2 Emit Clusters | 变换直观，需 build-time 对齐 |
| 远期 | α Override Tree | 主角级精度 |

---

## 六、全局流水线图

```
Instance Cull → BVH Traverse (animated: EmitClusters / ExpandAABB)
  → Cluster Cull (Frustum + HiZ, SW/HW threshold split)
  → ClusterBin 4-pass (唯一 cluster-level bin)
      binKey = VertexEval × RasterFlags(AlphaTest|HasTess) × SW/HW(runtime)
      SlotBuffer field[0,1] + runtime screenArea
  ┌───────────────────────────────────────────────────────────────┐
  │ Per-Bin:                                                       │
  │   [if Cache] DeformCS (BinnedClusterIndex → DeformCache)       │
  │   [SW path]  SWRasterCS (→ VisBuffer, A64)                     │
  │   [HW path]  HW DrawInstancedIndirect (→ VisBuffer, A64)       │
  │   [Tess bin] Split → Dice SW / HW DrawInstancedIndirect        │
  │              (→ 同一 VisBuffer, Split 按 TF pattern 分组)       │
  └───────────────────────────────────────────────────────────────┘
  → ShadeBin 3-pass (pixel-level)
      shadeKey = ShadeGroupID (Cache) 或 ShadeGroupID × VertexEval (Inline)
      SlotBuffer field[2] (+ field[0] if Inline)
  → Per-Bin MaterialShade CS → OutputColor
```

### 正交配置项

```csharp
enum DepthMode     { SharedA64, SeparateA64, HWOnly }
enum DeformMode    { Inline, Cache }
enum AnimBVHMode   { ExpandAABB, EmitClusters, OverrideTree }
// 3 × 2 × 3 = 18 种组合，运行时按 Feature Flag 选择
```
