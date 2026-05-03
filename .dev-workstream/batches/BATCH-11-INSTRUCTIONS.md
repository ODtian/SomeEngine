# BATCH-11: Unified FrameTarget And History Resource System

**Status:** Completed  
**Phase:** Phase 3 - Render Pipeline Foundation  
**Tasks:** TASK-311a, TASK-311b, TASK-311c, TASK-311d, TASK-311e  
**Priority:** High  
**Depends On:** BATCH-10, TASK-309g, TASK-309h  

## Purpose

This batch turns frame outputs, intermediate render targets, and temporal history resources into one unified renderer contract.

The key rule is simple: user-defined FrameTargets and engine-standard FrameTargets must have no behavioral difference. Standard targets such as SceneColor, SceneDepth, HiZ, MotionVectors, or TAA history are only predeclared well-known keys. They must not receive privileged code paths that custom targets cannot use.

This batch is the bridge between the current render graph and a production game renderer. After it lands, PBR lighting, post processing, temporal effects, shadows, and user renderer extensions should all plug into the same resource declaration and extraction model.

## Core Rules

- Built-in and user-defined FrameTargets use the same declaration, lookup, override, extraction, history, and invalidation paths.
- Do not add APIs named like `DeclareBuiltinTexture`, `DeclareUserTexture`, `RegisterUserTarget`, or similar split-brain concepts.
- Standard targets are constants or presets only, for example `StandardFrameTargets.SceneColor`; they are not special storage.
- Do not force custom targets into a `User.*` namespace. Collision rules should be explicit and registry-driven.
- `FrameTargetHandle` is a stable semantic renderer handle. `RenderGraphTextureHandle` and `RenderGraphBufferHandle` remain frame-local graph handles.
- RenderGraph extraction primitives must not know about FrameTarget names. They only manage physical graph resources and extraction ownership.
- The existing `RenderGraph.Import(...)` APIs are real external import support. This batch adds symmetric extraction/history behavior and a renderer-level target registry on top of that, not a replacement for import.
- HiZ history should become the first migrated registry-backed history target. It must not remain a one-off special case.

## Required Reading

- `src/SomeEngine.Render/Graph/RenderGraph.cs`
- `src/SomeEngine.Render/Graph/RenderGraphBuilder.cs`
- `src/SomeEngine.Render/Graph/RenderGraphContext.cs`
- `src/SomeEngine.Render/Graph/PingPongHandle.cs`
- `src/SomeEngine.Render/ClusterPipeline.cs`
- `src/SomeEngine.Render/Cluster/ClusterHiZStage.cs`
- `src/SomeEngine.Render/Cluster/ClusterShade.cs`
- `src/SomeEngine.Render/Materials/ShaderParamBag.cs`
- `docs/TASK-DETAIL.md#task-311-统一-frametarget-与-history-资源系统`
- `.dev-workstream/reports/BATCH-10-REPORT.md`
- `.dev-workstream/DEBT-TRACKER.md`

## Task 1 - TASK-311a: Unified FrameTargetRegistry

### What It Is

Add the renderer-level registry that declares semantic frame targets before graph construction. A frame target can be standard or custom, texture or buffer, transient or history-backed, imported or graph-created. The registry resolves those declarations into frame-local RenderGraph handles each frame.

### Why It Is Needed

The current runtime manually imports color/depth and individual pipeline code owns its own target conventions. That is workable for a sample loop, but not for a renderer where post processing, temporal effects, shadows, editor overlays, debugging views, and user extensions all need to agree on resources.

This task prevents a future split where built-in passes receive privileged access while user passes are second-class.

### Implementation Method

- Add a small frame target model under `src/SomeEngine.Render/Frame/` or the nearest existing renderer namespace:
  - `FrameTargetKey`
  - `FrameTargetHandle`
  - `FrameTargetDeclaration`
  - `FrameTargetRegistry`
  - texture and buffer declaration descriptors
- Support declaration fields for:
  - key
  - resource kind
  - descriptor factory using current frame size and renderer settings
  - lifetime: frame-local, persistent history, imported external
  - initial state / intended usage metadata where useful
  - debug name
  - invalidation policy for resize, format change, sample-count change, and custom version changes
- Add standard presets as ordinary declarations:
  - SceneColor
  - SceneDepth
  - HiZ
  - optional future placeholders only when they are actually consumed by this batch
- Implement compatible merge rules:
  - repeated compatible declarations are allowed
  - incompatible declarations fail with clear diagnostics
  - explicit override is allowed only before registry freeze
  - no hidden preference for standard keys
- Freeze the registry before graph pass declaration for each frame.
- Add focused unit tests for custom target parity, declaration merge, override, freeze behavior, and resize invalidation.

## Task 2 - TASK-311b: RenderGraph Resource Extraction Primitives

### What It Is

Add graph-level extraction primitives that let a pass or caller request that a graph resource survive past `Execute` and become available as an external resource or next-frame history input.

### Why It Is Needed

`RenderGraph.Import(...)` already covers external resources entering the graph. The missing half is formal extraction: a graph-created resource becoming the next frame's imported history, swapchain output, debugger output, or user-owned target. Without this, history resources remain ad hoc stable-name cache behavior.

### Implementation Method

- Add extraction APIs to `RenderGraph`, using names such as:
  - `QueueTextureExtraction(RenderGraphTextureHandle handle, Action<ITextureView?> sink)` or an engine-appropriate equivalent
  - `QueueBufferExtraction(RenderGraphBufferHandle handle, Action<IBuffer?> sink)` or an engine-appropriate equivalent
  - optional typed extraction records if callbacks do not fit the codebase style
