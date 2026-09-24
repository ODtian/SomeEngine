# Cluster Material Contract

本文记录 cluster material 命名和边界的当前结论。这里的目标是把通用 material 模型、cluster 私有 target 协议、RHI PipelineState 状态和 bin/slot 派生状态拆开，不再让一个名字跨多个层级复用。

## 核心结论

最终词表：

| 当前名 | 目标名 | 结论 |
|--------|--------|------|
| `MaterialPass.Pipeline` | `MaterialPass.Target` | 通用 material pass 面向哪个消费目标。不是 RHI pipeline，不是 cluster 私有 role。 |
| `MaterialPass.Entry` | `MaterialPass.EntryPoint` | shader entry point。和 shader metadata / `PassShader` 对齐。 |
| `PipelineState` | `MaterialState` | material asset 解析出的材质状态。不是 RHI pipeline state。 |
| `ClusterPipes` | 删除，解析收回 `MaterialItems.ParseTarget` | cluster 私有 target 字符串协议和解析规则。不是 pipe，不是 pipeline，也不再单独做 parser 类型。 |
| `ShaderVariantRef` | `PassShader` | runtime shader handle + entry point 引用。backend/stage variant 在 material 构建边界解析 handle 后，通过 runtime `Shader` 的 variant/reflection 快照解析。 |
| `ClusterItem` | `MaterialItem` | 一个 material 在 cluster 中展开后的 target/entry 投影。不是 cluster geometry item。 |
| `ClusterBatch` | `MaterialBin` | 单个 cluster pass target 的 runtime state：PipelineTicket、bindings、shader entries、material handle、bin index、args index。 |
| `ClusterBatches` | `MaterialItems` | cluster material runtime owner：解释 material pass、维护 material slots/bins、创建和回收 `MaterialBin`。 |
| `ClusterDispatch` / `ClusterDispatches` | 删除 | 这层只重复包装 pass index、bin、args 和 material，没有独立领域职责。 |

`ClusterBatch` 字段同步改名：

| 当前字段 | 目标字段 |
|----------|----------|
| `RhiDevice` | `Device` |
| `Pipeline` | `PipelineState` |
| `Layout` | `Bindings` |
| `ComputeVariant` | `Compute` |
| `VertexVariant` | `Vertex` |
| `PixelVariant` | `Pixel` |
| `Bin` | `BinIndex` |
| `ArgsBin` | `ArgsIndex` |

## MaterialPass

`MaterialPass` 是通用 material 暴露给 renderer 的入口记录。它不属于 cluster，也不表达 RHI PipelineState。

目标形态：

```csharp
public readonly record struct MaterialPass(
    string Target,
    Handle<Shader> Shader,
    string EntryPoint,
    MaterialState State);
```

字段含义：

- `Target`：这个 pass 面向哪个 renderer 或 renderer 内部路径。
- `Shader`：运行时 shader asset handle。
- `EntryPoint`：shader entry point。
- `State`：material asset 解析出的材质状态。

`Target` 保持通用字符串，不使用全局 `MaterialRole` enum。原因是 forward、shadow、deferred、cluster 等 renderer 会有不同 target 体系，通用 `MaterialPass` 不能被 cluster 的 shade/raster/deform 槽位绑死。

示例 target：

```text
cluster.shade
cluster.shade.cached
cluster.raster.sw
cluster.raster.sw.cached
cluster.raster.vs
cluster.raster.vs.cached
cluster.raster.ps
cluster.deform.eval
forward.lit
forward.depth
shadow.depth
deferred.gbuffer
```

已有旧 target 字符串需要兼容时，兼容逻辑只能放在对应 renderer 的 target 解析器中，不能回灌到 `MaterialPass`。

## MaterialState

`PipelineState` 改名为 `MaterialState`。当前字段来自 material asset tag/component 解析，不是 RHI pipeline state。cluster 当前实际消费的是 `BoundsExpansion`，其它字段不能让名字暗示已经驱动 PipelineState 创建。

目标形态：

```csharp
public enum SurfaceMode
{
    Opaque,
    Masked,
    Translucent,
}

public readonly record struct MaterialState
{
    public SurfaceMode Surface { get; init; }
    public bool TwoSided { get; init; }
    public int OverlayLayer { get; init; }
    public float BoundsExpansion { get; init; }
    public byte StencilRef { get; init; }
    public StencilCompare StencilCompare { get; init; }
    public StencilOp StencilPass { get; init; }

    public static MaterialState Default => new()
    {
        Surface = SurfaceMode.Opaque,
        StencilCompare = StencilCompare.Always,
        StencilPass = StencilOp.Keep,
    };
}
```

`Opaque` / `Masked` / `Translucent` 三个 bool 合并为 `SurfaceMode`，避免非法组合。

## Cluster Target

