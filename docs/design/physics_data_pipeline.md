# 物理系统数据通路设计

## 1. 问题概述

物理系统涉及大量 GPU 解算（屏幕空间查询、粒子模拟）和 CPU 解算（刚体、关节），核心挑战是搭建 CPU↔GPU、GPU↔GPU 的无缝数据通路，同时与渲染系统保持零耦合。

### 数据流方向

| 方向 | 典型场景 | 延迟容忍 |
|------|---------|---------|
| CPU → GPU | 约束参数、力场、新增/删除物体、CPU 碰撞体上传 | 低（每帧批量） |
| GPU → GPU | 物理解算输出 → 渲染输入（Transform）、碰撞中间结果 | 极高（零拷贝） |
| GPU → CPU | 屏幕空间拾取、碰撞事件回调、关节反馈 | 中（1-2帧延迟） |

---

## 2. 核心抽象：GpuDataBus

与渲染无关的 GPU 数据总线，作为所有子系统间数据交换的中枢。

```
┌──────────┐     ┌─────────────┐     ┌──────────┐
│ Physics  │────▶│  GpuDataBus │◀────│ Render   │
│ System   │◀────│             │────▶│ System   │
└──────────┘     │  ┌───────┐  │     └──────────┘
                 │  │Slots  │  │
┌──────────┐     │  │───────│  │     ┌──────────┐
│ AI /     │────▶│  │Transf.│  │◀────│ Particle │
│ Gameplay │     │  │Veloc. │  │     │ System   │
└──────────┘     │  │Forces │  │     └──────────┘
                 │  └───────┘  │
                 └─────────────┘
```

### 设计原则

1. **Slot-Based 发布/订阅**：每种数据（Transform、Velocity 等）是一个具名 Slot，生产者写入，消费者读取
2. **Double Buffer 语义**：每个 Slot 维护 Front/Back 两份 GPU Buffer，物理写 Back、渲染读 Front，帧末 Swap
3. **无类型依赖**：`GpuDataBus` 定义在公共基础层（`SomeEngine.Core`），物理和渲染都只依赖此接口
4. **RenderGraph 兼容**：Slot 中的 Buffer 可被 Import 到 RenderGraph，参与自动屏障管理

### 接口设计

```csharp
// SomeEngine.Core —— 不依赖 Render
public interface IGpuDataBus
{
    SlotHandle RegisterSlot(SlotDesc desc);
    IBuffer GetWriteBuffer(SlotHandle slot);   // Back Buffer
    IBuffer GetReadBuffer(SlotHandle slot);    // Front Buffer
    void SwapBuffers();                        // 帧末翻转
}

public struct SlotDesc
{
    public string Name;
    public uint Stride;
    public uint MaxElements;
    public SlotFlags Flags;
}

[Flags]
public enum SlotFlags
{
    None = 0,
    ReadbackEnabled = 1 << 0,  // 维护 Staging Buffer 用于 GPU→CPU 回读
    AllowResize     = 1 << 1,
    Persistent      = 1 << 2,  // 跨帧不清除（增量更新）
}
```

---

## 3. 数据流路径

### 3.1 CPU → GPU：参数上传

```
ECS World → 脏标记变化量收集 → Ring Buffer (CPU-visible) → CopyPass → GPU Slot Buffer
```

- Ring Buffer 避免每帧分配；只传变化量（Dirty Flag）
- 可走 Copy Queue 异步 DMA，不阻塞 Graphics/Compute

### 3.2 GPU → GPU：物理结果直通渲染

零拷贝、零 CPU 参与，通过 Double Buffer Swap：

```
帧 N:   Physics → 写 Buffer[1](Back)  |  Render → 读 Buffer[0](Front)
帧 N+1: Physics → 写 Buffer[0](Back)  |  Render → 读 Buffer[1](Front)
```

物理和渲染永远不同时访问同一个 Buffer，天然无同步冲突。帧间一帧延迟（行业标准做法）。

**同帧直通路径**（Zero-Latency，如摄像机碰撞响应）：不走 Double Buffer，通过 RenderGraph 依赖图保证执行顺序 + UAV→SRV 屏障。

### 3.3 GPU → CPU：异步回读

