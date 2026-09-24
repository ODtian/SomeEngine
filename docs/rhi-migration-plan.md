# Diligent to SomeEngine.Rhi Migration Plan

## Boundary

The migration replaces Diligent as the GPU backend under the existing render architecture.

Keep these C# boundaries as the active path:

- `SomeEngine.Render.Graph.RenderGraph`
- `SomeEngine.Render.Pipelines.ClusterPipeline`
- runtime UI and input flow

Do not add a second public frame graph, a second public cluster pipeline, or public `Rhi*` high-level wrappers. Temporary implementation code can stay internal while it is being folded into the existing graph and pipeline.

`Diligent` code stays in the repository as a reference implementation until the migration no longer needs it. It is not the active runtime path.

## Migration Order

1. Isolate the reference runtime.
   - Keep the Diligent runtime path in a separate reference file.
   - Keep active `Program.cs` free of Diligent identifiers.
   - Keep temporary RHI fork types internal.

2. Move the active runtime back to the existing render boundary.
   - Runtime owns scene setup, input, asset loading, and debug UI state.
   - Rendering is submitted through `RenderGraph` and `ClusterPipeline`, not through direct `Render.RHI.FrameGraph` stage assembly.

3. Replace `RenderContext` internals.
   - Preserve the existing `RenderContext` name.
   - Back it with `SomeEngine.Rhi.IDevice`, `IQueue`, and `ISwapchain`.
   - Move shader creation, swapchain resize, presentation, and backend lifetime to `SomeEngine.Rhi`.

4. Replace `RenderGraph` physical resources and execution.
   - Keep `RenderGraph`, `RenderGraphHandle`, `RenderGraphBuilder`, `RenderGraphContext`, and `IRenderGraphPass`.
   - Convert descriptors, states, barriers, imports, extraction, transient pooling, and command submission to `SomeEngine.Rhi`.
   - Keep resource pooling in the existing `RenderGraph`; do not keep a separate RHI graph.

5. Port `ClusterPipeline` passes in place.
   - Port upload, BVH traversal, cull, binning, draw, HiZ, resolve, temporal, debug readback, material slot upload, and ImGui preview use sites.
   - Reuse the current pass/stage decomposition and shader asset reflection.
   - Fold useful temporary RHI stage code into the corresponding `Pipelines/ClusterRender` files, then remove the temporary fork.

6. Port runtime UI and input.
   - Keep input and UI state in runtime.
   - Render ImGui through the existing renderer utility boundary after it uses `SomeEngine.Rhi`.
   - Keep debug UI wiring against `ClusterPipeline` outputs and debug counters.

7. Remove active Diligent dependencies.
   - Remove Diligent package usage from active projects only after the migrated graph, pipeline, UI, and input build and run.
   - Keep reference code separated so deleting it later does not touch active files.

## Non-goals

- No public `Rhi` prefix on high-level rendering abstractions.
- No separate public active frame graph or cluster pipeline.
- No fast-fail branches that silently skip missing render work.
- No duplicate Diligent/RHI implementations of the same active C# boundary after a slice is migrated.