cluster 只在 cluster 模块内部解释 `MaterialPass.Target`。通用 material 不知道 cluster target 的枚举。

目标形态：

```csharp
internal enum ClusterPass
{
    Shade,
    CachedShade,
    SwRaster,
    CachedSwRaster,
    VsRaster,
    CachedVsRaster,
    PsRaster,
    DeformEval,
}

internal sealed class MaterialItems
{
    private const string ShadeTarget = "cluster.shade";
    private const string CachedShadeTarget = "cluster.shade.cached";
    private const string SoftwareTarget = "cluster.raster.sw";
    private const string CachedSoftwareTarget = "cluster.raster.sw.cached";
    private const string VertexTarget = "cluster.raster.vs";
    private const string CachedVertexTarget = "cluster.raster.vs.cached";
    private const string PixelTarget = "cluster.raster.ps";
    private const string DeformTarget = "cluster.deform.eval";

    private static bool ParseTarget(string? target, out ClusterPass pass)
    {
        pass = target switch
        {
            ShadeTarget => ClusterPass.Shade,
            CachedShadeTarget => ClusterPass.CachedShade,
            SoftwareTarget => ClusterPass.SwRaster,
            CachedSoftwareTarget => ClusterPass.CachedSwRaster,
            VertexTarget => ClusterPass.VsRaster,
            CachedVertexTarget => ClusterPass.CachedVsRaster,
            PixelTarget => ClusterPass.PsRaster,
            DeformTarget => ClusterPass.DeformEval,
            _ => default,
        };

        return target is ShadeTarget
            or CachedShadeTarget
            or SoftwareTarget
            or CachedSoftwareTarget
            or VertexTarget
            or CachedVertexTarget
            or PixelTarget
            or DeformTarget;
    }
}
```

`MaterialAssetLoader` 可以从 shader attributes 生成 `MaterialPass.Target`，但 cluster target 到 `ClusterPass` 的解析必须由 `MaterialItems` 私有持有。`MaterialPass` 不持有 `ClusterPass`。

## MaterialItem

`MaterialItem` 是一个 material 在 cluster renderer 中的解释结果。它保存 material handle、material state 和 cluster 认可的 shader entry。

目标形态：

```csharp
internal readonly record struct MaterialItem
{
    public Handle<Material> Handle { get; init; }
    public uint PassVersion { get; init; }
    public uint BindingVersion { get; init; }
    public MaterialState State { get; init; }

    public PassShader Shade { get; init; }
    public PassShader ShadeCache { get; init; }
    public PassShader Sw { get; init; }
    public PassShader SwCache { get; init; }
    public PassShader Vs { get; init; }
    public PassShader VsCache { get; init; }
    public PassShader Ps { get; init; }
    public PassShader Deform { get; init; }
}
```

`MaterialItem` 不保存 PipelineState，不拥有 RHI 生命周期。它是 material pass target 到 cluster shader entry 的 CPU-side 投影。

## PassShader

`ShaderVariantRef` 改为 `PassShader`。

目标形态：

```csharp
internal readonly record struct PassShader(
    Handle<Shader> Shader,
    string? EntryPoint)
{
    public bool IsEmpty => !Shader.IsValid || string.IsNullOrWhiteSpace(EntryPoint);
}
```

这个类型只表达 runtime shader handle + entry point。schema `ShaderAsset` 不进入 cluster material identity；backend/stage 具体入口在 `MaterialItems` 解析 handle 后，通过 runtime `Shader` 的 immutable variant/reflection 快照解析，所以它不是完整 shader variant。

## MaterialBin

`MaterialBin` 是一个 cluster pass target 编译出的 runtime state。它不是 `MaterialPass` 的 GPU 版本，也不是 GPU buffer layout。

目标形态：

```csharp
internal sealed class MaterialBin : IDisposable
{
    public IDevice? Device;
    public PipelineTicket PipelineState;
    internal ShaderBindingTable? Bindings;

    public PassShader Compute;
    public PassShader Vertex;
    public PassShader Pixel;
    public Handle<Material> MaterialHandle;
    public int BinIndex;
    public int ArgsIndex;

    public void Dispose()
    {
        if (Device != null)
        {
            if (Bindings.HasValue)
                ShaderBindings.Destroy(Device, Bindings.Value);
        }

        PipelineState = default;
        Bindings = null;
        Device = null;
    }
}
```

`MaterialBin` 持有 `PipelineTicket` 和 binding reflection table，并记录 material handle、bin/args 映射。材质 compute、graphics、deform 都是 optional pipeline 用户；下游 pass 通过 RenderGraph context 解析 ready pipeline，queued/failed 时跳过该 bin。固定引擎 pipeline 才用 required 策略并在初始化或 loading gate 等待：

