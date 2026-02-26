# Cluster BVH Culling Design

## 1. 概述
为了加速 Cluster 剔除过程，我们将从线性扫描所有 Cluster 切换为基于 BVH (Bounding Volume Hierarchy) 的层次化剔除。
该方案通过构建 Cluster 的 BVH 树，在 GPU 上进行层次遍历，利用父节点的包围盒和 LOD 信息快速剔除大片不可见或误差满足要求的 Cluster，从而减少每帧需要处理的 Cluster 数量。

## 2. 数据结构设计 (Data Structures)

### 2.1 BVH 节点定义 (ClusterBVHNode)
所有节点大小一致，存储在持久化的 `StructuredBuffer` 中。

```cpp
struct ClusterBVHNode
{
    float3 BoundMin;
    float3 BoundMax;
    float4 LODSphere; // xyz: Center, w: Radius
    float  LODError; 

    // 子节点指针 / Cluster 引用 (Union)
    // 内部节点：指向 BVH Buffer 中的子节点绝对索引
    // 叶子节点：编码 Page 偏移与驻留状态
    uint   ChildPointer; 
    uint   ChildCount;   
    
    uint   NodeType; // 0 = Internal, 1 = Leaf
    float  _Pad0; 
};
```

#### 字段编码详解 (Dynamic Patching 方案)
*   **内部节点 (Internal Node)**:
    *   `ChildPointer`: 子节点在全局 `GlobalBVHBuffer` 中的起始索引。在 Mesh 加载时由 CPU 完成重定位 (Relocation)。
    *   `ChildCount`: 子节点数量。
*   **叶子节点 (Leaf Node)**:
    *   `ChildPointer`: 
        *   `0xFFFFFFFF` (~0): **Page Fault** (缺页)。
        *   其他值: `PageByteOffset` (Page 在 `PageHeap` 中的直接字节偏移)。由运行时动态修补。
    *   `ChildCount`:
        *   **Bits 12-31**: `ClusterCount` (该 Leaf 包含的 Cluster 数量)。
        *   **Bits 0-11**: `ClusterStart` (该组 Cluster 在其所属 Page 内的起始偏移)。

### 2.2 运行时资源 (Runtime Resources)

1.  **Global BVH Buffer**: `RWStructuredBuffer<ClusterBVHNode>`
    *   存储场景中所有静态物体的 Cluster BVH 节点。
    *   **支持运行时修补**：当 Page 状态变化时，通过 Compute Shader 修改叶子节点的 `ChildPointer`。
2.  **Page Heap**: `ByteAddressBuffer`
    *   存储所有已加载的 Cluster Pages。
3.  **Work Queues**: `RWStructuredBuffer<uint>` (Ping-Pong)
    *   层级遍历时存储待处理节点。

## 3. 动态修补逻辑 (Dynamic Patching)

为了消除 `PageTable` 的间接寻址开销，我们采用动态修补方案：

1.  **加载阶段 (Load-time)**:
    *   CPU 将 Mesh 局部 BVH 拷贝到全局 Buffer。
    *   **重定位**：修正内部节点的 `ChildPointer`。
    *   **注册**：建立 `PageID -> List<BVHNodeIndex>` 的映射追踪表。
    *   **预上传**：如果包含永远驻留的粗粒度 LOD，直接填充其 `ChildPointer` 并设 `IsResident=1`。
2.  **运行阶段 (Runtime)**:
    *   **Page 流入**：Streamer 分配 `PageHeap` 空间后，通知 `ClusterResourceManager`。
    *   **异步修补**：调度 `BVH_Patch` Compute Shader，根据该 PageID 对应的节点列表，批量更新 `ChildPointer` 为 `Resident | HeapOffset`。
    *   **Page 流出**：同理，将对应节点的 `IsResident` 位清零。

## 4. GPU 遍历算法 (Traversal Algorithm)

### Pass 2: BVH 遍历 (Traversal Loop)
1.  **剔除评估**：视锥体检测与 LOD 误差评估。
2.  **分类处理**:
    *   **Internal Node**: 如果未被剔除，将子节点推入下一层队列。
    *   **Leaf Node**: 
        *   如果 `ChildPointer == 0xFFFFFFFF`: 标记 Page Fault 并请求流式加载。
        *   否则: 使用其存储的 `PageByteOffset` 访问数据并 Append 任务。

## 5. 资产管线 (Asset Pipeline)

在 `ClusterBuilder` 中增加 BVH 构建步骤：

1.  **Cluster 生成**: 使用 `meshopt` 生成 Clusters。
2.  **BVH 构建**:
    *   **输入**: 一组 Clusters (每个 Cluster 有 Bounds, LODError, ParentLODError)。
    *   **策略**: Bottom-up 构建。
        *   将 Clusters 分组为 Leaf Nodes。
        *    Leaf Node 继承父级 DAG 节点的 LOD Sphere 与 Error。
        *   Internal Node 递归聚合子节点的 Bounds。
    *   **序列化**: 将构建好的 BVH 线性化并保存。

## 6. 层级调度 (Level Scheduling)
采用层级调度的方式进行遍历，通过 Indirect Dispatch 避免 CS 中的深度递归。

## 7. 目前进度现状 (Current Status)

### 已完成 (Done)
- [x] **数据结构定义**：C# 端的 `ClusterBVHNode` 已支持 16 字节对齐及字段打包。
- [x] **资产管线构建**：`ClusterBuilder` 已实现自底向上的 BVH 构建逻辑，并支持基于 Morton Code 的叶子节点聚类。
- [x] **单实例渲染管线**：`ClusterBVHTraversePass` 已实现层级调度渲染与 Ping-Pong 队列管理。
- [x] **渲染反馈回读**：实现了异步读取 GPU 每一层遍历的节点数量用于调试。

### 进行中/待办 (WIP & TODO)
- [ ] **移除页表依赖**：正在重构着色器与 C# 端逻辑，将 `PageTable` 彻底删除，改用 `Leaf Node` 直接寻址。
- [ ] **实现动态修补系统**：
    - [ ] `ClusterResourceManager` 建立 `Page -> BVH Node` 追踪映射。
    - [ ] 编写 `bvh_patch.slang` 处理逻辑。
- [ ] **多实例并发支持**：目前 `_queueA` 注入逻辑仍为单实例硬编码，需扩展为批量注入所有可见实例的根节点任务。
- [ ] **鲁棒性优化**：处理 `GlobalBVHBuffer` 的溢出保护与碎片整理。
