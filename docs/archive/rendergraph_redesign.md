# RenderGraph 类型体系设计

原则：**最小职责、组合优于继承、自然命名**

---

## 公共类型

### RenderGraphHandle

帧内资源引用。

```csharp
public readonly struct RenderGraphHandle(int index)
{
    internal readonly int Index = index;
    public bool IsValid => Index >= 0;
    public static readonly RenderGraphHandle Invalid = new(-1);
}
```

### RenderGraphBuilder

Pass 声明依赖。持有 graph 引用 + passIndex，将 Read/Write 写入 graph 内部。

```csharp
public struct RenderGraphBuilder(RenderGraph graph, int passIndex)
{
    public RenderGraphHandle Read(RenderGraphHandle h, ResourceState state = ResourceState.ShaderResource);
    public RenderGraphHandle Write(RenderGraphHandle h, ResourceState state = ResourceState.RenderTarget);
    public RenderGraphHandle ReadWrite(RenderGraphHandle h, ResourceState state);
}
```

### RenderGraphContext

Pass 执行时获取物理资源。

```csharp
public class RenderGraphContext(RenderGraph graph, IDeviceContext deviceContext)
{
    public ITexture? GetTexture(RenderGraphHandle h);
    public IBuffer? GetBuffer(RenderGraphHandle h);
    public ITextureView? GetView(RenderGraphHandle h, TextureViewType type);
    public ITextureView? GetMipView(RenderGraphHandle h, TextureViewType type, uint mip);
    public IBufferView? GetBufferView(RenderGraphHandle h, BufferViewType type);
    public IDeviceContext CommandList => deviceContext;
}
```

### IRenderGraphPass

```csharp
public interface IRenderGraphPass
{
    string Name { get; }
    void Setup(RenderGraphBuilder builder);
    void Execute(RenderGraphContext context);
}
```

### RenderGraph

```csharp
public class RenderGraph : IDisposable
{
    // ── 帧内 ──
    List<IRenderGraphPass> _passes;
    List<RenderGraphResource> _resources;
    Dictionary<string, int> _resourceLookup;
    Dictionary<int, ITexture> _importedTextures;  // Import 的物理资源
    Dictionary<int, IBuffer> _importedBuffers;
    List<PassMetadata> _passMetadata;

    // ── 跨帧 ──
    Dictionary<string, CachedTexture> _textureCache;
    Dictionary<string, CachedBuffer> _bufferCache;
    List<RenderGraphMemoryHeap> _heaps;
    IRenderDevice _device;
    IFence _fence;
    ulong _fenceValue;
    Queue<(ulong fence, IDisposable)> _deferredReleases;

    // ── API ──
    void BeginFrame();

    RenderGraphHandle CreateTexture(string name, TextureDesc desc);
    RenderGraphHandle CreateBuffer(string name, BufferDesc desc);
    RenderGraphHandle Import(string name, ITexture texture, ResourceState state);

    void AddPass(IRenderGraphPass pass);
    void AddPass<TData>(string name,
        Action<RenderGraphBuilder, TData> setup,
        Action<RenderGraphContext, TData> execute) where TData : class, new();
    void MarkOutput(RenderGraphHandle h);

    void Compile();
    void Execute(IDeviceContext ctx);
    void EndFrame();
}
```

---

## 内部类型

### RenderGraphResource

帧内节点。最小化。不存 Desc（Desc 在缓存中）。无 IsImported（导入通过 side-table）。

```csharp
internal struct RenderGraphResource(string name, ResourceKind kind)
{
    public string Name = name;
    public ResourceKind Kind = kind;
    public ResourceState CurrentState;
}

internal enum ResourceKind { Texture, Buffer }
```

### CachedTexture / CachedBuffer

跨帧缓存的**值类型**（字典中的条目）。不是管理器类。缓存逻辑在 RenderGraph 内部。

```csharp
internal class CachedTexture
{
    public TextureDesc Desc;
    public ITexture? Texture;
    public int IdleFrames;
    public ulong LastUsedFence;
    public ResourceState LastState;
}

internal class CachedBuffer
{
    public BufferDesc Desc;
    public IBuffer? Buffer;
    public int IdleFrames;
    public ulong LastUsedFence;
    public ResourceState LastState;
}
```

### LambdaRenderGraphPass

Lambda 便利封装。内部类型，用户不直接使用。

```csharp
internal class LambdaRenderGraphPass<TData>(
    string name, TData data,
    Action<RenderGraphBuilder, TData> setup,
    Action<RenderGraphContext, TData> execute) : IRenderGraphPass where TData : class, new()
{
    public string Name => name;
    public void Setup(RenderGraphBuilder builder) => setup(builder, data);
    public void Execute(RenderGraphContext context) => execute(context, data);
}
```

### PassMetadata

```csharp
internal class PassMetadata
{
    public bool Active;
    public List<(RenderGraphHandle Handle, ResourceState State)> Reads = [];
    public List<(RenderGraphHandle Handle, ResourceState State)> Writes = [];
}
```

