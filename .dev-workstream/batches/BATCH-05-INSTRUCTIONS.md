# BATCH-05: ShaderAsset Metadata Runtime Path

**Batch Number:** BATCH-05  
**Tasks:** TASK-305  
**Phase:** Phase 3 - Next Features  
**Estimated Effort:** 4-8 hours  
**Priority:** HIGH  
**Dependencies:** BATCH-04

---

## Onboarding & Workflow

### Developer Instructions
本批是 material 系统补全的第一个真实实现批。目标是让 `ShaderAsset.Metadata.MaterialBindings` 不再只是 importer 产物，而是真正参与 runtime 的 pass 签名和 Sig1 资源布局。

### Required Reading (IN ORDER)
1. **Workflow Guide:** `.dev-workstream/guides/DEV-GUIDE.md`
2. **Onboarding:** `ONBOARDING.md`
3. **Design:** `docs/DESIGN.md` — `Current Gaps And Next Priorities`
4. **Task Details:** `docs/TASK-DETAIL.md` — `TASK-305`
5. **Current Material Design:** `docs/materials/architecture.md`
6. **Current Runtime Code:** `src/SomeEngine.Render/Materials/MaterialPass.cs`, `src/SomeEngine.Render/Materials/ShaderParamBag.cs`
7. **Current Shade Path:** `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`
8. **Current Tests:** `tests/SomeEngine.Tests/Materials/MaterialRegistryTests.cs`, `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs`

### Source Code Locations
| Area | Path |
|---|---|
| Shader schema | `assets/Schema/shader_asset.fbs` |
| Shader importer | `src/SomeEngine.Assets/Importers/SlangShaderImporter.cs` |
| Material pass/signature logic | `src/SomeEngine.Render/Materials/MaterialPass.cs` |
| Runtime param bag | `src/SomeEngine.Render/Materials/ShaderParamBag.cs` |
| Sig1 layout/cache | `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs` |
| Material tests | `tests/SomeEngine.Tests/Materials/MaterialRegistryTests.cs` |
| Pipeline tests | `tests/SomeEngine.Tests/Pipelines/ClusterShadeSig1Tests.cs` |

### Build & Test Commands
```bash
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~ClusterShadeSig1Tests"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --filter "FullyQualifiedName~MaterialTests.ComputeSignature_IgnoresUnusedRuntimeResources_WhenMetadataPresent"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj
```

### Report Submission
完成后提交到：  
`.dev-workstream/reports/BATCH-05-REPORT.md`

如需提问，创建：  
`.dev-workstream/questions/BATCH-05-QUESTIONS.md`

---

## Context

`ShaderAsset` schema 和 importer 其实已经能产出：

- `metadata.tags`
- `metadata.material_bindings`

但当前 runtime 只稳定消费了 `metadata.tags`。`MaterialPass.ComputeSignature()` 和 `ClusterShade` 的 Sig1 资源布局仍然主要从运行时 bag 全量推导，这会把与当前 pass 无关的资源也混进签名与缓存语义中。

本批的目标是把 metadata 真正打通到 runtime：

- pass 签名只关心 shader 实际声明的 material bindings
- Sig1 descriptor 只关心 shader 实际需要的资源布局
- metadata 缺失时仍向后兼容现有行为

**Related Tasks:**
- `TASK-305` — ShaderAsset 最小元数据通路

---

## Batch Objectives

- 让 `MaterialPass` 基于 shader metadata 计算 pass 级绑定签名
- 让 `ClusterShade` 基于 shader metadata 构建 Sig1 布局与 cache key
- 保持 metadata 缺失时的 fallback 行为
- 增加直接测试，证明无关 runtime 资源不会污染 pass 签名和 Sig1 cache

---

## Tasks

### Task 1: Connect Shader Metadata To MaterialPass Signatures (TASK-305)

**File:** `src/SomeEngine.Render/Materials/MaterialPass.cs`  
**Task Definition:** `docs/TASK-DETAIL.md#task-305-shaderasset-最小元数据通路`

**Description:**
让 `MaterialPass` 在 metadata 存在时，只对 shader 声明的 material bindings 计算签名和资源布局。

**Requirements:**
- 兼容 metadata 缺失时的旧行为
- 不把无关 runtime bag 资源混进 pass signature
- 保留标量参数对 signature 的影响

**Tests Required:**
- ✅ metadata 存在时，无关 runtime 资源不影响 `ComputeSignature()`
- ✅ metadata 缺失时，旧行为保持不变

---

### Task 2: Connect Shader Metadata To Sig1 Layout (TASK-305)

**File:** `src/SomeEngine.Render/Materials/ShaderParamBag.cs`, `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`  
**Task Definition:** `docs/TASK-DETAIL.md#task-305-shaderasset-最小元数据通路`

**Description:**
让 Sig1 cache key 和 descriptor 构建优先依赖 metadata 声明的绑定布局，而不是 runtime bag 的全量资源。

**Requirements:**
- Sig1 cache key 只反映当前 pass 实际需要的资源布局
- descriptor 顺序稳定
- metadata 缺失时 fallback 到当前 bag 枚举路径

**Tests Required:**
- ✅ metadata 存在时，unused runtime resource 不影响 Sig1 cache key
- ✅ metadata 存在时，descriptor 只包含 declared bindings
- ✅ cache key / descriptor 构建保持稳定顺序

---

## Mandatory Workflow: Test-Driven Task Progression

1. **Task 1:** 实现 -> 写测试 -> **当前任务相关测试全绿**
2. **Task 2:** 实现 -> 写测试 -> **当前任务相关测试全绿**
3. 最后跑 **完整测试集**

**不得跳步。**

---

## Testing Requirements

- **Minimum:** 3 个以上新增/扩展测试
- **Quality bar:** 验证 metadata 对 runtime 行为的真实影响，而不是只断言字段存在
- **Required categories:** signature filtering / Sig1 layout filtering / fallback compatibility
- 提交前运行完整测试集

---

## Quality Standards

- 不要把未来 `MaterialStore / ShaderEntry` 重构混进本批
- 不要破坏 metadata 缺失时的旧路径
- 不要让 Sig1 layout 继续依赖 bag 中无关资源
- runtime 与测试命名保持和 `ShaderAsset.Metadata.MaterialBindings` 一致

---

## Report Requirements

报告文件：`.dev-workstream/reports/BATCH-05-REPORT.md`

必须包含：
- metadata 在 runtime 中新增了哪些消费点
- 对 fallback 行为的说明
- 新增/扩展测试列表
- 全量测试结果

---

## Success Criteria

- [ ] TASK-305 完成
- [ ] `MaterialPass` 签名支持 metadata filtering
- [ ] `ClusterShade` Sig1 cache/layout 支持 metadata filtering
- [ ] 相关测试新增并通过
- [ ] 全量测试通过
- [ ] report 已提交

---

## Reference Materials

- `assets/Schema/shader_asset.fbs`
- `src/SomeEngine.Assets/Importers/SlangShaderImporter.cs`
- `docs/materials/architecture.md`
- `docs/TASK-DETAIL.md`
- `.dev-workstream/TASK-DEFINITIONS.md`
