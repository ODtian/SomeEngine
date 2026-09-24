# RenderGraph

RenderGraph owns a single frame of render work. Callers declare resources and pass dependencies; RenderGraph compiles that declaration into `CompiledGraph`, then records RHI command buffers with automatic resource barriers, queue waits, aliasing barriers, binding set reuse, and fence-based retirement.

## Public Surface

| Type | Role |
|------|------|
| `RenderGraph` | Frame-local declaration surface and owner-thread frame boundary |
| `RenderGraphBlackboard` | Typed same-frame exchange surface |
| `RenderGraphCompiler` | Compile orchestration and compile cache ownership |
| `RenderGraphExecutor` | Execute/runtime ownership for pooled resources, cached views, bind sets, and fence retirement |
| `RenderGraphHandle` | Frame-local resource handle |
| `RenderGraphBuilder` | Pass setup API for declared reads, writes, side effects, and state requirements |
| `RenderGraphContext` | Pass execution API for resolved RHI handles, views, bindings, and pipelines |
| `GraphQueues` | Graphics, compute, and copy queue set supplied by the caller |

Frame lifecycle:

```text
BeginFrame -> declare resources -> add passes -> Compile -> Execute
```

`Execute` performs compile when needed, builds alias placement, records command buffers for the requested queues, submits them with fence waits/signals, commits final/extract states, and retires resources after GPU completion.

## Async Boundary

Live `RenderGraph` mutation is owner-thread only. Background work may compile a
captured `RenderGraphSnapshot`, but that work must not call live declaration
APIs such as `CreateTexture`, `CreateBuffer`, `AddRasterPass`,
`AddComputePass`, `AddCopyPass`, or `Extract*`. The owner
thread records the frame through `BeginFrame(...)`, then hands only the frozen
declaration snapshot to background compile.

After the declaration has been recorded, callers may start `CompileAsync`. The
owner thread first captures a `RenderGraphSnapshot` containing pass modes,
side-effect flags, declared resource uses, resource descriptors, exports, and
final states. Background compilation consumes only that snapshot and its
schema; it does not read or mutate the live `_passes`, `_resources`, pass
declarations, resource pools, RHI handles, or view caches. While the snapshot
task is pending, live graph mutation APIs fail. `Execute`, `DumpText`,
`Compile`, `BeginFrame`, and `Dispose` join the pending task and publish the
compiled result at the owner-thread boundary before consuming or destroying
graph state.

The parallel execution layers are command buffer recording and GPU queue submission. `RecordBatches` may record independent queue batches in parallel when timestamp collection is disabled, and `GraphQueues` enables graphics, compute, and copy queue submission with explicit waits derived from compiled dependencies.

## Compile Model

Pass setup runs when passes are registered during frame declaration. The
structural `GraphSchema` identifies a stable declaration shape. When the same
shape is seen again and no render features are registered, RenderGraph still
records the live frame objects and execution delegates, but it reuses the
cached declaration snapshot, skips pass setup, skips schema writer calls, and
uses prebuilt schemas for the requested queue capabilities. A same-shape
mismatch is rejected at the declaration boundary before cached compile results
are reused.

`CompileGraph` consumes those recorded declarations. The expensive analysis result is cached by `GraphCache<CompiledGraph>` using a structural schema that includes:

- pass count, pass mode, side effects, and declared resource uses;
- resource kind, descriptors, import flags, initial states, publish/final-state flags, and initial-data presence;
- exports, final states, and async compute/copy availability.

Debug names are intentionally excluded from the schema. Renaming a pass or resource changes markers and error text, not graph identity. On a cache hit, RenderGraph materializes a fresh `CompiledGraph` copy and skips culling, dependency analysis, ordering, batching, resource gather, barrier gather, and resolve gather. Runtime state such as physical handles, current resource state, alias readiness, pending frames, and binding set ownership is not stored in the graph cache. Lookup uses a hash bucket with full schema equality, so hash equality is only a locality filter.

Within the same frame, an explicit `Compile()` result is also retained for matching queue capabilities. `DumpText()` and `Execute()` materialize copies of that retained result instead of running pass setup again. Any declaration change, such as adding a resource, pass, output, export, final state, or render feature, drops the retained result. Runtime's input version is a structural hash of graph-shaping data such as surface descriptors, render-world version, cluster mode switches, history readiness, material pipeline version, material slot capacity, ImGui buffer descriptors, and debug capture pass requests; frame index and camera matrices are execution data and are not part of graph identity.

## Queue Model

Pass mode is fixed at registration:

- `AddPass` records on the graphics queue unless the pass was registered as copy.
- `AddCopyPass` records on the copy queue when a copy queue is supplied.
- `AddComputePass` records on the compute queue when a compute queue is supplied.

Compile performs a deterministic queue-aware topological schedule before batching. Ready passes first preserve dependency order; when async compute or copy is available, the scheduler keeps taking ready work from the current queue to form larger queue batches. Side-effect passes remain declaration-order anchors, so external effects are not crossed by later independent work. This policy is structural only: it does not estimate pass cost or use timing heuristics.

