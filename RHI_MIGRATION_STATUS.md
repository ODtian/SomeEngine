# SomeEngine RHI Migration Status

## 2026-05-23 Pipeline/Graph Fold Update

This update supersedes the older "Current State", "Progress", and "Known Gaps" sections below for the active ClusterPipeline graph.

Completed in the active pipeline/graph slice:

- ClusterPipeline now constructs and runs only the folded `Pipelines/ClusterRender` RHI stages.
- Temporary `src/SomeEngine.Render/RHI/Cluster*.cs`, RHI `PostTonemapPass`, RHI `TemporalResolvePass`, and RHI `ImGuiRenderer` files are gone from the active file set.
- The active `src/SomeEngine.Render/Pipelines/ClusterRender` file set has no legacy `Stages/` files and no old Diligent pass files in the active source tree.
- Phase-1 and phase-2 cluster flow is wired through RHI graph stages: upload, BVH patch, traverse, cull, raster bin, optional deform cache/deform, SW raster, depth merge, HW draw, HiZ build, shade bin, material shade, temporal resolve, post tonemap, and ImGui.
- HiZ history is graph-owned through `RenderHistoryRegistry`; phase 1 uses previous/current seed correctly, and final HiZ writes the current history texture.
- Debug counter readback is now RHI-native. `ClusterPipeline` copies candidate count, draw args, candidate dispatch args, phase-2 candidate count, and phase-2 draw args into a CPU readback buffer and populates the debug properties from RHI `MapBuffer(Read)`.
- HiZ debug dump readback is now RHI-native through `BufferReadbackPasses`; `DebugHiZData` is populated and the text dump path is restored without changing shaders.
- GPU page fault readback is now wired through graph copy/extraction from `PageFault` to persistent `PageFaultReadback`, then decoded back into `ClusterStreamer`.
- Material shade no longer silently falls back to resolve when active raster features exist but no RHI material PSO group was built. `UseVisBuffer=false` remains the explicit resolve/debug path.
- The fake transparent alias was removed. `OpaqueAndTransparent`, `IncludeTransparentPass`, and `LastTransparentRasterOutput` are no longer active public surface in this pipeline because there was no real RHI graph branch behind them.
- No files under `assets/Shaders` were modified in this slice.

Static source gates run after the update:

```text
No matches in src/SomeEngine.Render, src/SomeEngine.Runtime, src/SomeEngine.Editor for:
using Diligent
Diligent.
IRenderDevice
IDeviceContext
ISwapChain
IShaderResourceBinding
IPipelineState
IEngineFactory
DiligentGraphics
UsesDiligentEngine
--diligent
--diligent-reference
DiligentReference
```

```text
src/SomeEngine.Render/RHI contains only:
BufferCopyPasses.cs
BufferReadbackPasses.cs
BufferUploadPasses.cs
ComputeProgram.cs
RenderContext.cs
ShaderBindings.cs
TextureCopyPasses.cs
```

```text
No active source matches for:
SomeEngine.Render.RHI.Cluster
RhiCluster
ClusterRender\Stages
ClusterDeformBinStage
OpaqueAndTransparent
IncludeTransparentPass
LastTransparentRasterOutput
DebugCandidateCount => 0
DebugDraw* => 0
DebugPhase2* => 0
DebugCandidateArgsX => 0
DebugPhase2Count => 0
```

Build/test/run were intentionally not executed in this update because the current instruction is to ignore compile/test work for now.

Date: 2026-05-22

This document records the current migration conclusion for the active render path. It is based on the current source scan and worker reports in this session, without reading existing documentation, git state, or running build/test/run.

## Hard Target

The final active renderer has exactly one runnable path:

```text
Runtime / Editor
  -> SomeEngine.Render.RHI.RenderContext.Initialize
  -> SomeEngine.Rhi device / graphics queue / swapchain
  -> SomeEngine.Render.Graph.RenderGraph.BeginFrame
  -> ClusterPipeline.PrepareFrame / SetCamera / AddPasses
  -> RHI-backed cluster passes
  -> RenderGraph.Execute
  -> RenderContext.Present
```

