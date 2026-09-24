# 软光栅化设计

## 1. 为什么需要软光栅

| 场景 | HW 光栅 | SW 光栅 |
|------|---------|---------| 
| 纯不透明静态 | ✅ 最优 | ❌ 不需要 |
| Masked / Alpha Test | ⚠️ PS discard 打断 Early-Z | ✅ CS 采样纹理 → 条件写入 |
| PDO（Pixel Depth Offset）| ⚠️ 修改深度输出 | ✅ CS 直接计算偏移深度 |
| Tessellation Dice | ❌ 微三角形无法 DrawIndirect | ✅ **必须** CS |
| 小三角形（< 2px）| ❌ quad overshading | ✅ 逐像素扫描 |
| WPO | ⚠️ 需变体 | ✅ DeformCache 已就绪 |
| Two-Sided | ⚠️ 需禁用背面剔除 | ✅ CS 跳过 backface cull |

## 2. 核心架构：1 Wave = 1 VRB Batch（32 threads）

> [!IMPORTANT]
> 相比 Nanite 的 64 线程 + LDS 全量顶点缓存方案，我们采用 **32 线程 + VRB 去重 + WaveQueue** 的统一架构。
> 这使得 SW / Mesh Shader / Tessellation 共用同一套顶点变换代码。

```
Wave (32 threads) — 处理 1 个 VRB Batch（≤32 tris, ≤32 unique verts）
│
├── Stage 1: VRB Vertex Dedup (DeduplicateVertIndexes)
│   ├── 解码三角形索引 → 256-bit bitmask 去重
│   ├── 确定 NumUniqueVerts（≤32）和 lane → vertex 映射
│   └── 每 lane 负责 1 个 unique vertex
│
├── Stage 2: Vertex Transform
│   ├── 读 DeformCache / PageHeap → World Space → Clip Space
│   └── 结果留在 lane 寄存器中（零 LDS！）
│
├── Stage 3: Triangle Setup
│   ├── WaveReadLaneAt(clipPos, VertLaneIndexes[k]) 拉取 3 顶点
│   ├── Clip → SubPixel 坐标 (8-bit, 256 sub-pixels/pixel)
│   ├── 边方程 Edge(x,y) = Ax + By + C
│   ├── 背面剔除 + BBox clamp
│   └── 输出 FRasterTri
│
└── Stage 4: Pixel Distribution (WaveQueue)
    ├── WaveQueue 负载均衡分发像素任务
    ├── Edge function test → 重心坐标 → 插值深度
    └── 原子深度测试 + VisBuffer 写入
```

**关键优势**：
- **零 LDS**：顶点数据全在寄存器中，通过 `WaveReadLaneAt` 跨 lane 访问
- **统一代码**：SW Raster / Mesh Shader / Tessellation 共享 Stage 1-2
- **完美负载均衡**：WaveQueue 消除 Nanite 1-thread-1-triangle 模式的 SIMD 发散问题

### 与 Nanite 架构的对比

| | **Nanite SW (Default)** | **Nanite SW (Programmable)** | **我们** |
|--|--|--|--|
| THREADGROUP_SIZE | 64 | 64 | **32** |
| 顶点缓存 | LDS `GroupVerts[256]` | Streaming Register Cache | **Wave 寄存器** |
| VRB 去重 | ❌ 不用 | ❌ 不用（streaming cache 隐式复用）| **✅ `DeduplicateVertIndexes`** |
| 像素分发 | 1 thread = 1 tri 独立光栅 | 1 thread = 1 tri 独立光栅 | **WaveQueue 负载均衡** |
| Occupancy 策略 | MAX_OCCUPANCY（堆 wave 数）| MAX_OCCUPANCY | **WaveQueue 提升 intra-wave 利用率** |
| LDS 使用 | 3KB (GroupVerts) | 0-3KB | **0** |

> [!TIP]
> Nanite 选择 THREADGROUP_SIZE=64 + MAX_OCCUPANCY 是为了弥补 "1 thread 1 triangle" 导致的 SIMD 利用率低下。
> 我们的 WaveQueue 在 wave 内部实现了完美负载均衡，不需要靠高 occupancy 来补偿空闲 lane。
> 因此 32 线程不是妥协，而是更优的选择。

## 3. VRB (Vertex Reuse Batch) 设计

### 3.1 约束

- 每个 batch **≤32 个三角形**
- 每个 batch 引用的 **unique vertex indices ≤ 32 个**
- 使用 **256-bit bitmask**（`uint[8]`），覆盖 MaxVertices=256，**无 span 限制**

### 3.2 Build-Time（ClusterBuilder.cs）

**已完成✅**：线性扫描 + degenerate padding 方案（当前工作版本）。

