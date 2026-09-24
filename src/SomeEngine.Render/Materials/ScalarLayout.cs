using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace SomeEngine.Render.Materials;

public sealed class ScalarLayout
{
    public const int HeaderByteSize = 16;
    public const int PayloadAlignment = 16;

    public static readonly ScalarLayout Empty = new([], 0, 0);

    private readonly ScalarFieldLayout[] _fields;

    private ScalarLayout(ScalarFieldLayout[] fields, uint payloadByteSize, uint layoutHash)
    {
        Array.Sort(fields, static (left, right) => left.Offset.CompareTo(right.Offset));
        _fields = fields;

        PayloadByteSize = payloadByteSize;
        LayoutHash = layoutHash;
    }

    public IReadOnlyList<ScalarFieldLayout> Fields => _fields;

    public uint PayloadByteSize { get; }

    public uint LayoutHash { get; }

    public int ByteSize => HeaderByteSize + AlignUp((int)PayloadByteSize, PayloadAlignment);

    public static ScalarLayout FromFields(
        IEnumerable<ScalarFieldLayout> fields,
        uint payloadByteSize)
    {
        var materialFields = new List<ScalarFieldLayout>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScalarFieldLayout field in fields)
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
            : new ScalarLayout([.. materialFields], payloadByteSize, ComputeHash(materialFields, payloadByteSize));
    }

    internal void WriteHeader(Span<byte> destination)
    {
        if (destination.Length < ByteSize)
        {
            throw new ArgumentException("Destination span is smaller than the material scalar region.", nameof(destination));
        }

        destination[..ByteSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, PayloadByteSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(sizeof(uint)), LayoutHash);
    }

    internal void Write(string name, float value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteFloatField(fieldBytes, field.ComponentCount, new Vector4(value, 0, 0, 0));
    }

    internal void Write(string name, int value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteIntegerField(fieldBytes, field, unchecked((uint)value));
    }

    internal void Write(string name, Vector3 value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteFloatField(fieldBytes, field.ComponentCount, new Vector4(value, 0));
    }

    internal void Write(string name, Vector4 value, Span<byte> payload)
    {
        if (TryField(name, payload, out ScalarFieldLayout field, out Span<byte> fieldBytes))
            WriteFloatField(fieldBytes, field.ComponentCount, value);
    }

    private bool TryField(
        string name,
        Span<byte> payload,
        out ScalarFieldLayout field,
        out Span<byte> fieldBytes)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            field = _fields[i];
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                fieldBytes = payload.Slice((int)field.Offset, (int)field.Size);
                return true;
            }
        }

        field = default;
        fieldBytes = default;
        return false;
    }

    private static void WriteIntegerField(Span<byte> fieldBytes, ScalarFieldLayout field, uint raw)
    {
        switch (field.ScalarType)
        {
            case ScalarBool:
            case ScalarInt32:
            case ScalarUInt32:
                if (fieldBytes.Length >= sizeof(uint))
                    BinaryPrimitives.WriteUInt32LittleEndian(fieldBytes, raw);
                break;
            case ScalarFloat32:
                WriteFloatField(fieldBytes, field.ComponentCount, new Vector4(raw, 0, 0, 0));
                break;
        }
    }

    private static void WriteFloatField(Span<byte> fieldBytes, uint componentCount, Vector4 vector)
    {
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

    private static uint ComputeHash(IReadOnlyList<ScalarFieldLayout> fields, uint payloadByteSize)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;

        uint hash = offsetBasis;
        HashUInt(ref hash, payloadByteSize);
        foreach (ScalarFieldLayout field in fields)
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

public readonly record struct ScalarFieldLayout(
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
