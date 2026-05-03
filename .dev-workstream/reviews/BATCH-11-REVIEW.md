# BATCH-11 Review

**Decision:** Approved  
**Date:** 2026-05-03  
**Reviewed Scope:** TASK-311a through TASK-311e

## Findings

No blocking correctness issues found in the implemented batch scope.

## Verification

- `dotnet build SomeEngine.slnx --no-restore -v minimal` passed.
- Focused BATCH-11 test set passed: 57 passed, 0 failed, 0 skipped.
- Full `SomeEngine.Tests` passed: 200 passed, 0 failed, 0 skipped.

## Architecture Check

- Built-in and custom FrameTargets share the same registry APIs.
- RenderGraph extraction is independent from `FrameTargetRegistry` keys.
- Extraction participates in DCE as a sink.
- HiZ now uses registry-backed history on the Runtime/Editor path.
- Material fallback binding is deterministic and backed by renderer-owned fallback resources.

## Residual Risk

- The review did not include a live RenderDoc/GPU frame validation pass.
- The compatibility `AddPasses(RenderGraph)` path still exists for old callers; it should be retired when a renderer facade lands.
- The next batches should avoid direct string target names and should consume `FrameTargetRegistry` or a higher-level frame resource wrapper.

## Follow-Up

- BATCH-12 should build HDR SceneColor and post-chain targets on top of `FrameTargetRegistry`.
- BATCH-13 should add motion vectors and temporal history using the same history APIs.
