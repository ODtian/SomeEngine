using SomeEngine.Assets.Data;

namespace SomeEngine.Assets.Importers;

public class RawAttribute
{
    public string Name;
    public SomeEngine.Assets.Data.ValueType TargetType;
    public byte NumComponents;
    public bool Normalized;
    
    public float[] Data; // Flattened data
    public int Dimension; // Number of floats per element (1, 2, 3, 4) in source

    public RawAttribute(string name, float[] data, int dimension, SomeEngine.Assets.Data.ValueType targetType, byte numComponents, bool normalized)
    {
        Name = name;
        Data = data;
        Dimension = dimension;
        TargetType = targetType;
        NumComponents = numComponents;
        Normalized = normalized;
    }
}
