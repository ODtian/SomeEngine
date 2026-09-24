# Profile Boundary

Render and RHI runtime code must not grow profile-only event streams, counter hubs, miss lists, timing tables, or report buffers. Core Diagnostics is the only profiler center; `Profiler` is the only public profiler facade, and event/sink plumbing stays internal to Core Diagnostics. If a value has no non-profile consumer, it must not be stored in RenderGraph, RHI, or PipelineCache state.

Single center rule:

- Core Diagnostics owns profiler enablement, scopes, frame marks, queue submit marks, present marks, validation marks, and profiler sinks.
- RenderGraph, RHI backends, and PipelineCache may call Core Diagnostics, but must not own separate profile storage.
- Tracy and other external-profiler bridge sinks are Core Diagnostics implementations, not new centers.
- Render/RHI calls are facade calls, not a second event bus: storage, aggregation, output, and sink selection stay inside Core Diagnostics.
- Count-style diagnostics must enter through typed `Profiler.Report(...)` overloads. Do not expose a generic public counter/event API from runtime modules.

Removed centers:

- RenderGraph frame diagnostics, pass timings, device timings, queue reports, barrier reports, and binding reports.
- RenderGraph per-frame counter events and graph-cache hit/miss public counters.
- D3D12 backend counter helper instrumentation.
- Render binding instrumentation.
- PipelineCache profile-only miss lists, miss kinds, and counter snapshots. Runtime pipeline issue attribution is allowed when it feeds loading, fallback, or debug UI decisions.
- Public RenderGraph stats used only for profiling or cache observation.

Allowed runtime visibility:

- GPU debugger markers, such as PIX debug groups, because they are command stream annotations.
- Core Diagnostics scopes through `Profiler.BeginScope`, `Profiler.BeginGraphPass`, or `Profiler.BeginMarker`; disabled profiler builds take the existing no-op path.
- Core Diagnostics typed counters through `Profiler.Report(in BarrierStats)`, `Profiler.Report(in DescriptorStats)`, `Profiler.Report(in BindingStats)`, and `Profiler.Report(in PipelineStats)`, plus device timing samples through `Profiler.DeviceTime`. Runtime modules may only pass typed stat records; metric names, string categories, and sink routing stay internal to Core Diagnostics. These values must be forwarded to an external profiler sink; Core Diagnostics must not aggregate them into an engine-local summary.
- Runtime modules should call the `Profiler` facade directly for scopes and markers. `Profiler.IsActive` gates are allowed only when the guarded branch does profile-only work such as stats construction, timestamp allocation, or debug marker name formatting; the branch must not change normal rendering, resource lifetime, synchronization, or pipeline behavior.
- RenderGraph pass sub-scopes must be block scoped. `Resolve`, `Barriers`, and `Execute` markers must cover only their own work; do not use `using var` for these sub-scopes unless the intended lifetime is the rest of the surrounding method block.
- GPU timing samples must include the compiled pass index in the external-profiler event key. Duplicate pass names must not merge into one timing stream.

Disallowed runtime visibility:

- Per-frame counter events from RenderGraph execution.
- Backend counter helpers or generic counter-event APIs.
- Public generic `Profiler.Count` style APIs or public metric enums.
- Pipeline cache profile-only miss/counter lists in render-layer state.
- Public RenderGraph stats or graph-cache counters.
- New profile-only "event", "trace", "sink", or "report" centers inside RenderGraph or RHI.
- New profile-only centers inside PipelineCache.

Capture rule:

- Profiling code explicitly enables Core Diagnostics, backend tool data, or GPU debugger output.
- Normal execution does not sample CPU timings, write GPU timestamp queries, or populate pass and barrier timing lists.
- RenderGraph may allocate timestamp query/readback resources only when `Profiler.IsActive` and the device supports timestamp queries. The timestamp readback is reported after the submitted frame fence retires, then destroyed with the same pending-frame lifecycle as command buffers. Do not add a RenderGraph-specific profiler center for these samples.
- Backend counters that remain in source must be GPU debugger or driver-tool data, not engine-owned profile state.
- Runtime GPU/RHI validation is opt-in for profiling and debugging; use `--gpu-validation` when validation cost is the target.

Pipeline rule:

- RenderGraph only records and executes ready pipeline handles.
- Pipeline creation happens through `RenderContext` warmup, loading, initialization, or material change paths.
- Loading gates use `WaitRequired` for explicit ticket lists and `WaitSources` for registered asset/component sources. Both create required pipeline states without forcing optional states through an unlimited budget. Per-frame production warmup uses `ProcessPipelines` or `ProcessSources`, which return only the number of created pipeline states. `WarmupPipelines`, `InspectPipelines`, `WarmupSources`, and `InspectSources` build diagnostic reports and are for tooling or explicit inspection.
- `PipelineSourceLease` is lifecycle state, not profile state: it owns tickets so loading screens and material rebuilds can make deterministic wait/warmup decisions outside RenderGraph execution.
- Cluster material bins are optional pipeline users. Rebuilds may spend `ClusterPipeline.MaterialPipelineBudget`, but must not call `WaitRequired`; material dispatch and draw paths skip bins whose tickets are not ready.
- RenderGraph execution must not call `PipelineCache.ProcessQueue`; that keeps pipeline hitch attribution separate from graph compile, aliasing, barrier, and command recording cost.
