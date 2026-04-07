# Developer Guide

## 适用范围

本文适用于所有在 SomeEngine 上工作的开发者和 AI agent。

## 环境

- **OS**: Windows
- **Shell**: 默认使用 PowerShell；文中的 `dotnet` 命令在 PowerShell / git bash 都可直接运行
- **Runtime**: .NET 10
- **IDE**: Visual Studio / Rider / VS Code

## 构建

```bash
dotnet build SomeEngine.slnx
```

期望结果：`0 个错误`。警告可能存在（pre-existing），但不应引入新警告。

## 测试

```bash
# 全部测试
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj

# 按名称过滤
dotnet test --filter "FullyQualifiedName~DeformCacheTests"

# 不重建直接测试
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-build
```

当前基线（2026-03-30）：`191 passed, 0 failed`。

## 关键约束

1. **禁止 `unsafe`** — 必须使用 Span API
2. **DiligentCore bindings** 参考路径：`external/DiligentCore/build/.NET/.../SharpGen.Bindings.g.cs`
3. **meshopt / diligent** 所有 API 都有 span 版本，如果发现没有则停止并告知
4. **Python** 使用 `uv` + `venv`，禁止直接操作 python
5. 遇到 pre-existing 测试失败时不得静默忽略，必须修复或在 report/review 中明确记录

## Batch 工作流

### 目录结构

```
.dev-workstream/
├── guides/           # 本文件所在
├── batches/          # BATCH-XX-INSTRUCTIONS.md
├── reports/          # BATCH-XX-REPORT.md
├── reviews/          # BATCH-XX-REVIEW.md + BASELINE-REVIEW.md
├── questions/        # BATCH-XX-QUESTIONS.md (如有)
├── DEBT-TRACKER.md   # 技术债追踪
└── TASK-TRACKER.md   # 任务状态总表
```

### 执行流程

```
batch 指令 → 执行 → report → review → 更新 tracker/debt → next batch
```

### 状态建议

- `TODO`：尚未开始
- `PARTIAL`：已完成部分交付，但成功条件未全部满足
- `DONE`：已满足当前任务成功条件
- `BLOCKED`：被外部依赖或决策阻塞

### 开发者检查清单

每个 batch 结束前必须确认：

- [ ] 所有指定任务完成
- [ ] `dotnet build` 0 errors
- [ ] `dotnet test` 全部通过；若存在已知失败项，必须在 report/review 中点名记录
- [ ] 无新引入的 warning
- [ ] 写了 BATCH-XX-REPORT
- [ ] pre-existing 测试失败已修复（如遇到）

### Commit Message 格式

```
batch-XX: 简短描述

- 任务 1 描述 (TASK-XXX)
- 任务 2 描述 (TASK-XXX)

Build: 0 errors, Test: NNN/NNN passed
```

## 文档

- 设计文档在 `docs/` 下，按领域组织
- 文档索引在 [`docs/README.md`](../../docs/README.md)
- 任务详情在 [`docs/TASK-DETAIL.md`](../../docs/TASK-DETAIL.md)
- 修改代码时如果发现文档过时，记入 DEBT-TRACKER
