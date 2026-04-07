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

### 解决方案：Mutable + `ALLOW_OVERWRITE`

`ALLOW_OVERWRITE` 允许每帧覆写 Mutable 绑定。由于 immediate context 顺序执行，帧间 `Present`/fence **天然满足 GPU 同步要求**。覆写只是 CPU 侧写 descriptor（ns 级），Commit 时仍走与 Static 相同的 descriptor table 路径。

---

## 3. 变量分层策略

| 变量类别 | 推荐类型 | 理由 |
|---------|---------|------|
| RG transient 资源（VisBuffer, PageHeap, OutputColor 等） | **Mutable + `ALLOW_OVERWRITE`** | placed resource 每帧对象可能变，但 Commit 开销与 Static 相同 |
| Uniform CB（`USAGE_DYNAMIC`） | **Dynamic** | Map/Unmap 改 GPU 地址，需要 Dynamic 路径 |
| 材质纹理（未来） | **Mutable**（每材质独立 SRB） | 材质创建时绑定一次，不变 |

---

## 4. 关键优化标志

### `SHADER_VARIABLE_FLAG_NO_DYNAMIC_BUFFERS`

Dynamic 变量若绑定的不是 `USAGE_DYNAMIC` buffer，加此标志可跳过 GPU 地址刷新。

### `DRAW_FLAG_DYNAMIC_RESOURCE_BUFFERS_INTACT`

连续 Dispatch 间 dynamic buffer 未被 Map 时使用，跳过 root view 刷新。**注意**：当前逐材质 `MapBuffer(Discard)` 导致不可用。

### `SetBufferOffset`

对 constant/structured buffer 设置动态偏移，无需重新 `CommitShaderResources`。用于替代逐材质 Map/Unmap。

---

## 5. 当前管线诊断

当前 `ClusterMaterialShadePass` 的问题：

```csharp
// ⚠️ 全部 Dynamic → 每次 Commit 有额外开销
DefaultVariableType = ShaderResourceVariableType.Dynamic

// ⚠️ 逐材质 Map/Unmap → GPU 地址每次变化，无法用 BUFFERS_INTACT
for (matID...) {
    MapBuffer(Discard) → UnmapBuffer → CommitSRB → Dispatch
}
```

---

## 6. 资源绑定演进路径（S0 → S3）

### S0（当前）→ S1：变量类型优化

- `DefaultVariableType` 从 Dynamic 改为 Mutable
- RG 资源用 Mutable + `ALLOW_OVERWRITE` 每帧重绑
- 只有 Uniforms 保留 Dynamic
- 用大 uniform buffer + `SetBufferOffset` 替代逐材质 Map/Unmap

### S1 → S2：多 SRB 切换

- 每材质创建独立 SRB，绑定材质纹理
- 管线公共资源作为 Mutable 绑定在每个 SRB
- Dispatch 前切 SRB

### S2 → S3：Bindless

- Bin Key 从 `MaterialID` → `ShaderTypeID`，Dispatch 次数 N → 1
- 所有纹理进 bindless heap，材质参数存 StructuredBuffer 查表
- 当前 Binning 架构天然适配

---

## 7. 性能对比

| 操作 | S0 (当前) | S1 (优化变量类型) | S2 (多SRB) | S3 (Bindless) |
|------|-----------|-----------------|-----------|--------------|
| Dynamic descriptor 开销 | 全部变量 | **仅 Uniform** | 仅 Uniform | 无 |
| Per-material MapBuffer | N次 | **0次** | 0次 | 0次 |
| Per-material CommitSRB | N次（重） | N次（**轻**） | N次（轻） | 1次 |
| Per-material 纹理支持 | ❌ | ❌ | ✅ | ✅ |
| Dispatch 次数 | N | N | N | **1** |
