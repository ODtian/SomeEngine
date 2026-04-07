# RenderGraph 深度解耦与架构演进重构计划

## 1. 核心目标与架构准则
根据对现代渲染管线（Frostbite, SRP, RDG 等）最佳实践的分析，本次重构的核心目标是彻底剥离逻辑系统对底层 GPU 显存（`IBuffer` / `ITexture`）的直接控制权，将其全面交由 RenderGraph 托管，以实现高效的内存别名复用、精准的状态追踪与自动化 Barrier 推导。

**引擎宏观数据流向：严格的单向解耦**
- **ECS (前端 / 权威数据源)**：负责逻辑世界的推演。对 GPU 与 RenderGraph 毫无感知，仅产出本帧的 CPU 侧业务数据快照（如 Transform 数组、材质参数）。
- **Extraction (提取与转换层)**：作为生产者与消费者的桥梁，将 ECS 产生的逻辑数据拷贝/转换为渲染管线能够理解的后端渲染数据，实现领域隔离。
- **RenderGraph (后端 / 调度中心)**：接管提取好的数据，唯一合法的大管家，负责物理显存的生命周期管理、重叠分配、拓扑排序和自动安插 Barrier，以极致性能执行绘制。不懂具体业务。

**架构铁律（渲染内部三角分工）：**
- **RenderGraph（执行引擎）**：仅负责根据声明的图依赖推导执行顺序与内存别名，对具体 Pass 逻辑黑盒。
- **RenderPass（打工节点）**：纯粹的无状态执行闭包。禁止在内部调用 `CreateBuffer` 或任何底层资源创建，必须通过 Graph Builder 瞬时申请所需句柄。
- **RenderFeature / DataManager（大脑与记忆）**：负责跨越边界从 ECS 提取数据、维护脏标记、持有跨帧资源的“句柄”（非原生显存），并在图构建期注入具体 RenderPass 序列。

---

## 2. 实施路线图 (Implementation Plan)

### Phase 1: 基础设施建设 (Infrastructure)
目标：引入持久化资源的容器与隔离层，强化 RenderGraph 的跨帧资源接入能力。

1.  **新增 `RGPooledBuffer` / `RGPooledTexture`**：
    - 作为跨帧持有的安全句柄，包装底层的 `IBuffer`/`ITexture`。
    - 记录 `LastKnownState`，为跨帧 Barrier 推导提供依据。
2.  **新增 `RGResourcePool`**：
    - 作为底层显存真正的所有者，提供 `Allocate` API。
    - 未来可扩展基于 Hash 和 Desc 的空闲内存块复用。
3.  **扩充 RenderGraph API**：
    - `RegisterHistoryBuffer`：在帧起点导入持久化句柄及其状态。
    - `QueueHistoryExtraction`：在帧终点回调更新跨帧句柄的最新状态。
    - `MapBuffer<T>` / `UpdateBuffer`：在 `RenderGraphContext` 中暴露安全的数据写入接口，屏蔽底层 Context 直接操作。

### Phase 2: 管线系统解耦验证 (Pipeline Migration)
目标：拿现有的 `ClusterPipeline` 及其附庸作为试点，彻底消灭游离在 Graph 之外的 GPU 调用。

1.  **动态瞬态资源改造**：
    - 废除 `ClusterPipeline` 中的 `_cullingUniformBuffer`、`_drawUniformBuffer` 成员。
    - 改为在 `AddToRenderGraph` 期间通过 `graph.CreateBuffer()` 动态创建。
    - 插入独立的 `UploadUniformsPass` 完成 CPU 到 GPU 的数据更新。
2.  **复杂 Immediate Dispatch 改造（核心痛点）**：
    - **现状**：`ClusterResourceManager.ExecutePatchBVHLeafNodes` 直接 `CreateBuffer`、Map、`DispatchCompute`。
    - **重构**：将其拆解。CPU 侧只积累需要 Patch 的列表（`PendingPatch`）。在图构建时，动态申请 `PatchNodeIndices` Buffer，插入正规的 `ClusterBVHPatchPass`。利用 Graph 在该 Pass 和后续的 TraversePass 之间自动推导并插入 `Transition`。

### Phase 3: ECS 前端与 RG 后端的界限划定 (Data Extraction 层引入)
目标：确立 ECS 为逻辑侧权威数据源，RenderGraph 为纯粹的后端执行器，规范化两者之间的数据流向与隔离。

1.  **确立 ECS 的纯 CPU 边界**：
    - 剥夺 ECS System（以 `InstanceSyncSystem` 为例）直接维护和操作底层 `IBuffer` / `ITexture` 的权利。ECS 的逻辑计算（如 Update）不再感知任何 GPU 或渲染图相关概念。
    - 使其 Update 逻辑仅在 CPU 内存中计算和生成本帧的逻辑数据快照（例如活跃 `Transforms` 的数组或 `Span`）。
2.  **引入 Extraction (数据提取) 机制**：
    - 在 ECS Tick 完成后与 RG Setup 开始前，增加一个清晰的 Extraction（提取）阶段。
    - 由渲染侧的 `DataManager`（如 `InstanceDataManager`）从 ECS 提取本帧需要渲染的数据，跨越“逻辑-渲染”边界，并转换为渲染所需要的数据结构。
3.  **统一 Upload 范式**：
    - **瞬态数据（如每帧可见的 Instance 数据）**：提取层将数据交给管线，管线在图中动态创建 Transient Buffer，插入正规的 `UploadPass` 执行数据上传，用完即毁。
    - **持久化局部更新数据（如大世界 BVH、全局材质）**：渲染层持有 `RGPooledBuffer`，依据提取阶段得到的脏区域（Dirty Ranges），在构建 RenderGraph 时注册历史资源并插入增量更新的 `UploadPass`。

### Phase 4: 历史状态对齐与 Barrier 收尾 (M1 目标前置准备)
目标：确保资源状态在帧首尾闭环。

- 在管线最后，使用 `QueueHistoryExtraction` 将 `PageHeap`、`GlobalBVH` 等资源本帧被改变的最终状态写回 `RGPooledBuffer.LastKnownState`。
- 验证第二帧开始时，`RegisterHistoryBuffer` 能够提供正确的 `InitialState` 给 RenderGraph 的自动 Barrier 算法。

---

## 3. 验收标准
1. 全局搜索 `Device.CreateBuffer` 和 `ImmediateContext.UpdateBuffer`，确保它们**绝不会**出现在 Pass 的 `Execute` 回调之外或 `RGResourcePool` 之外。
2. 彻底移除 `ClusterResourceManager` 初始化的底层 GPU 强绑定。
3. `ClusterBVHPatch` 和 `HiZBuild` 流程全部被纳入 RenderGraph 的节点视图（DAG）中可视化，并有正确的依赖线条连接。
