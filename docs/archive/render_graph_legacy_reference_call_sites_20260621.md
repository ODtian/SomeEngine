# Legacy Render Graph Reference Call Sites

Created: 2026-06-21

This document records the current code locations that still reference the archived legacy render graph API after the old implementation was moved out of the project tree.

Archived legacy source:

- `docs/archive/render_graph_code_20260621-233439/src/SomeEngine.Render/Graph`
- `docs/archive/render_graph_code_20260621-233439/tests/SomeEngine.Tests/RenderGraphTests.cs`
- `docs/archive/render_graph_code_20260621-233439/tests/SomeEngine.Tests/RenderGraphTestHelpers.cs`
- `docs/archive/render_graph_code_20260621-233439/tests/SomeEngine.Tests/RenderGraphRhiTests.cs`

## Scope

Included:

- Current C# files under `src`, `tests`, and `tools`.
- Files that explicitly import `SomeEngine.Render.Graph`.
- Files that would directly lose old graph types such as `RenderGraph`, `RenderGraphHandle`, `RenderGraphContext`, `RenderGraphBuilder`, `GraphQueues`, `ImportDesc`, `ResourceLifetime`, `PassBindings`, `IParameterSink`, `IComputeCommands`, `UavBarrier`, and `SubResourceRange`.

Excluded:

- The archived legacy source directory itself.
- Markdown/workstream references.
- RHI methods named `UavBarrier` in files that do not import `SomeEngine.Render.Graph`; those are command-list API names, not old graph namespace references.
- Profiler label strings such as `"RenderGraph"` in `SomeEngine.Core`; these are textual labels, not dependencies on the archived graph namespace.

Search basis:

```powershell
rg -n "using SomeEngine\.Render\.Graph|SomeEngine\.Render\.Graph" src tests tools -g "*.cs"
```

The type-use line list below was then restricted to the files that matched that namespace import.

## Summary

Direct old graph dependency files: 57

| Area | Count |
| --- | ---: |
| Cluster pipeline | 25 |
| Tests | 14 |
| Frame/history | 4 |
| Render RHI helper passes | 4 |
| Other render pipelines | 4 |
| Runtime | 2 |
| Other render systems/material/UI | 2 |
| Editor | 1 |
| Render systems | 1 |

The widest migration surfaces are:

- `src/SomeEngine.Render/Pipelines/ClusterPipeline/*`: pass construction, builder setup, resource handles, pass contexts, parameter binding, resource lifetime declarations, and Uav barriers.
- `tests/SomeEngine.Tests/RenderWorldRefactorTests.cs`: large structural audit suite that asserts old graph API shape and migration guards.
- `src/SomeEngine.Runtime/RuntimeApp.cs` and `src/SomeEngine.Editor/EditorApp.cs`: top-level graph lifecycle, frame recording, compile, and execute calls.
- `src/SomeEngine.Render/RHI/*Passes.cs` and `src/SomeEngine.Render/RHI/UniformGpu.cs`: helper APIs that currently expose `RenderGraph` and `RenderGraphHandle` directly.

## Direct Reference Table

`Import line` is the `using SomeEngine.Render.Graph` line. `Graph API lines` are lines in the same file containing old graph API type names. They do not list every chained method call on a local `graph`, `builder`, `context`, `pass`, or `sink`; the referenced type lines are the direct dependency anchors for each file.

