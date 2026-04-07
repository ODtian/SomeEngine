# Render Graph V2: 异步计算与全自动屏障升级方案

本文档描述了 Render Graph (RG) 演进到 V2 的架构设计方案，核心目标是**彻底消除假依赖**，**实现精准的全自动屏障**，并原生支持**异步计算 (Async Compute)** 与**细粒度子资源追踪**。

---

## 1. 核心痛点与设计目标

### 当前 V1 局限性
1. **假依赖导致过度同步**：目前以 `Resource ID` 为追踪粒度。如果 Pass A 写 Mip 0，Pass B 写 Mip 1，RG 会插入不必要的 UAV 屏障。
2. **缺乏并发写语义**：不支持 `InterlockedAdd` 等无序原子并发操作的表达，同一资源的连续 UAV 写会被强制插入屏障，阻断了如 "32个 BVH 遍历 Pass" 等逻辑并行的 Dispatch 重叠（Overlap）。
3. **集中式屏障执行**：在 Pass 开始前统一执行屏障（Pipeline Flush），GPU 会有一瞬间处于饥饿状态，掩盖了屏障的延迟开销。
4. **单队列串行图**：拓扑排序将图完全展平为一维数组，丢失了并发性，无法将没有依赖的 Compute Pass 派发到异步计算队列。

### V2 设计目标
1. **精准子资源追踪 (Subresource Tracking)**：追踪 Mip / Array Slice。
2. **支持并发访问 (Concurrent Access)**：允许用户声明 `NoSync` / `Concurrent` 标记，打破不必要的序列化。
3. **分离屏障 (Split Barriers)**：将 Barrier 拆分为 `Begin` 和 `End`，利用 Pass 的执行窗口掩盖内存同步延迟。
4. **多队列异步派发 (Async Compute & Copy)**：根据依赖图自动拆分 Graphics、Async Compute、Copy 队列的 CommandList 并自动插入跨队列 Semaphore 同步。

---

## 2. 核心概念升级

### 2.1 资源状态与访问标记
引入细粒度的访问标记（Access Flags），允许用户向 RG 传达更明确的意图：

```csharp
[Flags]
public enum RGAccessFlags
{
    None = 0,
    // 允许并发写：当两个相邻的 Pass 都使用 Concurrent 时，不插入同步屏障
    Concurrent = 1 << 0,
    // 子资源不相关：表示此次操作只关注指定的 Mip / Slice
    SubresourceSpecific = 1 << 1,
}
```

### 2.2 子资源追踪掩码 (Subresource Mask)
将原来的 `trackedStateByResource[ResourceID]` 升级为包含子资源信息的追踪结构。可以使用 64 位的 Mask 树，或按 Mip/Slice 构建区间树，来精确表示哪个 Mip 处于什么状态。
如果 Pass A 申请 Write Texture(Mip=0)，Pass B 申请 Write Texture(Mip=1)，它们的状态互不干扰，依赖图上不会产生连线。

### 2.3 Pass 的物理队列属性
在声明 Pass 时，指定期望的硬件队列：
```csharp
public enum RGQueueType
{
    Graphics,      // 默认，通用队列
    AsyncCompute,  // 异步计算队列（如果硬件不支持则回退到 Graphics）
    Copy           // 专用 DMA 队列（适合从 CPU 传数据）
}
```

---

## 3. 编译期分析管线 (Compile-Time Pipeline)

编译期将经历以下阶段：

### Phase 1: 节点构建与 DCE (剔除)
（与 V1 类似）从 Sink 节点出发，进行反向可达性分析，剔除没有被用到的 Pass。

### Phase 2: 精确依赖图构建 (Subresource-Aware Dependency Graph)
不仅比较 Resource ID，还要比较 Subresource Mask：
- **Read-After-Write (RAW)** -> 产生强依赖边。
- **Write-After-Write (WAW)** -> 若带有 `RGAccessFlags.Concurrent`，则**不产生依赖边**；否则产生强依赖边。
- **Write-After-Read (WAR)** -> 产生反依赖边（防止读取前被覆盖）。

### Phase 3: 多队列调度与跨队列同步 (Queue Scheduling & Fork/Join)
- 为 `AsyncCompute` 的 Pass 分配独立的执行时间线。
- 当 `AsyncCompute` 的结果被 `Graphics` 读取时，RG 自动插入跨队列的 Semaphore（信号量）/ Fence 等待。
- RG 将并行的分支（Fork）最大化，并在必须合并（Join）处注入等待。

### Phase 4: 分离屏障插入 (Split Barrier Insertion)
不再将所有 Barrier 堆积在 Pass 的最前头。
- **Barrier Begin**：在**生产者 Pass 结束**后立刻触发（或者挂在紧接着的下一个 Pass 之前）。
- **Barrier End**：在**消费者 Pass 开始**前触发。
如果生产者和消费者之间隔了多个其他的 Pass，那么屏障的延迟就被完美隐藏了。

---

## 4. API 演进示例

为了保持对使用者的友好，上层 API 的变动极小：

```csharp
// 声明 32 个 BVH Pass 时，不再触发强制的 WAW 同步
for (int i = 0; i < 32; i++)
{
    int level = i;
    rg.AddPass<BVHPassData>($"BVH_Traverse_Level_{level}",
        RGQueueType.AsyncCompute, // 指定跑在异步计算队列
        (builder, data) => 
        {
            // 声明 UAV 写，并明确带有 Concurrent 标记，RG 将允许它们 Overlap
            data.NodeBuffer = builder.WriteBuffer(nodeBuffer, 
                                                 ResourceState.UnorderedAccess, 
                                                 RGAccessFlags.Concurrent);
        },
        (context, data) => 
        {
            context.ImmediateContext.Dispatch(1024, 1, 1);
        }
    );
}

// 声明子资源读写
rg.AddPass<MipGenData>("GenerateMip1", RGQueueType.Graphics,
    (builder, data) => 
    {
        // 读 Mip 0
        data.SrcTex = builder.ReadTexture(myTex, ResourceState.ShaderResource, mipLevel: 0);
        // 写 Mip 1
        data.DstTex = builder.WriteTexture(myTex, ResourceState.UnorderedAccess, mipLevel: 1);
    },
    ...
);
```

---

## 5. 实施路径 (Milestones)

建议将 V2 升级分为三个阶段平稳过渡：

### 阶段一：Concurrent Flag 与消除假依赖（性价比最高）
- **实现内容**：在 `RegisterResourceRead/Write` 中增加 `RGAccessFlags` 参数。
- **编译期改动**：在判断 `oldState == required.State` 时，如果都是 `UnorderedAccess` 且两者都有 `Concurrent` 标记，跳过屏障插入，不建立强制先后依赖。
- **收益**：立刻解决当前 BVH 等连续 Compute Pass 性能浪费的问题。

### 阶段二：子资源追踪 (Subresource Tracking)
- **实现内容**：将 `TrackedState` 粒度细化。在 `RGResourceHandle` 内部附带 Subresource 的 Range (MipStart, MipCount, SliceStart, SliceCount)。
- **编译期改动**：在依赖构图时，只有 Range 相交的读写才被视为依赖。
- **收益**：Mipmap 生成、TextureArray 分片渲染实现无障碍自动屏障。

### 阶段三：异步计算队列与 Split Barrier（终极形态）
- **实现内容**：在 AddPass 引入 `RGQueueType`。
- **编译期改动**：实现图的并行拓扑调度，利用 Diligent / RHI 的跨 CommandList 同步原语（Fences）实现队列同步；实现 Barrier_Begin / End 的剥离。
- **收益**：引擎彻底进入现代高并发渲染架构。