- Keep extraction independent of `FrameTargetRegistry`.
- Ensure extracted resources are considered graph outputs so DCE cannot prune their producer passes.
- Define ownership clearly:
  - graph-created extracted resources remain valid after execute according to the graph cache policy
  - imported resources are never accidentally disposed by graph-owned cleanup
  - extracted resource final states are tracked in `_persistedSubresourceStates` or a documented successor
- Add tests for:
  - extracted output keeps producer pass alive
  - non-extracted dead pass is still culled
  - extracted texture can be imported on the next frame with correct state tracking
  - extraction and explicit `MarkOutput` can coexist

## Task 3 - TASK-311c: History Lifetime Integration And HiZ Migration

### What It Is

Move temporal/history resources behind the unified target model, then migrate HiZ ping-pong history onto it.

### Why It Is Needed

HiZ is currently the best proof case for frame-to-frame GPU data. If HiZ remains special, future TAA, SSR, denoisers, exposure history, temporal shadows, and user temporal effects will repeat the same custom lifecycle code.

### Implementation Method

- Model history targets as ordinary `FrameTargetDeclaration` entries with persistent lifetime and invalidation rules.
- Keep ping-pong behavior as a policy or helper, not as a separate target universe.
- Update `ClusterHiZStage` and any `PingPongHandle` usage so:
  - previous-frame HiZ is acquired through the same registry path a custom history target would use
  - current-frame HiZ is produced through RenderGraph and extracted through the new extraction primitive
  - resize or descriptor mismatch invalidates history deterministically
- Add a custom history test target to prove users get the same behavior as HiZ.
- Preserve the current two-phase HiZ pipeline behavior and existing pass ordering.

## Task 4 - TASK-311d: Material Fallback Resource Binding

### What It Is

Close `DEBT-017` by ensuring material resource slots always have valid fallback bindings when content omits a texture or buffer.

### Why It Is Needed

The renderer cannot be a robust game rendering foundation if missing optional material resources can produce null bindings, unstable shader behavior, or backend-specific failures. This becomes more important once user-defined passes and targets make material combinations less controlled.

### Implementation Method

- Add renderer-owned default resources for common material fallbacks:
  - white texture
  - black texture
  - flat normal texture
  - default scalar buffer or equivalent for missing scalar blocks
- Update `ShaderParamBag` binding application so declared resource slots receive deterministic fallback resources.
- Keep fallback behavior visible in diagnostics so missing content can still be found.
- Add tests that load or construct materials with omitted optional resources and verify complete bindings.

## Task 5 - TASK-311e: Runtime And Pipeline Adoption

### What It Is

Make the sample/runtime frame setup and cluster pipeline use the unified registry rather than manually threading a separate set of built-in render targets.

### Why It Is Needed

The architecture only counts if normal renderer code and user extension code both exercise it. This task removes the old mental model where the runtime owns privileged targets and the graph only receives them after the fact.

### Implementation Method

- Introduce a per-frame object such as `FrameTargetSet`, `FrameResources`, or a similarly named type if it fits local style.
- Register standard target presets through the same API available to custom target declarations.
- Convert `Program.cs` frame setup to declare/import/resolve SceneColor and SceneDepth through the registry.
- Convert `ClusterPipeline.AddPasses` and related code to consume resolved target handles from the registry.
- Add at least one test or sample path that declares a custom frame target and verifies it can be produced, extracted, and consumed like a standard target.
- Keep APIs narrow; do not introduce a full renderer facade in this batch unless needed to finish the target lifecycle.

## Testing Requirements

Run focused tests for graph behavior, target registry behavior, material fallback binding, and cluster pipeline integration. Suggested commands:

```powershell
dotnet build SomeEngine.slnx --no-restore -v minimal
dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --verbosity quiet --filter "FullyQualifiedName~RenderGraph|FullyQualifiedName~FrameTarget|FullyQualifiedName~MaterialShader|FullyQualifiedName~MaterialAssetPipeline|FullyQualifiedName~Cluster"
```

Per repository instructions, if a `dotnet build`, `dotnet run`, or test command would restore packages or perform SDK setup, run it outside the sandbox with approval.

## Non-Goals

- Do not implement full PBR lighting in this batch.
- Do not implement the post-processing stack in this batch.
- Do not implement TAA, motion vectors, SSR, or exposure in this batch.
- Do not implement the full renderer facade here; keep that for a later batch.
- Do not create separate built-in and custom target APIs.
- Do not hide user targets behind extension-only or debug-only code paths.

## Success Criteria

- A standard SceneColor target and a custom user target can be declared through the same API.
- The cluster pipeline consumes standard targets through the same registry path custom passes would use.
- RenderGraph has explicit extraction primitives for graph-created resources.
- Extracted graph resources can become next-frame history inputs without relying only on stable-name cache behavior.
- HiZ history is backed by the same target/history mechanism that custom history targets use.
- Material fallback bindings are deterministic and covered by tests.
- Existing RenderGraph DCE, pass ordering, and automatic barrier tests remain valid.
- Documentation and trackers refer to unified FrameTarget semantics, not built-in/user split semantics.

## Report Requirements

The batch report must include:

- Changed files grouped by task.
- Public API shape for `FrameTargetRegistry` and extraction primitives.
- How custom and standard targets share the same code path.
- Any compatibility notes for existing `RenderGraph.Import(...)` users.
- Tests run and results.
- Any follow-up debt created by the batch.