```csharp
foreach (MaterialBin state in states)
{
    if (!state.PipelineState.IsValid || !state.Bindings.HasValue)
        continue;

    PipelineHandle pipeline = context.GetPipeline(state.PipelineState, PipelineNeed.Optional);
    if (!pipeline.IsValid)
        continue;

    pass.SetPipeline(pipeline);
}
```

## MaterialItems

`MaterialItems` 是 cluster material runtime owner。它不是 `MaterialBin` 的普通复数集合。

职责：

- 扫描 `RenderWorld` 中的 material handle。
- 从 `AssetStore` 读取 `Material`。
- 遍历 `Material.Passes`。
- 使用私有 `ParseTarget` 解释 `MaterialPass.Target`。
- 构建 `MaterialItem`。
- 创建 shade / SW raster / HW draw / deform 的 `MaterialBin`。
- 维护 material slot buffer、raster/shade/deform bin index 和 args index。
- 写入 instance header 中的 cluster material 派生数据。
- 缓存构建结果。
- 延迟 retire 旧 `MaterialBin`，释放 binding reflection/layout 并释放 PipelineTicket；PipelineState 生命周期仍归全局 PipelineCache。

目标形态：

```csharp
internal sealed class MaterialItems : IDisposable
{
    private readonly List<MaterialBin[]> _old = [];

    private MaterialBin[] _shade = [];
    private MaterialBin[] _sw = [];
    private MaterialBin[] _draw = [];
    private MaterialBin[] _deform = [];

    public IReadOnlyList<MaterialBin> Shade => _shade;
    public IReadOnlyList<MaterialBin> Sw => _sw;
    public IReadOnlyList<MaterialBin> Draw => _draw;
    public IReadOnlyList<MaterialBin> Deform => _deform;
}
```

关系：

```text
MaterialItems owns and produces MaterialBin instances.
```

不是：

```text
ClusterBatches = List<ClusterBatch>
```

## 删除 ClusterDispatch

`ClusterDispatch` / `ClusterDispatches` 删除，不改名保留。

原因：

- 它只重复包装 `BatchIndex`、`Bin`、`ArgsBin`、`Material`。
- 这些字段已经在 `MaterialBin` 上。
- 它没有独立生命周期。
- 它没有独立业务规则。
- 保留它只会继续制造命名和所有权误解。

需要 `MaxArgs` 时使用 helper：

```csharp
internal static uint MaxArgs(
    IReadOnlyList<MaterialBin>? states,
    uint minimum,
    string owner)
{
    uint maxArgs = Math.Max(minimum, 1u);
    if (states == null)
        return maxArgs;

    for (int i = 0; i < states.Count; i++)
    {
        MaterialBin state = states[i];

        if (state.BinIndex < 0)
            throw new InvalidOperationException($"{owner} bin index must be non-negative.");
        if (state.ArgsIndex < 0)
            throw new InvalidOperationException($"{owner} args index must be non-negative.");

        maxArgs = Math.Max(maxArgs, checked((uint)state.ArgsIndex + 1u));
    }

    return maxArgs;
}
```

## 禁止结论

以下名字和边界已经明确否定：

- `MaterialRole`：会把 cluster 私有 shader 槽位变成通用 material 概念，污染 forward/shadow/deferred 等路径。
- `MaterialPassGpu`：会误导为 `MaterialPass` 的 GPU 镜像。当前对象不是 GPU buffer layout，也不是 material pass 上传结构。
- `ClusterBatch` / `ClusterBatches`：batch 语义错误，且和 BVH 持久线程文档中的 cluster batch 机制冲突。
- `ClusterPipes`：pipe 不是领域词，会和 pipeline 概念继续混淆。
- `ClusterDispatch` / `ClusterDispatches`：无独立职责，删除。
- 让 `MaterialPass` 持有 cluster enum：违反通用 material 和 renderer 私有协议的边界。

## 验收搜索

当前代码完成重命名后，以下旧名不应再出现在生产代码和相关测试中：

```powershell
rg -n "ClusterBatch|ClusterBatches|ClusterDispatch|ClusterDispatches|ClusterPipes|ShaderVariantRef|PipelineState|pass\.Pipeline|BatchIndex|ArgsBin|RhiDevice|ComputeVariant|VertexVariant|PixelVariant" src\SomeEngine.Render tests\SomeEngine.Tests
```

`ClusterPipeline` 内部不应再有 runtime PipelineState 字段叫 `Pipeline`：

```powershell
rg -n "PipelineHandle Pipeline|\.Pipeline\b" src\SomeEngine.Render\Pipelines\ClusterPipeline
```

允许保留的 `Pipeline` 语义：

- `PipelineHandle`。
- `PipelineLayout`。
- `CreateComputePipeline`。
- `CreateGraphicsPipeline`。
- `DestroyPipeline`。
- `pass.SetPipeline(...)`。
