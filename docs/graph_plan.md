# RenderGraph 使用 Placed 接口实现资源别名

## 背景

当前 RenderGraph 中的 transient 资源通过 `RGResourcePool` 管理，采用简单的"描述符匹配复用"策略：
- 资源生命周期结束时归还池中
- 新资源优先从池中匹配同描述符的资源

这种方式无法实现**真正的内存别名（memory aliasing）**——即生命周期不重叠的资源共享同一块 GPU 内存。Diligent 提供了 Placed 资源 API，可以在同一块 `IDeviceMemory` 的不同偏移处创建资源，从而实现内存别名，大幅减少显存占用。

## 核心 API

Diligent Placed 资源 API 如下：

| API | 说明 |
|-----|------|
| `GetTextureMemoryRequirements(desc)` &rarr; `MemoryRequirements` | 查询纹理所需内存大小和对齐 |
| `GetBufferMemoryRequirements(desc)` &rarr; `MemoryRequirements` | 查询缓冲区所需内存大小和对齐 |
| `CreateDeviceMemory(createInfo)` &rarr; `IDeviceMemory` | 创建设备内存堆（`DEVICE_MEMORY_TYPE_PLACED`） |
| `CreatePlacedTexture(desc, memory, offset)` &rarr; `ITexture` | 在内存堆指定偏移处创建纹理 |
| `CreatePlacedBuffer(desc, memory, offset)` &rarr; `IBuffer` | 在内存堆指定偏移处创建缓冲区 |

`MemoryRequirements` 结构：`{ Size, Alignment, MemoryTypeBits }`

## Proposed Changes

### 1. 新建 `RGMemoryHeap` 类

#### [NEW] [RGMemoryHeap.cs](file:///d:/SomeEngine/src/SomeEngine.Render/Graph/RGMemoryHeap.cs)

管理一块 `IDeviceMemory`，使用线性分配器 + 别名重叠支持。

关键功能：
- 封装 `IDeviceMemory` 的创建（`DEVICE_MEMORY_TYPE_PLACED`）
- 提供 `Allocate(size, alignment)` &rarr; `(offset, heapIndex)` 方法
- 在 Compile 阶段预计算所有资源的偏移（离线分配），Execute 时用偏移创建 Placed 资源
- 支持多个堆（如果一个堆放不下，或者 `MemoryTypeBits` 不兼容，则创建新堆）

核心数据结构：
```csharp
public class RGMemoryHeap : IDisposable
{
    private IDeviceMemory? _memory;
    private ulong _capacity;
    
    // 预计算的分配条目
    private List<AllocationEntry> _allocations = new();
    
    public struct AllocationEntry
    {
        public ulong Offset;
        public ulong Size;
        public int ResourceId;     // 对应 RGResource 索引
        public int FirstPassIndex; // 生命周期起始
        public int LastPassIndex;  // 生命周期结束
    }
}
```

---

### 2. 修改 `RGResource`

#### [MODIFY] [RGResource.cs](file:///d:/SomeEngine/src/SomeEngine.Render/Graph/RGResource.cs)

为 `RGResource` 添加内存别名元数据：

```diff
 public abstract class RGResource
 {
     // ... existing fields ...
+    // Placed resource memory info (set during Compile)
+    internal ulong MemorySize { get; set; }
+    internal ulong MemoryAlignment { get; set; }
+    internal ulong MemoryOffset { get; set; } = ulong.MaxValue; // offset in heap
+    internal int HeapIndex { get; set; } = -1; // which heap this resource belongs to
 }
```

---

### 3. 修改 `RenderGraph.Compile()`

#### [MODIFY] [RenderGraph.cs](file:///d:/SomeEngine/src/SomeEngine.Render/Graph/RenderGraph.cs)

在现有的 **步骤5（计算资源生命周期）** 之后，新增 **步骤6：内存别名分配**。

算法：
1. 收集所有非 imported、有生命周期的 transient 资源
2. 对每个 transient 资源，查询 `GetTextureMemoryRequirements` / `GetBufferMemoryRequirements` 获取 `MemoryRequirements`
3. 按 `Size` 降序排列（大资源优先分配，贪心策略）
4. 对每个资源，在堆的时间线上查找一个可用的偏移区间，使得该区间在此资源的 `[FirstPassIndex, LastPassIndex]` 生命周期内不与其他已分配资源重叠
5. 如果找不到合适位置，则扩展堆容量或创建新堆
6. 记录每个资源的 `(HeapIndex, MemoryOffset)`

