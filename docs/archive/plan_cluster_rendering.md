# Cluster Rendering & Asset Pipeline Plan (v2)

## 目标
实现基于 **Page-Based Streaming** 的高性能 Cluster Asset Pipeline。
核心设计理念：**Disk-to-GPU Zero Copy**。
- 数据以 **Page (128KB)** 为单位组织，磁盘数据直接作为二进制 Blob 上传至 GPU。
- **BVH 加速剔除**：构建 Cluster BVH 树，在 GPU 上进行层次遍历剔除，取代全量线性扫描。
  - 详见 [Cluster BVH Culling Design](design/cluster_bvh_culling.md)。
- **SoA 布局**：Page 内部将 Cluster 元数据、位置、属性、索引分离，最大化 Culling 阶段的缓存命中率。

## 1. 数据结构设计 (Memory Layout)

### 1.1 GPU Page Layout (Binary Blob)
一个 Page 是一个连续的内存块，直接映射到 GPU `ByteAddressBuffer`。

```text
[ Page Header       ] (Fixed Size)
[ Cluster List      ] (Array of GPUCluster)  <-- Hot Data (Culling)
[ Position Stream   ] (Array of u16/u8)      <-- Cold Data (Vertex Fetch)
[ Attribute Stream  ] (Array of PackedAttr)  <-- Cold Data (Rasterization)
[ Index Stream      ] (Array of u8/u16)      <-- Cold Data (Topology)
```

#### Page Header
```cpp
struct GPUPageHeader {
    uint ClusterCount;
    uint TotalVertexCount;
    uint TotalTriangleCount;
    uint _Pad0;

    // Byte Offsets relative to Page Start
    uint ClustersOffset;
    uint PositionsOffset;
    uint AttributesOffset;
    uint IndicesOffset;
};
```

#### Cluster Struct (Metadata)
```cpp
struct GPUCluster {
    // Bounds (16 bytes)
    float3 Center;
    float  Radius;

    // LOD Logic (8 bytes)
    float  LODError;       // Error of this cluster
    float  ParentLODError; // Error of parent (for cut selection)

    // Local Offsets & Counts (8 bytes)
    // Note: Offsets are INDICES, not bytes. 
    // AbsoluteByteOffset = Page.StreamOffset + Cluster.Start * Stride
    uint   VertexStart;    // Index of the first vertex in Page Position/Attr Stream
    uint   TriangleStart;  // Index of the first triangle index in Page Index Stream
    
    // Counts (4 bytes)
    uint8  VertexCount;    // Num vertices (max 255)
    uint8  TriangleCount;  // Num triangles (max 124)
    uint8  _Pad0;
    uint8  _Pad1;
}; // Total: 36 bytes -> Align to 48 bytes? Or pack tighter.
```


## 2. 资产管线 (Asset Importer)

位于 `SomeEngine.Assets`。

### 流程
1.  **Import**: 读取原始模型。
2.  **Clusterization (MeshOptimizer)**:
    *   使用 `meshopt_buildMeshlets` 将模型切分为小块。
    *   计算 Cluster Bounds (Center, Radius)。
3.  **LOD Generation**:
    *   构建简化层级，计算 `LODError` 和 `ParentError`。
4.  **Page Generation**:
    *   将 Cluster 分组填入 128KB Pages。
    *   **Quantization**:
        *   Position: 归一化到 Cluster Bounds，量化为 `u16` 或 `u8`。
        *   Normal/Tangent: Octahedral Packing (32bit)。
        *   UV: Half float 或 unorm16。
    *   **Layout Assembly**:
        *   构建 `GPUPageHeader`。
        *   写入 `GPUCluster[]`。
        *   写入 `PositionStream` (SoA)。
        *   写入 `AttributeStream` (SoA)。
        *   写入 `IndexStream`。
5.  **Serialization**: 生成 FlatBuffers 文件。

## 3. 运行时 (Runtime)

### GPU Data Management
*   **Page Buffer**: 一个巨大的 `ByteAddressBuffer` (Bindless Heap)。
*   **Page Table**: 记录每个 Page 在 Buffer 中的偏移量。

### Rendering Pipeline (Compute)
1.  **Culling Shader**:
    *   Input: `PageID`, `CameraUniforms`.
    *   Load `PageHeader`.
    *   Parallel Loop over `ClusterCount`.
    *   Load `GPUCluster`.
    *   **Check**:
        1.  Frustum Cull (using `Center`, `Radius`).
        2.  LOD Cull (`ParentError > Threshold && LODError <= Threshold`).
        3.  Hiz Occlusion Cull.
    *   **Output**: `DrawArgument` (Indirect Draw) or `VisibilityBuffer` commands.
2.  **Rasterizer (Mesh Shader / VS)**:
    *   Load Vertex Position: `PageBase + PositionsOffset + (Cluster.VertexStart + VertexID) * stride`.
    *   Decompress Position using `Cluster.Center` and `Cluster.Radius`.

## 4. 任务计划

- [ ] **Schema**: 更新 `mesh_asset.fbs`。
- [ ] **Importer**: 实现 Page Builder 和 Quantization 逻辑。
- [ ] **Runtime**: 实现 `PageStreamer` 和 GPU Buffer 管理。
- [ ] **Shader**: 编写支持 Page 结构的 Culling Compute Shader。