| File | Import line | Graph API lines | Symbols |
| --- | ---: | --- | --- |
| `src/SomeEngine.Editor/EditorApp.cs` | 15 | 35,266,299,301,379,380,384,388 | `GraphQueues`, `RenderGraph` |
| `src/SomeEngine.Render/Frame/FrameData.cs` | 1 | 18,19,20,21,22,23 | `RenderGraphHandle` |
| `src/SomeEngine.Render/Frame/FrameResources.cs` | 1 | 20,30,47,48 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Frame/RenderHistory.cs` | 1 | 14,15 | `RenderGraphHandle` |
| `src/SomeEngine.Render/Frame/ViewHistory.cs` | 1 | 17,24,31,38,71,84,92,102,106 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Materials/MaterialGpu.cs` | 2 | 25,26,31,32,59,152,154,158,171,172,259,275,276 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/BindInput.cs` | 2 | 47,51,68,86,112,114,118,138,145,147 | `IParameterSink`, `PassBindings`, `RenderGraphAccess`, `RenderGraphContext` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/BvhPatchPass.cs` | 1 | 51,52,69,81,147,148,149 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterBinGpu.cs` | 1 | 13,14,35,52,53,57,80,81,85,88 | `RenderGraph`, `RenderGraphHandle`, `ResourceLifetime` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterDebugFeature.cs` | 3 | 9,15,16,17,18,19,54,55,56,57,58,69,70,71,72,73,76,129,131,132,133,137,138,183,185,225,339,343 | `IRenderFeature`, `RenderGraph`, `RenderGraphHandle`, `SubResourceRange` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterDeformPass.cs` | 3 | 23,24,25,61,62,88,95,96,212,213,220,223,264,265,272,278,281,338,344,346,367,373,379,534,537,538,545,568 | `IComputeCommands`, `IParameterSink`, `PassBindings`, `RenderGraph`, `RenderGraphAccess`, `RenderGraphContext`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterDrawPass.cs` | 2 | 84,93,94,95,96,106,117,118,160,162,163,165,191,227,232,235,236,237,238,385,386,392,396,397,401,432,433,439,443,444,450,518,523,527,528,560,565,569,570 | `IParameterSink`, `PassBindings`, `RenderGraph`, `RenderGraphContext`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterOutputs.cs` | 1 | 6,7,8,9,10,13,14,15,16,17,18,21,22,23,24,25,26,32,33,34,37,38,39,42,43,44,45,48,49,51,52 | `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterOutputStage.cs` | 2 | 26,32,39,40,41,42,43,44,45,46 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterPipeline.Runtime.cs` | 12 | 55,56,57,58,59,175,176,177,178,179,298,326,327,328,329,330,331,332,429,434,435,520,628,695,883,908,909,912,949,982,983,984,1095,1104,1111,1121,1131,1135,1147,1174 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterRasterStage.cs` | 1 | 44,48,49,76,80,81,101,107,108,140,149,150,151,152,153,228,229,232 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterResolvePass.cs` | 1 | 54,60,120,121,127,131,147,160,174,188,202,216,230,244,257,258 | `PassBindings`, `RenderGraph`, `RenderGraphAccess`, `RenderGraphContext`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterResources.cs` | 5 | 11,12,13,14,25,42,53,54,55,62,71,80,106,109,150,153,230 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle`, `ResourceLifetime` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterSceneStage.cs` | 2 | 105,109,137,138,139,140,165,166,167,203,208,209,243,246,247,257,258,260,261,265,283,287,348,352,353,354,355,356,357,434,439,440,503,504,565,566,574,587,591,599,604,605 | `RenderGraph`, `RenderGraphContext`, `RenderGraphHandle`, `ResourceLifetime` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterShadeStage.cs` | 3 | 28,53,68,69,70,71 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterSlotGpu.cs` | 3 | 14,27,39,43,90,91,164 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ClusterTraversePass.cs` | 3 | 81,106,112,113,114,115,140,141,142,200,203,204,214,215,217,218,222,246,247,250,251,252 | `RenderGraph`, `RenderGraphHandle`, `ResourceLifetime` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/DepthMergePass.cs` | 1 | 63,65,73,84,85,86,116,117,118,171,172,173 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/HiZPass.cs` | 1 | 10,91,92,95,163,164,165,176,215,216,217,218,243,244,245,246,274,275,276,277,315,316,319,321 | `IComputeCommands`, `RenderGraph`, `RenderGraphContext`, `RenderGraphHandle`, `SubResourceRange`, `UavBarrier` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/LightBuffer.cs` | 6 | 52,53,54,80,115,184,188,193,197,202,206,258,280,298,316,978,980,992,1003,1015,1017,1034,1035,1070,1071,1073,1169 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/MaterialBin.cs` | 2 | 23 | `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/MaterialItems.cs` | 5 | 20,182,248,267,292,1203,1209,1223 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/MaterialShadePass.cs` | 4 | 36,51,52,53,54,68,69,88,90,91,93,273,274,292,293,312,313,345,346,347,398,399,400,413,465,466,475,530,556,700,834,846,847,848,849 | `IParameterSink`, `PassBindings`, `RenderGraph`, `RenderGraphContext`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/RasterBinPass.cs` | 1 | 313,317,318,319,358,404,408,409,410,416,417,458,476,558,562,563,564,565,566,567,568,569,570,571,572,591,646,658,666,675,679,680,681,682,683,684,685,686,687,688,689,690,691,692,693,694,695,715,793,794,808,809,819,820,829 | `IComputeCommands`, `RenderGraph`, `RenderGraphAccess`, `RenderGraphHandle`, `ResourceLifetime`, `UavBarrier` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/ShadeBinPass.cs` | 1 | 82,87,122,128,138,192,216,217,228,229,242,243,253,268,271,274,293,301,303,322,337,359,360,366,390,457,485,488,489,499,500,501,502,503,504,505,506,507,508,509,510,511,512,513,516,518,524 | `PassBindings`, `RenderGraph`, `RenderGraphAccess`, `RenderGraphBuilder`, `RenderGraphContext`, `RenderGraphHandle`, `ResourceLifetime`, `UavBarrier` |
| `src/SomeEngine.Render/Pipelines/ClusterPipeline/SwRasterPass.cs` | 3 | 28,34,37,38,39,40,80,81,145,172,174,175,177,222,223,224,225,324,325,344,345,366,371,372,373,374,378,379,473,474,480,481,482,483,485,486,493,563,568,569,570,571,573,574,609,614,615,616,617,619,620,721,857 | `IParameterSink`, `PassBindings`, `RenderGraph`, `RenderGraphContext`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/FrameOutputs.cs` | 1 | 6,7,8,9 | `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/FullscreenPipeline.cs` | 1 | 117,142,232,238 | `PassBindings`, `RenderGraph`, `RenderGraphContext`, `RenderPassCommands` |
| `src/SomeEngine.Render/Pipelines/PostTonemapPass.cs` | 1 | 30,63,64 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Pipelines/TemporalResolvePass.cs` | 3 | 58,59,60,61,65,88,89,90,91,92,93,94,95,101,102,103,104,105,106,107,110,167,170,177,178,179,180,181,182,183,184 | `RenderGraph`, `RenderGraphContext`, `RenderGraphHandle` |
| `src/SomeEngine.Render/RHI/BufferCopyPasses.cs` | 1 | 7,8,16,18,19,29,46,47 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/RHI/BufferUploadPasses.cs` | 1 | 6,17,27,36,51,103,134,142,150,152,165 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/RHI/TextureCopyPasses.cs` | 1 | 7,9,15,17,18,51,73,77,94,96,98,113,117,133,135,136,147 | `RenderGraph`, `RenderGraphHandle`, `SubResourceRange` |
| `src/SomeEngine.Render/RHI/UniformGpu.cs` | 4 | 10,30,41,91,108,128,132 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/Systems/InstanceGpu.cs` | 8 | 19,20,21,22,64,211,227,242,252,340,436,443,452,461,470,1461,1468,1479,1585,1644 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Render/UI/ImGuiLayer.cs` | 1 | 79,98,103,426,427 | `ImportDesc`, `RenderGraph`, `RenderGraphContext`, `RenderGraphHandle`, `RenderPassCommands` |
| `src/SomeEngine.Runtime/FrameCapture.cs` | 1 | 13 | `RenderGraph`, `RenderGraphHandle` |
| `src/SomeEngine.Runtime/RuntimeApp.cs` | 24 | 156,158,253,1516,1517,1519,1524,1563 | `GraphQueues`, `RenderGraph` |
| `tests/SomeEngine.Tests/ClusterMeshesTests.cs` | 6 | 23,177,200,226,265,270,280,284,414,440 | `ImportDesc`, `RenderGraph`, `RenderGraphHandle` |
| `tests/SomeEngine.Tests/Materials/MaterialGpuTests.cs` | 1 | 19,23,24,50,72,96,110,125,140,148 | `RenderGraph`, `RenderGraphHandle` |
| `tests/SomeEngine.Tests/MotionVectorAndTemporalHistoryPassTests.cs` | 2 | 17,20,24,39,42,46,61,64,67,70,77,81,85,105,106,107,117,118,122 | `RenderGraph`, `RenderGraphHandle` |
| `tests/SomeEngine.Tests/PipelineCacheTests.cs` | 2 | 52,350 | `RenderGraph` |
| `tests/SomeEngine.Tests/Pipelines/BinGpuTests.cs` | 1 | 12,14,30,32,47,49,64,66,79 | `RenderGraph`, `RenderGraphHandle` |
| `tests/SomeEngine.Tests/Pipelines/ClusterLightingTests.cs` | 5 | 81,87,88,89,385,397 | `RenderGraph`, `RenderGraphHandle` |
| `tests/SomeEngine.Tests/Pipelines/MaterialItemsTests.cs` | 8 | 27,64,95,121,151,191,216,239,247 | `RenderGraph` |
| `tests/SomeEngine.Tests/Pipelines/SlotGpuTests.cs` | 2 | 18,22,46,67,86,168,175 | `RenderGraph`, `RenderGraphHandle` |
| `tests/SomeEngine.Tests/PostTonemapPassTests.cs` | 4 | 19,49,88,98 | `RenderGraph`, `RenderGraphHandle` |
| `tests/SomeEngine.Tests/RenderWorldRefactorTests.cs` | 5 | 265,267,288,290,291,292,354,372,373,375,377,378,383,384,387,388,389,390,398,401,402,415,436,501,502,546,547,548,756,757,765,766,768,769,770,785,786,808,824,825,826,828,831,832,834,876,882,915,921,933,939,1000,1006,1007,1008,1010,1012,1056,1065,1119,1121,1122,1141,1159,1160,1236,1338,1350,1356,1362,1368,1380,1383,1424,1425,1429,1434,1459,1508,1526,1541,1558,1670,1672,1674,1675,1676,1677,1747,1885,1886,2174,2175 | `GraphQueues`, `IRenderFeature`, `PassBindings`, `PassParameters`, `RenderGraph`, `RenderGraphAccess`, `RenderGraphBuilder`, `RenderGraphContext`, `RenderGraphHandle`, `ResourceKind`, `ResourceLifetime` |
| `tests/SomeEngine.Tests/Systems/InstanceGpuTests.cs` | 7 | 29,84,99,153,178,212,253,300,348,386,428,460,489,517,565,594,625,643,673 | `RenderGraph` |
| `tests/SomeEngine.Tests/Systems/UniformGpuTests.cs` | 1 | 17,49,68,85,101,130,143,159 | `RenderGraph` |
| `tests/SomeEngine.Tests/TemporalPassTests.cs` | 4 | 19,74,99,100,101,109,267,270,271,282,283,294,295,307,308,309,310 | `RenderGraph`, `RenderGraphHandle`, `RenderGraphUniforms` |
| `tests/SomeEngine.Tests/ViewHistoryTests.cs` | 2 | 15,43,63,89,109 | `RenderGraph`, `RenderGraphHandle` |

## Textual References Not Counted As Old Graph Dependencies

These locations mention `RenderGraph` as profiling or diagnostic text but do not import `SomeEngine.Render.Graph`:

- `src/SomeEngine.Core/Diagnostics/Profiler.cs:225`
- `src/SomeEngine.Core/Diagnostics/ProfilerTypes.cs:83`
- `src/SomeEngine.Core/Diagnostics/ProfilerTypes.cs:84`

These locations matched `UavBarrier` by name but are RHI command API references, not legacy graph namespace references:

- `src/SomeEngine.Rhi/Interfaces.cs:155`
- `src/SomeEngine.Rhi/Interfaces.cs:156`
- `src/SomeEngine.Rhi/Backends/Null/NullCommandList.cs:102`
- `src/SomeEngine.Rhi/Backends/Null/NullCommandList.cs:113`
- `src/SomeEngine.Rhi.D3D12/D3D12CommandList.cs:448`
- `src/SomeEngine.Rhi.D3D12/D3D12CommandList.cs:451`
- `src/SomeEngine.Rhi.D3D12/D3D12CommandList.cs:467`
- `src/SomeEngine.Rhi.D3D12/D3D12CommandList.cs:470`
- `tests/SomeEngine.Rhi.Tests/D3D12BackendCoverageTests.cs:2509`
- `tests/SomeEngine.Rhi.Tests/NullRhiContractTests.cs:1886`
- `tests/SomeEngine.Rhi.Tests/NullRhiContractTests.cs:4797`
- `tests/SomeEngine.Rhi.Tests/NullRhiContractTests.cs:4812`
