# BATCH-11 Report: Unified FrameTarget And History Resource System

**Status:** Completed  
**Tasks:** TASK-311a, TASK-311b, TASK-311c, TASK-311d, TASK-311e  
**Date:** 2026-05-03

## Summary

BATCH-11 delivered a unified renderer-level FrameTarget system and connected it to RenderGraph extraction, HiZ history, material fallback bindings, and the Runtime/Editor frame setup.

Standard targets and custom targets now use the same `FrameTargetRegistry` declaration and resolve path. Standard names such as `StandardFrameTargets.SceneColor`, `SceneDepth`, and `HiZ` are only well-known keys; they do not receive separate built-in-only APIs.

## Files Modified

### TASK-311a - Unified FrameTargetRegistry

- `src/SomeEngine.Render/Frame/FrameTargetRegistry.cs`
- `tests/SomeEngine.Tests/FrameTargetRegistryTests.cs`

### TASK-311b - RenderGraph Resource Extraction Primitives

- `src/SomeEngine.Render/Graph/RenderGraph.cs`
- `tests/SomeEngine.Tests/RenderGraphTests.cs`

### TASK-311c - History Lifetime Integration And HiZ Migration

- `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterHiZStage.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
- `src/SomeEngine.Runtime/Program.cs`
- `src/SomeEngine.Editor/Program.cs`

### TASK-311d - Material Fallback Resource Binding

- `src/SomeEngine.Render/Materials/ShaderParamBag.cs`
- `src/SomeEngine.Render/Materials/MaterialResourceFallbacks.cs`
- `src/SomeEngine.Render/Materials/MaterialFallbackResources.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialShadePass.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`
- `tests/SomeEngine.Tests/Materials/MaterialAssetPipelineTests.cs`

### TASK-311e - Runtime And Pipeline Adoption

- `src/SomeEngine.Runtime/Program.cs`
- `src/SomeEngine.Editor/Program.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`

### Workflow State

- `.dev-workstream/batches/BATCH-11-INSTRUCTIONS.md`
- `.dev-workstream/TASK-TRACKER.md`
- `.dev-workstream/DEBT-TRACKER.md`
- `docs/TASK-DETAIL.md`

## Task Check

| Task | Result |
|---|---|
| TASK-311a | Added `FrameTargetRegistry`, `FrameTargetKey`, `FrameTargetHandle`, declarations, lifetimes, compatible merge, override, freeze, invalidation, and standard keys as presets only. |
| TASK-311b | Added `QueueTextureExtraction` and `QueueBufferExtraction`; extraction resources are sink resources and keep producer passes alive under DCE. |
| TASK-311c | Migrated the main HiZ path to registry-backed history when a registry is supplied; legacy `PingPongHandle` remains only for compatibility with old `IRenderFeature.AddPasses(RenderGraph)` callers. |
| TASK-311d | Added deterministic material fallback binding policy and renderer-owned fallback GPU resources for white, black, flat-normal, default buffer, and default sampler. |
| TASK-311e | Runtime and Editor now import/declare SceneColor and SceneDepth through `FrameTargetRegistry`; `ClusterPipeline` resolves them from the registry. |

## Public API Shape

- `FrameTargetRegistry.BeginFrame(RenderGraph, FrameTargetContext)`
- `DeclareTexture`, `DeclareBuffer`, `ImportTexture`, `ImportBuffer`
- `OverrideTexture`, `OverrideImportedTexture`
- `ResolveTexture`, `ResolveBuffer`
- `ResolveHistoryTexture`, `ResolveHistoryBuffer`
- `Invalidate`
- `RenderGraph.QueueTextureExtraction`
- `RenderGraph.QueueBufferExtraction`
- `ShaderParamBag.ApplyFallbacks`
- `ShaderParamBag.ApplyTo(..., MaterialResourceFallbacks?)`

## Design Decisions

- The registry does not expose built-in/user split APIs. A standard target and a custom target differ only by key value.
- `FrameTargetHandle` is semantic and stable within the renderer contract. `RenderGraphHandle` remains single-frame and graph-local.
- RenderGraph extraction is intentionally key-agnostic. It only knows graph handles and physical resource sinks.
- HiZ history uses ping-pong resource names internally through the registry-backed history state so the previous frame and current frame never alias the same cached texture.
- The old cluster `AddPasses(RenderGraph)` path falls back to `SceneColor`/`ColorTarget` and `SceneDepth`/`DepthTarget` names for compatibility, but Runtime/Editor now exercise the registry path.

## Test Results

Passed:

```powershell
dotnet build SomeEngine.slnx --no-restore -v minimal
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~RenderGraph|FullyQualifiedName~FrameTarget|FullyQualifiedName~MaterialShader|FullyQualifiedName~MaterialAssetPipeline|FullyQualifiedName~Cluster"
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet
```

Focused test result: 57 passed, 0 failed, 0 skipped.  
Full `SomeEngine.Tests` result: 200 passed, 0 failed, 0 skipped.

Notes:

- The sandboxed `dotnet build` failed before execution because .NET tried to write first-time-use files under `C:\Users\CodexSandboxOffline`. The verified build and test runs used repo-local `DOTNET_CLI_HOME=F:\SomeEngine\.dotnet_home` outside the sandbox.
- Build still reports pre-existing project warnings, including the known `Tmds.DBus.Protocol` advisory warning and existing test/analyzer warnings.

## Known Issues

- No live GPU frame capture was taken in this batch.
- Full Runtime visual validation remains a follow-up activity for the next rendering batch.
- `DEBT-002` remains open: `Program.cs` is still a large mixed runtime file, although frame target setup is now cleaner.
- `DEBT-015` remains open: `docs/assets/asset_identity.md` still has old material identity wording.

## Completion Statement

BATCH-11 is complete. The renderer now has the resource contract needed for the next HDR/post/temporal rendering batches without privileging built-in targets over user targets.
