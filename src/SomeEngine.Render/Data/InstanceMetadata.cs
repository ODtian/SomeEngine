using System;
using System.Buffers.Binary;

namespace SomeEngine.Render.Data;

public enum GpuInstanceHeaderFieldType
{
    UInt32,
    Float32,
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GpuInstanceHeaderFieldAttribute : Attribute
{
    public GpuInstanceHeaderFieldAttribute(
        string csharpName,
        GpuInstanceHeaderFieldType type,
        int order)
    {
        CSharpName = csharpName;
        Type = type;
        Order = order;
    }

    public string CSharpName { get; }
    public GpuInstanceHeaderFieldType Type { get; }
    public int Order { get; }
    public string? SlangName { get; set; }
    public string? LoadFunctionSuffix { get; set; }
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GpuInstanceDataFlagAttribute : Attribute
{
    public GpuInstanceDataFlagAttribute(string csharpName, int bit)
    {
        CSharpName = csharpName;
        Bit = bit;
    }

    public string CSharpName { get; }
    public int Bit { get; }
    public string? SlangName { get; set; }
}

public static partial class InstanceHeaderLayout
{
    public static uint ReadUInt32(ReadOnlySpan<byte> header, int byteOffset)
        => BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(byteOffset, sizeof(uint)));

    public static float ReadFloat32(ReadOnlySpan<byte> header, int byteOffset)
        => BitConverter.UInt32BitsToSingle(ReadUInt32(header, byteOffset));
}

public ref struct InstanceHeaderWriter
{
    private readonly Span<byte> _header;

    public InstanceHeaderWriter(Span<byte> header)
    {
        if (header.Length < InstanceHeaderLayout.StrideBytes)
            throw new ArgumentException("Instance header span is smaller than the generated layout stride.", nameof(header));

        _header = header[..InstanceHeaderLayout.StrideBytes];
    }

    public void Clear() => _header.Clear();

    public void SetUInt32(int byteOffset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_header.Slice(byteOffset, sizeof(uint)), value);

    public void SetFloat32(int byteOffset, float value)
        => SetUInt32(byteOffset, BitConverter.SingleToUInt32Bits(value));

    public void SetBvhRootIndex(uint value)
        => SetUInt32(InstanceHeaderLayout.BVHRootIndex, value);

    public void SetMaterialSlotOffset(uint value)
        => SetUInt32(InstanceHeaderLayout.MaterialSlotOffset, value);

    public void SetInstanceDataOffset(uint value)
        => SetUInt32(InstanceHeaderLayout.InstanceDataOffset, value);

    public void SetInstanceDataFlags(GpuInstanceDataFlags value)
        => SetUInt32(InstanceHeaderLayout.InstanceDataFlags, (uint)value);

    public void SetBoundsExpansionWorld(float value)
        => SetFloat32(InstanceHeaderLayout.BoundsExpansionWorld, value);
}
