# Render Graph 设计文档

> 合并自 `rendergraph_redesign.md`、`rg_enhancement_plan.md`、`graph_plan.md`。
> 
> **状态：✅ 已全部实施**

---

## 1. 类型体系

### 公共类型

| 类型 | 职责 |
|------|------|
| `RenderGraphHandle` | 帧内资源引用（index-based, lightweight struct） |
| `RenderGraphBuilder` | Pass 声明读写依赖 |
| `RenderGraphContext` | Pass 执行时获取物理资源（含 mip view） |
| `IRenderGraphPass` | Pass 接口（`Setup` + `Execute`） |
| `RenderGraph` | 帧内/跨帧统一管理 |

### 内部类型

| 类型 | 职责 |
|------|------|
| `RenderGraphResource` | 帧内节点（name + kind + state） |
| `CachedTexture` / `CachedBuffer` | 跨帧缓存（idle frames + deferred release） |
| `LambdaRenderGraphPass<T>` | Lambda 便利封装 |
| `PassMetadata` | 每 pass 的 reads/writes 元数据 |

### 帧循环

```
BeginFrame() → CreateTexture/Buffer/Import × N → AddPass × N
→ Compile() → Execute(ctx) → EndFrame()
```

---

## 2. 编译期能力（均已实施）

### 2.1 自动 Barrier

- 编译期：为每个 pass 生成 `PreBarriers`（`CompiledBarrier` 列表）
- 执行期：RG 在 `pass.Execute()` 前统一注入 `TransitionResourceStates`
- 规则：初始状态来自 imported/external 的 `InitialState` 或 transient 的 `Undefined`；若 `CurrentState != RequiredState` 则插入转换

### 2.2 Dead Pass 剔除（DCE）

- `CollectSinkResources()`：收集 `MarkAsOutput` + `QueueExtraction` 的 handle 作为 sink
- `BuildReachablePassSet()`：从 sink 反推 producer → 递归到上游 → 标记可达 pass
- 不可达 pass 的 `Active = false`，不执行

### 2.3 拓扑排序

- `BuildExecutionOrder(activePasses)`：Kahn 算法 + 同入度按 `OriginalIndex` 稳定排序
- Fallback：若 Kahn 排序不完整（环路），退化为声明顺序

### 2.4 Placed 资源内存别名

- `RGMemoryHeap`：封装 `IDeviceMemory`（`DEVICE_MEMORY_TYPE_PLACED`）
- Compile 阶段按生命周期贪心分配偏移
- Execute 时用偏移创建 Placed 资源
- 不支持时（OpenGL/D3D11）自动回退到池分配

---

## 3. 跨帧资源管理

### 外部资源注册

- `RegisterExternalTexture/Buffer`：导入上帧提取的物理资源到当前帧 RG
- 返回带 `IsExternal` 标记的 handle，不被 transient 池管理

### 资源提取

- `QueueTextureExtraction/BufferExtraction`：在 `Execute()` 末尾回写底层资源
- 提取的资源脱离 transient 池，由外部持有
- **提取 handle 参与 sink 可达性分析**（确保跨帧链路不被 DCE 剔除）

### PingPong

- `PingPongHandle`：封装帧间交替命名（`HiZ_A`/`HiZ_B`）
- 动态分辨率变化时旧 history 自然失效

---

## 4. 外部迁移约定

- 除 RG 外**禁止手动管理** Buffer/Texture 生命周期
- 物理资源通过 `graph.CreateTexture/Buffer` 获取
- per-mip view 通过 `RenderGraphContext.GetMipView()` 获取
- 手写 `TransitionResourceStates` 逐步清理（由 RG 自动 barrier 接管）
