# BATCH-10 Report: Render Prepare And Cache Lifecycle Rework

## Summary

BATCH-10 completed the corrective rework for the material pass ECS authoring path, render world extract path, cluster prepare lifecycle, and cache ownership cleanup.

The main outcome is that material pass entities now remain the single source of pipeline feature declarations. Extract copies those pass entities into RenderWorld once, and cluster prepare consumes RenderWorld feature components/tags directly. `PrepareFrame` now owns CPU-side derived data preparation; `AddPasses` only consumes prepared state and declares RenderGraph passes.

## Files Modified

- `.dev-workstream/batches/BATCH-10-INSTRUCTIONS.md`
- `src/SomeEngine.Render/Assets/MaterialAssetLoader.cs`
- `src/SomeEngine.Render/Materials/IBaker.cs`
- `src/SomeEngine.Render/Materials/MaterialAuthoringBakers.cs`
- `src/SomeEngine.Render/Materials/Material.cs`
- `src/SomeEngine.Render/Materials/BinSpace.cs`
- `src/SomeEngine.Render/Systems/MaterialPassBaker.cs`
- `src/SomeEngine.Render/Systems/RenderWorldExtractor.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterMaterialSlotPreparer.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/ClusterPipeline.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Stages/ClusterShade.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/RasterPSOBuilder.cs`
- `src/SomeEngine.Render/Pipelines/GlobalPsoCache.cs`
- `src/SomeEngine.Render/Pipelines/SRBPool.cs`
- `src/SomeEngine.Render/Pipelines/ClusterRender/Components/*.cs`
- `src/SomeEngine.Assets/AssetDatabase.cs`
- `src/SomeEngine.Runtime/Program.cs`
- `src/SomeEngine.Editor/Program.cs`
- focused tests under `tests/SomeEngine.Tests/Systems/`, `tests/SomeEngine.Tests/Pipelines/`, `tests/SomeEngine.Tests/Materials/`, `tests/SomeEngine.Tests/Assets/`

## Task Check

- TASK-310a: Completed. RenderWorld pass entities are created by `EntityStore.CopyEntity(sourcePass, runtimePass)`, then render metadata is appended. Material instances also copy pass entities and preserve feature tags/components.
- TASK-310b: Completed. `BinSpace` now has a real dirty lifecycle. `PrepareFrame` performs extract, feature gather, slot folding, bin rebuild, and PSO group rebuild. `AddPasses` no longer performs bin/PSO rebuild.
- TASK-310c: Completed. Cluster pipeline registers feature groups against RenderWorld feature entity spans and rebuilds shade/raster/deform PSO groups from prepared bins.
- TASK-310d: Completed. AssetDatabase no longer uses production reflection registration. Shader cache lifetime and PSO cache ownership are tightened. SRBPool now exposes a dispose lifecycle.

## Test Results

- Passed: `dotnet build SomeEngine.slnx --no-restore -v minimal`
- Passed: `dotnet test tests/SomeEngine.Tests/SomeEngine.Tests.csproj --no-restore --no-build --verbosity quiet --filter "FullyQualifiedName~MaterialPassBaker|FullyQualifiedName~RenderWorldExtractor|FullyQualifiedName~ClusterMaterialSlotPreparer|FullyQualifiedName~BinSpace|FullyQualifiedName~ShaderGroup|FullyQualifiedName~AssetDatabase|FullyQualifiedName~GeneratedAssetPipelineCatalog|FullyQualifiedName~MaterialAssetPipeline"`

Observed warnings:
- Existing Runtime nullable warnings remain.
- Existing `Tmds.DBus.Protocol` vulnerability warning from DagVisualizer remains.

## Design Decisions

- Used Friflo ECS `EntityStore.CopyEntity` as the only material pass extraction mechanism instead of introducing component clone registries, reflection, or per-feature copy lists.
- Added explicit `CopyValue` declarations only to component types that hold references and are expected to be copied by ECS.
- Replaced the previous baker class tree with three direct `IBaker<T>` implementations: tag entry, component entry, and shader attribute entry. This keeps the Baker layer but removes arrays of small baker classes and base classes.
- Replaced the old hidden RenderWorld iteration-order dependency in `ClusterMaterialSlotPreparer` with an in-place sort by `(source entity, local material slot, pass index, entity id)`.
- Kept dictionaries in cache/setup paths, but not in the per-dispatch execution path.

## Issues Encountered

- `EntityStore.CopyEntity` is a static API and requires `CopyValue` for components containing reference fields. This was fixed at the component boundary.
- Existing slot preparer tests were too weak: they could pass even when all fields pointed at the wrong pass entity. The test now uses two local material slots and distinct raster/shade pass entities so the old bug fails deterministically.
- A bulk replacement command partially failed on one file due an access issue after modifying earlier files. Remaining shader lifetime edits were finished with `apply_patch`, and the final grep confirms no `using var ...CreateShader(context, ...)` remains.

## Edge Cases Covered

- Material instance preserves pass tags/components and rewrites `MaterialRef.Owner`.
- RenderWorld rebuilds when material version changes.
- RenderWorld pass entities retain pass components/tags after extract.
- Cluster slot folding no longer depends on arbitrary ECS entity iteration order.
- Duplicate local material slots now prove that field-specific pass selection is correct.

## Known Issues

- Static SRB pools are still declared by legacy pass classes, but `SRBPool.DisposeAll()` now clears all registered pools from the cluster pipeline dispose path so device shutdown no longer leaves pooled SRBs alive.
- Runtime nullable warnings predate this batch and were not part of the render prepare/cache migration.
