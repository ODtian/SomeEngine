# Deform Cache 设计

## 1. 核心架构

在剔除结束后、光栅化前，插入 **PreDeform CS** 阶段，将存活的可变形 Cluster 顶点一次性变换完毕。后续光栅化和 Shade 直接读取结果。

```
Conservative Cull (扩展 Bounds)
    ↓ 可见 Cluster 列表
RasterBinning CS (分 Bin)
    ↓ 每个 Bin 按 VertexEval 类型分组
Per-Bin PreDeform CS  →  DeformedCacheBuffer
    ↓ UAV Barrier
Rasterize (读 DeformedCache)
    ↓
Shade (读 DeformedCache 或 inline 重算)
```

## 2. IVertexEvaluate — 用户态可编程接口

管线**不知道**任何具体变形逻辑（骨骼蒙皮、WPO、程序化变形等）。所有变形都是用户在 Slang 中实现的 `IVertexEvaluate` 接口。管线只提供基础设施：

- DeformedCacheBuffer 原子分配
- per-Bin 的 Dispatch 调度
- `VertexEvalContext` 上下文数据

```slang
interface IVertexEvaluate
{
    associatedtype DeformedVertex;  // 用户定义输出结构，sizeof 即 stride

    // PreDeform 阶段调用：变换原始顶点 → 写入 cache
    static DeformedVertex Evaluate(VertexEvalContext ctx, uint localVertexIdx);

    // 从 cache 读回 position（光栅化必需）
    static float3 GetPosition(DeformedVertex v);
};
```

### Slang 工具函数（非管线耦合）

管线在 `tess_utils.slang` 中提供 LBS、Dual Quaternion 等工具函数，但这些只是 library——用户自由选择使用或不使用。

## 3. 异构 Cache 设计

### 问题

不同 `IVertexEvaluate` 实现的输出布局完全不同：
- 只输出 Position：6B/vertex
- Position + Normal：12B/vertex
- Position + Normal + Tangent + CustomAttr：任意 stride

管线不能假设固定布局。

### 方案：Generic ByteBuffer + per-Bin compile-time stride

```
DeformedCacheBuffer: 一个大的 RWByteAddressBuffer
每个 Bin 的 IVertexEvaluate 变体在编译时确定 stride = sizeof(DeformedVertex)
分配: InterlockedAdd(globalCounter, vertexCount * sizeof(DeformedVertex)) → byteOffset
```

**stride 是 `associatedtype DeformedVertex` 的编译时属性**，不需要运行时存储。

### 分配时机

分配在 **PreDeform dispatch 的每个 thread group 的第一个线程**内完成：

```slang
[numthreads(128, 1, 1)]
void CSPreDeform(uint3 gid : SV_GroupID, uint tid : SV_GroupThreadIndex)
{
    groupshared uint gs_baseOffset;
    
    if (tid == 0)
    {
        uint vertCount = GetClusterVertexCount(...);
        uint byteSize = vertCount * sizeof(T::DeformedVertex);
        InterlockedAdd(DeformedAllocCounter[0], byteSize, gs_baseOffset);
        DeformAllocTable[visClusterIdx].ByteOffset = gs_baseOffset;
    }
    GroupMemoryBarrierWithGroupSync();
    
    // 从 PageHeap 读原始顶点 + 用户属性流 → 调用用户 Evaluate → 写入 cache
    if (tid < vertCount)
    {
        T::DeformedVertex dv = T::Evaluate(ctx, tid);
        uint addr = gs_baseOffset + tid * sizeof(T::DeformedVertex);
        DeformedCacheBuffer.Store(addr, dv);
    }
}
```

### 多 Bin Region 的 Cache 排布

多个 Bin 的 PreDeform dispatch 按顺序执行（UAV barrier 间隔），共享同一个 `DeformedAllocCounter` 和 `DeformedCacheBuffer`。数据自然按 dispatch 顺序填充，无需额外操作：

```
[Bin0 clusters' data][Bin1 clusters' data][Bin2 clusters' data]...
```

### Shade 时从 Cache 索引

