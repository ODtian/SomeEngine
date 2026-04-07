# BATCH-03: 文档补全

**Type:** Documentation Baseline  
**Scope:** TASK-201, TASK-202  
**Estimated Effort:** ~2-4 小时  
**Prerequisite:** BATCH-02 完成第一阶段

---

## Objective

补齐核心子系统和资产管线的独立文档，把隐含在代码和测试里的知识转成可维护的设计基线。

---

## Required Reading

- `docs/DESIGN.md`
- `docs/README.md`
- `.dev-workstream/reviews/BASELINE-REVIEW.md`
- `docs/TASK-DETAIL.md`

---

## Source Code Locations

- `src/SomeEngine.Core/ECS/`
- `src/SomeEngine.Core/Math/`
- `src/SomeEngine.Generators/`
- `src/SomeEngine.Assets/`
- `src/SomeEngine.Assets/Importers/ClusterBuilder.cs`

---

## Build & Test Commands

```bash
dotnet build SomeEngine.slnx
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj
```

---

## Tasks & Success Criteria

### TASK-201: 补 ECS / QVVS / Source Generator / VRB 独立文档
- [ ] `docs/core/ecs_design.md`
- [ ] `docs/core/qvvs.md`
- [ ] `docs/core/source_generator.md`
- [ ] `docs/rendering/vrb.md`

### TASK-202: 资产管线总览文档
- [ ] `docs/assets/pipeline_overview.md`
- [ ] `docs/README.md` 中索引可直达

---

## Report Requirements

产出 `.dev-workstream/reports/BATCH-03-REPORT.md`，记录新增/更新文档列表和验证结果。