No active path should contain:

- Diligent initialization.
- Diligent backend flags or fallback runtime.
- Diligent compatibility shims.
- A second public `RhiRenderGraph`.
- Duplicate cluster implementations split between active pipeline code and temporary RHI code.

The project-level render dependency is `SomeEngine.Rhi`; D3D12 backend creation is owned by `RenderContext`.

## Current State

The active source scopes `src/SomeEngine.Render`, `src/SomeEngine.Runtime`, and `src/SomeEngine.Editor` currently have no Diligent blockers from the static gate terms:

- `using Diligent`
- `Diligent.`
- `IRenderDevice`
- `IDeviceContext`
- `ISwapChain`
- `IShaderResourceBinding`
- `IPipelineState`
- `IEngineFactory`
- `DiligentGraphics`
- `UsesDiligentEngine`
- `--diligent`
- `--diligent-reference`
- `DiligentReference`

The three active project files also have no Diligent package or project references.

Runtime and Editor are already shaped as RHI hosts. Runtime creates `SomeEngine.Render.RHI.RenderContext`, builds the frame surfaces from the RHI swapchain texture, executes `RenderGraph`, and presents through `RenderContext.Present`. Editor now follows the same broad shape and has bounded `--frames` plus command-line profiling.

The main remaining architectural issue is not Diligent. It is that temporary cluster implementation code under `src/SomeEngine.Render/RHI/Cluster*` has been partially folded into `src/SomeEngine.Render/Pipelines/ClusterRender`, but compute-side duplicate files still exist in both places. The target is to finish the fold, make `ClusterRender` own the active cluster stages, and then delete the replaced temporary RHI cluster files.

## Progress

Completed:

- Active Runtime/Editor no longer expose Diligent backend selection.
- Runtime present ownership moved to `RenderContext.Present`.
- Editor host path was tightened to `RenderContext -> RenderGraph -> ClusterPipeline -> Execute -> Present`.
- Runtime bounded run and frame profiling remain available.
- Editor bounded run and frame profiling were added.
- Graphics/material/UI fold started:
  - `ClusterDrawStage` moved into `Pipelines/ClusterRender`.
  - `ClusterResolveStage` moved into `Pipelines/ClusterRender`.
  - `ClusterMaterialSlotStage` moved into `Pipelines/ClusterRender`.
  - `ImGuiRenderer` moved into `Pipelines/ClusterRender`.
  - pipeline tail now uses active `PostTonemapPass` and `TemporalResolvePass`.
- Graph/helper coverage improved:
  - texture copy helper added.
  - buffer readback staging helper added.
  - buffer upload now rejects empty uploads instead of silently returning.
  - existing public texture-copy helper delegates to the RHI helper.

Still in progress:

- Compute-stage fold:
  - `ClusterUploadStage`
  - `ClusterBvhPatchPass`
  - `ClusterResources`
  - `ClusterTraverseStage`
  - `ClusterCullStage`
  - `ClusterRasterBinStage`
  - `ClusterHiZBuildStage`
- Removal of duplicate `src/SomeEngine.Render/RHI/Cluster*` files after active `ClusterRender` replacements are complete.
- Repointing folded graphics stages away from `SomeEngine.Render.RHI.ClusterFrameResources`, `ClusterCullOutput`, and `ClusterRasterBinOutput`.
- Static gate after all folds.
- Only after the static gate: non-sandbox build/test/run and compile-error repair.

## Final Module Ownership

### Host Layer

Files:

- `src/SomeEngine.Runtime/Program.cs`
- `src/SomeEngine.Editor/Program.cs`

Responsibilities:

