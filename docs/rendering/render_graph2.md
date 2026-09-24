# RenderGraph2 设计

一句话约束：

```text
用户声明意图，RenderGraph 编译 `CompiledGraph`，RHI 执行结果。
```

这份设计只描述最终结构。`CompiledGraph` 是一次编译返回的帧描述，不是 RenderGraph 跨帧持有的状态包。生产代码的类型名和方法名应尽量保持三个概念词以内；超过三个词通常说明职责没有收敛，除非它来自 RHI 标准术语、领域 pass 名，或测试名需要保留失败信息。

## 1. 公共接口

RenderGraph 的公共接口只覆盖一帧 GPU 工作的完整生命周期：

```csharp
public sealed class RenderGraph : IDisposable
{
    public RenderGraphBlackboard Blackboard { get; }

    // frame
    public void BeginFrame();
    public void BeginFrame(Action<RenderGraph> record);
    public void BeginFrame<TState>(TState state, Action<RenderGraph, TState> record);

    // resources
    public RenderGraphHandle CreateTexture(string name, TextureDesc desc);
    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc);
    public RenderGraphHandle CreateBuffer(string name, BufferDesc desc, ReadOnlySpan<byte> data);

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

    // passes
    public void AddRasterPass<TData>(string name, Action<RenderGraphBuilder, TData> setup, Action<RenderGraphContext, TData> execute);
    public void AddComputePass<TData>(string name, Action<RenderGraphBuilder, TData> setup, Action<RenderGraphContext, IComputeCommands, TData> execute);
    public void AddCopyPass<TData>(string name, Action<RenderGraphBuilder, TData> setup, Action<RenderGraphContext, TData> execute);

    // roots and boundaries
    public void ExtractTexture(RenderGraphHandle handle, ResourceState finalState, Action<TextureHandle, ResourceState> sink);
    public void ExtractBuffer(RenderGraphHandle handle, ResourceState finalState, Action<BufferHandle, ResourceState> sink);

    // compile and execute
    public void Compile(GraphQueues queues);
    public void Execute(GraphQueues queues, ISwapchain? swapchain = null, uint syncInterval = 1);

    // long-lived cache control
    public void ClearBindSets(bool waitForGpu = false);
}
```

`AddRasterPass` means a graphics/raster command pass. `AddComputePass` means a
compute encoder pass. `AddCopyPass` means a copy pass. Pass mode is fixed at
registration; compile and execute must not rediscover it through runtime type
checks.

BindSet owner eviction is indexed by `BindingIndex`: binding set creation records its layout and every texture/buffer view it references. View destruction evicts only binding sets attached to that view. PipelineState/bin rebuilds clear only binding sets attached to the old binding layouts, not the full owner.

## 2. Record 接口

Pass setup can only declare resource use. Execute can only resolve physical RHI objects through the context.

```csharp
public readonly struct RenderGraphBuilder
{
    public void SideEffect();

    public RenderGraphHandle Read(RenderGraphHandle handle, ResourceState state, SubResourceRange range);
    public RenderGraphHandle Write(RenderGraphHandle handle, ResourceState state, SubResourceRange range);
    public RenderGraphHandle ReadWrite(RenderGraphHandle handle, ResourceState state, SubResourceRange range);

    public RenderGraphHandle Use(
        RenderGraphHandle handle,
        ResourceState entryState,
        ResourceState exitState,
        RenderGraphAccess access,
        SubResourceRange range);
}

public sealed class RenderGraphContext
{
    public TextureDesc GetTextureDesc(RenderGraphHandle handle);
    public BufferDesc GetBufferDesc(RenderGraphHandle handle);
    public TextureViewHandle GetTextureView(RenderGraphHandle handle, ViewKind kind, ...);
    public BufferViewHandle GetBufferView(RenderGraphHandle handle, ViewKind kind, ...);
    public RenderPassCommands BeginRenderPass(in RenderPassDesc desc);
    public void CopyBuffer(...);
    public void CopyTexture(...);
    public void CopyToBuffer(...);
}
```

Core invariant:

```text
setup declares all graph resource use
execute records RHI commands only
execute cannot create resources or passes
execute cannot access undeclared handles
```

## 3. Resource 边界

`ImportDesc` is the only import contract:

```csharp
public readonly record struct ImportDesc(ResourceState InitialState)
{
    public ResourceState? FinalState { get; init; }
    public bool AllowWrite { get; init; }
}
```

Import rules:

```text
InitialState   input state for the first barrier
FinalState     requested graph exit state; also a cull root
AllowWrite     imported resources are read-only unless this is true
```

Typical boundary code:

