# InstanceLayout

本文档只定义最终方案。中间讨论过的其它名字和层次不进入设计。

## 目标

`InstanceLayout` 是唯一布局概念。每个具体布局都是一个 C# 类型，例如：

```csharp
ForestInstance
ActorInstance
ShadowInstance
```

管线通过接口约束判断一个 `InstanceLayout` 能不能用于该管线。运行时不解释布局，不查字段表，不用字符串查 offset，不把布局装成接口对象。

性能目标是和手写静态布局相同：

```text
instanceIndex * const stride + const offset
```

C# 写入和 shader 读取都必须是固定 stride、固定 offset。

## 固定名字

只保留这些名字：

```text
InstanceLayout      最终布局类型
HeaderField         header 字段标记
DataField           data 字段标记
UsePipe             声明布局要实现哪个管线接口
IClusterLayout      Cluster 管线接口约束
InstanceGpu         上传实例 buffer
```

不使用这些名字：

```text
PipelineLayout
SceneLayout
HeaderPatch
DataPatch
LayoutStore
IHeaderBlock
字段要求表
运行时 layout 对象
```

## 手写内容

只手写两类代码：

```text
管线接口
最终 InstanceLayout 声明
```

其它能生成的全部生成。

## 管线接口

Cluster 管线定义自己需要的能力：

```csharp
public interface IClusterLayout<TLayout>
    where TLayout : struct, IClusterLayout<TLayout>
{
    static abstract int HeaderStride { get; }
    static abstract int DataStride { get; }
    static abstract ulong LayoutHash { get; }

    static abstract void WriteCluster(
        Span<byte> header,
        in RenderInstance instance,
        uint slotBase,
        uint dataBase,
        uint dataMask);

    static abstract uint ReadSlotBase(ReadOnlySpan<byte> header);
}
```

这个接口不是一个新布局概念。它只是 Cluster 管线对 `TLayout` 的编译期约束。

管线代码使用这个约束：

```csharp
public sealed class ClusterPipeline<TLayout>
    where TLayout : struct, IClusterLayout<TLayout>
{
    private readonly InstanceGpu<TLayout> _instances;
}
```

如果某个布局没有实现 `IClusterLayout<TLayout>`，它就不能用于 `ClusterPipeline<TLayout>`，编译期失败。

## 布局声明

业务声明最终布局：

```csharp
[InstanceLayout]
[UsePipe(typeof(IClusterLayout<>))]
public readonly partial struct ForestInstance
{
    [HeaderField] public uint BvhRoot;
    [HeaderField] public uint SlotBase;
    [HeaderField] public uint DataBase;
    [HeaderField] public uint DataMask;
    [HeaderField] public float Bounds;
    [HeaderField] public uint WindId;

    [DataField] public float WindScale;
    [DataField] public Vector4 Tint;
}
```

`ForestInstance` 就是最终 `InstanceLayout`。字段声明顺序就是布局顺序。

`UsePipe(typeof(IClusterLayout<>))` 是显式声明：

```text
ForestInstance 要实现 IClusterLayout<ForestInstance>
```

源码生成器不推断它属于哪个管线。

## 生成的 C# 布局代码

源码生成器为 `ForestInstance` 生成常量、接口实现和固定 offset 写入：

```csharp
public readonly partial struct ForestInstance :
    IClusterLayout<ForestInstance>
{
    public const int HeaderStrideConst = 32;
    public const int DataStrideConst = 32;
    public const ulong LayoutHashConst = 0x12345678ul;

    private const int BvhRootOffset = 0;
    private const int SlotBaseOffset = 4;
    private const int DataBaseOffset = 8;
    private const int DataMaskOffset = 12;
    private const int BoundsOffset = 16;
    private const int WindIdOffset = 20;

    private const int WindScaleOffset = 0;
    private const int TintOffset = 16;

    public static int HeaderStride => HeaderStrideConst;
    public static int DataStride => DataStrideConst;
    public static ulong LayoutHash => LayoutHashConst;

    public static void WriteCluster(
        Span<byte> header,
        in RenderInstance instance,
        uint slotBase,
        uint dataBase,
        uint dataMask)
    {
        WriteUInt32(header, BvhRootOffset, instance.BvhRootIndex);
        WriteUInt32(header, SlotBaseOffset, slotBase);
        WriteUInt32(header, DataBaseOffset, dataBase);
        WriteUInt32(header, DataMaskOffset, dataMask);
        WriteFloat32(header, BoundsOffset, instance.BoundsExpansion);
    }

    public static uint ReadSlotBase(ReadOnlySpan<byte> header)
        => ReadUInt32(header, SlotBaseOffset);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        => Unsafe.ReadUnaligned<uint>(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(bytes), offset));

    private static void WriteUInt32(Span<byte> bytes, int offset, uint value)
        => Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(bytes), offset),
            value);

    private static void WriteFloat32(Span<byte> bytes, int offset, float value)
        => Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(bytes), offset),
            value);
}
```

