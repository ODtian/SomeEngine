# Render Feature Integration

This document records the current integration contract for features around
`ClusterPipeline`. It intentionally does not expose Cluster private globals.

## Current Contract

`ClusterPipeline.AddPasses` returns only the frame outputs that other code may
depend on:

```csharp
SceneTextures sceneTextures = FrameResources.CreateSceneTextures(graph, frame, view);

FrameOutputs outputs = clusterPipeline.AddPasses(
    graph,
    renderWorld,
    sceneTextures,
    histories,
    cameraHistory,
    temporalState);
```

The public output contract is:

| Output | Meaning |
|---|---|
| `SceneColor` | scene color before post-tonemap |
| `PostSceneColor` | post-tonemap scene color |
| `MotionVectors` | frame motion vectors |
| `SceneDepth` | scene depth |

The following are not public feature contracts:

- `ClusterBuffers`;
- `InstanceFrame`;
- `ClusterMeshes`;
- `SlotFrame`;
- `ClusterSlotBuffer`;
- `MaterialItems`;
- `MaterialBin`;
- intermediate cull/bin/raster records;
- any `LastGlobalResources` or `Last*Output` cache.

Those objects either own Cluster-private lifecycle or contain frame-local
`RenderGraphHandle` values. A feature that needs one of them is not using a
stable public pipeline contract.

## Resource Exchange

Features exchange frame resources through typed outputs and
`RenderGraphBlackboard`:

```csharp
graph.Blackboard.Set(sceneTextures with
{
    PostSceneColor = outputs.PostSceneColor,
    MotionVectors = outputs.MotionVectors,
});
```

A later feature reads only typed data that was explicitly published:

```csharp
if (!graph.Blackboard.TryGet(out SceneTextures scene))
    return;

graph.AddComputePass(
    "Outline",
    builder =>
    {
        builder.Read(scene.PostSceneColor, ResourceState.ShaderResource);
        builder.Read(scene.SceneDepth, ResourceState.ShaderResource);
        builder.Write(outlineMask, ResourceState.UnorderedAccess);
    },
    (context, pass) =>
    {
        // Bind scene.PostSceneColor / scene.SceneDepth / outlineMask and dispatch.
    });
```

Do not recover private resources by matching graph resource names. Cross-feature
exchange goes through typed pass outputs or typed blackboard state only.

## Feature Shapes

### Post Effects

Post effects consume public frame outputs and write their own RenderGraph resources.
Examples: outline, SSR, SSAO, soft particles, temporal debug overlays.

They should not depend on Cluster cull/bin/raster internals. If a post effect
needs object selection or material metadata, that state needs a separate owner and
an explicit feature contract.

### Alternate Views

Shadows, planar reflections, minimaps, and probes are not implemented by
grabbing the main Cluster pipeline globals. They need one of these explicit
forms:

- a second pipeline instance with its own frame/history state;
- a dedicated feature owner that imports shared persistent GPU owners through a
  deliberate API;
- a new stable output contract added to the producing pipeline.

Sharing a private `ClusterMeshes`, `ClusterBuffers`, or `InstanceFrame`
between unrelated features is not a valid contract. Those types are scoped to
the pipeline instance and current frame.

### Debug Views

Debug features may read explicitly shared outputs, or use a debug owner that is
created by the pipeline. Debug code must not become a back door for reading
private stage state.

## When To Add A Contract

Add a new output or owner only when it has a real lifecycle:

- stable ownership;
- validation rules;
- resource state or cache invalidation;
- explicit reset/dispose timing;
- tests that prove the lifecycle.

Do not add a wrapper just to move several parameters around. A type that only
bundles arguments should stay local to the call site or be deleted.

## Invalid Patterns

These patterns are intentionally unsupported:

```csharp
var globals = pipeline.LastGlobalResources;
var cull = pipeline.LastCullOutput;
var bins = pipeline.LastRasterBinOutput;
var raster = pipeline.LastOpaqueRasterOutput;
```

They reintroduce a hidden global output surface and let other features depend on
Cluster-private frame handles. Use `FrameOutputs`, `RenderGraphBlackboard`, or a
new owner with a real lifecycle instead.
