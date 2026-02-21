# Cluster BVH Culling Design

## 1. 概述
为了加速 Cluster 剔除过程，我们将从线性扫描所有 Cluster 切换为基于 BVH (Bounding Volume Hierarchy) 的层次化剔除。
该方案通过构建 Cluster 的 BVH 树，在 GPU 上进行层次遍历，利用父节点的包围盒和 LOD 信息快速剔除大片不可见或误差满足要求的 Cluster，从而减少每帧需要处理的 Cluster 数量。

## 2. 数据结构设计 (Data Structures)

### 2.1 BVH 节点定义 (ClusterBVHNode)
所有节点（内部节点和叶子节点）大小一致，存储在持久化的 `StructuredBuffer` 中。

```cpp
struct ClusterBVHNode
{
    // 空间包围盒 (AABB) - 用于视锥体剔除
    float3 BoundMin;
    float3 BoundMax;

    // LOD 包围球 (Sphere) - 用于 LOD 误差评估
    // 对于叶子节点：存储 ParentLODBound (Parent Center, Parent Radius)
    // 对于内部节点：存储所有子节点 LOD Sphere 的包围球
    float4 LODSphere; // xyz: Center, w: Radius

    // LOD 误差 (Error)
    // 对于叶子节点：存储 ParentError
    // 对于内部节点：存储所有子节点的最大 Error
    float  LODError; 

    // 子节点指针 / Cluster 引用 (Union)
    // 内部节点：指向 BVH Buffer 中的子节点偏移和数量
    // 叶子节点：指向 PageIndex + ClusterStart + ClusterCount
    uint   ChildPointer; 
    uint   ChildCount;   
    
    // Padding to 64 bytes if needed, or kept compact (44 bytes currently)
    // 建议对齐到 16 字节倍数，例如 48 字节或 64 字节
    float  _Pad0; 
};
```

#### 字段编码详解
*   **ChildPointer & ChildCount**:
    *   **内部节点 (Internal Node)**:
        *   `ChildPointer`: 子节点在全局 BVH Buffer 中的起始索引 (Index)。
        *   `ChildCount`: 子节点数量 (Fanout)。
    *   **叶子节点 (Leaf Node)**:
        *   `ChildPointer`: 编码 `PageIndex` 和 `ClusterStart`。
            *   例如：`PageIndex` (高 20 位) | `ClusterStart` (低 12 位)。
            *   注：假定每个 Page 的 Cluster 数量不超过 4096。
        *   `ChildCount`: 该 Leaf 包含的 Cluster 数量。

### 2.2 运行时资源 (Runtime Resources)

1.  **Global BVH Buffer**: `StructuredBuffer<ClusterBVHNode>` (Persistent)
    *   存储场景中所有静态物体的 Cluster BVH 节点。
    *   动态物体可能需要单独的结构或每帧重建/更新（暂时聚焦静态）。
2.  **Page Offset Table**: `Buffer<uint>` (Dynamic)
    *   映射 `PageIndex` -> `ByteOffset`。
    *   由于 Cluster Page 是流式加载的，其在 `ByteAddressBuffer` 中的位置每一帧可能不同。
    *   叶子节点解包出 `PageIndex` 后，查此表获取 Page 的实际 GPU 地址。
3.  **Work Queues**: `RWStructuredBuffer<uint>` (Ping-Pong)
    *   用于存储待处理的节点索引。
    *   `Queue_Current`: 当前层级待处理节点。
    *   `Queue_Next`: 下一层级产生的子节点。

## 3. 资产管线 (Asset Pipeline)

在 `ClusterBuilder` 中增加 BVH 构建步骤：

1.  **Cluster 生成**: 使用 `meshopt` 生成 Clusters。
2.  **BVH 构建**:
    *   **输入**: 一组 Clusters (每个 Cluster 有 Bounds, LODError, ParentLODError)。
    *   **策略**: Bottom-up 或 Top-down 构建。
        *   将 Clusters 分组为 Leaf Nodes (例如 8-16 个 Clusters 一组)。
        *   Leaf Node 的 `LODSphere` = 这一组 Cluster 的 ParentLODSphere (即产生这些 Cluster 的上一级 DAG 节点的 Sphere)。
        *   Leaf Node 的 `LODError` = ParentError。
        *   Internal Node 递归聚合子节点的 Bounds 和 Max Error。
    *   **序列化**: 将构建好的 BVH 线性化并保存。

## 4. GPU 遍历算法 (Traversal Algorithm)

采用层级调度 (Level Scheduling) 的方式进行遍历，避免单个 Shader 里的深度递归栈溢出，并提高并行度。