**计划改造**：采用 Nanite 式 fast path / slow path：

```
线性扫描三角形，遇到 unique verts > 32 → close batch
├── batch count ≤ 5 → Fast Path
│   └── VRBBatchInfo 编码进 GPUCluster header（1 uint）
│       [4:0]  batch0_triCount - 1    (5 bits)
│       [9:5]  batch1_triCount - 1    (5 bits)
│       [14:10] batch2_triCount - 1   (5 bits)
│       [19:15] batch3_triCount - 1   (5 bits)
│       [24:20] batch4_triCount - 1   (5 bits)
│       [27:25] batchCount - 1        (3 bits)
│       [31:28] reserved
│
└── batch count > 5 → Slow Path（极罕见）
    └── 不做 VRB 去重，每 lane 独立 fetch 3 顶点
```

### 3.3 Runtime Binning

Binning shader 根据 VRBBatchInfo 将 cluster 拆成多个 bin entries：

```slang
// 每个 bin entry = uint2
// .x = VisibleClusterIndex
// .y = (RangeStart << 16) | RangeEnd
```

Fast path cluster 产生 ≤5 个 entries，slow path cluster 产生 1 个 entry（整个 range）。

### 3.4 DeduplicateVertIndexes（256-bit bitmask 版）

```slang
void DeduplicateVertIndexes(uint3 vertIndexes, uint laneIdx, bool bValid,
    out uint numUnique, out uint laneVertIndex, out uint3 vertLaneIndexes)
{
    // 1. 构建 256-bit bitmask
    uint triVertMask[8] = {};
    if (bValid) {
        [unroll] for (uint k = 0; k < 3; k++) {
            uint v = vertIndexes[k];
            triVertMask[v >> 5] |= (1u << (v & 31));
        }
    }

    // 2. Wave-level OR
    uint waveMask[8];
    [unroll] for (uint i = 0; i < 8; i++)
        waveMask[i] = WaveActiveBitOr(triVertMask[i]);

    // 3. 计算 unique count + 第 N 个 set bit 的位置
    numUnique = 0;
    [unroll] for (uint i = 0; i < 8; i++)
        numUnique += countbits(waveMask[i]);

    // 4. 分配 lane → vertex 映射
    laneVertIndex = FindNthSetBit256(waveMask, laneIdx);

    // 5. 反查 triangle vertex → lane 映射
    [unroll] for (uint k = 0; k < 3; k++)
        vertLaneIndexes[k] = GetBitPosition256(waveMask, vertIndexes[k]);
}
```

额外开销：+6 VGPR（uint2 → uint8），+6 次 `WaveActiveBitOr`，+6 次 `countbits`。均为 ALU bound，不影响实际瓶颈。

## 4. 子像素精度 & Fill Rule

匹配 D3D12 fixed-function rasterizer 的 8-bit 子像素精度：

```slang
static const uint SUBPIXEL_BITS = 8;
static const uint SUBPIXEL_SCALE = 1 << SUBPIXEL_BITS;  // 256

int2 ToSubPixel(float2 screenPos)
{
    return int2(floor(screenPos * SUBPIXEL_SCALE));
}
```

边方程用**整数算术**避免浮点误差：
```slang
// Edge: A = y1 - y0, B = x0 - x1, C = x1*y0 - x0*y1 (sub-pixel units)
// 像素 (px, py) 的采样点 = (px * 256 + 128, py * 256 + 128)
// Edge value = A * sampleX + B * sampleY + C
// > 0 → inside (with top-left tie-breaking)
```

**Top-left fill rule**：edge value == 0 时，只有 top edge（水平 y 递减）或 left edge（非水平 y 递增）才算 inside。保证共享边不会双写。

## 5. 像素光栅化

通过 WaveQueue 分发，每个 lane 从全 wave 的三角形中按需领取像素任务：

```slang
struct PixelRasterTask : IWaveTask
{
    FRasterTri tri;
    uint visData;
    float3 depths;
    int bboxW, bboxH;

    uint GetTaskCount()
    {
        return tri.bIsValid ? uint(bboxW * bboxH) : 0;
    }

    void ExecuteTask(uint srcLane, uint localIdx)
    {
        int minX  = WaveReadLaneAt(tri.MinPixel.x, srcLane);
        int minY  = WaveReadLaneAt(tri.MinPixel.y, srcLane);
        int width = WaveReadLaneAt(bboxW, srcLane);

        int x = minX + int(localIdx % width);
        int y = minY + int(localIdx / width);

        // edge test → depth test → VisBuffer write
    }
};

WaveQueue::Distribute(task);
```

## 6. 原子深度测试

### 方案 A：32-bit 双原子操作（Phase 3.5 初期推荐）

