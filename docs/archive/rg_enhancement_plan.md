# RenderGraph 增强实施计划（Barrier / DCE / 历史资源）

目标：把当前 `RenderGraph` 从“声明读写 + 顺序执行”升级为“可编译图（依赖、可达性、自动屏障、跨帧资源提取）”，优先满足你指出的四个核心价值：

1. 自动处理 Resource Barriers / State Transitions。
2. 依赖追踪与 Dead Pass 剔除。
3. 可靠的跨帧提取、Ping-Pong、动态分辨率兼容。
4. 统一的 RG 资源 API 体验。

---

## 1. 当前现状（基于代码）

基于 `src/SomeEngine.Render/Graph/RenderGraph.cs`：

- 已有能力：
  - 资源创建/导入：`CreateTexture/Buffer`、`ImportTexture/Buffer`。
  - 外部资源注册：`RegisterExternalTexture/Buffer`（已落地）。
  - 提取队列：`QueueTextureExtraction/QueueBufferExtraction`（已落地，`Execute` 末尾回写）。
  - Pass 声明读写：`RenderGraphBuilder.Read*/Write*` -> `_passMetadata`。
  - 内存别名分配：按生命周期做 placed heap 分配。

- 主要缺口：
  - `Compile()` 内会 `_compiledResources.Clear()`，导致 `MarkAsOutput()` 标记未参与后续裁剪。
  - `Execute()` 仍是顺序执行，且注释写明 barrier 由 pass 自己处理。
  - 无“从输出/提取反推可达性”的 Dead Pass 剔除。
  - `Queue*Extraction` 仅做执行后回写，尚未作为图的“根节点（sink）”参与编译优化。

---

## 2. 范围与里程碑

### M1（最高优先级）：自动 Barrier
- 编译期生成每个 pass 的 `PreBarriers`。
- 执行期统一由 RG 注入 `TransitionResourceStates`。
- pass 中逐步移除手写 `TransitionResourceStates`（先新代码遵循，老代码渐进迁移）。

### M2：依赖追踪 + Dead Pass 剔除
- 将 `MarkAsOutput` 与 `QueueExtraction` 一起作为 sink。
- 基于资源生产者/消费者关系做反向可达性分析。
- 仅执行可达 pass，且按依赖拓扑排序（保持稳定性）。

### M3：跨帧提取语义完善（Ping-Pong/动态分辨率）
- 规范 Register + Extract 的历史资源生命周期。
- 增加尺寸/格式校验策略，支持分辨率变化时安全替换。
- 避免外部资源误释放、误复用。

### M4：统一 API 与验证体验
- 强化 RG 内部校验：未声明读写即访问、非法状态组合、未生产资源读取等。
- 输出可读编译日志（可开关），辅助 RenderDoc 对照排查。

---

## 3. 设计方案

## 3.1 编译期数据结构增强

在 `RenderGraph` 内新增（或等价）编译产物：

- `CompiledPass`：
  - `RenderPass Pass`
  - `int OriginalIndex`
  - `bool Active`
  - `List<StateTransitionDesc> PreBarriers`
- `Dictionary<int, int> LastWriterPassByResource`（资源 -> 最后写入 pass）
- `Dictionary<int, List<int>> ReadersByResource`（资源 -> 读者 pass 列表）
- `HashSet<int> SinkResources`（来自 `MarkAsOutput` + `QueueExtraction`）
- `List<int> ExecutionOrder`（拓扑排序后）

说明：
- 继续复用现有 `_passMetadata`（Reads/Writes），避免大规模改接口。
- `QueueExtraction(handle, ...)` 在编译阶段视为 sink，确保历史输出链路不会被误剔除。

## 3.2 自动 Barrier 规则

针对每个 pass 的 reads/writes，计算“当前状态 -> 目标状态”：

- 初始状态：
  - Imported/External：使用 `InitialState`。
  - Transient：初始视为 `Undefined`。
- 目标状态：
  - 由该 pass 在 builder 里声明的状态（如 `ShaderResource`、`UnorderedAccess`、`RenderTarget`）。
- 生成策略：
  - 若 `CurrentState != RequiredState`，在该 pass 前插入 transition。
  - 若同一 pass 对同一资源既读又写，按写入优先（或显式报错并要求拆分，先从保守策略开始）。

执行期：
- RG 在 `pass.Execute(...)` 前统一提交 `PreBarriers`。
- Pass 层约定：不再重复对 RG 管理资源做手工 Transition（逐步清理）。

## 3.3 Dead Pass 剔除

步骤：
1. 建立“资源生产者映射”：资源由哪个 pass 写出。
2. 收集 sink 资源：
   - `MarkAsOutput` 标记。
   - `QueueTextureExtraction/QueueBufferExtraction` 的 handle。
3. 反向遍历：
   - 从 sink 资源找到 producer pass。
   - producer 的输入资源继续反推上游 producer。
4. 标记可达 pass 为 `Active=true`，其余 `Active=false`。

注意：
- 若 pass 有 side effect（例如写外部 query、时间戳），后续可扩展 `RenderPassFlags.NeverCull`。

