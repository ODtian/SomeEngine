# BATCH-03 REPORT

**Date:** 2026-03-29  
**Status:** DONE

## Completed

- [x] TASK-201: 新增 / 补齐 `docs/core/ecs_design.md`, `docs/core/qvvs.md`, `docs/core/source_generator.md`, `docs/rendering/vrb.md`
- [x] TASK-202: 补齐 `docs/assets/pipeline_overview.md`
- [x] 更新 `docs/README.md` 索引，核心/渲染/资产文档可直达

## Verification

- 文档索引已能定位到新增文档
- 当前复核基线（2026-03-30）：`dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj` -> `177 passed, 1 failed`
- 失败项：`TransformSystemTests.TestRotation`

## Tracker Impact

- `TASK-201` -> `DONE`
- `TASK-202` -> `DONE`
