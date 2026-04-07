# Page Streaming 设计

## 现状

当前 `ClusterStreamer` + `ClusterResourceManager` 实现了基础的 page fault → readback → load → patch BVH 链路：

1. **Shader 端**：BVH 遍历时，叶节点 `ChildPointer == 0xFFFFFFFF` 写入 `PageFaultBuffer`
2. **Readback**：`ClusterBVHPageFaultCopyPass` 复制到 staging buffer，CPU 读取
3. **CPU 处理**：`ClusterStreamer.EnqueueFaultNodes` → `Update` → `TryLoadPage` → upload + patch
4. **Heap 管理**：64MB heap，first-fit 分配，LRU 驱逐

### 已验证可用
- Evict All Pages → page fault 产生 → streaming 重新加载 → 画面恢复 ✅

### 缺失特性
- 无优先级排序
- 无预算控制（单帧全量加载）
- 无预取机制
- 无异步 IO（当前 source data 在内存中）

---

## 设计方案

### 1. 优先级排序

#### 信息来源

Page fault 本身只包含 BVH node index，但可以从中推导优先级：

| 信息 | 来源 | 开销 |
|------|------|------|
| 到相机距离 | BVH node 的 LODSphere center + instance transform | 需要 CPU 查表 |
| LOD level | BVH 深度（遍历时已知） | 需要 shader 额外写入 |
| 屏幕覆盖面积 | 距离 + sphere radius | 可推导 |
| fault 次数 | CPU 端累计 | 几乎无开销 |

#### 推荐方案：距离优先级

```
Priority = 1.0 / max(distanceToCamera, epsilon)
```

实现思路：
- Shader 端：PageFaultBuffer 增加 `instanceID` 字段（当前只写了 nodeIndex）
- CPU 端：从 nodeIndex 查 BVH 节点的 LODSphere，结合 instance transform 计算世界空间距离
- 排序后按优先级从高到低处理

#### 扩展：多帧持久 fault

```csharp
struct PendingPage
{
    uint PageID;
    float Priority;       // 距离优先级
    int FramesSinceFault; // fault 持续帧数，用于 aging/starvation 防止
}
```

aging 策略：每帧 `Priority += AgingBoost * FramesSinceFault`，防止远处 page 饿死。

---

### 2. Per-frame 预算控制

#### 目标

限制每帧的加载量，避免单帧卡顿。

#### 参数

```csharp
public class StreamingBudget
{
    public int MaxPagesPerFrame = 8;          // 每帧最多加载 page 数
    public uint MaxUploadBytesPerFrame = 2 * 1024 * 1024; // 每帧最多上传 2MB
}
```

#### Update 逻辑

```
1. 收集所有 pending fault pages（新 fault + 上帧遗留）
2. 按优先级排序
3. 按预算从高到低依次加载，直到达到 MaxPagesPerFrame 或 MaxUploadBytesPerFrame
4. 未完成的 page 保留到下一帧继续处理
```

#### 关键变化

- `_pendingFaultNodes` 从 HashSet 改为持久的 priority queue
- 每帧不再 Clear，而是只移除已成功加载的
- 新增 `StreamingBudget` 配置

---

### 3. PageFaultBuffer 扩展

当前 shader 只写入 `nodeIndex`：

```hlsl
PageFaultBuffer.Store(4 + faultIndex * 4, nodeIndex);
```

建议扩展为写入 `(nodeIndex, instanceID)` 对：

```hlsl
PageFaultBuffer.Store(4 + faultIndex * 8, nodeIndex);
PageFaultBuffer.Store(4 + faultIndex * 8 + 4, instanceID);
```

这样 CPU 端可以直接拿到 instance transform 计算距离优先级，不需要额外查表。

> **Buffer 大小**：从 `4 + MaxPageFaults * 4` 增加到 `4 + MaxPageFaults * 8`

---

### 4. 预取（Prefetch）— 后续

基于 LOD 和相机运动方向，预测即将需要的 page。

可能的策略：
- **邻域预取**：加载某 page 时，同时加载 BVH 树中的兄弟 page
- **运动预取**：根据相机速度方向预测穿过的 BVH 区域
- **LOD 预取**：当前 LOD level 的相邻 level page

优先级低于 reactive streaming，后续实现。

---

### 5. 异步磁盘 IO — 后续

当前 `_pageSourceData` 保存了所有 page 的 CPU 内存副本，实际就是同步读取。

真正的 streaming 应当：
1. `AddMesh` 只注册 page 元数据（offset, size），不加载数据
2. Page fault 时异步读取磁盘
3. 读取完成后进入上传队列

这需要文件格式支持（page 对齐、可随机访问），后续结合资产系统设计。

---

## 实现优先级

| 阶段 | 特性 | 复杂度 |
|------|------|--------|
| **P0** | Per-frame 预算控制 | 低 |
| **P0** | PageFaultBuffer 扩展(+instanceID) | 低 |
| **P1** | 距离优先级排序 | 中 |
| **P1** | 多帧持久 fault + aging | 中 |
| **P2** | 邻域预取 | 中 |
| **P3** | 异步磁盘 IO | 高 |
