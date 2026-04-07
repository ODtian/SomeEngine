# BASELINE-REVIEW: SomeEngine

**Project:** SomeEngine  
**Date:** 2026-03-30  
**Mode:** Full Conversion  
**Status:** healthy-baseline

---

## Executive Summary

SomeEngine 现在已经具备可用的 retrofit workstream 基线：`DESIGN / TASK-DETAIL / TASK-TRACKER / DEBT-TRACKER / ONBOARDING / BATCH / REPORT / REVIEW` 均已落地。Phase 0 文档同步、Phase 1 核心 hardening、Phase 2 文档补全都已完成；当前重点已经从“清基线问题”转向“按 batch 继续补缺失系统和前推功能”。

---

## Evidence Reviewed

- `docs/` — 当前设计与未来规划文档
- `src/` — Core / Render / Assets / Runtime / Generators 等主模块
- `tests/SomeEngine.Tests/` — 当前测试集
- `.dev-workstream/` — BATCH-01~04 的 instructions / reports / reviews
- 当前验证（2026-03-30）：
  - `dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj`
  - 结果：`191 passed, 0 failed`

---

## Current Capability Map

| Area | Status | Evidence | Notes |
|---|---|---|---|
| Job System | `stable` | `docs/core/job_system.md`, `tests/.../Jobs/` | 有文档、有测试、职责清晰 |
| ECS 封装 | `stable` | `docs/core/ecs_design.md`, `tests/ECS/`, `TransformSystem.cs` | 文档和层级旋转测试已对齐 |
| QVVS 坐标 | `stable` | `docs/core/qvvs.md`, `TransformQvvs.cs` | 基础组合行为已有测试覆盖 |
| Render Graph | `stable` | `docs/rendering/render_graph.md`, `tests/RenderGraphTests.cs` | 自动 barrier / DCE / aliasing 成熟 |
| Cluster Pipeline | `partial` | `docs/rendering/cluster_pipeline.md`, `src/.../ClusterRender/` | 主链路清晰，但端到端渲染验证仍弱 |
| HiZ 剔除 | `stable` | `docs/rendering/hiz_culling.md`, `ClusterBVHTraversePass.cs` | 设计和实现一致度较高 |
| 材质系统 | `stable` | `docs/materials/architecture.md`, `tests/Materials/`, `MaterialPass.cs` | 文档、测试、结构都较完整，metadata 已开始进入 runtime |
| Shade Pipeline | `stable` | `docs/rendering/shading_pipeline.md`, `ClusterShade.cs`, `ShaderGroupTests.cs`, `ClusterShadeSig1Tests.cs` | Sig0/Sig1 已接入，cache key/reuse 已直接覆盖 |
| DeformCache | `stable` | `ClusterDeformPass.cs`, `DeformDispatchCalc.cs`, `DeformCacheTests.cs` | dispatch / offsets / cached-path 边界已有镜像测试 |
| 资产管线 | `stable` | `docs/assets/pipeline_overview.md`, `tests/Assets/` | Importer / Meta / Manifest / Workspace 文档齐全 |
| Source Generator | `needs-review` | `docs/core/source_generator.md`, `src/SomeEngine.Generators/` | 文档已补，但仍缺更直接的测试保障 |
| VRB | `partial` | `docs/rendering/vrb.md`, `ClusterBuilder.cs` | 文档已补，仍偏实现导向 |
| Runtime / ImGui | `needs-review` | `src/SomeEngine.Runtime/Program.cs` | 功能集中在单文件，测试覆盖缺失 |
| Editor / Physics / Animation / UI | `undocumented` | `src/SomeEngine.Editor/`, `docs/future/` | 多数仍为框架或远期计划 |

---

## Strengths

- **Retrofit workflow 已成形**：仓库内已形成完整的 batch / report / review / debt 节奏
- **材料与资产子系统基础扎实**：文档与测试覆盖相对完整，命名和职责边界清晰
- **Phase 0 文档漂移已收口**：顶层活跃设计文档与当前代码现状基本对齐
- **Phase 1 硬化已收口**：测试基线已恢复为全绿

---

## Current Risks

### Risk 1: Runtime `Program.cs` 过于集中
- **Type:** architecture
- **Evidence:** `src/SomeEngine.Runtime/Program.cs`
- **Impact:** 启动、场景、输入、debug UI 全耦合在一个文件，测试难度高
- **Recommendation:** 在进入更多系统补全前，规划拆分批次

### Risk 2: Cluster Pipeline 仍缺端到端渲染验证
- **Type:** testing
- **Evidence:** 目前主要是 helper / unit 级测试
- **Impact:** GPU 端整链路回归仍可能漏检
- **Recommendation:** 未来补集成验证批次

---

## Documentation State

### 已同步到当前代码的文档

- `docs/rendering/cluster_pipeline.md`
- `docs/materials/architecture.md`
- `docs/rendering/shading_pipeline.md`
- `docs/rendering/rasterization.md`
- `docs/rendering/overview.md`
- `docs/rendering/sw_raster/sw_raster.md`
- `docs/assets/pipeline_overview.md`
- `docs/core/ecs_design.md`
- `docs/core/qvvs.md`
- `docs/core/source_generator.md`
- `docs/rendering/vrb.md`

### 仍需关注的文档点

- 缺少一份专门的 Dual-Signature 设计文档
- UI / Runtime 侧仍缺单独文档

---

## Test Gaps

- 全量测试当前全绿：`191 passed, 0 failed`
- 缺少 Cluster Pipeline 的端到端渲染验证
- 缺少 Runtime / ImGui / RHI 的直接测试

---

## Workstream Status

- **Phase 0: Baseline & Documentation** — DONE
- **Phase 1: Review Existing Core** — DONE
- **Phase 2: Hardening & Debt Reduction** — DONE
- **Phase 3: Next Features** — TODO

---

## Recommended Next Batch

1. **TASK-306: MaterialRegistry 自动推导 MVP**
2. **TASK-301: 光照系统强化**
3. **TASK-302: Page 流式加载**

---

## Conversion Decision

- **Can continue in batch workflow now?** Yes
- **Current interpretation:** workflow 已稳定，可以转入下一阶段系统补全
- **Suggested next step:** 以 feature / system batch 开始推进 material、lighting、streaming 等缺失系统
