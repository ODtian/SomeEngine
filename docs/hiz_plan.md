# HiZ 2-Phase Culling Implementation Plan (Codebase Grounded)

目标：在当前 Cluster 渲染链路中落地 Two-Phase Occlusion Culling，优先复用已有 BVH/Compute/RenderGraph 架构，减少对现有流程（尤其是 `ClusterBVHTraversePass` 与 Page Streaming）的扰动。

## 1. 已确认现状（基于仓库）

1. 当前渲染链路为：
  - `ClusterBVHTraversePass` -> `ClusterCullPass` -> `ClusterDrawPass` -> (`ClusterDebugPass`)
  - 入口在 `ClusterPipeline.AddToRenderGraph(...)`。

2. 深度约定为 **Standard Z**：
  - 深度清屏值为 `1.0`。
  - 相机使用标准透视投影。
  - 当前默认深度比较行为等价于 `LESS` 路径。

3. 跨帧资源管理机制：
  - 将仿照 Unreal RDG 扩展 RenderGraph API，提供资源注册（RegisterExternal）与提取（QueueExtraction）机制，以优雅地管理跨帧历史（如 `PrevHiZ`）。
  - `PrevViewProj` 等非纹理状态仍需在 CullingUniforms 级别或 Pipeline 级别进行帧间缓存。

4. RenderGraph 可创建/导入纹理与缓冲，但没有现成 HiZ 构建 pass：
  - 需要新增独立 compute pass（`HiZBuildPass` + `hiz_build.slang`）。

## 2. 设计原则

1. **不改 BVH 遍历主逻辑**：`ClusterBVHTraversePass` 仍只负责输出 `Candidates`。
2. **两阶段拆在 Cull/Draw 层**：避免把跨帧/深度纹理逻辑塞进 BVH pass。
3. **Phase 1 保守，Phase 2 纠偏**：
  - Phase 1 用上一帧 HiZ 做粗遮挡（可能保守漏剔）。
  - Phase 2 用当前帧 Phase 1 深度生成的 HiZ 做补测，避免“晚一帧出现”。
4. **按当前 Standard Z 实现 HiZ 聚合**：HiZ 每层取 `max` 深度（保守遮挡）。

## 3. 资源与数据结构改造

## 3.1 Culling Uniform 扩展

在 `src/SomeEngine.Render/Pipelines/ClusterPipeline.cs` 的 `CullingUniforms` 扩展：

- `Matrix4x4 PrevViewProj`
- `uint HasPrevHistory`
- `uint HiZMipCount`
- `Vector2 HiZInvSize`（或 `float2` 等价布局）
- 必要 padding（保持 16-byte 对齐）

并确保以下定义布局一致：

- C#: `ClusterPipeline.cs` 的 `CullingUniforms`
- Slang: `assets/Shaders/cluster_cull.slang` 的 `CullingUniforms`
- Slang: `assets/Shaders/cluster_bvh_traverse.slang` 的 `CullingUniforms`（即使该 pass 不使用新字段，也要保持共享 UB 二进制兼容）

## 3.2 RenderGraph 跨帧资源提取机制（仿 RDG）与 HiZ 纹理

为了优雅地管理跨帧历史资源（如 `PrevHiZ`），将扩展当前 RenderGraph 的能力，仿照 Unreal RDG 的机制：

1. **外部资源注册 (`RegisterExternalTexture`)**：
   - 提供 API 将上一帧提取的 `ITexture` 导入到当前帧 RenderGraph，并返回 `RGTextureRef`，由 RenderGraph 追踪生命周期但避免在 Transient 池中分配。
2. **资源提取队列 (`QueueTextureExtraction`)**：
   - 提供 API 请求从 Graph 提取内部的 `RGTextureRef` 对应的底层纹理。在 RenderGraph `Execute(...)` 结束后，将实际分配的 `ITexture` 取出，供下一帧使用。这部分纹理资源必须不被资源池销毁。
3. **Pipeline 级别的历史句柄持有**：
   - `ClusterPipeline` 不再维护 `Prev` / `Curr` 两个纹理实例的手动 Ping-Pong。
   - 仅持有一个历史句柄：`ITexture _historyHiZTexture;`
   - 以及状态信息：`_hizWidth`, `_hizHeight`, `_hizMipCount`, `_hasPrevHistory`, `_prevViewProjT`。
   - 每帧开始时注册 `_historyHiZTexture` 作为 `PrevHiZ` 输入，每帧结束前把构建好的 `CurrHiZ` 放入提取队列并写回 `_historyHiZTexture`。

## 3.3 新增中间缓冲（两阶段）

在 `ClusterPipeline.AddToRenderGraph(...)` 新增 transient buffer：

