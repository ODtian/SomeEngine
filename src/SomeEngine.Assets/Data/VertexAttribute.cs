using System;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

// Matching Diligent::VALUE_TYPE
public enum ValueType : byte
{
    Undefined = 0,
    Int8,
    Int16,
    Int32,
    UInt8,
    UInt16,
    UInt32,
    Float16,
    Float32,
    Float64
}

public class VertexAttributeDescriptor
{
    public string Name = "ATTRIB";
    public ValueType Type = ValueType.Float32;
    public byte NumComponents = 3;
    public bool IsNormalized = false;
    public ushort Offset;

    public int GetSize()
    {
        int componentSize = Type switch
        {
            ValueType.Int8 or ValueType.UInt8 => 1,
            ValueType.Int16 or ValueType.UInt16 or ValueType.Float16 => 2,
            ValueType.Int32 or ValueType.UInt32 or ValueType.Float32 => 4,
            ValueType.Float64 => 8,
            _ => 0
        };
        return componentSize * NumComponents;
    }
}