> **注意**：因为 `GetTextureMemoryRequirements` 需要 `IRenderDevice`，而 `Compile()` 目前不接受 device 参数。需要修改 `Compile` 签名以接收 `IRenderDevice` 以实现 Placed 资源分配。

---

### 4. 修改 `RenderGraph.Execute()`

#### [MODIFY] [RenderGraph.cs](file:///d:/SomeEngine/src/SomeEngine.Render/Graph/RenderGraph.cs)

修改 **步骤2.1（分配资源）**：

```diff
 // 2.1. Allocate resources starting here
 foreach (var handle in GetResourcesStartingAt(i))
 {
     var res = _resources[handle.Id];
     if (res.IsImported) continue;
 
-    if (res is RGTexture tex && tex.InternalTexture == null)
-        tex.InternalTexture = _resourcePool.AcquireTexture(context.Device, tex.Desc);
-    else if (res is RGBuffer buf && buf.InternalBuffer == null)
-        buf.InternalBuffer = _resourcePool.AcquireBuffer(context.Device, buf.Desc);
+    if (res.HeapIndex >= 0)
+    {
+        // Use Placed resource from memory heap
+        var heap = _memoryHeaps[res.HeapIndex];
+        if (res is RGTexture tex && tex.InternalTexture == null)
+            tex.InternalTexture = context.Device.CreatePlacedTexture(tex.Desc, heap.Memory, res.MemoryOffset);
+        else if (res is RGBuffer buf && buf.InternalBuffer == null)
+            buf.InternalBuffer = context.Device.CreatePlacedBuffer(buf.Desc, heap.Memory, res.MemoryOffset);
+    }
+    else
+    {
+        // Fallback: pool-based allocation
+        if (res is RGTexture tex && tex.InternalTexture == null)
+            tex.InternalTexture = _resourcePool.AcquireTexture(context.Device, tex.Desc);
+        else if (res is RGBuffer buf && buf.InternalBuffer == null)
+            buf.InternalBuffer = _resourcePool.AcquireBuffer(context.Device, buf.Desc);
+    }
 }
```

修改 **步骤2.4（释放资源）**：Placed 资源只需 Dispose 资源对象（释放 view 等），内存堆本身在帧结束时 / RenderGraph.Dispose 时释放。

---

### 5. 修改 `RGResourcePool`（可选重构）

#### [MODIFY] [RGResourcePool.cs](file:///d:/SomeEngine/src/SomeEngine.Render/Graph/RGResourcePool.cs)

保留现有池作为 Placed 资源不可用时的回退方案。不做大改动。

---

### 6. 新增单元测试

#### [MODIFY] [RenderGraphTests.cs](file:///d:/SomeEngine/tests/SomeEngine.Tests/RenderGraphTests.cs)

添加测试用例 `TestPlacedResourceAliasing`，验证：
- 生命周期不重叠的两个同大小纹理被分配到同一偏移（完美别名）
- 生命周期重叠的纹理被分配到不同偏移
- 无 device 时退化为不执行 Placed 分配

## User Review Required

> [!IMPORTANT]
> `Compile()` 方法需要新增 `IRenderDevice?` 参数来查询 `MemoryRequirements`。这是一个 **API breaking change**。现有调用 `graph.Compile()` 的代码不受影响（参数可选），但新的 Placed 别名功能需要传入 device。

> [!WARNING]
> Placed 资源 API 在 OpenGL / D3D11 后端不受支持（`RenderDeviceBase` 中标记为 `UNSUPPORTED`）。代码需要在运行时判断是否支持 Placed 资源，不支持时由于 Compile 时的检查，会自动回退分配。

## Verification Plan

### Automated Tests
- `dotnet build` 编译通过
- `dotnet test --filter RenderGraphTests` 所有测试通过

### Manual Verification
- 检查新增的 `TestPlacedResourceAliasing` 测试的内存偏移分配结果