`BuildBatches` groups the scheduled live passes by queue. `BuildQueueLinks` derives cross-queue waits from dependency edges. Execution submits one command buffer per batch and uses per-queue fences for batch synchronization. If all batches run on graphics and no final command buffer is needed, the last batch also signals the frame fence.

When a multi-queue frame has no final resolves or transitions, RenderGraph does not record an empty graphics command buffer just to close the frame. It submits a graphics queue wait/signal packet with no command buffers, so the frame fence still represents all queue work without adding command recording overhead.

## Barrier Model

Compile produces `TransitionBatch` data for each live pass and for final states. Execution then validates the runtime resource state and emits only needed RHI barriers:

- whole-texture transitions use a direct fast path when possible;
- required transitions use compile-time state directly when the runtime resource state is trusted and the resource is not imported or aliased;
- compatible states avoid redundant barriers;
- unordered-access dependencies still emit UAV barriers when required;
- aliasing barriers are emitted before first use of placed resources;
- final-state transitions are emitted only for requested final states and exports.

RHI backends still validate submitted barriers, but RenderGraph is responsible for the high-level dependency and state model.

## Resource Model

Transient buffers and textures are created through `CreateBuffer` and `CreateTexture`. Imported resources enter with `ImportBuffer` and `ImportTexture`; persistent state handoff is expressed through `ExtractTexture` / `ExtractBuffer` or an explicit `ImportDesc.FinalState`, not through an import-time publish callback.

RenderGraph manages:

- descriptor-compatible transient resource pools;
- placed resource aliasing for eligible device-local resources;
- view lifetime and view-based binding set eviction;
- command buffer, view, heap, and exported resource retirement through GPU fences.

`ViewHistory` owns cross-frame history resources. RenderGraph only owns
current-frame handles and current-frame dependencies.

## Binding Model

Binding sets are owned by RenderGraph. `PassParameters` and `PassBindings` provide declared binding inputs and stable hashes so execution can reuse binding sets without rebuilding descriptor arrays when the same layout/resources repeat. `BindingIndex` records layout and view references so invalidating a layout or view evicts only the affected binding sets.

Shader reflection may drive both pass setup and execution binding. `RenderGraphBuilder.Use(ReflectedBinding, handle)` reads the shared RHI `BindRules` table to declare constant-buffer, shader-resource, and unordered-access state requirements; sampler and acceleration-structure bindings are rejected because they do not represent frame-local RenderGraph resources. Passes with intentional write-only UAV semantics may still override access while using `BindRules` for state selection.

Production passes build bindings through `RenderGraphContext.Bindings` and submit them with `SetParameters`. `RenderBindings.Set`, stack-allocated `BindingResourceDesc` arrays, and per-pass binding resource lists are not used in Graph, Pipelines, or UI production code. `BindInput.Fill` writes material fallback results into `PassBindings`; the explicit `FillList` helper exists only for tests that verify the fallback rule itself.

## Pipeline Boundary

Pipeline creation is outside RenderGraph execution. Passes and bins store `PipelineTicket`; execution resolves ready `PipelineHandle` values through `RenderGraphContext.GetPipeline`, and RenderGraph tracks handles used by the submitted frame for fence-based `PipelineCache` retirement.

Asset loading, component systems, and material systems declare expected pipeline states through `PipelineCollector` or `IPipelineSource`. For one-shot rebuilds, `RenderContext.CollectPipelines` returns tickets that the caller owns. For long-lived asset, component, or scene sources, `RenderContext.AddPipelineSource` registers the source and returns a `PipelineSourceLease`; `RefreshSources` rebuilds the held ticket lease when the source set changes or any registered source reports a new `Version`.

`WaitRequired` is the loading-screen gate for already collected tickets and creates only required pipeline states. `WaitSources` is the equivalent gate for registered sources. `ProcessPipelines` and `ProcessSources` apply an explicit creation budget for remaining queued states without building a diagnostic report. `WarmupPipelines`, `InspectPipelines`, `WarmupSources`, and `InspectSources` report queued or failed tickets with owner attribution when callers need diagnostics. Material rebuilds use `PipelineLease` to hold collected tickets until bins take ownership. RenderGraph execution must not process the pipeline creation queue.

Cluster material pipelines use the same boundary: material compute and graphics tickets are optional frame work. Material rebuilds assign tickets to bins and apply only `ClusterPipeline.MaterialPipelineBudget`; they do not call `WaitRequired`. Material dispatch and draw paths resolve tickets with `PipelineNeed.Optional` and skip bins that are queued or failed. Engine-owned pipelines such as fixed cluster, fullscreen, and ImGui passes remain required and may use `WaitRequired` during initialization or loading gates.

## Profiling Boundary

RenderGraph may call Core Diagnostics scopes, typed Core counters, device timestamp samples, and GPU debug markers. It must not own profile-only report tables, counter hubs, or cache hit/miss counters. See `docs/rendering/profile_boundary.md`.