```
GPU Slot Buffer → ReadbackPass → Staging Buffer → Fence 完成后 Map → CPU
```

```csharp
public interface IGpuReadback
{
    ReadbackFuture RequestReadback(SlotHandle slot, uint offset, uint count);
}

public struct ReadbackFuture
{
    public bool IsReady { get; }
    public ReadOnlySpan<byte> GetData();
    public int FrameLatency { get; }
}
```

Ring Staging Buffer（3-4 帧深度），天然 1-2 帧延迟。适用于鼠标拾取、碰撞事件、调试。

---

## 4. 与渲染系统解耦

### 依赖关系

```
SomeEngine.Core / .Gpu
  ├── IGpuDataBus, SlotHandle, IGpuReadback   ← 公共接口
  │
  ├── SomeEngine.Physics                      ← 写入 Slot
  │     └── PhysicsSolver
  │
  └── SomeEngine.Render                       ← 读取 Slot → Import 到 RG
        └── PhysicsBridge (IRenderFeature)
```

### PhysicsBridge（渲染侧适配器）

```csharp
// SomeEngine.Render —— 物理完全不知道此类的存在
public class PhysicsBridge : IRenderFeature
{
    private readonly IGpuDataBus _dataBus;

    public void AddPasses(RenderGraph rg)
    {
        var physTransforms = _dataBus.GetReadBuffer(
            _dataBus.GetSlotHandle("PhysicsTransforms"));
        var handle = rg.Import("PhysicsTransforms", physTransforms,
                               ResourceState.ShaderResource);
    }
}
```

---

## 5. 与现有 Transform 系统的集成

**现有路径无需修改。** 当前数据流：

```
ECS TransformQvvs → InstanceSyncSystem → InstanceDataManager (CPU)
    → ClusterUploadInstanceDataPass (RG) → "GlobalTransform" Buffer (GPU)
```

物理只需在 Upload 之后、BVH Traverse 之前插入一个覆盖 Pass：

```
ClusterUploadInstanceDataPass          ← 写入所有 Instance Transform (现有)
    ↓
PhysicsOverwritePass (新增 Compute)    ← 物理结果覆盖动态物体的子区间
    ↓
BVH Traverse / Cull / Draw            ← 读取最终 Transform (不变)
```

通过 `GpuInstanceHeader.DeformFlags`（已存在，当前值为 0）标记哪些 Instance 由物理驱动。

---

## 6. CPU/GPU 混合碰撞

### 6.1 CPU Collider ↔ GPU Particle

CPU 碰撞体以 Primitive List 上传 GPU：

```csharp
struct GpuColliderPrimitive {
    float4x3 InverseTransform;
    uint     ShapeType;        // Sphere=0, Box=1, Capsule=2
    uint     ShapeDataOffset;
    uint     CollisionGroup;   // bitmask
    uint     CollisionMask;    // bitmask
}
```

**Pair Group 机制**：通过 `CollisionGroup/Mask` 位掩码控制全局或局部碰撞对组。

数据流：

```
CPU 收集脏 Collider → DataBus Slot "ColliderPrimitives" → GPU Broadphase → Narrowphase → 响应
```

### 6.2 HiZ 屏幕空间碰撞

利用渲染已有的 HiZ 金字塔，让粒子与任意复杂屏幕几何碰撞，无需代理：

```
帧 N-1: Render → Depth → HiZ[N-1]
帧 N:   Particle Solver 读 HiZ[N-1] → 屏幕空间碰撞
```

现有 HiZ Ping-Pong（`HiZ_A`/`HiZ_B`）直接复用，粒子读 `hPrevHiZ` 即可。

算法要点：
1. 粒子世界坐标 → PrevViewProj 投影到屏幕空间
2. 根据粒子半径选 HiZ mip level
3. 比较粒子深度与 HiZ 深度 → 穿透即碰撞
4. 用深度差 + 屏幕法线估计碰撞响应

限制：仅可见面参与碰撞（背面/被遮挡面漏碰）。

### 6.3 碰撞精度分级

运行时按需求切换：

