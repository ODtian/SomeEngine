# VRB (Variable Rate Binning)

## 概述

VRB 是 SomeEngine 光栅化管线的核心批处理策略。每个 Cluster 最多 32 个三角形，VRB 将这些三角形划分为**可变大小的 batch**，使每个 batch 的唯一顶点数不超过 wave 宽度（32），从而实现 wave 内顶点去重 + 共享。

> **代码**：
> - CPU 构建：[ClusterBuilder.cs](file:///f:/SomeEngine/src/SomeEngine.Assets/Importers/ClusterBuilder.cs) `BuildVRBBatches()`
> - GPU 解码：[cluster_structures.slang](file:///f:/SomeEngine/assets/Shaders/cluster_structures.slang) `DecodeVRBBatch()`
> - GPU 去重：[cluster_deform.slang](file:///f:/SomeEngine/assets/Shaders/cluster_deform.slang) `DeduplicateVertIndexes()`

## 编码格式

`VRBBatchInfo` 打包在 `GPUCluster` 的最后 4 字节（offset 60）：

```
[bit 0..4]   batch[0] count - 1  (5 bit, max 32)
[bit 5..9]   batch[1] count - 1
[bit 10..14] batch[2] count - 1
[bit 15..19] batch[3] count - 1
[bit 20..24] batch[4] count - 1
[bit 25..27] batchCount - 1      (3 bit, max 5 batches)
[bit 28..31] reserved
```

每个 batch 的三角形数量范围 `[1, 32]`，最多 5 个 batch。

## CPU 构建 (BuildVRBBatches)

线性扫描三角形，模拟 wave-level 顶点去重：

1. 维护 64-bit bitmask（覆盖最多 64 个唯一顶点索引 rebased 后的范围）
2. 每加入一个三角形，检查 unique vertex count ≤ 32
3. 超出时关闭当前 batch，开启新 batch
4. 材质边界强制关闭 batch（确保 batch 内材质纯净）
5. 超过 5 个 batch 后停止编码，剩余三角形走 slow residual path

## GPU 解码

```c
VRBBatch DecodeVRBBatch(GPUCluster c, uint batchIdx)
{
    batch.start = sum of previous batch sizes;
    batch.count = ((VRBBatchInfo >> (batchIdx * 5)) & 0x1F) + 1;
}
```

## GPU 顶点去重 (DeduplicateVertIndexes)

每个 wave（32 thread）处理一个 batch：
1. 每 thread 加载一个三角形的 3 个 vertex index
2. 将 index rebase 到 `[0, 63]` 范围
3. 使用 `WaveActiveBitOr` 合并所有 thread 的 vertex bitmask
4. 通过 `FindNthSetBit` 将 lane index 映射到 unique vertex index
5. 仅 unique vertex 对应的 lane 执行实际顶点运算

## 性能

- 避免 shared memory 去重的 bank conflict
- 利用 wave intrinsics (`WaveActiveBitOr`, `WaveGetLaneIndex`) 零开销
- 批次大小自适应三角形局部性