### Pass 1: 实例剔除 (Instance Culling) & Root 初始化
*   **输入**: 所有 Instances。
*   **逻辑**:
    *   对每个 Instance 进行视锥体剔除。
    *   如果可见，将其对应的 BVH Root Node Index 加入 `Queue_Next` (作为第 0 层)。
    *   或者直接加入 `Queue_Level_0`。

### Pass 2: BVH 遍历 (Traversal Loop)
在 CPU 端循环调度 Compute Shader，直到队列为空或达到最大深度。

*   **输入**: `Queue_Current` (Node Indices), `BVHBuffer`, `PageOffsetTable`。
*   **输出**: `Queue_Next` (Node Indices), `VisibleClusterList` (Draw Args)。
*   **逻辑**:
    1.  获取当前线程要处理的 Node Index。
    2.  加载 Node 数据。
    3.  **剔除检查 (Culling)**:
        *   **Frustum Culling**: 使用 `BoundMin/Max` 检查是否在视锥体内。
        *   **LOD Culling**:
            *   计算 `Dist = distance(Camera, Node.LODSphere.Center) - Node.LODSphere.Radius`.
            *   如果是 Leaf Node:
                *   这里存储的是 `ParentError`。检查 `ParentError / Dist > Threshold` ?
                *   如果满足 (Parent 误差够大，需要细分)，则说明该 Leaf 下的 Clusters **可能** 需要被渲染（进入 Cluster 级判断）。
                *   如果 `ParentError / Dist <= Threshold`，说明 Parent 已经足够精细，不需要展开到这一层？(注意 Cluster LOD 通常是切图逻辑：找误差满足要求的**最粗**层级)。
                *   **Cluster LOD 逻辑修正**: 
                    *   通常我们寻找 `Error <= Threshold` 且 `ParentError > Threshold` 的节点。
                    *   BVH 遍历用于快速找到这些节点。
                    *   如果 Node 的 `LODError / Dist <= Threshold`: 该节点足够精细，可以渲染（如果是 Cluster/Leaf），或者不需要继续细分（如果是 Internal，代表其下的所有 Cluster 都太精细了？不，通常是 Error 越往下越小）。
                    *   **Nanite/Cluster 逻辑**: 总是尝试渲染满足误差的**最大** Cluster。
                    *   遍历时：如果当前节点 `Error / Dist > Threshold`，说明当前节点误差太大，不能渲染，必须**分裂**（访问子节点）。
                    *   如果 `Error / Dist <= Threshold`，说明当前节点误差达标，**可以渲染**。
                        *   如果是 Leaf，将其加入渲染列表。
                        *   如果是 Internal，这代表该 Internal 节点覆盖的区域整体误差达标？这在 Cluster 渲染中比较少见，通常精确到 Cluster 粒度。但如果 Internal Node 代表一个聚合体（Imposter），可以渲染 Imposter。如果仅仅是加速结构，则必须遍历到 Leaf。
                        *   **假设**: 纯加速结构，必须遍历到 Leaf。
                        *   **修正**: 遍历仅做视锥剔除。LOD 检查用于判断是否需要**展开**。
                        *   但在 Cluster 渲染中，LOD 决定了我们是否渲染该 Cluster。
                        *   **Leaf Node Check**:
                            *   Leaf 存储了 `ParentError`。
                            *   如果 `ParentError / Dist <= Threshold`: 说明 Parent 就已经足够好，不需要切分到这个 Leaf Group。剔除（Implicitly culled by parent selection, but here we traverse top-down）。
                            *   Wait, standard top-down: start root. If root error ok, draw root. Else children.
                            *   But here we have a BVH over a list of Clusters (which are the finest or intermediate levels?).
                            *   User says: "Leaf node stores parent lod bound and parent error (so cluster culling doesn't need these)".
                            *   这意味着 Leaf Node 代表了一组 Clusters，这组 Clusters 的 Parent 是同一个。
                            *   如果 `ParentError / Dist <= Threshold`，说明 Parent 这一层级就够了，不需要这一组 Clusters。所以剔除该 Leaf（不处理其包含的 Clusters）。
                            *   如果 `ParentError / Dist > Threshold`，说明 Parent 误差太大，必须使用这一组 Clusters（或更细的）。
                            *   进入 Leaf 内部，对每个 Cluster：
                                *   检查 `Cluster.Error / Dist <= Threshold`。如果满足，则渲染该 Cluster。
                                *   如果 `Cluster.Error` 还不满足？说明需要更细的？但如果这是最细级，只能渲染它。
    4.  **分类处理**:
        *   **Internal Node**:
            *   如果通过剔除（Frustum 可见 && LOD 需要细分），将所有子节点索引写入 `Queue_Next`。
        *   **Leaf Node**:
            *   如果通过剔除（Frustum 可见 && Parent 误差大），则说明需要处理内部 Clusters。
            *   计算 Page 偏移: `PageOffset = PageOffsetTable[Node.PageIndex]`.
            *   遍历该 Leaf 的所有 Clusters (Loop `ChildCount`):
                *   Cluster 自身 Frustum Culling。
                *   Cluster 自身 LOD Check (`Error / Dist <= Threshold`)。
                *   如果是，Append 到 `VisibleClusterList`。