| 级别 | 方式 | 成本 | 覆盖 |
|------|------|------|------|
| Level 1 | HiZ 屏幕空间 | 免费（已有） | 可见面 |
| Level 2 | Primitive List | 低（上传碰撞体） | 基本体 |
| Level 3 | SDF 采样 | 需 SDF 基础设施 | 360° 全方位 |

```csharp
enum GpuCollisionMode {
    HiZScreenSpace,
    PrimitiveList,
    SDF,
    HiZAndPrimitive,
    HiZAndSDF,
}
```

---

## 7. 碰撞体格式策略

**核心原则：不同步碰撞体格式，各用各的最优表示，只同步 Transform。**

| 用途 | 格式 | 来源 |
|------|------|------|
| CPU 物理引擎 | Convex Hull / BVH (GJK/EPA) | Asset Pipeline 离线生成 |
| GPU 粒子碰撞 | Primitive List + HiZ | 运行时上传 / 渲染产出 |
| GPU 复杂碰撞 | SDF（未来） | Asset Pipeline 离线烘焙 |

```
Asset Pipeline (离线)
    ├── CPU Collision: 凸分解 → Convex Hulls
    ├── GPU SDF: 体素化 → SDF Volume (未来)
    └── Render Mesh: Cluster/Nanite
```

运行时同步的只有 **Transform**（已有路径）和 **碰撞事件/结果**（Readback）。

---

## 8. GPU Primitive 碰撞的分支问题

不同 Primitive 类型（球/盒/胶囊）的 switch 会导致 warp divergence，但：

- 每条分支仅 3-5 条 ALU 指令
- 最坏 warp divergence 总代价 ~15 ALU，可忽略
- 现代 GPU 编译器可能优化为 predicated execution，无真实分支跳转

**结论：直接 switch，不需要额外优化。**

当碰撞对 >100K 时可选 Binning 排序消除分支；有 SDF Scene 时自然过渡到零分支。

---

## 9. SDF Scene 决策

**现阶段不单独为物理构建 SDF Scene。**

- HiZ + Primitive List 覆盖 95% 的粒子碰撞需求
- SDF 基础设施成本高（Clipmap、流式、50-200MB VRAM）
- 仅体积流体、绳索穿过复杂结构等 5% 场景真正需要 360° SDF 碰撞

**策略：延迟绑定。** 接口预留 SDF 支持（`GpuCollisionMode.SDF`），等软光追引入 SDF Scene 后物理免费复用。

---

## 10. 一帧内完整时间线

```
[CPU 阶段] (与上帧 GPU 并行)
  ├── CPU Rigid Body Solver
  │   └→ 写 DataBus "RigidTransforms" (Back)
  ├── 收集脏 Collider → Staging Upload Buffer
  └── 读取 GPU Readback (帧 N-2 的碰撞事件)

[GPU Upload] (Copy Queue)
  ├── Upload GlobalTransform (ECS 非物理实体)
  ├── Upload ColliderPrimitives
  └── Upload RigidTransforms

[GPU Compute - Physics] (可 AsyncCompute)
  ├── Particle Broadphase (空间哈希)
  ├── Particle ↔ CPU Collider Narrowphase
  ├── Particle ↔ HiZ Screen Collision (读 HiZ[N-1])
  ├── Particle Integration
  └── PhysicsOverwritePass: 合并结果 → GlobalTransform

[GPU Graphics - Render]
  ├── BVH Traverse + Cull (读 GlobalTransform)
  ├── Cluster Draw → Visibility Buffer
  ├── Depth → HiZ Build → HiZ[N] (供下帧碰撞)
  ├── Material Shade
  └── Particle Render

[GPU → CPU Readback]
  └── 碰撞事件 / 拾取结果 → Staging → 帧 N+1~2 可读
```

---

## 11. 开放问题

1. **Buffer 扩容策略**：预分配 vs Geometric Growth + 延迟释放
2. **多 Solver 写同一 Slot**：分配子区间 or 各用独立 Slot
3. **帧内物理子步进**：GPU 侧多次 Dispatch，只有最终结果写 Slot
4. **RG 内置 vs 独立 CommandList**：初期用 Double Buffer + 独立 CommandList（零耦合），RG V2 异步计算就绪后可切换
