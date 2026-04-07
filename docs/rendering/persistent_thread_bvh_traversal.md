# 持久线程 BVH 遍历方案

## 概述

将当前的逐层 ping-pong indirect dispatch BVH 遍历替换为 Nanite 风格的持久线程（Persistent Threads）版本，在单次 dispatch 中完成 BVH 遍历 + Cluster Culling。

**当前实现**：每层 BVH 产生 3 个 RenderGraph pass（ClearArgs → Traverse → UpdateArgs），8 层深度 = 24 个 pass + 大量全局 GPU barrier。

**持久线程方案**：1 个 dispatch 启动足够的 workgroup 填满 GPU，通过 MPMC 队列 + `globallycoherent` buffer 在单个 shader 内完成全部遍历和 cluster culling。

### 核心收益
- 消除 24 个逐层 pass 和 pass 间的全局 barrier
- 利用空闲线程 — 等待 node 时自动处理 cluster cull（关键路径优化）
- 降低 CPU 提交开销 — 从 26+ pass 减至 InitQueue + 单次 PersistentCull

## 参考

Nanite 相关源码位于 `external/UnrealEnigne/Engine/Shaders/Private/Nanite/`:
- `NaniteHierarchyTraversal.ush` — 持久线程主循环 (`PersistentNodeAndClusterCull`)
- `NaniteHierarchyTraversalCommon.ush` — `FQueueState` / `FQueuePassState` 数据结构
- `NaniteClusterCulling.usf` — `FNaniteTraversalClusterCullCallback` 回调实现

## 架构

### 数据结构

```hlsl
struct QueuePassState {
    uint ClusterBatchReadOffset;  // cluster batch 读偏移（以 batch 为单位）
    uint ClusterWriteOffset;      // cluster 写偏移（以 cluster 为单位）
    uint NodeReadOffset;          // node 读偏移
    uint NodeWriteOffset;         // node 写偏移
    int  NodeCount;               // 当前未处理 node 数（可暂时偏高，保守计数）
};

struct QueueState {
    uint           TotalClusters;
    QueuePassState PassState;
};
```

### Buffer 布局

| Buffer | 用途 | 大小 | 标记 |
|--------|------|------|------|
| `NodeQueue` | BVH 节点 MPMC 队列 | MaxQueueNodes × 8B | `globallycoherent RWStructuredBuffer<uint2>` |
| `QueueState` | 队列读写偏移 + 计数 | 32B | `globallycoherent RWByteAddressBuffer` |
| `ClusterBatches` | 每 batch 的 cluster 就绪计数 | (MaxCandidates / GroupSize) × 4B | `globallycoherent RWByteAddressBuffer` |
| `CandidateClusters` | cluster 候选输出 | MaxCandidates × 12B | `RWStructuredBuffer<uint3>`（不需要 coherent，只由 batch 机制同步） |

### 持久线程主循环

```
while(true) {
    // 阶段 1：尝试获取 node batch
    if (bProcessNodes) {
        读取 NANITE_MAX_BVH_NODES_PER_GROUP 个 node
        检查 node 是否 ready（数据已完整写入）
        if (first node ready) {
            ProcessNodeBatch():
                - 对每个子节点做 frustum cull + LOD 选择
                - 非叶子：子节点写回 NodeQueue
                - 叶子：cluster 写入 CandidateClusters，更新 ClusterBatches 计数
            continue  // 优先推进 node 关键路径
        }
    }

    // 阶段 2：无 node ready，转去处理 cluster
    获取 ClusterBatch（从 ClusterBatches 读计数）
    if (batch 满了 || 已无 node 可处理) {
        ProcessClusterBatch():
            - LOD filter + Frustum cull
            - 通过者写入 VisibleClusters / DrawArgs
    }

    // 终止条件
    if (!bProcessNodes && 无更多 cluster batch)
        break;
    if (bProcessNodes && NodeCount == 0)
        bProcessNodes = false;
}
```

### Node Ready 检测

持久线程中，一个 workgroup 读取的 node 可能尚未被另一个 workgroup 完全写入。Nanite 的策略：
1. node 缓冲区初始化为 `0xFFFFFFFF`（sentinel）
2. 写入 node 数据后，通过 `DeviceMemoryBarrier` 保证可见
3. 读取方检查 `IsNodeDataReady(data)` — 任何有效 node 都不会全为 `0xFFFFFFFF`
4. 如果 first node 不 ready，跳过本批转去处理 cluster（避免阻塞）

### Cluster Batch 机制

Cluster 通过 batch 协调读写同步，避免读到半写入的 cluster：

1. 叶子节点写入 N 个 cluster 到 `CandidateClusters[WriteOffset..WriteOffset+N]`
2. `DeviceMemoryBarrier()` — 确保 cluster 数据可见
3. 更新 `ClusterBatches[batchIndex] += 覆盖的计数`
4. 读取方检查 `ClusterBatches[batchIndex]` 是否等于 `GROUP_SIZE`（batch 满了才处理）
5. 最后无 node 时，不满的 batch 也会被处理

### 与 HiZ 2-Phase Cull 的交互

当前的 HiZ 2-Phase 流程（Phase1 Cull → Draw → HiZ Build → Phase2 Cull → Draw）保持不变。
持久线程只替换 **BVH 遍历** 阶段（产出 `CandidateClusters`），不影响下游的 Phase1/Phase2 cluster culling。

## 改动清单

### Shader

| 文件 | 操作 | 说明 |
|------|------|------|
| `cluster_bvh_traverse.slang` | 重写 | 持久线程循环，融合 node 遍历 + cluster 候选输出 |
| `cluster_cull.slang` | 不变 | Phase1/Phase2 cluster culling 保留原样 |

### C#

| 文件 | 操作 | 说明 |
|------|------|------|
| `ClusterBVHTraversePass.cs` | 重写 | 去掉 ping-pong 双 buffer、UpdateArgs/ClearArgs PSO；新增 QueueState + ClusterBatches buffer；Execute 改为单次 DispatchCompute |
| `ClusterRenderFeature.cs` | 修改 | 删除 L893-L924 的 8 层循环，替换为单个 PersistentTraverse pass；删除 `hBvhQueueB`、`hBvhArgsB`；新增 `hQueueState`、`hClusterBatches` |
| `ClusterGraphPasses.cs` | 修改 | 删除 `ClusterBVHTraverseDepthPass`、`ClusterBVHUpdateArgsPass`、`ClusterBVHClearArgsPass`；新增 `ClusterBVHPersistentPass` |

### 关键技术依赖

- **`globallycoherent`**：Slang 兼容 HLSL 语法，直接 `globallycoherent RWStructuredBuffer<T>` 即可
- **`DeviceMemoryBarrier()` / `DeviceMemoryBarrierWithGroupSync()`**：HLSL SM5.0+ intrinsic，Slang 直接支持
- **`AllMemoryBarrierWithGroupSync()`**：同时刷新 groupshared + device memory

## 注意事项

1. **Workgroup 数量**：Nanite 使用 `MAX_OCCUPANCY` 属性让 driver 决定最大占用率。简单实现可先硬编码 512 个 group，后续可查询 GPU adapter 信息调整。

2. **`NANITE_MAX_BVH_NODES_PER_GROUP`**：Nanite 中此值 = `ThreadGroupSize / NANITE_MAX_BVH_NODE_FANOUT`。当前 BVH fanout = 最多 8 子节点，`numthreads(64)` → 每 group 处理 8 个 node。

3. **调试**：建议保留旧的逐层遍历路径（通过 bool flag 切换），方便 A/B 性能对比和 correctness 验证。
