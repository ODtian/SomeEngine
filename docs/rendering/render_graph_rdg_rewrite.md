# RenderGraph RDG Rewrite

> Authority: this document defines the final RenderGraph architecture target for SomeEngine.
> When it conflicts with `.dev-workstream/batches/BATCH-26-INSTRUCTIONS.md` or
> `.dev-workstream/batches/BATCH-27-INSTRUCTIONS.md` on graph authoring,
> frame ownership, history ownership, binding model, or resource lifetime,
> this document wins.
>
> This is a destructive rewrite target.
> It is not a compatibility migration.
> Legacy graph entry points, legacy ownership splits, and Diligent-shaped graph
> abstractions are deleted rather than preserved behind adapters.

## 1. Why The Current Model Must Be Rewritten

Current SomeEngine RenderGraph has several good RDG-adjacent pieces:

- frame-local graph rebuild
- setup / execute separation
- import / export primitives
- automatic barrier derivation
- culling and queue scheduling

But the full authoring model is still mixed.

Current mismatches:

- Frame-local truth is split across `RenderGraph`, `FrameSurfaces`,
  `RenderHistoryRegistry`, runtime host code, and mutable pipeline fields.
- Cross-feature exchange can still happen through string publication APIs
  such as `ShareResource` / `GetSharedResource`, which is not a true graph edge.
- Resource names do more than debug labeling; they still participate in
  identity and flow decisions.
- Binding hot paths still rely on reflection-time or string-time lookup in
  several cluster/material call paths.
- Queue assignment is not derived from one compiled fact source; current
  compile logic has split queue-decision paths.
- The graph object is long-lived and still owns too much runtime machinery:
  pools, views, binding-set caches, command lifetime bookkeeping, and
  compile/cache state all sit in the same type.
- Frame entry is not sealed around typed immutable frame facts. Too many facts
  still live in mutable host / pipeline state.

Result:

- terminology is RDG-like
- ownership is not RDG-like
- performance work gets trapped in local patches instead of one coherent model

## 2. External Design Inputs And Naming Baseline

Only mature maintained codebases are used for naming and structure direction.

| Source | Observed names | Decision for SomeEngine |
|---|---|---|
| Unreal Engine RDG | `FRDGBuilder`, `FRDGBlackboard`, `FRDGPass`, `TRDGLambdaPass`, `FRDGResource`, `FRDGViewableResource`, `FRDGTexture`, `FRDGBuffer`, `RegisterExternalTexture`, `RegisterExternalBuffer`, `QueueTextureExtraction`, `QueueBufferExtraction`, `FRDGPassHandle`, `FRDGPassRegistry` | Keep the `RenderGraph` / `RenderGraphBuilder` / `RenderGraphBlackboard` family. Keep internal pass/resource objects and handle-indexed registries; do not introduce `*Node` types. Keep `Import*` for external registration. Rename local `Export*` semantics to `Extract*` to match RDG extraction terminology without carrying the `Queue*` C++ wording. |
| O3DE Atom | `FrameGraph`, `FrameGraphCompiler`, `FrameGraphExecuteContext`, `FrameGraphExecuter` | Use explicit compiler / executor split. Keep execution context as a dedicated type instead of making the graph own all execution behavior directly. |
| Filament FrameGraph | `FrameGraph::Builder`, `FrameGraphResources`, `FrameGraphPass`, `Blackboard` | Use a dedicated resolved resource view for pass execution without copying Filament's `*Node` naming into the RDG target. |

Chosen type names for SomeEngine:

- `RenderGraph`
- `RenderGraphBuilder`
- `RenderGraphBlackboard`
- `RenderGraphCompiler`
- `RenderGraphExecutor`
- `RenderGraphResources`
- internal `Pass`
- internal `Resource`

Chosen method families:

- `ImportTexture` / `ImportBuffer`
- `CreateTexture` / `CreateBuffer`
- `ExtractTexture` / `ExtractBuffer`
- `AddRasterPass`
- `AddComputePass`
- `AddCopyPass`

Names explicitly rejected for this rewrite:

- `FrameTargetRegistry`
- `RenderHistoryRegistry`
- `ShareResource`
- `GetSharedResource`
- `BindPlan`
- `BindCache`
- `SlotPlan`
- `SlotCache`
- any `*Plan`, `*Run`, or `*Program` type used as a core architecture owner

## 3. Non-Negotiable Contract

The rewrite is complete only if all rules below become true.

### 3.1 Graph Authority

- One frame has one graph build.
- The graph is the only authority for frame-local resource identity,
  pass ordering, lifetime, and queue ownership.
- Frame-local resources do not live in side registries.
- Resource publication by string name is forbidden.

### 3.2 Cross-Frame Ownership

- Cross-frame persistence exists only as external renderer-owned state that is
  imported into the graph and extracted from the graph.
