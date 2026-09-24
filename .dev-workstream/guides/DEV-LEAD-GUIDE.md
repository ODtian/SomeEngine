# Dev Lead Guide

## 职责

Dev Lead 负责任务拆分、batch 编排、review 和 debt 分流。

## 任务拆分规则

每个 TASK 必须有：
- 唯一 ID（按 Phase 编号：`TASK-001`, `TASK-101`, `TASK-201`）
- 明确的 deliverable
- `docs/TASK-DETAIL.md` 中的详细描述
- 设计文档引用
- 成功条件

建议任务状态使用：
- `TODO`
- `PARTIAL`
- `DONE`
- `BLOCKED`

## Batch 编排

### 粒度
- < 2 小时：不值得单独 batch
- 4–10 小时：最佳
- \> 12 小时：继续拆

### Combined Batch
当多个任务耦合紧密时，允许 combined batch，但必须：
- 任务按顺序推进
- 当前任务实现 + 测试全绿后才能进下一个

### 指令模板
参考 `.dev-workstream/batches/BATCH-01-INSTRUCTIONS.md`，至少包含：
1. Batch 编号、任务 ID、依赖
2. Required Reading
3. Source Code Locations
4. Build & Test Commands
5. Tasks & Success Criteria
6. Report Requirements

## Review 流程

1. 读 BATCH-XX-REPORT
2. 看代码改动
3. 看测试代码（验证真实行为，拒绝空测试）
4. 运行测试确认
5. 写 BATCH-XX-REVIEW

### 判定
- **APPROVED** → 更新 tracker，生成 commit message，准备 next batch
- **MINOR FIX** → 列具体需修复项，等回补后快速复审
- **REJECTED** → 创建 corrective batch

> 允许出现“batch 已 APPROVED，但父 TASK 仍为 `PARTIAL`”的情况。
> 这种情况通常表示本批完成了某个子层级交付，但原任务的成功条件尚未全部满足。

## Debt 分流

| 级别 | 处理方式 |
|---|---|
| P1 (blocker) | 下一批最前面加 Corrective Task 0 |
| P2 (近期修) | 写入 DEBT-TRACKER，安排进近期 batch |
| P3 (触及时) | 写入 DEBT-TRACKER，工作涉及时顺手修 |

## Tracker 更新时机

- Batch review 后
- 任务状态变化后
- 新增/解决 debt 后

## Decision Log

> 来自 .codex 工作区 Render Graph 调研 batch 的架构决策迁移

| Date | Decision | Rationale |
|---|---|---|
| 2026-06-21 | Handle-based resource ref over string-based | 零开销查找，string 仅 debug |
| 2026-06-21 | Greedy interval aliasing over ILP | O(n log n) 且足够好 |
| 2026-06-21 | Phase pipeline (IRenderGraphPhase) | 可扩展，参考 AMD RPS |
| 2026-06-21 | D3D12 Enhanced Barrier priority | 更精确，减少不必要 transition |
| 2026-06-21 | No Immediate Mode | UE5 的 immediate mode 是迁移期逃逸阀，我们从头设计不需要 |
| 2026-06-21 | MarkOutput() over NeverCull flag | NeverCull 是声明式盲区的妥协，显式 output 才是正道 |
| 2026-06-21 | All passes must declare dependencies | UE5 parameterless pass 破坏声明式模型，无图外 pass |
| 2026-06-21 | Unified external resource model | 无 UseExternalAccessMode，ResourceFlags.External 自动处理 barrier |
| 2026-06-21 | Explicit setOutput() over extraction side effect | QueueTextureExtraction 副作用隐式决定 culling root，不可 |
| 2026-06-21 | Builder→Compiled→Executed typestate | Compile 后不可 addPass，类型系统保证 |
| 2026-06-21 | CompileConfig over global CVar | UE5 GRDG* 全局变量不可测试、不可组合 |
| 2026-06-21 | Explicit queue declaration | UE5 OverridePassFlags 隐式转换 queue 是 bug 温床 |
| 2026-06-21 | MergeGroup over implicit merge | UE5 pass merge 隐式且不可预测，用户显式声明组才合并 |
| 2026-06-21 | Chain API over _RDG_ macro reflection | C# 可用 Source Generator 但核心模型不依赖反射 |