```csharp
var output = graph.ImportTexture(
    "OutputColor",
    backbuffer,
    backbufferDesc,
    new ImportDesc(ResourceState.Present)
    {
        FinalState = ResourceState.Present,
        AllowWrite = true,
    },
    backbufferRtv);

var history = graph.ImportTexture(
    "TemporalHistory_A",
    texture,
    desc,
    new ImportDesc(previousState));

var current = graph.ImportTexture(
    "TemporalHistory_B",
    texture,
    desc,
    new ImportDesc(currentState)
    {
        AllowWrite = true,
    });
graph.ExtractTexture(current, ResourceState.ShaderResource, (_, state) => historyState = state);
```

Boundary invariants:

```text
Import without AllowWrite cannot be written by any pass.
Import with FinalState remains live even if only the final barrier is needed.
ExtractTexture and ExtractBuffer are cull roots and request final state.
Internal SetFinalState is reserved for graph-owned helper paths without external ownership transfer.
Import.FinalState, Extract*, and internal SetFinalState must not conflict.
Present state is expressed by ImportDesc.FinalState.
Same-frame cross-feature exchange goes through typed pass data or `RenderGraphBlackboard`.
```

## 4. CompiledGraph

`CompiledGraph` is the compiled frame graph returned by `CompileGraph()`. RenderGraph only keeps it in `_activeCompile` while pass setup or execute-time validation is running; it is not retained as cross-frame state.

```csharp
private sealed class CompiledGraph
{
    public List<int> Passes;                  // live pass order
    public int[] PassSlots;                   // pass index -> slot
    public bool[] LivePasses;

    public List<ResourceUse>[] Uses;          // declarations from setup
    public List<PassDependency> Dependencies; // RAW / WAR / WAW
    public ResourceRecord[] Resources;          // lifetime and pool records

    public List<QueueBatch> QueueBatches;     // command pass or compute batch
    public List<QueueSync> QueueSyncs;        // queue transition metadata

    public BarrierSet[] Barriers;            // per pass entry barriers
    public BarrierSet FinalBarriers;         // export/final/import exit barriers

    public List<int>[] Resolves;              // resources to materialize before pass
    public List<int> RootResolves;            // resources needed without live pass
    public List<int> Retains;                 // resources held until fence retirement

    public CompiledGraph(int passCount, int resourceCount);
    public void SetOrder(ReadOnlySpan<int> passOrder);
    public int SlotOf(int passIndex);
}
```

Compile lifecycle:

```csharp
CompileGraph()
{
    ApplyFeaturesOnce();

    compile = new CompiledGraph(passCount, resourceCount);

    foreach (pass)
        pass.Setup(builder);        // fills compile.Uses

    ValidateOutputs();
    CullPasses();
}

CullPasses()
{
    MarkCullRoots();                // exports and final states
    AddLivePasses();                // live pass list
    ValidateInitializedReads();     // no live read before live write
    AnalyzeVersions();              // resource SSA-like versions
    reordered = SortPasses();       // dependency order
    AnalyzeLifetimes();             // first/last/use count
    if (reordered)
        AnalyzeVersions();          // refresh versions after reorder
    BuildBatches();                 // queue batching
    GatherBarriers();            // per-pass and final barriers
    GatherResolves();            // materialization points
    GatherResources();           // pool, retain, publish records
}
```

The second `AnalyzeVersions` pass is only needed when sorting changes pass order.

## 5. ResourceUse

Resource use is the single source of truth for dependency, lifetime, and barrier compilation.

```csharp
private readonly record struct ResourceUse(
    int ResourceIndex,
    ResourceState EntryState,
    ResourceState ExitState,
    RenderGraphAccess Access,
    SubResourceRange Range,
    int InVersion = -1,
    int OutVersion = -1,
    int SourcePass = -1);
```

Dependency pseudocode:

```csharp
AnalyzeVersions()
{
    foreach (resource)
    {
        version = 0;
        lastWriter = -1;
        readers.Clear();
    }

    foreach (pass in compile.Passes)
    foreach (use in compile.Uses[pass])
    {
        reads = use.Access has Read;
        writes = use.Access has Write;

        if (reads && lastWriter exists)
            AddDependency(lastWriter, pass, Raw);

        if (writes && lastWriter exists)
            AddDependency(lastWriter, pass, Waw or Raw);

        if (writes)
            foreach (reader in readers)
                AddDependency(reader, pass, War);

        if (writes)
            version++;
    }
}
```

This gives bottleneck analysis a concrete target: dependency cost scales with live uses and reader lists, not with arbitrary feature code.

## 6. Barrier

Barrier compilation is a state machine over `ResourceUse`:

```csharp
GatherBarriers()
{
    state = CreateBarrierState();       // imported current states or transient initial states

    foreach (pass in compile.Passes)
    foreach (use in compile.Uses[pass])
    {
        if (resource is texture)
            TrackTextureUse(state, compile.Barriers[pass], resource, use);
        else
            TrackBufferUse(state, compile.Barriers[pass], use);
    }

    BuildFinalBarriers(state);
}
```

