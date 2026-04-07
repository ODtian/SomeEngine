# Cluster BVH 修复与重构计划 (Deferred Material 架构)

基于前期的代码审查报告（`walkthrough.md`）和 Deferred Material (无 DrawBatch) 的架构设计，融合制定以下分阶段修复与功能完善计划。

核心思想：**彻底解耦实例数据与渲染批次，增强 GPU 端扁平化寻址，并补齐原设计中缺失的剔除与流式功能。**

---

## Phase 1: 实例数据结构重构与多 Mesh 修复 (Critical & Immediate)

**目标**：解决目前 `roots[0]` 硬编码导致的多 Mesh / 多实例渲染错误，并为未来的材质扩展打下扁平化的性能基础。

### 1. 实例属性存储 (QVVS 支持)
目前 `TransformSyncSystem` 维护了 `GpuTransform`，但后续需要能存储 `BVHRootIndex` 和未来的 `MaterialID`。

**改动**: 
为保持 Traverse 和 Cull pass 的最高性能，高频必选属性（如 Transform）**不走**复杂的 Metadata 间接寻址，而是采用平行的 StructuredBuffer 设计。

- 保持 `StructuredBuffer<GpuTransform>` 专门存储变换矩阵 (QVVS：Quaternion, Position, Stretch, Scale，每实例 48B)。
- 新增平行的 `StructuredBuffer<InstanceHeader>`，索引同样对应 `InstanceID`。

#### [NEW] `InstanceHeader` 组件与结构
```csharp
// C# End (InstanceMetadata.cs)
[StructLayout(LayoutKind.Sequential)]
struct GpuInstanceHeader
{
    public uint BVHRootIndex;       
    public uint MaterialID;         // Reserved for Deferred Material Binning
    public uint MetadataOffset;     // Reserved for Phase 4 (Any Property)
    public uint MetadataCount;      
}
```

### 2. 多 Mesh / 根节点寻址修复
- **Entity Component**: 新增 `MeshInstanceComponent` 记录该实例应使用的 `BVHRootIndex`。
- **System Sync**: 将 `TransformSyncSystem` 升级为 `InstanceSyncSystem`，在 `OnUpdate` 中同步上传 `GpuTransform[]` 与 `GpuInstanceHeader[]`。
- **Traverse Shader Fix**: 
  - 将 C# 端 `ClusterBVHTraversePass.Execute` 初始队列构建逻辑直接移交 GPU（或在 CPU 端读取 Header 数组），消除 `initArgs[i*2] = roots[0]` 的硬编码。
  - 在 `cluster_bvh_traverse.slang` 中，所有实例按自己的 `BVHRootIndex` 展开 BVH。

---

## Phase 2: 完善 GPU 剔除管线 (High Priority)

**目标**：补齐设计文档中要求但当前未实现的 Frustum Culling，大幅提升渲染性能。

### 1. 视锥体剔除 (Frustum Culling)
- **Shader 补全**: 修改 `cluster_bvh_traverse.slang`。
- **提取平面**: 从 `Uniforms.ViewProj` 提取 6 个视锥体平面。
- **空间变换**: 将 BVH 节点的本地 AABB 结合 `Instances[id].Transform` (QVVS 格式) 变换到世界空间。由于存在非均匀缩放和旋转，优先采用“中心点+扩展半径”的保守计算法，或变换 8 个角点重新计算 AABB。
- **剔除测试**: 增加对 AABB 和 6 个平面的内外测试，丢弃完全在视锥体外的节点。

---

## Phase 3: 异步流式加载与动态修补 (Medium Priority)

**目标**：真正实现从磁盘到 GPU PageHeap 的异步加载、淘汰机制，并连接起当前“沉睡”的 `PatchBVHLeafNodes` 代码。

### 1. 缺页反馈机制 (Page Fault Feedback)
- **Shader 写入**: 当前 `cluster_bvh_traverse.slang` 遇到 `ChildPointer == 0xFFFFFFFF` 时仅返回。修改为通过 `InterlockedAdd` 向预先绑定的 `PageFaultBuffer` 写入缺失的 global node index `GlobalNodeIdx`。

### 2. Streamer 调度器
- 新增 `ClusterStreamer.cs` 系统，每帧回读（Readback） `PageFaultBuffer`。
- 分析缺少的 Node，反向查找到对应的 PageID 及其磁盘/文件偏移。
- 发起异步文件 I/O 加载 Page 数据到内存预留池。

### 3. PageHeap 管理与回收
- 替换 `ClusterResourceManager` 中简单的 Bump Allocator。
- 引入 **Free-list Allocator** 或 **Bitset Allocator**管理 64MB (或更大) 的 `PageHeap`。
- 实现 Page 的 LRU (Least Recently Used) 或基于相机距离的淘汰策略。

### 4. 激活动态修补 (Dynamic Patching)
- 流式加载完成后，将数据 Upload 到 `PageHeap` 的新槽位。
- 获取当前被修补过的叶子节点 `PageToLeafNodes[globalPageIdx]`。
- **触发已有代码**: 调用 `ClusterResourceManager.PatchBVHLeafNodes(pageID, byteOffset, true)`，利用 `bvh_patch.slang` 并行更新所有相关叶子节点的 `ChildPointer`。

---

## Phase 4: 健壮性与进阶材质能力 (Low / Future Priority)

### 1. 缓冲溢出保护 (Overflow Protection)
- **全局检查**: 在 `ClusterResourceManager.AddMesh` 和 `AllocateHeap` 时检测容量。
- 保证 `GlobalBVHBuffer` 和 `PageHeap` 不超出显存设定上限，超出时安全降级或请求流出逻辑。

### 2. 泛型 Metadata 属性解耦 (Deferred Material Hook)
- 引入完整的 `InstanceDataHeap` (SoA Bytes Buffer) 和 `MetadataBuffer`。
- 实现针对各材质特有参数（如基础色、粗糙度、发光等）在全局缓冲中的灵活寻址：
  ```hlsl
  // 伪代码
  float4 baseColor = GetProperty_float4(header, PROP_BASECOLOR_HASH); 
  ```
- 此阶段将允许同屏出现成千上万具有不同视觉表现参数（但共享模型和主要着色逻辑）的实例，为真正无 Batch 束缚的 Deferred Material 管线铺平道路。