---

## 资源解析流程

```
Execute 时获取物理资源:
  1. 查 _importedTextures[handle.Index] → 命中则直接返回（外部资源）
  2. 查 _textureCache[resource.Name] → 命中且 Desc 兼容 → 复用
  3. 未命中/不兼容 → 创建新的，旧的入 _deferredReleases
```

---

## 帧循环

```
BeginFrame() — 清帧内状态, ProcessDeferredReleases()
CreateTexture/CreateBuffer/Import × N
AddPass × N
Compile() — Setup → 依赖图 → 拓扑排序 → barriers → heap aliasing
Execute(ctx) — 解析物理资源 → barriers → 执行 passes
EndFrame() — 标记活跃, idle++, 过期入延迟释放, Signal fence
```

---

## 外部迁移（除 RenderGraph 外禁止手动管理 Buffer/Texture）

### ClusterResourceManager

- 移除 `InitHeap()` 中 4 个 `device.CreateBuffer` 调用
- `PageHeap`/`GlobalBVHBuffer` 改为只存 `BufferDesc`，物理 buffer 通过 `graph.CreateBuffer("PageHeap", desc)` 获取
- `PageFaultBuffer`/`PageFaultReadbackBuffer` 同上
- 移除 `Dispose()` 中的 buffer Dispose
- `ExecutePendingUploads` 改用 Span API（消除 unsafe）

### RenderContext

- 移除 `DepthBuffer`/`DepthBufferDSV` 的 `CreateTexture`/`Dispose`
- 只提供 `DepthBufferDesc`（Resize 时更新 Desc）
- 物理纹理通过 `graph.CreateTexture("DepthBuffer", desc)` 获取

### ClusterPipeline (HiZ History)

- 移除 `_prevHiZTexture`/`_currHiZTexture` 和 `PromoteCurrentHiZHistory()`/`ValidateHiZHistoryForCurrentFrame()`
- HiZ history 通过 ping-pong 命名自动实现：
  ```csharp
  string currName = _pingPong ? "HiZ_A" : "HiZ_B";
  string prevName = _pingPong ? "HiZ_B" : "HiZ_A";
  var hCurrHiZ = graph.CreateTexture(currName, hizDesc);
  var hPrevHiZ = graph.CreateTexture(prevName, hizDesc);
  _pingPong = !_pingPong;
  ```

### HiZBuildPass

- 移除 `_cachedHiZTexture`/`_srvMipViews`/`_uavMipViews`/`_disposeQueue`/`_frameIndex`
- per-mip view 通过 `RenderGraphContext.GetMipView()` 获取

### ClusterDebugReadbackPass

- 移除 `_readbackBuffer` 和 `Init()` 中的 `device.CreateBuffer`
- readback buffer 通过 `graph.CreateBuffer("DebugReadback", desc)` 获取

### ClusterUploadInstanceDataPass

- `unsafe`/`fixed` + `(IntPtr)` 替换为 Span API 版 `UpdateBuffer`

### ClusterResourceManager.ExecutePendingUploads

- `unsafe`/`fixed` + `(IntPtr)` 替换为 Span API 版 `UpdateBuffer`

---

## 类型对照

| 旧 | 新 | 变化 |
|---|---|---|
| `RGResourceHandle` | `RenderGraphHandle` | 去 Version |
| `RGTextureNode` + `RGBufferNode` + `RGResourceNode` | `RenderGraphResource` | 合并为最小 struct |
| `RGTexture` + `RGBuffer` + `RGPhysicalResource` | `CachedTexture` + `CachedBuffer` | 无基类 |
| `RenderPass` (abstract class) | `IRenderGraphPass` (interface) | 组合优于继承 |
| `LambdaRenderPass<T>` | `LambdaRenderGraphPass<T>` | 保留，内部类型 |
| `RGResourcePool` | 删除 | 缓存内置 |
| `PassMetadata` (内嵌 class) | `PassMetadata` | 独立内部类型 |

---

## 文件清单

| 操作 | 旧文件 | 新文件/说明 |
|---|---|---|
| **重写** | `RenderGraph.cs` | 帧内/跨帧分离 |
| **重写** | `RGResource.cs` | → `RenderGraphResource.cs` |
| **重写** | `RenderGraphBuilder.cs` | 精简 |
| **重写** | `RenderGraphContext.cs` | + mip view |
| **重写** | `RenderPass.cs` | → `RenderGraphPass.cs`（接口 + Lambda） |
| **删除** | `LambdaRenderPass.cs` | 合入 RenderGraphPass.cs |
| **删除** | `RGResourcePool.cs` | — |
| **修改** | `RGMemoryHeap.cs` | → 重命名 + aliasing |
| **修改** | 全部使用方 | 适配新接口 |

## Verification

```bash
dotnet build -c Debug
dotnet test tests/SomeEngine.Tests --filter "FullyQualifiedName~RenderGraphTests" -c Debug
```