Texture barrier rules:

```text
whole texture with uniform state -> one BarrierStep
partial range or mixed state     -> per subresource BarrierStep
UAV read/write overlap           -> UAV dependency
ExitState updates the tracked state after execute
FinalState uses exact matching, not compatible matching
```

Runtime apply:

```csharp
ApplyBarriers(encoder, barriers)
{
    BuildBarriers(barriers);      // converts BarrierStep to RHI texture/buffer barriers
    encoder.Barrier(...);
}
```

`BarrierStep.Intent` keeps diagnostic state even when runtime state already satisfies the transition.

## 7. Execute

Execution consumes the local `CompiledGraph` returned by `CompileGraph()`:

```csharp
Execute(device, queue, swapchain)
{
    CompileGraph();
    RetirePendingFrames(false);

    if (compile has no passes and no final intent)
    {
        ResolveResources(device, compile.RootResolves);
        PublishExports();        // direct path because no command buffer was submitted
        return;
    }

    encoder = device.CreateCommandEncoder(...);
    context = new RenderGraphContext(this, device, encoder);

    foreach (batch in compile.QueueBatches)
    {
        if (batch.Kind == Command)
            ExecuteCommandPass(encoder, context, batch.StartSlot, ...);
        else
            ExecuteComputeBatch(encoder, context, batch, ...);
    }

    ResolveResources(device, compile.RootResolves);
    ApplyBarriers(encoder, compile.FinalBarriers);

    commandBuffer = encoder.Finish();
    queue.Submit(commandBuffer, signalFence);

    QueueFrame(fence, value, commandBuffer);
    swapchain?.Present(...);
}
```

Export sink lifecycle:

```text
submitted frame owns exported handles until fence completes
RetireFrameResources calls export sinks after fence completion
non-imported exported resources are removed from graph ownership
```

Imported export lifecycle:

```text
imported resources extracted through ExtractTexture / ExtractBuffer call their sink after submitted state commit
non-imported exports are retired through the pending-frame fence path before their sink transfers ownership
history owners update their state in the extract sink
```

## 8. History

`ViewHistory` owns cross-frame resources. RenderGraph only owns the current frame records.

```csharp
registry.BeginFrame(graph, device);

RenderHistoryTexture history = registry.Texture(
    RenderHistoryNames.TemporalSceneColor,
    desc,
    ResourceState.ShaderResource);

graph.AddComputePass(
    "Temporal Resolve",
    builder =>
    {
        if (history.HasPrevious)
            builder.Read(history.Previous, ResourceState.ShaderResource);
        builder.Write(history.Current, ResourceState.UnorderedAccess);
    },
    (context, pass) => { ... });
```

History invariants:

```text
previous is imported read-only
current is imported writable only when backed by a persistent RHI handle
persistent current imports declare an extract final state and update history through the extract sink
graph-only current resources use internal final-state retention and do not publish cross-frame state
registry handles resize/reset/device ownership
graph handles current-frame dependencies and barriers
```

## 9. Bottleneck Checklist

Use these checks when measuring RenderGraph cost:

```text
CompileGraph setup count
  high value means features are doing expensive work during setup

compile.Uses total count
  drives validation, dependency analysis, lifetimes, barriers, and dump size

compile.Dependencies count
  grows from RAW/WAR/WAW edges; dependency insertion is keyed by EdgeKey in GraphScratch

texture subresource count
  mixed state textures force per mip/slice barrier records

compile.Transitions with no required barrier
  too many no-op transitions indicate overdeclared state changes

compile.Resolves and compile.RootResolves
  high value means physical resources materialize early or without a live pass

compile.Retains count
  high value means pool reuse is delayed by fence retirement

bind set owner eviction count
  should grow only with binding sets that reference destroyed views, not total owner size

bind set owner collisions
  BindingSetHash bucket scan cost grows with same-hash entries

pending frame count
  delayed fences keep buffers, textures, views, command buffers, and exports alive
```

## 10. Design Pressure

Names that should stay short because they are core concepts:

```text
CompiledGraph
Resource
ResourceUse
ResourceRecord
QueueBatch
BarrierSet
BarrierState
BarrierStep
ImportDesc
FinalState
TextureExport
BufferExport
RenderHistory
```

If a new operation cannot fit into this lifecycle, do not add another thin method. Decide which lifecycle owns it:

```text
Record      pass/resource declarations
Compile     validation, culling, dependency, lifetime, barrier, resolve, resource records
Execute     RHI command recording and submit
Retire      fence-complete cleanup, export publish, pool return
History     cross-frame resource ownership
Feature     high-level pass composition
RHI         backend handles and command primitives
```

Deletion test:

```text
Deleting a wrapper should not leave callers needing hidden rules.
Deleting a lifecycle module should move real rules to another module.
If deletion only removes a forwarded name, the wrapper was shallow.
```