- `Phase1VisibleClusters` (`uint4`, stride 16)
- `Phase1DrawArgs` (16 bytes)
- `Phase2CandidateClusters` (`uint3`, stride 12)
- `Phase2CandidateCount` (4 bytes)
- `Phase2CandidateArgs` (16 bytes, 用于 Phase2 间接 dispatch)
- `Phase2VisibleClusters` (`uint4`, stride 16)
- `Phase2DrawArgs` (16 bytes)

## 4. Pass 编排（最终执行顺序）

1. `ClusterBVHTraversePass`
  - 输入：实例/BVH/PageHeap
  - 输出：`Candidates` + `CandidateCount` (+ 现有 page fault)

2. `ClusterCullPass (Phase1)`
  - 输入：`Candidates`, `CandidateCount`, `CandidateArgs`, `PrevHiZ`, `PrevViewProj`
  - 输出：`Phase1VisibleClusters`, `Phase1DrawArgs`, `Phase2CandidateClusters`, `Phase2CandidateCount`

3. `ClusterDrawPass (Phase1)`
  - 输入：`Phase1VisibleClusters`, `Phase1DrawArgs`
  - 输出：Color + Depth（写入当前帧深度）

4. `HiZBuildPass`
  - 输入：当前 Depth（Phase1 后）
  - 输出：`CurrHiZ` 全 mip 链

5. `ClusterCullPass (Phase2)`
  - 输入：`Phase2CandidateClusters`, `Phase2CandidateCount`, `CurrHiZ`, `ViewProj`
  - 输出：`Phase2VisibleClusters`, `Phase2DrawArgs`

6. `ClusterDrawPass (Phase2)`
  - 输入：`Phase2VisibleClusters`, `Phase2DrawArgs`
  - 输出：Color + Depth（补画漏检可见 Cluster）

7. `ClusterDebugPass`（可选）
  - 初期可先保持现状；若需要“完整可见集合”调试，再追加双缓冲合并/双次调试绘制。

## 5. Shader 级实现细节

## 5.1 `cluster_cull.slang` 改造

建议提供两套入口（或单入口 + `CullPhase` 分支）：

- `main_phase1`
- `main_phase2`
- `UpdateIndirectArgs`（保留）

核心逻辑：

1. 用 cluster 的世界空间球（`LODCenter/LODRadius` 经实例变换）投影到屏幕。
2. 估算屏幕包围尺寸，选择 `mip = clamp(floor(log2(pixelDiameter)), 0, HiZMipCount-1)`。
3. 采样 HiZ（Standard Z 路径取 `max` 金字塔）。
4. 遮挡判定（保守）：
  - 设 `objNearDepth` 为对象最靠近相机深度；
  - 若 `objNearDepth > hizDepth + epsilon`，判为 occluded。

Phase 分流：

- Phase 1：
  - `occluded` -> 写 `Phase2CandidateClusters`
  - `visible/uncertain` -> 写 `Phase1VisibleClusters` + `Phase1DrawArgs`
- Phase 2：
  - 仅对 `Phase2CandidateClusters` 再测，`visible` 才写 `Phase2VisibleClusters` + `Phase2DrawArgs`

无历史降级：

- `HasPrevHistory == 0` 时，Phase 1 不做 occlusion，全部送入 Phase 2（功能正确优先）。

## 5.2 `hiz_build.slang` 新增

新增 compute shader：

- Kernel A：从 Depth 生成 mip0（或直接复制 + 标准化）
- Kernel B：`mip(n-1) -> mip(n)` 下采样
- 线程组建议：`[numthreads(8,8,1)]`

聚合规则：

- Standard Z: `max(d0, d1, d2, d3)`
- 边界像素使用 clamp 读取

## 6. C# Pass/管线改造点

## 6.1 新增 `HiZBuildPass`

新文件：`src/SomeEngine.Render/Pipelines/HiZBuildPass.cs`

职责：

1. 在 `Setup` 中声明：读 Depth、写 CurrHiZ。
2. 在 `Execute` 中逐 mip dispatch。
3. 管理 mip 级 SRV/UAV 绑定（可缓存 view 数组，尺寸变化时重建）。

## 6.2 扩展 `ClusterCullPass`

文件：`src/SomeEngine.Render/Pipelines/ClusterCullPass.cs`

改造建议：

1. 支持 phase 配置（构造参数或属性）：
  - `Phase1` / `Phase2`
2. 支持纹理输入：
  - `HHiZTexture`
3. 支持双输出（Phase1）与单输出（Phase2）：
  - Phase1 额外输出 `Phase2Candidate...`
4. 保留现有 `UpdateIndirectArgs` 路径，用于输入候选 count -> dispatch args。

## 6.3 `ClusterDrawPass` 可复用双实例

文件：`src/SomeEngine.Render/Pipelines/ClusterDrawPass.cs`

建议允许传入 pass 名称（如 `ClusterDraw_Phase1` / `ClusterDraw_Phase2`），便于 RenderGraph 调试与日志区分。绘制逻辑可完全复用。

## 6.4 `ClusterPipeline` 主编排