### Pass 3: 队列交换与循环
*   `Queue_Current` 清空 (Reset Count)。
*   Swap(`Queue_Current`, `Queue_Next`)。
*   **Indirect Dispatch**: 使用 `DispatchIndirect` 发起下一轮遍历。
    *   Dispatch 参数 (ThreadGroupCount) 由上一轮 Shader 在填充 `Queue_Next` 时原子计数计算得出，并写入 `IndirectArgsBuffer`。
    *   **无需 CPU 回读计数**，从而避免流水线停顿。
    *   CPU 端仅需循环固定次数（最大深度）。如果某一层队列为空，Indirect Dispatch 的 ThreadGroupCount 为 0，GPU 将自动跳过执行，开销极小。

## 5. 关键实现细节 (Implementation Details)

### 5.1 动态 Page Offset 计算 (Dynamic Page Offset Resolution)

由于 Cluster 数据是以 Page 为单位流式加载的，每个 Page 在 GPU 的 `GlobalPageBuffer` (巨大的 ByteAddressBuffer) 中的物理地址在每一帧都可能变化（例如加载新 Page，卸载旧 Page）。因此，BVH 的叶子节点不能存储绝对的显存地址，而必须存储逻辑上的 `PageIndex`。

#### 1. Page Table (页表)
系统维护一个全局的页表 `StructuredBuffer<uint> PageOffsetTable`，索引为 `PageIndex`，值为该 Page 在 `GlobalPageBuffer` 中的字节偏移量 (`ByteOffset`)。
*   **大小**: `MaxPages * 4 bytes`。
*   **更新**: CPU 在每帧流式加载/卸载 Page 后，更新此 Buffer。
*   **无效值**: 若 Page 未加载，值为 `0xFFFFFFFF`。

#### 2. 叶子节点编码
叶子节点的 `ChildPointer` 字段是一个 32 位整数，压缩存储了 `PageIndex` 和 `ClusterStart` (该 Node 包含的第一个 Cluster 在 Page 内的索引)。
*   **格式**: `[ PageIndex (20 bits) | ClusterStart (12 bits) ]`
    *   **20 bits**: 支持最多 $2^{20} \approx 100$ 万个 Pages。
    *   **12 bits**: 每个 Page 最多支持 $2^{12} = 4096$ 个 Clusters (通常一个 128KB Page 仅包含数百个 Cluster，远小于 4096)。

#### 3. Shader 中的计算流程
当 Compute Shader 遍历到叶子节点并判定需要绘制时：
```hlsl
// 1. 解包逻辑索引
uint packedPtr = node.ChildPointer;
uint pageIndex = packedPtr >> 12;      // 高 20 位
uint clusterStart = packedPtr & 0xFFF; // 低 12 位

// 2. 查表获取物理偏移
uint pageByteOffset = PageOffsetTable[pageIndex];

// 3. 驻留性检查 (可选，若保证 BVH 只包含已加载 Page 可跳过)
if (pageByteOffset == 0xFFFFFFFF) return; 

// 4. 计算 Cluster 的实际 GPU 地址
// Cluster 数据紧跟在 PageHeader 之后
// Address = PageOffset + SizeOf(PageHeader) + ClusterStart * SizeOf(GPUCluster)
// 注意：ClusterStart 是索引，需要乘以 Cluster 结构体大小
uint clusterByteOffset = pageByteOffset + PAGE_HEADER_SIZE + clusterStart * GPU_CLUSTER_STRIDE;

// 5. 读取 Cluster 数据
GPUCluster cluster = LoadCluster(GlobalPageBuffer, clusterByteOffset);
```

#### 4. 优势
*   **Zero-Copy**: 磁盘上的 BVH 数据无需修改即可直接上传（PageIndex 是静态的）。
*   **灵活性**: 允许内存碎片整理或 Page 移动，只需更新 PageOffsetTable。

### 5.2 队列管理
*   使用 `InterlockedAdd` 在 GPU 上分配队列槽位。
*   需要一个 `IndirectArgs` Buffer 来存储下一轮 Dispatch 的 ThreadGroup 数量。

## 6. 总结 (Summary)
该方案通过构建持久化 BVH 加速了 Cluster 的查找过程，避免了全量扫描。同时利用 Page Table 解决了流式加载带来的地址变化问题。
下一步任务：
1.  修改 `ClusterBuilder` 生成 BVH 数据。
2.  实现 GPU 端的 BVH 遍历 Shader。
3.  集成到 Render Graph。
