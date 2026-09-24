using System;
using System.Buffers.Binary;

namespace SomeEngine.Render.Data;

public enum HeaderFieldType
{
    UInt32,
    Float32,
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class HeaderFieldAttribute : Attribute
{
    public HeaderFieldAttribute(
        string csharpName,
        HeaderFieldType type,
        int order)
    {
        CSharpName = csharpName;
        Type = type;
        Order = order;
    }

    public string CSharpName { get; }
    public HeaderFieldType Type { get; }
    public int Order { get; }
    public string? SlangName { get; set; }
    public string? LoadFunctionSuffix { get; set; }
    public string? InstanceMember { get; set; }
    public bool Source { get; set; } = true;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class InstanceFlagAttribute : Attribute
{
    public InstanceFlagAttribute(string csharpName, int bit)
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
    public static uint ReadU32(ReadOnlySpan<byte> header, int byteOffset)
        => BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(byteOffset, sizeof(uint)));

    public static float ReadFloat32(ReadOnlySpan<byte> header, int byteOffset)
        => BitConverter.UInt32BitsToSingle(ReadU32(header, byteOffset));

    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> headers, int index)
        => headers.Slice(
            checked(index * StrideBytes),
            StrideBytes);

    public static Span<byte> Slice(Span<byte> headers, int index)
        => headers.Slice(
            checked(index * StrideBytes),
            StrideBytes);

    public static void Clear(Span<byte> header)
    {
        if (header.Length < StrideBytes)
            throw new ArgumentException("Instance header span is smaller than the generated layout stride.", nameof(header));

        header[..StrideBytes].Clear();
    }

    public static void WriteU32(Span<byte> header, int byteOffset, uint value)
    {
        if (header.Length < StrideBytes)
            throw new ArgumentException("Instance header span is smaller than the generated layout stride.", nameof(header));

        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(byteOffset, sizeof(uint)), value);
    }

    public static void WriteFloat32(Span<byte> header, int byteOffset, float value)
        => WriteU32(header, byteOffset, BitConverter.SingleToUInt32Bits(value));
}