实际生成代码可以把公共读写 helper 放到共享类里，但热路径必须仍然是常量 offset。

## 生成校验

源码生成器必须校验 `UsePipe` 指向的接口能否由布局字段满足。

例如 `IClusterLayout<>` 需要：

```text
HeaderStride
DataStride
LayoutHash
WriteCluster(...)
ReadSlotBase(...)
```

生成器负责生成这些成员。生成 `WriteCluster(...)` 时必须确认布局中存在这些字段：

```text
BvhRoot
SlotBase
DataBase
DataMask
Bounds
```

缺字段时报编译错误：

```text
ForestInstance uses IClusterLayout<> but misses header field SlotBase.
```

类型不匹配也报编译错误：

```text
ForestInstance field Bounds must be float for IClusterLayout<>.
```

## InstanceGpu

通用上传类使用管线接口约束：

```csharp
public sealed class InstanceGpu<TLayout>
    where TLayout : struct, IClusterLayout<TLayout>
{
    private byte[] _headers = [];
    private byte[] _data = [];

    private void WriteHeader(
        int index,
        in RenderInstance instance,
        uint slotBase,
        uint dataBase,
        uint dataMask)
    {
        Span<byte> header = _headers.AsSpan(
            index * TLayout.HeaderStride,
            TLayout.HeaderStride);

        TLayout.WriteCluster(
            header,
            in instance,
            slotBase,
            dataBase,
            dataMask);
    }
}
```

这里没有运行时布局对象。`TLayout = ForestInstance` 后，JIT 或 AOT 对封闭泛型生成特化代码。

如果需要完全显式静态代码，源码生成器可以生成具体上传类：

```csharp
public sealed class ForestInstanceGpu
{
    private byte[] _headers = [];

    private void WriteHeader(
        int index,
        in RenderInstance instance,
        uint slotBase,
        uint dataBase,
        uint dataMask)
    {
        Span<byte> header = _headers.AsSpan(
            index * ForestInstance.HeaderStrideConst,
            ForestInstance.HeaderStrideConst);

        ForestInstance.WriteCluster(
            header,
            in instance,
            slotBase,
            dataBase,
            dataMask);
    }
}
```

这个具体类也是生成物，不是手写架构层。

## 生成的属性视图

属性视图可以生成，但不是管线必需路径。它用于调试、测试和直接业务写入：

```csharp
public ref struct ForestHeader
{
    private Span<byte> _bytes;

    public ForestHeader(Span<byte> bytes)
        => _bytes = bytes;

    public static implicit operator ForestHeader(Span<byte> bytes)
        => new(bytes);

    public uint SlotBase
    {
        get => ForestInstance.ReadUInt32(_bytes, ForestInstance.SlotBaseOffset);
        set => ForestInstance.WriteUInt32(_bytes, ForestInstance.SlotBaseOffset, value);
    }

    public float Bounds
    {
        get => ForestInstance.ReadFloat32(_bytes, ForestInstance.BoundsOffset);
        set => ForestInstance.WriteFloat32(_bytes, ForestInstance.BoundsOffset, value);
    }
}
```

使用方式：

```csharp
ForestHeader header = headerSpan;
header.SlotBase = slotBase;
```

属性内部仍然是固定 offset，不允许字符串查找。

## DataHeap

`DataField` 进入同一个 `InstanceLayout` 的 data 区。`DataStride` 由源码生成器按字段声明顺序和对齐规则计算。

每个实例的 data 地址：

```text
instanceIndex * TLayout.DataStride
```