- Create a Silk window with `GraphicsAPI.None`.
- Create and initialize `RenderContext`.
- Own asset database, world, input, debug UI, camera, and frame timing.
- Create one `RenderGraph`.
- Create one `ClusterPipeline`.
- Per frame:
  - update world/input.
  - read RHI swapchain/backbuffer desc.
  - prepare frame surfaces.
  - call `ClusterPipeline.PrepareFrame`.
  - call `ClusterPipeline.SetCamera`.
  - call `ClusterPipeline.AddPasses`.
  - add UI pass where applicable.
  - mark output.
  - execute graph.
  - present through `RenderContext`.

Host layer must not know about backend selection beyond constructing `RenderContext`.

### RenderContext

Type:

```csharp
namespace SomeEngine.Render.RHI;

public sealed class RenderContext : IDisposable
{
    public SomeEngine.Rhi.IDevice? GraphicsDevice { get; }
    public SomeEngine.Rhi.IQueue? GraphicsQueue { get; }
    public SomeEngine.Rhi.ISwapchain? GraphicsSwapchain { get; }
    public SomeEngine.Rhi.TextureDesc GraphicsDepthBufferDesc { get; }

    public void Initialize(IWindow window, bool enableValidation = false);
    public void Resize(uint width, uint height);
    public void Present();
    internal ShaderModuleHandle CreateShaderModule(ShaderAsset asset, string entryPointName);
    public void Dispose();
}
```

Responsibilities:

- Create `SomeEngine.Rhi.IInstance`, `IDevice`, graphics queue, and swapchain.
- Own swapchain lifetime.
- Own shader module lifetime.
- Own present policy from `PresentSyncInterval`.
- Expose RHI device/queue/swapchain to host and pipeline.
- Provide depth descriptor synchronized with swapchain size.

Non-responsibilities:

- No Diligent factories/devices/contexts/swapchains.
- No runtime backend fallback.
- No render graph ownership.
- No cluster-specific state.

### RenderGraph

Primary type:

```csharp
namespace SomeEngine.Render.Graph;

public sealed partial class RenderGraph : IDisposable
{
    public void BeginFrame();
    public RenderGraphHandle ImportTexture(...);
    public RenderGraphHandle ImportBuffer(...);
    public RenderGraphHandle CreateTexture(string name, TextureDesc desc);
    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc);
    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc, ReadOnlySpan<byte> initialData);
    public void AddPass(IRenderGraphPass pass);
    public void AddGraphicsPass(string name, Action<RenderGraphBuilder> setup, Action<RenderGraphContext> execute);
    public void MarkOutput(RenderGraphHandle handle);
    public void TrackFinalState(RenderGraphHandle handle, Action<ResourceState> stateSink);
    public void QueueTextureExtraction(RenderGraphHandle handle, Action<TextureHandle, ResourceState> sink);
    public void QueueBufferExtraction(RenderGraphHandle handle, Action<BufferHandle, ResourceState> sink);
    public RenderGraphHandle GetResourceHandle(string name);
    public void Compile();
    public void Execute(IDevice device, IQueue queue, ISwapchain? swapchain = null, uint syncInterval = 1);
}
```

Execution shape:

```text
BeginFrame
  -> add/import graph resources
  -> add passes
  -> CompilePasses
  -> ResolveCreatedResources
  -> apply pass entry barriers
  -> pass.Execute(RenderGraphContext)
  -> apply pass exit states
  -> transition marked outputs to Present
  -> submit command buffer
  -> publish final states and extractions
  -> retire pooled resources after fence completion
```

Responsibilities:

- Resource declaration and import.
- Pass dependency state declarations.
- RHI barrier emission.
- Physical resource creation and pooling.
- Per-frame view creation and retirement.
- Imported final-state tracking.
- Texture/buffer history extraction.
- Marked output transition to `ResourceState.Present`.

Non-responsibilities:

- No Diligent graph helper.
- No second public RHI graph.
- No hidden missing-resource fallback.
- No rendering policy decisions specific to ClusterPipeline.

### RenderGraphBuilder

Public pass setup API:

```csharp
public readonly struct RenderGraphBuilder
{
    public RenderGraphHandle Read(RenderGraphHandle handle, ResourceState state = ResourceState.ShaderResource);
    public RenderGraphHandle Read(RenderGraphHandle handle, ResourceState state, SubResourceRange range);
    public RenderGraphHandle Write(RenderGraphHandle handle, ResourceState state = ResourceState.RenderTarget);
    public RenderGraphHandle Write(RenderGraphHandle handle, ResourceState state, SubResourceRange range);
    public RenderGraphHandle ReadWrite(RenderGraphHandle handle, ResourceState state);
    public RenderGraphHandle ReadWrite(RenderGraphHandle handle, ResourceState state, SubResourceRange range);
    public RenderGraphHandle Use(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access);
}
```

The pass setup API is the only place a pass declares graph-visible state transitions.

### RenderGraphContext

Execute-time API:

```csharp
public sealed class RenderGraphContext
{
    public IDevice Device { get; }
    public ICommandEncoder Encoder { get; }

    public TextureHandle GetTexture(RenderGraphHandle handle);
    public BufferHandle GetBuffer(RenderGraphHandle handle);
    public TextureDesc GetTextureDesc(RenderGraphHandle handle);
    public BufferDesc GetBufferDesc(RenderGraphHandle handle);

    public TextureViewHandle GetTextureView(
        RenderGraphHandle handle,
        ViewKind kind,
        Format format = Format.Unknown,
        TextureViewDimension dimension = TextureViewDimension.Texture2D,
        uint firstMip = 0,
        uint mipCount = 1,
        uint firstSlice = 0,
        uint sliceCount = 1);

    public BufferViewHandle GetBufferView(
        RenderGraphHandle handle,
        ViewKind kind,
        bool raw = false);

    public BufferViewHandle GetBufferView(
        RenderGraphHandle handle,
        ViewKind kind,
        ulong offset,
        ulong sizeInBytes,
        Format format = Format.Unknown,
        uint strideInBytes = 0,
        bool raw = false);
}
```

Execute code uses `RenderGraphContext.Device`, `Encoder`, and graph-created RHI views. Passes should not cache graph-local physical resources across frames unless explicitly extracted.

## ClusterPipeline Final Shape

Public surface remains in:

```csharp
namespace SomeEngine.Render.Pipelines;

public sealed class ClusterPipeline : IRenderFeature
{
    public static ClusterPipeline Opaque(...);
    public static ClusterPipeline OpaqueAndTransparent(...);

    public void Initialize(RenderContext context);
    public void PrepareFrame(EntityStore sourceStore, Func<AssetGuid, Material?> materialResolver);
    public void SetCamera(...);
    public ClusterFrameOutputs AddPasses(RenderGraph graph, FrameSurfaces frameSurfaces, RenderHistoryRegistry histories);
    public void AddPasses(RenderGraph graph);
    public void AddImGuiPass(RenderGraph graph, RenderGraphHandle output, ImDrawDataPtr drawData);
    public void RequestTemporalHistoryReset();
    public void Dispose();
}
```

Feature/debug properties remain on `ClusterPipeline`:

- `HiZMode`
- `DebugMode`
- `WireframeEnabled`
- `OverdrawEnabled`
- `DebugSpheresEnabled`
- `UseVisBuffer`
- `BypassCulling`
- `DumpNextFrame`
- `DebugShowHiZAABBs`
- `IncludeTransparentPass`
- `UseSWRaster`
- `UseDeformCache`
- `DeformCacheByteCapacity`
- `TemporalResolveEnabled`
- `TemporalJitterEnabled`
- `TemporalResolveSettings`
- `TemporalJitterPixels`
- `TemporalHistoryReadyForJitter`

Output snapshots remain:

- `LastGlobalResources`
- `LastCullOutput`
- `LastOpaqueRasterOutput`
- `LastTransparentRasterOutput`
- `LastRasterBinOutput`
- `LastShadeBinOutput`
- `LastShadeOutput`
- `LastHiZTextureHandle`

