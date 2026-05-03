using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Materials;

public sealed class MaterialScalarRegionLayout
{
    public const int HeaderByteSize = 16;
    public const int PayloadAlignment = 16;

    public static readonly MaterialScalarRegionLayout Empty = new([], 0, 0);

    private readonly MaterialScalarFieldLayout[] _fields;

    private MaterialScalarRegionLayout(MaterialScalarFieldLayout[] fields, uint payloadByteSize, uint layoutHash)
    {
        Array.Sort(fields, static (left, right) => left.Offset.CompareTo(right.Offset));
        _fields = fields;

        PayloadByteSize = payloadByteSize;
        LayoutHash = layoutHash;
    }

    public IReadOnlyList<MaterialScalarFieldLayout> Fields => _fields;

    public uint PayloadByteSize { get; }

    public uint LayoutHash { get; }

    public int ByteSize => HeaderByteSize + AlignUp((int)PayloadByteSize, PayloadAlignment);

    public static MaterialScalarRegionLayout FromShaderLayout(ShaderMaterialScalarLayout? layout)
    {
        if (layout?.Fields == null || layout.Fields.Count == 0)
        {
            return Empty;
        }

        uint maxFieldEnd = 0;
        uint minFieldOffset = uint.MaxValue;
        foreach (ShaderMaterialScalarField field in layout.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || field.Size == 0)
            {
                continue;
            }

            minFieldOffset = Math.Min(minFieldOffset, field.Offset);
            maxFieldEnd = Math.Max(maxFieldEnd, field.Offset + field.Size);
        }

        if (minFieldOffset == uint.MaxValue)
        {
            return Empty;
        }

        if (minFieldOffset != 0)
        {
            throw new InvalidOperationException(
                $"Shader material scalar layout '{layout.Name}' uses non-zero payload base offset {minFieldOffset}. Reimport the shader asset.");
        }

        if (layout.Size < maxFieldEnd)
        {
            throw new InvalidOperationException(
                $"Shader material scalar layout '{layout.Name}' size {layout.Size} is smaller than its fields end {maxFieldEnd}. Reimport the shader asset.");
        }

        var fields = new List<MaterialScalarFieldLayout>(layout.Fields.Count);
        foreach (ShaderMaterialScalarField field in layout.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                continue;
            }

            fields.Add(new MaterialScalarFieldLayout(
                field.Name,
                field.Offset,
                field.Size,
                field.RowCount,
                field.ColumnCount,
                field.ScalarType));
        }

        return FromFields(fields, layout.Size);
    }

    public static MaterialScalarRegionLayout FromFields(
        IEnumerable<MaterialScalarFieldLayout> fields,
        uint payloadByteSize)
    {
        var materialFields = new List<MaterialScalarFieldLayout>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MaterialScalarFieldLayout field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !seen.Add(field.Name))
            {
                continue;
            }

            uint fieldEnd = field.Offset + field.Size;
            if (field.Size == 0 || fieldEnd > payloadByteSize)
            {
                continue;
            }

            materialFields.Add(field);
        }

        return materialFields.Count == 0
            ? Empty
            : new MaterialScalarRegionLayout([.. materialFields], payloadByteSize, ComputeHash(materialFields, payloadByteSize));
    }

    public void Write(ShaderParamBag parameters, Span<byte> destination)
    {
        if (destination.Length < ByteSize)
        {
            throw new ArgumentException("Destination span is smaller than the material scalar region.", nameof(destination));
        }

        destination[..ByteSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, PayloadByteSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(sizeof(uint)), LayoutHash);

        Span<byte> payload = destination.Slice(HeaderByteSize, (int)PayloadByteSize);
        foreach (MaterialScalarFieldLayout field in _fields)
        {
            object? value = parameters.GetScalar(field.Name);
            if (value == null)
            {
                continue;
            }

            WriteField(payload.Slice((int)field.Offset, (int)field.Size), field, value);
        }
    }

    private static void WriteField(Span<byte> fieldBytes, MaterialScalarFieldLayout field, object value)
    {
        switch (field.ScalarType)
        {
            case ScalarBool:
            case ScalarInt32:
            case ScalarUInt32:
                WriteIntegerField(fieldBytes, value);
                break;
            case ScalarFloat32:
                WriteFloatField(fieldBytes, field.ComponentCount, value);
                break;
        }
    }

    private static void WriteIntegerField(Span<byte> fieldBytes, object value)
    {
        uint raw = value switch
        {
            int i => unchecked((uint)i),
            float f => unchecked((uint)f),
            Vector4 v => unchecked((uint)v.X),
            _ => 0,
        };

        if (fieldBytes.Length >= sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fieldBytes, raw);
        }
    }

    private static void WriteFloatField(Span<byte> fieldBytes, uint componentCount, object value)
    {
        Vector4 vector = value switch
        {
            float f => new Vector4(f, 0, 0, 0),
            int i => new Vector4(i, 0, 0, 0),
            Vector4 v => v,
            _ => default,
        };

        int writableComponents = Math.Min((int)componentCount, fieldBytes.Length / sizeof(uint));
        for (int i = 0; i < writableComponents; i++)
        {
            WriteFloat(fieldBytes, i * sizeof(uint), GetComponent(vector, i));
        }
    }

    private static float GetComponent(Vector4 value, int index)
        => index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            3 => value.W,
            _ => 0,
        };

    private static void WriteFloat(Span<byte> destination, int byteOffset, float value)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(byteOffset, sizeof(uint)),
            BitConverter.SingleToUInt32Bits(value));

    private static uint ComputeHash(IReadOnlyList<MaterialScalarFieldLayout> fields, uint payloadByteSize)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        uint hash = offsetBasis;
        HashUInt(ref hash, payloadByteSize);
        foreach (MaterialScalarFieldLayout field in fields)
        {
            foreach (char c in field.Name)
            {
                HashUInt(ref hash, c);
            }

            HashUInt(ref hash, 0);
            HashUInt(ref hash, field.Offset);
            HashUInt(ref hash, field.Size);
            HashUInt(ref hash, field.RowCount);
            HashUInt(ref hash, field.ColumnCount);
            HashUInt(ref hash, field.ScalarType);
        }

        return hash;

        static void HashUInt(ref uint hash, uint value)
        {
            hash ^= value;
            hash *= prime;
        }
    }

    private static int AlignUp(int value, int alignment)
        => ((value + alignment - 1) / alignment) * alignment;

    // Values are persisted from SlangScalarType in shader_asset.fbs.
    private const byte ScalarBool = 2;
    private const byte ScalarInt32 = 3;
    private const byte ScalarUInt32 = 4;
    private const byte ScalarFloat32 = 8;
}

public readonly record struct MaterialScalarFieldLayout(
    string Name,
    uint Offset,
    uint Size,
    uint RowCount,
    uint ColumnCount,
    byte ScalarType)
{
    public uint ComponentCount
    {
        get
        {
            uint rows = RowCount == 0 ? 1 : RowCount;
            uint columns = ColumnCount == 0 ? 1 : ColumnCount;
            return Math.Max(1u, rows * columns);
        }
    }
}
