# Concrete Render Pipeline Assets

Pipeline-owned shaders are modeled as ordinary assets. There is no separate
built-in shader lookup path: default engine shaders and user shaders both load
through `AssetDatabase.Load<ShaderAsset>(AssetGuid)`.

## Model

Each render path owns its own concrete render asset type. The cluster
renderer uses `ClusterRenderAsset`; another renderer should define its
own asset shape instead of sharing one string-keyed universal table.

`ClusterRenderAsset` contains the shader/program fields that
`ClusterPipeline` knows how to interpret:

- cluster frame shader modules such as `TemporalResolve` and `DepthMerge`
- cluster shader module references such as `ClusterCull`, `ClusterBinning`,
  `ClusterDraw`, `HiZBuild`, and `BvhPatch`
- single-entry shader modules such as `ClusterMotionVectors`

`ShaderAssetRef` is the only shader reference field type in the asset. The
asset selects shader modules; concrete pass/stage code owns the entry point ABI
for those modules.

## Runtime Loading

The caller selects a concrete render asset, then asks the matching pipeline
code to interpret it:

```csharp
ClusterRenderAsset asset =
    assetDatabase.Load<ClusterRenderAsset>(clusterPipelineGuid)
    ?? throw new InvalidOperationException(...);

ClusterPipeline pipeline = ClusterPipeline.Opaque(
    context,
    assetDatabase,
    asset,
    renderWorld);
```

`ClusterPipeline` may use `assetDatabase` while it constructs its pass/resource
objects, but it does not store the database or pass it to render graph execution
passes. Passes receive already resolved `ShaderAsset` instances and define
their own entry points in pass code.

Host output passes are not cluster pipeline state. Runtime and Editor load
`PostTonemap` and `ImGui` through `HostShaders`, then compose those passes after
`ClusterPipeline.AddPasses(...)` returns `FrameOutputs.PostSceneColor`.

## Replacement

Users replace built-in shaders by editing or cloning the concrete pipeline
asset and pointing its fields at different `ShaderAsset` GUIDs. The default
cluster render is just a `ClusterRenderAsset` generated into the
project assets; it is not a separate built-in lookup path.

Missing shader GUIDs or missing shader assets must fail fast. Shader entry
points are part of the concrete pipeline/pass ABI, so a replacement shader must
provide the entry points expected by that pass. There is no fallback to source
paths or default shader modules.

## PipelineState Ownership

PipelineState objects remain runtime objects. Render assets select shader
assets; pass/resource classes still provide entry point names and describe
`ComputeState` / `GraphicsState` inputs. `PipelineCache` derives the canonical
key from device, backend, render-target format, resource layout, material
state, and shader entry data, then returns `PipelineTicket` ownership to the
caller.
