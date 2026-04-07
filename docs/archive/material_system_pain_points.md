# 材质系统痛点分析

> 基于 [material_system_integration.md](file:///f:/SomeEngine/docs/design/material_system_integration.md) 的现状，结合游戏开发社区通识，分析潜在痛点及应对策略。

---

## 1. Shader Permutation 爆炸

每个材质特性（法线贴图、自发光、透明度、双面、顶点色等）都可能衍生一个 shader 变体，N 个 feature flag 最坏 2^N 个 PSO。

- **当前状态**：MVP 仅 1 个 ShaderType（StandardPBR），暂安全
- **风险**：后续 ShaderType 增长时爆发
- **缓解策略**：Ubershader + 动态分支、编译时裁剪、分层变体 key

**结论**：确实存在这么多需求（多着色模型、多特性组合），需要提前考虑变体管理策略。

---

## 2. 资源绑定与提交开销（SRB → Bindless 梯度）

### 当前已做到的

当前 Shade Binning 流程（Count → Reserve → Scatter → MaterialShade）已经实现了按材质分桶，**每个 MaterialID 只有一次 `DispatchComputeIndirect`**，这已经是最少的提交次数。相比 Unity BRG（合并传统 draw call），我们的 Visibility Buffer + Compute Shade 路线更进一步 —— 根本没有传统 draw call。

### S0-S4 梯度

SRB 和 Bindless 并非二选一，中间存在多个演进级别：

| 级别 | 策略 | Bin Key | 每 ShaderType Dispatch 次数 | CPU 开销 | 实现复杂度 |
|------|------|---------|---------------------------|---------|-----------|
| **S0: 单 SRB 逐材质 Uniform** | 当前实现。共用 1 个 SRB，循环中只改 uniform 的 MaterialID | `MaterialID` | N（每材质 1 次） | Map/Unmap × N | ★☆☆☆ |
| **S1: 多 SRB 切换** | 每个 Material 独立 SRB（含材质纹理），Dispatch 前切 SRB | `MaterialID` | N（每材质 1 次） | CommitSRB × N | ★★☆☆ |
| **S2: 分层 SRB** | 管线资源设 Static，材质参数设 Dynamic，切换时只换 Dynamic 层 | `MaterialID` | N（每材质 1 次） | 只切小 SRB | ★★★☆ |
| **S3: Bindless 纹理 + 参数表** | 所有纹理进 bindless heap，材质参数存 StructuredBuffer 查表 | **`ShaderTypeID`** | **1** | 极低 | ★★★☆ |
| **S4: 完全 Bindless** | 纹理、采样器、Buffer 全部 bindless | **`ShaderTypeID`** | **1** | 极低 | ★★★★ |

### Bin Key 的转变（S2→S3）

S0-S2 与 S3-S4 之间存在**质变**：Bin Key 从 `MaterialID` 变为 `ShaderTypeID`。

- **S0-S2**：每个材质需要独立 Dispatch（切 SRB/Uniform），所以必须按 MaterialID 分桶
- **S3-S4**：同一 ShaderType 下所有材质在一次 Dispatch 内通过查表区分，按 ShaderTypeID 分桶即可

```slang
// S0-S2: Bin Key = MaterialID
uint materialID = InstanceHeaders[req.InstanceID].MaterialID;
InterlockedAdd(BinCounts[materialID], 1, dummy);   // bin 数 = 活跃材质数（可达数百）

// S3-S4: Bin Key = ShaderTypeID
uint shaderTypeID = InstanceHeaders[req.InstanceID].ShaderTypeID;
InterlockedAdd(BinCounts[shaderTypeID], 1, dummy);  // bin 数 = ShaderType 数（通常 < 10）
```

S3 下 shade shader 需自行从 VisBuffer → VisibleClusters → InstanceHeader 重新读取 MaterialID 来查参数表，而 S0-S2 中这一步是冗余的（外层循环已确定 MaterialID）。

> [!NOTE]
> **Cache trade-off**：S0-S2 按 MaterialID 分桶时，同材质像素在内存中连续，纹理采样 cache 命中率更高。S3 按 ShaderType 合并后不同材质像素混在一起，但实践中影响通常很小——像素的空间局部性主要来自屏幕位置而非材质分组。

### 当前不完善之处

| 问题 | 现状 | 影响 |
|------|------|------|
| 逐材质 Map/Unmap uniform | 每个 matID 做一次 `MapBuffer(Discard)` + `UnmapBuffer` | CPU 开销随材质数线性增长 |
| SRB 只有一个 | 所有材质共用 `_shadeSRB`，无法区分材质纹理 | 不同材质不同纹理时不可用 |
| 缺少间接查表 | MaterialID 通过 uniform 传入，shader 不查参数表 | 无法做 per-material 参数差异化 |

### 建议演进路径

```
当前 S0 → MVP 阶段走 S1 → 材质数量增长后走 S2 → 性能瓶颈明确时走 S3
```

S2→S3 是最大的架构跳跃（Bin Key 变更 + Bindless 基础设施），但 Binning 架构天然适合：shader 已经通过 MaterialID 索引像素，加一层参数查表改动不大，主要工作在 C# 侧建立 bindless descriptor 管理和参数 buffer。

---

## 4. Per-Instance 参数覆盖

同一材质不同实例需要不同参数（如颜色 tint、破损程度），需要 GPU 侧 per-instance 参数堆。

- **寻址框架已预留**：`GpuInstanceHeader` 中已有 `MetadataOffset` 和 `MetadataCount` 字段，shader 侧 `cluster_structures.slang` 也有对应定义
- **链路未接通**：
  - `InstanceSyncSystem` 中硬编码 `MetadataOffset = 0, MetadataCount = 0`
  - 缺少 metadata payload buffer（per-instance 参数的实际 GPU 存储）
  - Shader 侧无消费者读取 `MetadataOffset` 查表
  - ECS 侧无 metadata payload 组件
- **方向**：需实现存储层（metadata heap buffer）+ 生产链路（ECS → Sync → GPU）+ 消费链路（shader 查表读取）

---

## 5. 材质系统与渲染管线的耦合（多 Pass）

### 5.1 两种需求来源

**A. 管线内置 Pass**：引擎默认需要的渲染阶段，材质必须配合提供对应 shader 变体。
- Shadow Map 生成（ShadowCaster pass）
- Depth Prepass / Z-Prepass
- Motion Vector 输出
- G-Buffer 写入（如果混合 Deferred 路径）

**B. 用户自定义多 Pass**：美术/程序主动要求的额外渲染效果。
- 描边 / Outline pass（角色轮廓）
- 自定义后处理叠加（毛发、皮肤次表面散射的多 pass 方案）
- 特殊光照累积 pass

### 5.2 结果复用级别

根据新 pass 与主渲染的关系，复用程度不同：

| 级别 | 描述 | 可复用内容 | 典型场景 |
|------|------|-----------|---------|
| **L0: Pixel Overlay** | 只需对已有像素叠加不同的 shade 逻辑 | VisBuffer + Binning 结果 + PixelCoordBuffer 全部复用，只换 PSO/SRB 重新 Dispatch | 多光源累积、后处理 overlay、debug 可视化 |
| **L1: 重新光栅化** | 剔除结果有效，但需要重新走光栅化（不同投影/不同几何输出） | BVH 遍历 + 剔除结果可复用，需重新 rasterize 生成新 VisBuffer 并重新 Binning | Shadow Map（同一可见集，不同投影矩阵）、Cube Map Face 渲染 |
| **L2: Multi-View 共享遍历** | 剔除结果不可直接用（不同 view），但可通过 Multi-View 一次遍历多个视角 | BVH 遍历共享，避免多次 Dispatch；每个 view 各自的 VisBuffer 和 Binning | 级联阴影（CSM）多 split、VR 双眼、反射 probe 六面 |
| **L3: 完全独立** | 与主渲染无任何共享关系 | 无复用，独立的遍历 → 剔除 → 光栅化 → 着色全流程 | 独立场景渲染（如 UI 3D 预览、画中画）|
| **L4: 材质驱动的额外几何** | 材质本身要求额外的几何 pass（非当前帧主几何） | 遍历结果可能复用，但需要不同几何处理 | 毛发 shell/fin 生成、描边膨胀顶点 |

### 5.3 设计要点

- 材质应支持**多 Pass Slot 声明**：一个材质可注册 `Shade`、`ShadowCaster`、`MotionVector` 等 slot，每个 slot 对应不同 PSO
- L0 级复用最高效：同一份 bin 数据多次 Dispatch 即可，是多 pass 的首选路径
- L1/L2 需要管线层面的**View 管理**：材质系统提供 shader 变体，管线负责编排 view 和资源
- Multi-View（L2）可显著降低 CSM 等场景的 CPU/GPU 开销，值得作为管线层功能优先设计

---

## 6. Shader 编译与热重载

修改材质 shader → 需重编译 PSO → 涉及 SRB 失效和缓存刷新。在 D3D12 等低级 API 下实现可靠热重载难度较高。

- **当前状态**：未涉及
- **重要性**：程序员引擎同样需要快速迭代 shader，热重载是开发效率的关键

---

## 7. 调试与可视化

材质参数在 GPU 上难以追踪，出渲染错误不易定位。`MAX_MATERIALS = 256` 需要运行时检查。

- **当前状态**：未涉及
- **优先级**：MVP 可后补

---

## 不适用的痛点

| 痛点 | 不适用原因 |
|------|-----------|
| 材质图 / 美术友好度 | 本引擎面向程序员，无需 Material Graph |
| 材质 LOD | 通过 mesh 资产创建时为不同 LOD 赋值不同材质解决，无需材质系统内建 |
| 跨平台 descriptor model 差异 | 通过 Diligent 抽象层处理 |

---

## 优先级排序

| 优先级 | 痛点 | 理由 |
|--------|------|------|
| 🔴 P0 | 多 SRB / 纹理绑定 | 当前单 SRB 无法支持多材质核心功能 |
| 🔴 P0 | Per-instance 参数 | 基础视觉表现力依赖此功能 |
| 🟡 P1 | 多 Pass 支持 | Shadow、Depth Prepass 等必须的管线功能 |
| 🟡 P1 | Uniform 更新优化 | 逐材质 Map/Discard 在材质数量增长后成为瓶颈 |
| 🟡 P1 | Permutation 管理 | 着色模型增多时需要策略 |
| 🟢 P2 | Shader 热重载 | 开发效率，可逐步完善 |
| 🟢 P2 | 调试工具 | MVP 后补 |