- No frame-local path may special-case history outside graph import/extract.
- HiZ, temporal history, debug carry-over, and any future history resources use
  the same import/extract contract.

### 3.3 Pass Authoring

- Pass setup declares resource usage only.
- Pass execute records RHI commands only.
- Execute cannot create graph resources, create passes, or resolve undeclared
  resources.
- Passes exchange data through typed pass data and blackboard state, not through
  string lookups or hidden mutable pipeline members.

### 3.4 Binding Model

- Steady-state hot paths do not map shader resource names to graph handles by
  string.
- Binding layouts, reflected slots, and pass-local bindings are prepared before
  dispatch / draw hot loops.
- Graph resource access is declared from typed pass data, not from runtime
  string switches.

### 3.5 Runtime Ownership

- `RenderGraph` is a frame-local authoring object.
- `RenderGraphCompiler` owns compilation only.
- `RenderGraphExecutor` owns execution only.
- Long-lived resource pools, view caches, bind-set caches, and command list
  reuse belong to renderer runtime services, not to the frame authoring object.

### 3.6 No Compatibility Path

- No Diligent-shaped compatibility namespace or adapter layer is introduced.
- No legacy graph-only cluster entry remains.
- No dual authoring APIs remain for the same concept.
- No transitional frame registry is allowed to survive as a hidden fallback.

## 4. Final Public Surface

The final public graph surface is intentionally small.

```csharp
public sealed class RenderGraph : IDisposable
{
    public RenderGraphBlackboard Blackboard { get; }

    public RenderGraphHandle ImportTexture(
        string name,
        TextureHandle texture,
        TextureDesc desc,
        ImportDesc import,
        ReadOnlySpan<TextureViewHandle> views = default);

    public RenderGraphHandle ImportBuffer(
        string name,
        BufferHandle buffer,
        BufferDesc desc,
        ImportDesc import,
        ReadOnlySpan<BufferViewHandle> views = default);

    public RenderGraphHandle CreateTexture(string name, TextureDesc desc);
    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc);

    public void ExtractTexture(
        RenderGraphHandle handle,
        ResourceState finalState,
        Action<TextureHandle, ResourceState> sink);

    public void ExtractBuffer(
        RenderGraphHandle handle,
        ResourceState finalState,
        Action<BufferHandle, ResourceState> sink);

    public void AddRasterPass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, TData> execute)
        where TData : class, new();

    public void AddComputePass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, IComputeCommands, TData> execute)
        where TData : class, new();

    public void AddCopyPass<TData>(
        string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, TData> execute)
        where TData : class, new();

    public void Compile(GraphQueues queues);
    public void Execute(GraphQueues queues, ISwapchain? swapchain = null, uint syncInterval = 1);
}
```

Public APIs removed from the current model:

- `ShareResource`
- `GetSharedResource`
- `MarkOutput`
- public feature publication by hidden graph side channels

Why `Extract*` replaces `Export*`:

- the operation is not a generic export system
- it is specifically graph lifetime extraction
- the name should match RDG semantics rather than storage semantics

## 5. Frame Data Model

The renderer feeds the graph with typed immutable frame facts.

Required frame data families:

- `FrameData`
- `ViewData`
- `SceneTextures`
- `ViewHistory`
- `DebugData`

Rules:

- `FrameData` contains CPU-side facts for the frame only.
- `ViewData` contains camera, jitter, viewport, and per-view render facts.
- `SceneTextures` is a typed blackboard struct containing frame-local graph
  handles such as scene color, depth, motion vectors, output color, and other
  same-frame surfaces.
- `ViewHistory` is the only persistent owner for temporal / history resources.
- `DebugData` is explicit frame input or explicit extracted output; it is not a
  hidden global sink.

`FrameSurfaces` and `RenderHistoryRegistry` are removed.
Any surviving data from them is folded into `SceneTextures` or `ViewHistory`.

## 6. Resource Model

There are only three meaningful graph resource origins:

- imported external resource
- graph-created transient resource
- extracted resource

Rules:

- Imported resources declare initial state, writable capability, and optional
  final state publication.
- Transient resources are graph-only and may alias.
- Extraction is a cull root.
- Import final-state requests and extraction final-state requests are part of
  graph compilation, not post-hoc side logic.
- Resource names are debug labels only. They are not a behavior key, cache key,
  or ownership key.
- Present is just an imported swapchain texture with final `Present` state.

This means:

- no `GetOrCreateNamedTexture`
- no runtime-side stable name lookup for frame-local resources
- no special built-in target path distinct from user-created graph resources

## 7. Pass Model

Every pass has:

- a fixed queue kind
- typed pass data
- declared reads
- declared writes
- an execute callback

Internal representation uses graph-owned RDG objects:

- `Pass`
- `Resource`