```slang
void DepthTestAndWrite(int x, int y, float depth, uint visData,
                       RWTexture2D<uint> visBuffer, RWTexture2D<uint> depthUAV)
{
    uint depthBits = asuint(depth);  // Reversed-Z: 更大 = 更近
    uint oldDepth;
    InterlockedMax(depthUAV[int2(x,y)], depthBits, oldDepth);
    if (depthBits > oldDepth)
        visBuffer[int2(x,y)] = visData;
}
```

### 方案 B：64-bit 单原子操作（Phase 4 推荐终态）

```slang
void DepthTestAndWrite(int x, int y, float depth, uint visData,
                       RWTexture2D<uint2> visBuffer64)
{
    uint2 packed = uint2(asuint(depth), visData);
    InterlockedMax(visBuffer64[int2(x,y)], packed);
}
```

## 7. 四条光栅化路径的统一

| 路径 | 顶点来源 | VRB 去重 | 光栅化 | 分发 |
|------|---------|---------|--------|------|
| **SW Opaque** | DeformCache / PageHeap | ✅ DeduplicateVertIndexes | SW scanline | WaveQueue |
| **SW Programmable** | DeformCache + WPO | ✅ DeduplicateVertIndexes | SW scanline + material eval | WaveQueue |
| **Mesh Shader** | DeformCache / PageHeap | ✅ DeduplicateVertIndexes | HW rasterizer | HW |
| **Tessellation** | 固定拓扑（全局查表，无需预计算 VRB） | ✅ DeduplicateVertIndexes | SW dice → WaveQueue | WaveQueue |
| **Fallback HW VS** | DeformCache / PageHeap | ❌ 不需要 | HW rasterizer | HW |

> [!NOTE]
> 所有 SW/Mesh Shader 路径共享同一个 `DeduplicateVertIndexes` + `Vertex Transform` 代码。
> 分叉点仅在 Stage 3 之后：SW 继续 WaveQueue 像素分发，Mesh Shader 导出到 HW 光栅器。

## 8. VisBuffer 编码

SW 和 HW 完全一致：

```
(VisibleClusterIndex + 1) << 7 | TriangleID
```

- 25 bits clusterIdx → 最多 33M visible clusters
- 7 bits triID → 最多 128 triangles/cluster
- 0 = 无几何体（clear value）

## 9. SW/HW 深度统一

### 路线 1（Phase 3.5 初期）

```
HW Raster → HW DSV (D32_Float) + RT (R32_UINT VisBuffer)
SW Raster → DepthUAV + VisBufferUAV
→ CS Merge Pass: 对每像素比较两路深度，选更近的写入统一 buffer
→ HiZ Build from merged depth
```

### 路线 2（Phase 4 终态，推荐）

```
彻底放弃 HW DSV 的深度测试
HW Raster 的 PS: 关闭 HW depth test，InterlockedMax 写 UAV
SW Raster 的 CS: 同样 InterlockedMax 写同一个 UAV
→ 天然统一，无 merge pass
→ HiZ Build from UAV depth
```

## 10. 已完成工作

- [x] WaveQueue 泛型分发器实现（[wave_queue.slang](file:///f:/SomeEngine/assets/Shaders/wave_queue.slang)）
- [x] `DeduplicateVertIndexes` 64-bit bitmask 版本实现（[sw_raster.slang](file:///f:/SomeEngine/assets/Shaders/sw_raster.slang)）
- [x] VRB build-time：`ReorderForVRB` 贪心重排 + degenerate padding（[ClusterBuilder.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/ClusterBuilder.cs)）
- [x] Raster Binning 基础设施（[cluster_binning.slang](file:///f:/SomeEngine/assets/Shaders/cluster_binning.slang) + `RasterBinPass` / `ClusterRasterStage`）
- [x] SlotBuffer / MaterialItems 系统

## 11. 待完成工作

- [ ] VRB build-time 改造：贪心 reorder → Nanite 式线性扫描 + fast/slow path
- [ ] 256-bit bitmask `DeduplicateVertIndexes`（当前 64-bit，待扩展到 MaxVertices=256）
- [ ] Cluster 参数扩展：128 tri / 256 vert（与 Nanite 一致）
- [ ] GPUCluster header 新增 `VRBBatchInfo` 字段（1 uint）
- [ ] Binning shader 改造：per-batch entries + `BinnedClusterIndexBuffer` 从 `uint` → `uint2`
- [ ] SW raster shader 接收 `TriRange(Start, End)` 而非处理整个 cluster
- [ ] Mesh Shader 路径复用 `DeduplicateVertIndexes`
- [ ] DeformCache 集成：顶点变换与光栅化解耦