```slang
uint visClusterIdx = DecodeVisBuffer(pixel);
uint baseOffset = DeformAllocTable[visClusterIdx].ByteOffset;
uint vi = FetchVertexIndex(pageHeap, ...);

// stride 是编译时常量，零运行时开销
MyDeformedVertex v = DeformedCacheBuffer.Load<MyDeformedVertex>(baseOffset + vi * sizeof(MyDeformedVertex));
```

### Stride 不进 Bin Key

Stride 是 `IVertexEvaluate` 变体的隐含属性。不同变体已经在不同的 Bin 中（不同 shader）。Stride 作为 key 是冗余的。

## 4. PreDeform On/Off 双路径

管线必须支持完全关闭 PreDeform，用于性能对比基准测试。

**Path A: PreDeform ON (Cache)**
```
Cull → Binning → PreDeform CS (writes cache) → Raster (reads cache) → Shade (reads cache)
```

**Path B: PreDeform OFF (Inline Re-evaluate)**
```
Cull → Binning → Raster (inline Evaluate) → Shade (inline Evaluate)
```

通过 Slang 泛型编译时特化实现，不是运行时分支：

```slang
interface IVertexSource
{
    static float3 FetchPosition(uint visClusterIdx, uint localVIdx);
};

struct CachedSource : IVertexSource { ... }           // 读 cache
struct InlineSource<T : IVertexEvaluate> : IVertexSource { ... }  // inline 调用 Evaluate

// Raster/Shade kernel 用泛型参数选择
void RasterKernel<VS : IVertexSource>(...) { ... }
```

编译出两套 PipelineState，运行时 toggle。

### Slang 死代码消除

`VertexEvalContext` 可能很大（BoneBuffer、MaterialParams、HeightMap 等），但 Slang + 后端编译器会自动消除未使用的资源绑定：
- 未使用的 Buffer/Texture：**从 shader reflection 中完全消失**，SRB 不需要绑定
- ConstantBuffer 未读字段：不产生 GPU 指令开销
- 前提：per-bin PipelineState 是编译时特化，DCE 完全生效

## 5. 骨骼系统 GPU 布局（继承模型）

### 场景

子物体（衣服）绑定到父物体（人体）的骨骼，复用父级矩阵，不出现两份。

### GPU 内存

`BoneMatrixBuffer`（全局 `StructuredBuffer<float3x4>`）分段上传：

```
[HumanA: 0..64] [HumanB: 65..129] [ClothA1_own: 130..137] [ClothA2_own: 138..143] ...
```

### Instance 数据

```
GpuInstanceHeader:
    uint BoneOffset0;       // 父级（或自身）骨骼区域
    uint BoneOffset1;       // 自身额外骨骼区域
    uint PackedBoneCounts;  // ParentBoneCount:16 | OwnBoneCount:16
```

| 实体 | BoneOffset0 | BoneOffset1 | ParentCount | OwnCount |
|------|------------|------------|-------------|----------|
| HumanA | 0 | — | 65 | 0 |
| ClothA1 (on HumanA) | 0 | 130 | 65 | 8 |

### Shader 读取

```slang
float3x4 FetchBoneMatrix(GpuInstanceHeader inst, uint logicalBoneIdx)
{
    uint parentCount = inst.PackedBoneCounts & 0xFFFF;
    if (logicalBoneIdx < parentCount)
        return BoneBuffer[inst.BoneOffset0 + logicalBoneIdx];
    else
        return BoneBuffer[inst.BoneOffset1 + (logicalBoneIdx - parentCount)];
}
```

骨骼索引在 **Asset 烘焙阶段** 统一映射为 logical index：`0..parentCount-1` → 父级，`parentCount..total-1` → 自身。运行时零计算。

### 压缩机会

- `BoneOffset1` 存为相对 `BoneOffset0` 的 delta
- ParentBoneCount / OwnBoneCount 各 16-bit

## 6. 保守 Bounds 剔除

可变形 Instance 在 BVH 遍历和 Cluster 剔除时使用扩展后的 AABB：

```
GpuInstanceHeader:
    float BoundsExpansion;   // 世界空间保守扩展量
    uint  DeformFlags;       // bit0: Skinned, bit1: WPO, bit2: Tess
```

Cull Shader:
```slang
if (DeformFlags != 0)
{
    aabb.Min -= BoundsExpansion;
    aabb.Max += BoundsExpansion;
    lodSphereRadius += BoundsExpansion;
}
```