Each pass execute callback receives:

- `RenderGraphContext`
- typed pass data
- `IComputeCommands` when the pass is compute

`RenderGraphResources` is the resolved per-pass resource view used by execute
time.

## 8. Compiler Model

`RenderGraphCompiler` is the single owner for:

- pass/resource finalization
- dependency derivation
- cull-root propagation
- topological order
- queue assignment
- queue synchronization
- subresource lifetime
- aliasing plan
- barrier plan

Hard rule:

- queue assignment comes from one function and one fact model only
- there are no split `ModeQueue(...)` code paths
- transition compatibility, pass kind, and queue availability feed one unified
  queue resolver

Compile caches are allowed, but only as internal reuse of compiled results keyed
by a graph fingerprint.

Compile caches are not allowed to change the authoring model:

- users still rebuild the graph every frame
- there is no public static graph template API
- there is no alternate "fast path" authoring contract

## 9. Executor Model

`RenderGraphExecutor` is the only owner for frame execution.

It is responsible for:

- materializing imported / transient / extracted resources
- asking renderer runtime services for pooled resources and cached views
- recording command buffers
- applying compiled barrier plans
- submitting queues and fences
- retiring extracted resources

It is not responsible for:

- discovering dependencies
- inferring pass queues
- resolving hidden pipeline state

Long-lived renderer runtime services remain separate and are used by the
executor:

- transient allocator
- view cache
- bind-set cache
- pipeline store
- command-list reuse
- fence retirement

## 10. Binding And Shader Resource Rewrite

Current binding-name hot paths are removed.

Final rules:

- shader reflection produces stable slot metadata once
- pass code binds by reflected slot or generated accessor, not by resource name
- graph usage declaration is emitted from typed pass data, not by
  `switch(binding.Name)`
- `FindShadeResource`, `AccessFor(string name, ...)`, and similar string-driven
  resource routers are deleted

`PassBindings` may survive as a convenience builder, but only if:

- it stays slot-based
- it does not do string-based graph routing
- it materializes immutable binding packets before dispatch / draw hot loops

## 11. Renderer Integration Rewrite

The renderer does not talk to the graph through side registries anymore.

Final integration shape:

- Runtime / Editor build immutable `FrameData` and `ViewData`
- renderer-owned scene state builds or updates `ViewHistory`
- top-level renderer code imports backbuffer / history resources
- pipelines record passes into the graph using typed pass data
- blackboard structs carry graph handles between pipeline parts
- the graph compiles and executes once
- extracted resources update `ViewHistory`

Cluster-specific rewrite rules:

- `ClusterPipeline` loses hidden frame-local mutable state
- graph-only entry points are deleted
- pass outputs move through typed pass data and blackboard structs
- page, slot, instance, and material derived data owners become explicit
  renderer-owned services or explicit pass data, not ad hoc graph side state

## 12. Delete List

The rewrite is incomplete until all items below are gone:

- `ShareResource`
- `GetSharedResource`
- `MarkOutput`
- `RenderHistoryRegistry`
- `FrameSurfaces`
- graph resource exchange by string name
- string-driven steady-state binding/resource routers
- hidden graph-only cluster entry
- hidden frame-local pipeline state that is not part of typed frame input
- legacy or adapter-only Diligent graph wrappers
- queue assignment split across multiple competing resolver paths

## 13. Validation Gates

Required correctness gates:

- graph compile tests for cull roots, extraction liveness, import write rules,
  duplicate resource debug labels preserving distinct handles, queue assignment stability, and subresource state
  propagation
- pass tests for typed binding access and undeclared-resource rejection
- cluster/material/post/runtime integration tests covering scene color, depth,
  motion vectors, temporal history, HiZ history, and output presentation
- Runtime bounded frame run on the default RHI backend

Required architecture gates:

- no public API duplicate path for the same concept
- no frame-local registry
- no string-based pass resource routing in hot paths
- no legacy graph entry retained for convenience

Required profiler gates:

- external-profiler evidence before and after the rewrite
- `Runtime.RenderGraph.BeginFrame.RecordGraph` and queue submission work are
  measured on the rewritten model
- no in-engine managed/self profiler is introduced

## 14. Batch Mapping

This design maps to `TASK-330` / `BATCH-30`.

Task mapping:

- `TASK-330a`: public authoring contract rewrite
- `TASK-330b`: compiler rewrite
- `TASK-330c`: executor and runtime ownership split
- `TASK-330d`: frame data and history rewrite
- `TASK-330e`: binding and pass-data rewrite
- `TASK-330f`: cluster/material/post/runtime integration rewrite
- `TASK-330g`: delete legacy paths
- `TASK-330h`: validation, simplify, report, review

Completion means the renderer runs on the rewritten graph model only.
It does not mean the old model and the new model coexist behind switches.
