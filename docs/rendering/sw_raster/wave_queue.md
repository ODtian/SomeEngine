# WaveQueue — Wave-Level 泛型任务分发

## 1. 问题

在 SW Raster、Dice、Split 等 compute hot path 中，每个线程的工作量严重不均：

| Pass | 线程的任务 | 不均衡来源 |
|------|-----------|-----------|
| SW Raster | bbox 内像素 | 背面三角形=0, 小三角形=1-4, 中三角形=20+ |
| Dice | 微三角形光栅 | 不同 TF 产生不同数量 micro-tri |
| Split | 子 patch emit | 有些三角形 TF=1, 有些 TF=64 |

传统做法用 groupshared queue → barrier → 消费。代价：LDS 占用降 occupancy + 完整 barrier 延迟。

## 2. 核心思路

**零 LDS，纯寄存器 + wave intrinsics。** 每线程声明任务数量 → WavePrefixSum 求偏移 → Boundary bitmask + countbits O(1) 查找 producer → WaveReadLaneAt 按需拉取数据。

关键特性：**任务不预存**——通过 `(srcLane, localIdx)` 按需从 producer 动态创建。

## 3. 算法

```
Phase 1: 每线程声明任务数
  myCount = GetTaskCount()

Phase 2: Wave 前缀和
  myOffset = WavePrefixSum(myCount)
  totalTasks = WaveActiveSum(myCount)

Phase 3: Boundary bitmask + countbits 分发
  每轮 waveSize 个任务:
    构造 boundaryBits: 每个 producer 的最后一项位置置 1
    consumer 的 producerOffset = countbits(boundaryBits & lowerMask)
    srcLane = sourceHead + producerOffset
    localIdx = taskIdx - srcStart
    → ExecuteTask(srcLane, localIdx)
```

### Boundary Bitmask + countbits（替代二分搜索）

传统方案用 log₂(waveSize) 次 `WaveReadLaneAt` 做二分搜索。改用 bitmask：

```
假设 4 个 producer 的任务数: [3, 2, 0, 4]
前缀和: [0, 3, 5, 5]

批次 0 (output slots 0..waveSize-1):
  Producer 0 最后一项在 slot 2  → bit 2
  Producer 1 最后一项在 slot 4  → bit 4
  Producer 3 最后一项在 slot 8  → bit 8
  (Producer 2 无任务 → 不设 bit)

  WorkBoundaryBits = 0b_1_0001_0100

Consumer lane 3: countbits(bits & ((1<<3)-1)) = 1 → producer 1 ✓
Consumer lane 5: countbits(bits & ((1<<5)-1)) = 2 → producer 3 ✓
```

**1 条 `WaveActiveBitOr` + 1 条 `countbits`** 替代 6 条 `WaveReadLaneAt`。快 2-3x。

## 4. 泛型接口

```slang
interface IWaveTask
{
    /// 当前 lane 产出的子任务数量
    uint GetTaskCount();
    
    /// Consumer 执行第 localIdx 个子任务
    /// srcLane: producer lane，consumer 通过 WaveReadLaneAt(myField, srcLane) 拉取数据
    /// localIdx: producer 内的第几个任务
    void ExecuteTask(uint srcLane, uint localIdx);
};
```

## 5. 分发器实现

```slang
struct WaveQueue
{
    static void Distribute<T : IWaveTask>(T task)
    {
        uint myCount = task.GetTaskCount();
        uint myOffset = WavePrefixSum(myCount);
        uint totalTasks = WaveActiveSum(myCount);
        
        uint laneIdx = WaveGetLaneIndex();
        uint waveSize = WaveGetLaneCount();
        uint sourceHead = 0;
        uint workHead = 0;
        
        while (workHead < totalTasks)
        {
            // ---- Boundary Bitmask ----
            uint lastSlot = myOffset + myCount - 1 - workHead;
            uint bmask = 0;
            if (myCount > 0 && lastSlot < waveSize)
                bmask = 1u << lastSlot;
            
            uint boundaryBits = WaveActiveBitOr(bmask);
            
            // ---- Consumer lookup (O(1)) ----
            uint taskIdx = workHead + laneIdx;
            uint prodOffset = countbits(boundaryBits & ((1u << laneIdx) - 1));
            uint srcLane = sourceHead + prodOffset;
            
            // ---- Local index ----
            uint srcStart = WaveReadLaneAt(myOffset, srcLane);
            uint localIdx = taskIdx - srcStart;
            
            // ---- Execute ----
            if (taskIdx < totalTasks)
                task.ExecuteTask(srcLane, localIdx);
            
            // ---- Advance ----
            workHead += waveSize;
            sourceHead += countbits(boundaryBits);
        }
    }
};
```

### Wave64 适配

Wave64 需要 64-bit bitmask：

```slang
// 方案 A: 两个 uint32
uint2 boundaryMask64;
// 方案 B: uint64_t (SM 6.0+)
uint64_t boundaryMask;
```

`countbits` 对 uint64：可用 `countbits(low) + countbits(high)` 或原生 64-bit 版。

## 6. 使用示例

### SW Raster 像素分发

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
        
        // 按需拉取边方程
        float3 e0 = float3(WaveReadLaneAt(tri.Edge0.x, srcLane),
                           WaveReadLaneAt(tri.Edge0.y, srcLane),
                           WaveReadLaneAt(tri.Edge0.z, srcLane));
        // ... edge test, depth test, VisBuffer write
    }
};

// 调用
PixelRasterTask task = { myTri, myVisData, myDepths, myW, myH };
WaveQueue::Distribute(task);
```

### 嵌套分发

WaveQueue 是纯寄存器操作，无 LDS 和 barrier，可自由嵌套：

```
Level 0: 每线程 1 patch → WaveQueue 分发微三角形
Level 1: 每线程 1 micro-tri → WaveQueue 分发像素
```

## 7. 性能特性

| | GroupShared Queue | WaveQueue |
|--|------------------|-----------|
| LDS 使用 | 有 | **零** |
| Barrier | `GroupMemoryBarrierWithGroupSync` | **无** |
| Occupancy | 降低 | **无影响** |
| 分发粒度 | Group 级 (128 threads) | Wave 级 (32/64 threads) |
| 嵌套 | 困难 (barrier 嵌套 = UB) | **自由嵌套** |
| 反向查找 | — | O(1) countbits |

### 开销

| 操作 | Cycles |
|------|--------|
| `WavePrefixSum` | ~5 |
| `WaveActiveBitOr` | ~2 |
| `countbits` | 1 |
| Context WaveReadLaneAt × N | ~2N |
| **总计 per-batch** | **~10 + 2N** |

当 wave 内 max(taskCount)/avg(taskCount) > 2x 时收益显著。