## 3.4 拓扑排序与稳定执行

- 边定义：
  - `A -> B` 当 B 读取或写入了 A 生产的资源（RAW/WAW 依赖都保守连边）。
- 使用 Kahn 拓扑排序。
- 同入度节点按 `OriginalIndex` 稳定排序，尽量贴近当前行为，降低回归风险。

## 3.5 Register/Extract 与跨帧语义

统一约束：
- 跨帧资源仅通过 `RegisterExternal*` 输入、`Queue*Extraction` 输出。
- 被 extraction 的 transient 资源在本帧结束后转为 `IsExternal=true`，由外部持有。
- 下一帧由外部再 `RegisterExternal*` 回图内。

动态分辨率策略：
- 每帧创建 `CurrHiZ`（或其他 history target）按当前尺寸描述。
- 若尺寸变化，外部旧 history 指针在被新 extraction 覆盖后自然失效/回收。
- 在 Register 时增加 desc 校验与日志，防止误把旧分辨率资源接入新 pass。

---

## 4. API 调整计划

## 4.1 保留并增强现有 API
- `RegisterExternalTexture/Buffer`：保留。
- `QueueTextureExtraction/BufferExtraction`：保留，新增“参与 sink 可达性”的编译语义。
- `MarkAsOutput`：修复 compile 流程中被清空的问题，确保生效。

## 4.2 可选新增 API（非阻塞）
- `QueueTextureExtraction(handle, Action<ITexture?> assign, ResourceState finalState)`
- `QueueBufferExtraction(handle, Action<IBuffer?> assign, ResourceState finalState)`

用途：
- 指定提取后期望状态，便于下一帧 Register 时减少一次无意义切换。

---

## 5. 实施步骤（按提交顺序）

1. 编译产物重构（不改外部接口）
   - 引入 `ExecutionOrder`、`ActivePasses`、`PreBarriers` 缓存。
   - 修复 `MarkAsOutput` 生命周期（不要在 compile 开头直接清空语义数据）。

2. 自动 Barrier
   - 在 `Compile()` 计算每个 pass 的 barrier 列表。
   - 在 `Execute()` 统一提交 barrier。
   - 添加 debug 断言：pass 内重复 transition 可先警告。

3. Dead Pass 剔除
   - 将 extraction 句柄纳入 sink。
   - 做反向可达性标记。
   - 执行顺序仅保留 active pass。

4. 拓扑排序
   - 建立 pass 依赖图，按 active 子图排序。
   - 保持同层稳定序。

5. 历史资源与动态分辨率校验
   - Register 阶段做 desc 检查（尺寸、格式、mips）。
   - 提取后状态和外部所有权语义补齐。

6. 管线迁移（以 Cluster/HiZ 为首个落地点）
   - 清理 `Cluster*Pass` 中对 RG 管理资源的手写 transition。
   - 保留非 RG 资源过渡逻辑（若有）。

---

## 6. 验证计划

## 6.1 单元测试（建议新增 `tests/SomeEngine.Tests/RenderGraph*`）
- Barrier 生成：
  - A 写 UAV，B 读 SRV，验证 B 前存在 UAV->SRV transition。
- Dead Pass 剔除：
  - 存在不连向任何 sink 的分支，验证不执行。
- Extraction 作为 sink：
  - 仅 extraction 消费的资源链路应保留。
- 拓扑与稳定性：
  - 无依赖并列 pass 顺序应与声明顺序一致。

## 6.2 集成验证（Cluster HiZ）
- 第 N 帧构建 `CurrHiZ` 并 extraction。
- 第 N+1 帧 Register 为 `PrevHiZ` 并参与 Cull。
- 旋转相机 + resize，验证无 device lost、无明显闪烁。

## 6.3 调试输出
- 增加可开关日志：
  - active pass 列表
  - 每 pass barrier 列表
  - culled pass 列表

---

## 7. 风险与降级

- 风险 1：旧 pass 保留手写 transition，可能与自动 barrier 重复。
  - 对策：先允许重复（通常安全），加警告日志，逐步迁移。

- 风险 2：同 pass 对同资源多状态访问（读写混合）语义不清。
  - 对策：先采用保守写状态；复杂场景要求拆 pass。

- 风险 3：外部资源状态来源不可信。
  - 对策：Register 时必须传 `initialState`，debug 构建下加校验告警。

---

## 8. 完成定义（DoD）

满足以下条件即认为 RG 增强第一阶段完成：

- 自动 barrier 在 RG 层生效，核心 pass 不再依赖手工 transition。
- Dead pass 可稳定剔除，extraction 能正确保活跨帧输出链路。
- Cluster HiZ 历史纹理通过 Register/Extract 稳定跨帧运转。
- 构建与现有 demo 可通过，且无新增 GPU 同步错误。

---

## 9. 建议落地顺序（务实版）

1. 先只做 `Texture` 的完整自动 barrier + DCE + extraction sink。
2. 再把同样机制扩到 `Buffer`。
3. 最后做可选高级项（finalState hints、NeverCull flags、可视化 graph dump）。