Public output records:

```csharp
public readonly record struct ClusterFrameOutputs(
    RenderGraphHandle SceneColor,
    RenderGraphHandle PostSceneColor,
    RenderGraphHandle MotionVectors,
    RenderGraphHandle SceneDepth);

public readonly record struct ClusterGlobalResources(
    RenderGraphHandle GlobalBVH,
    RenderGraphHandle PageHeap,
    RenderGraphHandle GlobalTransform,
    RenderGraphHandle PreviousGlobalTransform,
    RenderGraphHandle GlobalInstanceHeader,
    RenderGraphHandle InstanceDataHeap);

public readonly record struct ClusterTraverseOutput(...);
public readonly record struct ClusterCullOutput(...);
public readonly record struct ClusterRasterBinOutput(...);
public readonly record struct ClusterDeformBinOutput(...);
public readonly record struct ClusterRasterOutput(...);
public readonly record struct ClusterShadeBinOutput(...);
public readonly record struct ClusterShadeOutput(...);
```

The final implementation should use these `SomeEngine.Render.Pipelines` records directly. Temporary `SomeEngine.Render.RHI.Cluster*Output` records should be removed once replaced.

## ClusterPipeline Pass Order

The target frame pass order is:

```text
1. Upload persistent cluster buffers
2. Apply BVH patches
3. Upload per-frame instance transforms / previous transforms / headers / metadata
4. Prepare material slots and bin metadata
5. BVH traverse
6. Cull phase 1 against previous/seed HiZ
7. Raster bin phase 1
8. Optional deform bin/cache/deform preparation
9. HW draw phase 1 / SW raster phase 1
10. Build HiZ from phase-1 depth
11. Cull phase 2 against current HiZ
12. Raster bin phase 2
13. Optional deform phase 2
14. HW draw phase 2 / SW raster phase 2
15. Shade binning
16. Material shade
17. Resolve/debug visualization path
18. Motion vector output
19. Temporal resolve and history update
20. Post tonemap to output
21. ImGui/debug UI
22. Readback copies for stats/page faults/debug data
23. Present transition
```

Current implemented active order is close for upload/traverse/cull/bin/draw/HiZ/resolve/temporal/post/UI, but deform, SW raster, material shade, motion-vector generation, transparent pass, full HiZ history, and readback stats still need completion or verification.

## Cluster Stage Ownership

Final ownership should be:

```text
src/SomeEngine.Render/Pipelines/ClusterRender/
  ClusterPipeline.cs
  ClusterPipelineTypes.cs
  ClusterUploadStage.cs
  ClusterBvhPatchPass.cs
  ClusterResources.cs
  ClusterTraverseStage.cs
  ClusterCullStage.cs
  ClusterRasterBinStage.cs
  ClusterDrawStage.cs
  ClusterHiZBuildStage.cs
  ClusterMaterialSlotStage.cs
  ClusterResolveStage.cs
  ImGuiRenderer.cs
  ClusterMaterialSlotPreparer.cs
  MaterialPSOGroup.cs
  RasterPSOBuilder.cs
  DeformDispatchCalc.cs
  Components/*
```

Allowed remaining RHI infrastructure:

```text
src/SomeEngine.Render/RHI/
  RenderContext.cs
  ComputeProgram.cs
  ShaderBindings.cs
  BufferCopyPasses.cs
  BufferUploadPasses.cs
  BufferReadbackPasses.cs
  TextureCopyPasses.cs
```

Not allowed after fold:

```text
src/SomeEngine.Render/RHI/Cluster*.cs
src/SomeEngine.Render/RHI/PostTonemapPass.cs
src/SomeEngine.Render/RHI/TemporalResolvePass.cs
src/SomeEngine.Render/RHI/ImGuiRenderer.cs
```

## Material And Binding API Shape

The final material binding model is RHI-native:

```csharp
public sealed class ShaderParamBag : IDisposable
{
    public void Set(string name, TextureViewHandle? view);
    public void Set(string name, BufferViewHandle? view);
    public void SetConstantBuffer(string name, BufferViewHandle? view);
    public void Set(string name, SamplerHandle? sampler);
    public void SetScalar(string name, float value);
    public void SetScalar(string name, int value);
    public void SetScalar(string name, Vector4 value);

    public IEnumerable<(string Name, BindingType Type)> EnumerateBindingTypes();
    public ulong GetResourceLayoutHash();
    public ulong GetFilteredSignatureHash(IEnumerable<string> resourceNames, bool includeScalars = true);
}
```

Resource binding creation should use:

- `TextureViewHandle`
- `BufferViewHandle`
- `SamplerHandle`
- `BindingResourceDesc`
- reflected RHI pipeline layouts
- RHI `PipelineHandle`

It should not expose:

- `ITextureView`
- `IBufferView`
- `IBuffer`
- `IShaderResourceBinding`
- Diligent SRB pools
- Diligent pipeline state objects

`MaterialPSOGroup` and `RasterPSOBuilder` should remain as RHI pipeline-layout and pipeline-handle cache/build helpers. They should not become a Diligent compatibility layer.

## Graph Helper Facilities

Final helper families:

```text
RHI/BufferCopyPasses.cs
  AddCopyPass(graph, name, source, destination, sourceOffset, destinationOffset, byteCount)

RHI/BufferUploadPasses.cs
  AddUploadPass(graph, name, destination, destinationOffset, data)

RHI/BufferReadbackPasses.cs
  AddReadbackPass / staging readback extraction helpers

RHI/TextureCopyPasses.cs
  AddCopyPass for full texture copies and explicit region copies

Pipelines/RenderGraphTextureCopy.cs
  public helper shim over TextureCopyPasses
```

Rules:

- Invalid resources throw.
- Empty uploads throw.
- Copy passes declare copy source and destination states.
- Texture copy helpers support mip/slice-aware regions.
- Readback helpers create explicit staging resources; CPU mapping must respect graph submission/fence lifetime.

## Static Gate Before Build

Before any build/test/run, the active source gate must be run with source search only. It must confirm no active Diligent blockers:

```text
using Diligent
Diligent.
IRenderDevice
IDeviceContext
ISwapChain
IShaderResourceBinding
IPipelineState
IEngineFactory
DiligentGraphics
UsesDiligentEngine
--diligent
--diligent-reference
DiligentReference
```

Additional migration gate:

```text
src/SomeEngine.Render/RHI/Cluster*.cs must not exist.
No active pipeline code should alias RhiCluster* output types.
ClusterPipeline should construct folded ClusterRender stages directly.
Post/Temporal/ImGui duplicate RHI pass files should not exist.
Runtime and Editor should not expose backend fallback flags.
Project files should not reference Diligent packages or projects.
```

Only after this gate passes should the migration enter non-sandbox build:

```text
dotnet build SomeEngine.slnx --no-restore -v minimal
```

Build repair rules:

- Fix compile errors without adding Diligent fallback.
- Do not restore Diligent types as shims.
- Do not keep duplicate slow-migration implementations.
- Delete replaced files as soon as their active replacement is in place.

## Known Gaps To Close

These are the main remaining delivery gaps:

- Finish compute-stage fold and remove duplicate `RHI/Cluster*` files.
- Repoint folded graphics stages away from temporary RHI cluster output records.
- Restore/complete deform cache resources instead of dummy cache buffers.
- Implement SW raster consumption of `BinnedSWDispatchArgs`.
- Restore material shade binning and material shade as the normal color path.
- Generate real motion vectors for temporal resolve.
- Wire HiZ history and `HiZMode` branches explicitly.
- Implement transparent pass instead of aliasing transparent output to opaque output.
- Implement readback for page faults, debug HiZ/AABB data, and counters.
- Populate debug counters instead of returning zeros.
- Run final static gate.
- Run non-sandbox build/test/runtime validation only after the static gate passes.
