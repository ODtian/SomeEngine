using SomeEngine.Assets;
using System.Numerics;
using SomeEngine.Render.RHI;
using SomeEngine.Rhi;

namespace SomeEngine.Render.Materials;

public sealed partial class Material
{
    public string Name { get; init; } = string.Empty;
    public ScalarLayout ScalarRegionLayout { get; private set; } = ScalarLayout.Empty;
    public MaterialPass[] Passes { get; private set; } = [];
    public uint ScalarVersion { get; private set; } = 1;
    public uint PassVersion { get; private set; } = 1;
    public uint BindingVersion { get; private set; } = 1;

    [BindField("AlbedoMap")]
    public Handle<Texture> AlbedoMap { get; internal set; }

    [BindField("NormalMap")]
    public Handle<Texture> NormalMap { get; internal set; }

    [BindField("ARMMap")]
    public Handle<Texture> ArmMap { get; internal set; }

    [BindField("EmissiveMap")]
    public Handle<Texture> EmissiveMap { get; internal set; }

    [ScalarField("BaseColorTint")]
    public Vector4 BaseColorTint { get; internal set; } = Vector4.One;

    [ScalarField("MetallicFactor")]
    public float MetallicFactor { get; internal set; } = 1.0f;

    [ScalarField("Roughness")]
    public float Roughness { get; internal set; } = 1.0f;

    [ScalarField("EmissiveFactor")]
    public Vector4 EmissiveFactor { get; internal set; } = Vector4.Zero;

    public int ScalarRegionByteSize => ScalarRegionLayout.ByteSize;

    public Material Clone()
    {
        var clone = (Material)MemberwiseClone();
        clone.Passes = Copy(Passes);
        clone.TouchScalars();
        return clone;
    }

    public void SetPasses(ReadOnlySpan<MaterialPass> passes)
    {
        Passes = passes.ToArray();
        TouchPasses();
    }

    internal void SetScalarLayout(ScalarLayout layout)
    {
        ScalarRegionLayout = layout ?? ScalarLayout.Empty;
        TouchScalars();
    }

    internal void WriteScalarRegion(Span<byte> destination)
    {
        ScalarRegionLayout.WriteHeader(destination);
        WriteFields(
            ScalarRegionLayout,
            destination.Slice(ScalarLayout.HeaderByteSize, (int)ScalarRegionLayout.PayloadByteSize));
    }

    internal partial bool ToBindSet(ReflectedBinding binding, AssetStore assets, out BindingResourceDesc resource);

    internal partial void WriteFields(ScalarLayout layout, Span<byte> payload);

    public void TouchScalars()
    {
        unchecked
        {
            ScalarVersion++;
        }
    }

    internal void TouchPasses()
    {
        unchecked
        {
            PassVersion++;
            BindingVersion++;
        }

        TouchScalars();
    }

    internal void TouchBindings()
    {
        unchecked
        {
            BindingVersion++;
        }
    }

    private static MaterialPass[] Copy(MaterialPass[] source)
    {
        if (source.Length == 0)
            return [];

        var copy = new MaterialPass[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }
}
