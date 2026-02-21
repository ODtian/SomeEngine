# 渲染架构设计

## 核心架构：GPU-Driven Cluster Rendering

本引擎采用 **Nanite-like** 架构，核心理念是**数据与索引分离**、**GPU 驱动**、**流式加载**。

### 1. 数据结构设计 (Linear DAG + Acceleration Index)
为了最大化并行遍历效率，避免指针跳转带来的延迟，我们将数据重组为线性结构。

*   **Logical DAG**:
    *   描述 Cluster Group 之间的合并/简化关系 (LOD 依赖)。
    *   仅作为构建资产时的逻辑模型，**运行时不直接存储图结构**。

*   **Runtime Linear Buffer (The Flat Array)**:
    *   由于剔除条件局部性，所有dag节点可以随机访问进行剔除判断
    *   **遍历方式**: Culling Shader 只需要访问这个线性数组，并行剔除

*   **Acceleration Index (The B+ Tree)**:
    *   为了避免线性扫描整个数组，预计算一个辅助的 **B+ 树索引**。
    *   **Inner Node**: 存储对应线性数组区间的包围盒与误差范围 (Interval Bounds)。
    *   **Leaf Node**: 指向 Linear Buffer 中的具体偏移量 (Page Offset)。
    *   **用途**: 快速剔除大块不满足条件的区间，直接定位到需要检查的细节节点。

### 2. 物理 Buffer 布局
严格区分索引与几何数据：

*   **Hierarchy Index Buffer (Hot)**:
    *   存储 `Acceleration Index` (B+ 树节点) 和 `Linear DAG Nodes`。
    *   极度紧凑，仅包含 Bounds, Error, ChildOffset。
*   **Cluster Payload Buffer (Cold)**:
    *   平铺存储所有层级的几何数据 (Vertices + Indices)。
    *   仅当 LOD Cut 选中某个 Node 时，通过其 ID 访问此处。

### 3. 运行时遍历 (GPU Traversal)
核心理念：**局部性决策 (Localized Decision)**。
一个 Cluster Group 是否应该被渲染，**只取决于它自身和它父节点的误差**，与整棵树的状态无关。这使得我们可以乱序、并行地扫描所有节点。

*   **Render Condition**:
    $$ (Error_{self} \le Threshold) \land (Error_{parent} > Threshold) $$
    *   如果满足此条件，该节点即为 **LOD Cut** 的一部分，**必须渲染**。
    *   如果不满足，则该节点被剔除（要么太糊需要细分，要么太细了被父节点覆盖）。

*   **Parallel Scan**:
    *   由于决策是局部的，Compute Shader 可以直接并行扫描 **Linear DAG Buffer** 中的所有节点。
    *   不需要维护栈 (Stack) 或递归状态。
    *   **Acceleration**: 使用 B+ 树索引剔除那些肯定不满足条件的**大块区间** (例如 Error 远大于 Threshold 的粗糙区域，或远小于 Threshold 的精细区域)，只对边界区域进行精确扫描。

### 4. 渲染管线 (Render Graph Driven)

### Phase 1: GPU Culling & LOD (Compute)
1.  **Instance Culling**: 粗粒度视锥剔除。
2.  **DAG Traversal (Persistent Threads)**:
    *   遍历 Hierarchy Buffer。
    *   计算屏幕误差，决定细分 (Visit Children) 还是选中 (Emit Cluster)。
    *   **Streaming Feedback**: 如果选中的 Cluster 未加载 (Page Not Resident)，写入请求队列回传 CPU。
3.  **Cluster Culling**:
    *   对选中的 Cluster 进行 Frustum Culling 和 Occlusion Culling (Hi-Z)。

### Phase 2: Rasterization (Visibility Buffer)
*   **Hybrid Rasterizer**:
    *   **HW Raster**: 大三角形。
    *   **SW Raster (Compute)**: 微小三角形。
*   **Output**: `VisibilityBuffer` (packed `InstanceID` + `TriangleID`)。

### Phase 3: Shading (Compute)
*   **Tile-Based Binning**:
    *   扫描 VisBuffer，统计每个 8x8 Tile 的 Material ID。
    *   将像素分类到不同材质的队列中。
*   **Deferred Lighting**:
    *   根据材质队列执行光照计算 (PBR, RayTraced DI, Virtual Shadow Map)。

---

## 数据传输与 ECS
*   **Transform**: ECS `TransformQvvs` -> GPU `StructuredBuffer` (只传变化量)。
*   **Assets**: Bindless 架构，Shader 通过 ID 访问全局资源堆。

### 3. 场景加速结构 (Scene Acceleration)
为了应对海量 Instance，需要两级剔除：

1.  **Instance Culling**:
    -   简单的视锥剔除 (Frustum Culling)。
    -   使用 **Sparse Grid** 或 **Linear BVH** 进行空间索引，加速查询。
2.  **Cluster Culling**:
    -   对可见 Instance 启动 DAG 遍历。
    -   **Persistent Threads**: 使用持久线程模型在 GPU 上高效遍历树结构，避免频繁 Dispatch。

## 实现路线图

1.  **基础建设**: 集成 DiligentCore，搭建 Render Graph 框架，实现 Hello Triangle。
2.  **ECS 同步**: 实现 ECS Transform -> GPU Buffer 同步，Bindless 资源管理。
3.  **Cluster 核心 (Cluster Rendering Core)**
    *   **3.1 资产预处理**: Mesh Cluster化 (Meshlets)，LOD 简化，DAG 构建与序列化。
    *   **3.2 GPU 数据架构**: Hierarchy Index/Payload Buffer 管理，Bindless Vertex Pulling。
    *   **3.3 Compute Culling 基础**: 并行视锥剔除，Indirect Draw 参数生成。
    *   **3.4 DAG 遍历与 LOD**: Persistent Threads 遍历，LOD 误差度量与选择逻辑。
4.  **Visibility Buffer 管线 (Visibility Buffer Pipeline)**
    *   **4.1 VisBuffer 写入**: 硬件光栅化写入 Packed ID (InstanceID + TriangleID)。
    *   **4.2 材质解析**: Tile-based Material Binning，Bindless 材质数据访问。
    *   **4.3 属性插值**: Barycentric 插值获取顶点属性，准备 Shading 数据。
5.  **高级渲染特性 (Advanced Features)**
    *   **5.1 软光栅 (Software Rasterization)**: Compute Shader 处理微小三角形。
    *   **5.2 两阶段剔除 (Two-Phase Occlusion)**: 上一帧重投影剔除 + Hi-Z 构建 + 补绘。
    *   **5.3 光照系统**: Tile-based Light Culling，Virtual Shadow Map，PBR Shading。