文件：`src/SomeEngine.Render/Pipelines/ClusterPipeline.cs`

改造内容：

1. 持有两套 Cull/Draw pass：
  - `_cullPhase1Pass`, `_cullPhase2Pass`
  - `_drawPhase1Pass`, `_drawPhase2Pass`
2. 新增 `_hizBuildPass`。
3. 每帧 `AddToRenderGraph` 中：
  - 调用 `renderGraph.RegisterExternalTexture` 导入上帧提取的 `_historyHiZTexture` 作为 `PrevHiZ`。
  - 按第 4 节顺序组装，最后 `HiZBuildPass` 输出得到 `CurrHiZ`。
  - 组装完成后，调用 `renderGraph.QueueTextureExtraction(currHiZRef, ref _historyHiZTexture)` 将当前帧结果放入提取队列，供下一帧使用。
4. 统一更新 `CullingUniforms`（含 `PrevViewProj`、`HasPrevHistory`、HiZ 参数）。
5. 帧结束更新矩阵历史：保存 `_prevViewProjT` 为当前帧投影。

## 6.5 RenderGraph 提取机制实现（框架级）

文件：
- `src/SomeEngine.Render/RenderGraph/RenderGraph.cs`
- `src/SomeEngine.Render/RenderGraph/RGResourceRegistry.cs`

改造点：
1. **注册接口**：提供 `RegisterExternalTexture(ITexture, string)`，返回一个特殊生命周期标记（如 `External`）的 `RGTextureRef`。这个资源在物理内存池中被豁免重新分配/释放。
2. **提取接口**：提供 `QueueTextureExtraction(RGTextureRef, ref ITexture)`。内部维护一个委托队列或句柄列表。在 RenderGraph 的 `Execute()` 或者底层 Graph Compile 阶段的末尾，将 `RGTextureRef` 关联的底层 `ITexture` 回写给提供的引用。同时需确保此被提取的纹理脱离 Transient 资源池的管理，转为由 Pipeline (或用户) 管理。

## 7. 风险与降级策略

1. **Depth SRV 可用性风险**：若交换链深度不能直接 SRV 读取，需增加 depth-copy 中间纹理（仅作 HiZ 输入）。
2. **Mip 子资源视图绑定风险**：若当前封装无法稳定创建“指定 mip 的 UAV/SRV”，可退化为“每级独立纹理”实现，先保证正确性。
3. **相机突变/历史失效**：窗口 resize、FOV 大变、相机瞬移时强制 `HasPrevHistory = 0` 一帧。

## 8. 验证与里程碑

1. 功能正确性：
  - RenderDoc 检查 CurrHiZ 每级 mip 值是否单调保守。
  - 验证 Phase2 能补回 Phase1 错误遮挡。

2. 统计验证：
  - 输出每帧 `CandidateCount`, `Phase1Visible`, `Phase2Input`, `Phase2Visible`。
  - 预期：相机稳定时 `Phase2Visible` 占比较低。

3. 画面稳定性：
  - 快速旋转/平移相机，确保无明显闪烁和“晚一帧出现”异常。

4. 性能回归：
  - 对比改造前后 GPU 时间（至少记录 Cull + Draw 区段）。

## 9. Task Checklist（按实现顺序）

- [ ] 1. Uniform 与历史状态
  - [ ] 扩展 `CullingUniforms`（C# + Slang 对齐）
  - [ ] 在 `ClusterPipeline` 增加 `PrevViewProj` / `PrevHiZ` / `HasPrevHistory`

- [ ] 2. HiZ 生成
  - [ ] 新增 `assets/Shaders/hiz_build.slang`
  - [ ] 新增 `src/SomeEngine.Render/Pipelines/HiZBuildPass.cs`
  - [ ] 完成 CurrHiZ 纹理创建、mip 构建与重建逻辑

- [ ] 3. 两阶段 Cull
  - [ ] 改造 `cluster_cull.slang` 支持 Phase1/Phase2
  - [ ] 改造 `ClusterCullPass.cs` 支持 HiZ 输入与 Phase 输出拓扑

- [ ] 4. 两阶段 Draw 与管线接线
  - [ ] 在 `ClusterPipeline.cs` 增加 Phase1/Phase2 的 buffer 与 pass wiring
  - [ ] 复用/扩展 `ClusterDrawPass.cs` 支持双实例命名

- [ ] 5. RenderGraph 历史提取机制
  - [ ] 在 `RenderGraph` 增加 `RegisterExternalTexture`
  - [ ] 在 `RenderGraph` 增加 `QueueTextureExtraction` 及其底层生命周期剥离
  - [ ] 在 `ClusterPipeline` 中应用提取机制管理 `_historyHiZTexture`

- [ ] 6. 调试与回归
  - [ ] 增加阶段统计输出
  - [ ] RenderDoc 验证 mip chain 与遮挡判定
  - [ ] 相机运动与 resize 场景稳定性验证
