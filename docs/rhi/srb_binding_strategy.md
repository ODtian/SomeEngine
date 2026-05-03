# SRB 资源绑定策略

> 基于 Diligent Engine 源码分析，结合 RenderGraph placed resource 约束，制定管线资源绑定的最优策略。

---

## 1. 变量类型语义速查

| 类型 | 绑定位置 | 绑定次数 | Commit 时开销 | D3D12 路径 |
|------|---------|---------|-------------|-----------|
| **Static** | PSO | 1次，不可改 | 最低（descriptor table） | Root Descriptor Table |
| **Mutable** | SRB | 1次/SRB（`ALLOW_OVERWRITE` 可多次） | **与 Static 相同** | Root Descriptor Table |
| **Dynamic** | SRB | 多次 | ⚠️ 每次 Commit 额外检查 GPU 地址 | Root View（逐次刷新） |

**关键差异**：Static/Mutable 在 `CommitShaderResources` 时走 descriptor table 路径；Dynamic 走 root view 路径，**即使绑定未变也有额外开销**，按 draw/dispatch 次数线性累加。

---

## 2. RenderGraph Placed Resource 约束

RenderGraph 使用 placed/aliased 资源，transient resource 共享 memory heap，导致：

- 每帧实际 `ITexture`/`IBuffer` 对象可能不同
- **Static 变量不可用**（PSO 创建时不知道具体资源）
- 为使用 Static 而放弃 aliasing（committed resource）**不值得**——用显存换 ns 级 CPU

---

## 3. 当前策略：全 Dynamic（BATCH-08 后）

所有 PSO 使用 `DefaultVariableType = Dynamic`，依赖 Diligent 隐式反射（不创建显式 `IPipelineResourceSignature`）。

### 理由

- 架构简单：无需手写 signature/layout，无需 sig 缓存
- PSO + SRB 创建快：隐式签名由引擎自动推导
- 运行时绑定统一：per-dispatch `Set()` + `CommitShaderResources`
- 对 RenderGraph placed resource 天然兼容

### Immutable Sampler

`MaterialSampler` 作为 `ImmutableSamplerDesc` 烘入 `PipelineResourceLayoutDesc`，运行时零 sampler 描述符开销。对应 shader 中所有 `SamplerState MaterialSampler` 声明。

### SRB 管理

| pass 类型 | SRB 来源 | 生命周期 |
|-----------|---------|---------|
| 材质着色（`ClusterMaterialShadePass`） | `MaterialPSOGroup.SRB`（per-group） | 随 group rebuild |
| 通用 compute pass | `SRBPool.Rent(pso)` / `SRBPool.Return(srb)` | 帧内借还 |

---

## 4. 关键优化标志

### `SetShaderResourceFlags.AllowOverwrite`

材质 `ShaderParamBag.ApplyTo()` 使用此标志覆写已绑定的纹理变量。

### `SHADER_VARIABLE_FLAG_NO_DYNAMIC_BUFFERS`

Dynamic 变量若绑定的不是 `USAGE_DYNAMIC` buffer，加此标志可跳过 GPU 地址刷新。

### `DRAW_FLAG_DYNAMIC_RESOURCE_BUFFERS_INTACT`

连续 Dispatch 间 dynamic buffer 未被 Map 时使用，跳过 root view 刷新。**注意**：当前逐材质 `MapBuffer(Discard)` 导致不可用。

### `SetBufferOffset`

对 constant/structured buffer 设置动态偏移，无需重新 `CommitShaderResources`。用于替代逐材质 Map/Unmap。

---

## 5. 资源绑定演进路径

### 当前 → S1：变量类型精细化

- 纹理等绑定稳定的变量改为 Mutable + `ALLOW_OVERWRITE`
- 只有 Uniforms 保留 Dynamic
- 用大 uniform buffer + `SetBufferOffset` 替代逐材质 Map/Unmap

### S1 → S2：多 SRB 切换

- 每材质创建独立 SRB，绑定材质纹理
- 管线公共资源作为 Mutable 绑定在每个 SRB
- Dispatch 前切 SRB

### S2 → S3：Bindless

- Bin Key 从 material slot 派生提交 → `ShaderTypeID`，Dispatch 次数 N → 1
- 所有纹理进 bindless heap，材质参数存 StructuredBuffer 查表
- 当前 Binning 架构天然适配

---

## 6. 性能对比

| 操作 | 当前 (全 Dynamic) | S1 (精细化类型) | S2 (多SRB) | S3 (Bindless) |
|------|-------------------|----------------|-----------|--------------|
| Dynamic descriptor 开销 | 全部变量 | **仅 Uniform** | 仅 Uniform | 无 |
| Per-material MapBuffer | N次 | **0次** | 0次 | 0次 |
| Per-material CommitSRB | N次 | N次（**轻**） | N次（轻） | 1次 |
| Per-material 纹理 | ✅ 每 dispatch 重绑 | ✅ | ✅ | ✅ |
| Dispatch 次数 | N | N | N | **1** |