生成代码示例：

```csharp
public ref struct ForestData
{
    private Span<byte> _bytes;

    public ForestData(Span<byte> bytes)
        => _bytes = bytes;

    public static implicit operator ForestData(Span<byte> bytes)
        => new(bytes);

    public float WindScale
    {
        get => ForestInstance.ReadFloat32(_bytes, ForestInstance.WindScaleOffset);
        set => ForestInstance.WriteFloat32(_bytes, ForestInstance.WindScaleOffset, value);
    }
}
```

如果某类数据需要真正变长，不能把 header 做成变长；只能在 header 中声明普通固定字段，例如：

```csharp
[HeaderField] public uint BlobBase;
[HeaderField] public uint BlobCount;
```

实际变长数据放外部 blob buffer。

## 生成的 Slang

每个 `InstanceLayout` 生成一个 Slang include：

```text
assets/Shaders/generated/instance/forest_instance.slang
```

内容示例：

```slang
static const uint INSTANCE_HEADER_STRIDE_BYTES = 32;
static const uint INSTANCE_DATA_STRIDE_BYTES = 32;
static const uint2 INSTANCE_LAYOUT_HASH = uint2(0x12345678u, 0x00000000u);

static const uint BVH_ROOT_OFFSET = 0;
static const uint SLOT_BASE_OFFSET = 4;
static const uint DATA_BASE_OFFSET = 8;
static const uint DATA_MASK_OFFSET = 12;
static const uint BOUNDS_OFFSET = 16;
static const uint WIND_ID_OFFSET = 20;

uint ReadBvhRoot(ByteAddressBuffer headers, uint instanceId)
{
    return headers.Load(instanceId * INSTANCE_HEADER_STRIDE_BYTES + BVH_ROOT_OFFSET);
}

uint ReadSlotBase(ByteAddressBuffer headers, uint instanceId)
{
    return headers.Load(instanceId * INSTANCE_HEADER_STRIDE_BYTES + SLOT_BASE_OFFSET);
}

float ReadBounds(ByteAddressBuffer headers, uint instanceId)
{
    return asfloat(headers.Load(instanceId * INSTANCE_HEADER_STRIDE_BYTES + BOUNDS_OFFSET));
}
```

shader variant 编译时选择对应 include。shader 中也不能运行时解释布局。

## 运行时校验

管线或 shader 绑定时校验 layout hash：

```csharp
if (shader.InstanceLayoutHash != TLayout.LayoutHash)
    throw new InvalidOperationException(...);
```

这个校验不在 per-instance 热路径。

## 零开销边界

允许：

```text
TLayout.HeaderStride
TLayout.WriteCluster(...)
封闭泛型特化
源码生成具体类
固定 offset 属性视图
固定 offset Slang loader
```

禁止：

```text
运行时 layout 对象
字段表遍历 pack
字符串查字段
Dictionary 查 offset
反射
把 layout 装成接口变量
把 header 装成接口变量
每 instance 变长 header
```

## 现有代码迁移

当前全局协议要被替换：

```text
InstanceHeaderLayoutRegistration.cs
InstanceHeaderLayout
InstanceHeaderData
InstanceMeta 固定 MaterialOverride stride
assets/Shaders/generated/instance_header_layout.slang
```

目标结构：

```text
[InstanceLayout] 具体布局声明
源码生成 C# stride / offset / hash / interface 实现
源码生成 Slang include
InstanceGpu<TLayout>
可选源码生成具体 InstanceGpu
```

`RenderInstance` 只保留所有布局共享的事实：

```text
SourceEntity
InstanceIndex
Transform
PrevTransform
BvhRootIndex
BoundsExpansion
Materials
```

布局专用字段不写回 `RenderInstance`：

```text
SlotBase
DataBase
DataMask
WindId
Tint
```

这些字段只在 `TLayout.Write...` 或生成属性视图中写入 header/data buffer。

## 最终规则

```text
唯一概念：InstanceLayout 类型。
管线复用：where TLayout : IClusterLayout<TLayout>。
布局实现：由 UsePipe + HeaderField/DataField 生成。
热路径：静态 stride + 静态 offset + 封闭泛型特化。
shader：每个 InstanceLayout 一个静态 include。
```
