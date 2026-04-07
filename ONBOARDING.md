# SomeEngine Onboarding

## Project Overview
- 全 C# GPU-Driven Cluster Rendering 引擎（Nanite-like）
- 当前状态：**evolving** — 渲染管线核心稳定，Material ECS 主线已落地；资产管线的 GUID/workspace/自动重导入仍处于下一批 corrective rework
- 已转换为 batch workstream 工作流

## Where Things Live

| 内容 | 路径 |
|---|---|
| Product code | `src/` — 9 个项目 |
| Tests | `tests/SomeEngine.Tests/` |
| Shaders | `assets/Shaders/` |
| Design docs | `docs/` — 按领域组织 |
| Dev log | `log.md` |
| Workstream files | `.dev-workstream/` |

## Read These First

1. [`docs/DESIGN.md`](docs/DESIGN.md) — 顶层架构总览
2. [`docs/rendering/overview.md`](docs/rendering/overview.md) — GPU 管线架构
3. [`docs/materials/architecture.md`](docs/materials/architecture.md) — 材质系统设计
4. [`docs/assets/asset_identity.md`](docs/assets/asset_identity.md) — 资产身份与源文件追踪设计
5. [`docs/TASK-DETAIL.md`](docs/TASK-DETAIL.md) — 当前任务详情
6. [`.dev-workstream/TASK-TRACKER.md`](.dev-workstream/TASK-TRACKER.md) — 进度跟踪
7. [`.dev-workstream/DEBT-TRACKER.md`](.dev-workstream/DEBT-TRACKER.md) — 技术债
8. [`.dev-workstream/reviews/BASELINE-REVIEW.md`](.dev-workstream/reviews/BASELINE-REVIEW.md) — 基线审查

## Build & Test

```bash
# 编译
dotnet build SomeEngine.slnx

# 运行测试
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj

# 运行特定测试
dotnet test --filter "FullyQualifiedName~Assets"

# 运行引擎
dotnet run --project src/SomeEngine.Runtime/SomeEngine.Runtime.csproj
```

当前测试基线（2026-04-03）：`129 passed, 0 failed, 1 skipped`

## Key Constraints

- **禁止 unsafe**，必须使用 Span API
- **DiligentCore API** 参考 `external/DiligentCore/build/.NET/...SharpGen.Bindings.g.cs`
- **meshopt/diligent** 所有 API 都有 span 版本
- 如果库没有 span API，停止并告知
- 使用 `uv` 与 `venv` 操作 Python，禁止直接操作 python
- 默认终端环境为 Windows PowerShell；文中的 `dotnet` 命令在 PowerShell 和 git bash 下都可直接运行

## Workflow Rules

- 新工作先进入任务体系（TASK-DETAIL → TASK-TRACKER）
- 每次工作以 batch 为单位
- 完成后写 report
- review 后更新 debt / tracker
- 完成功能后简短记录在 `log.md`

## Current Priorities

1. ~~Phase 0: 文档同步与清理~~ ✅
2. ~~Phase 1: 核心管线加固~~ ✅
3. ~~Phase 2: 文档补全~~ ✅
4. ~~Phase 3: corrective asset rework~~ ✅ — `TASK-307` / `BATCH-07` 已完成
5. **Phase 3: 后续功能候选** — `TASK-301` 光照 / `TASK-302` Page 流式 / `TASK-303` Tessellation

## Developer Quick Start

阅读 [DEV-GUIDE](.dev-workstream/guides/DEV-GUIDE.md) 了解构建、测试、约束和 batch 工作流。
