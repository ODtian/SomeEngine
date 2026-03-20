using FlatSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;

namespace SomeEngine.Tests.Materials;

[TestFixture]
public class MaterialAssetRoundtripTests
{
    [Test]
    public void Roundtrip_BasicMaterial()
    {
        var asset = new MaterialAsset
        {
            Name = "TestMat",
            Passes = new List<PassEntry>
            {
                new() { Shader = "pbr_cluster", Tags = new List<TagEntry> { new() { Name = "opaque", Value = 0 } } }
            },
            Textures = new List<TextureBinding>
            {
                new() { Name = "AlbedoMap", Path = "textures/white.dds" }
            },
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buf = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buf, asset);
        var parsed = MaterialAsset.Serializer.Parse(buf.AsSpan(0, written).ToArray());

        Assert.That(parsed.Name, Is.EqualTo("TestMat"));
        Assert.That(parsed.Passes!.Count, Is.EqualTo(1));
        Assert.That(parsed.Passes![0].Shader, Is.EqualTo("pbr_cluster"));
        Assert.That(parsed.Passes![0].Tags!.Count, Is.EqualTo(1));
        Assert.That(parsed.Textures!.Count, Is.EqualTo(1));
        Assert.That(parsed.Textures![0].Name, Is.EqualTo("AlbedoMap"));
    }

    [Test]
    public void Roundtrip_WithScalarParams()
    {
        var asset = new MaterialAsset
        {
            Name = "ScalarMat",
            Passes = new List<PassEntry> { new() { Shader = "pbr" } },
            Scalars = new List<ScalarParam>
            {
                new() { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.5f }) },
                new() { Name = "MetallicFactor", Value = new ParamValue(new IntVal { V = 1 }) },
                new() { Name = "UseNormalMap", Value = new ParamValue(new BoolVal { V = true }) },
                new()
                {
                    Name = "TilingOffset",
                    Value = new ParamValue(new Vec4Val { X = 2.0f, Y = 2.0f, Z = 0.0f, W = 0.0f })
                }
            }
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buf = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buf, asset);
        var parsed = MaterialAsset.Serializer.Parse(buf.AsSpan(0, written).ToArray());

        Assert.That(parsed.Scalars!.Count, Is.EqualTo(4));

        // Float
        Assert.That(parsed.Scalars![0].Name, Is.EqualTo("Roughness"));
        Assert.That(parsed.Scalars![0].Value!.Value.Kind, Is.EqualTo(ParamValue.ItemKind.FloatVal));
        Assert.That(parsed.Scalars![0].Value!.Value.FloatVal.V, Is.EqualTo(0.5f));

        // Int
        Assert.That(parsed.Scalars![1].Value!.Value.Kind, Is.EqualTo(ParamValue.ItemKind.IntVal));
        Assert.That(parsed.Scalars![1].Value!.Value.IntVal.V, Is.EqualTo(1));

        // Bool
        Assert.That(parsed.Scalars![2].Value!.Value.Kind, Is.EqualTo(ParamValue.ItemKind.BoolVal));
        Assert.That(parsed.Scalars![2].Value!.Value.BoolVal.V, Is.True);

        // Vec4
        Assert.That(parsed.Scalars![3].Value!.Value.Kind, Is.EqualTo(ParamValue.ItemKind.Vec4Val));
        Assert.That(parsed.Scalars![3].Value!.Value.Vec4Val.X, Is.EqualTo(2.0f));
    }

    [Test]
    public void Roundtrip_Vec2_Vec3()
    {
        var asset = new MaterialAsset
        {
            Name = "VecMat",
            Passes = new List<PassEntry> { new() { Shader = "s" } },
            Scalars = new List<ScalarParam>
            {
                new() { Name = "Tiling", Value = new ParamValue(new Vec2Val { X = 4.0f, Y = 2.0f }) },
                new() { Name = "Color", Value = new ParamValue(new Vec3Val { X = 1.0f, Y = 0.5f, Z = 0.0f }) }
            }
        };

        int maxSize = MaterialAsset.Serializer.GetMaxSize(asset);
        byte[] buf = new byte[maxSize];
        int written = MaterialAsset.Serializer.Write(buf, asset);
        var parsed = MaterialAsset.Serializer.Parse(buf.AsSpan(0, written).ToArray());

        Assert.That(parsed.Scalars![0].Value!.Value.Kind, Is.EqualTo(ParamValue.ItemKind.Vec2Val));
        Assert.That(parsed.Scalars![0].Value!.Value.Vec2Val.X, Is.EqualTo(4.0f));
        Assert.That(parsed.Scalars![0].Value!.Value.Vec2Val.Y, Is.EqualTo(2.0f));

        Assert.That(parsed.Scalars![1].Value!.Value.Kind, Is.EqualTo(ParamValue.ItemKind.Vec3Val));
        Assert.That(parsed.Scalars![1].Value!.Value.Vec3Val.Z, Is.EqualTo(0.0f));
    }
}

[TestFixture]
public class MaterialInstanceRoundtripTests
{
    [Test]
    public void Roundtrip_WithScalarOverrides()
    {
        var asset = new MaterialInstanceAsset
        {
            Parent = "materials/base.mat",
            Overrides = new List<ParamOverride>
            {
                new() { Name = "AlbedoMap", Path = "textures/brick.dds" }
            },
            ScalarOverrides = new List<ScalarOverride>
            {
                new() { Name = "Roughness", Value = new ParamValue(new FloatVal { V = 0.8f }) }
            },
            TagOverrides = new List<TagOverride>
            {
                new() { Name = "masked", Value = 0, Remove = false },
                new() { Name = "opaque", Value = 0, Remove = true }
            }
        };

        int maxSize = MaterialInstanceAsset.Serializer.GetMaxSize(asset);
        byte[] buf = new byte[maxSize];
        int written = MaterialInstanceAsset.Serializer.Write(buf, asset);
        var parsed = MaterialInstanceAsset.Serializer.Parse(buf.AsSpan(0, written).ToArray());

        Assert.That(parsed.Parent, Is.EqualTo("materials/base.mat"));
        Assert.That(parsed.Overrides!.Count, Is.EqualTo(1));
        Assert.That(parsed.ScalarOverrides!.Count, Is.EqualTo(1));
        Assert.That(parsed.ScalarOverrides![0].Value!.Value.FloatVal.V, Is.EqualTo(0.8f));
        Assert.That(parsed.TagOverrides!.Count, Is.EqualTo(2));
        Assert.That(parsed.TagOverrides![1].Remove, Is.True);
    }
}

[TestFixture]
public class TagStoreCopyTests
{
    [Test]
    public void CopyAllTags_CopiesToTarget()
    {
        var store = new TagStore<MaterialPass>();
        var matA = new Material { Name = "A" };
        var matB = new Material { Name = "B" };
        var passA = matA.Passes[0];
        var passB = matB.Passes[0];
        store.Register(passA);
        store.Register(passB);

        store.SetTag<OpaqueTag>(passA);
        store.SetTag(passA, new StencilRefTag { Value = 42 });

        store.CopyAllTags(passA, passB);

        Assert.That(store.HasTag<OpaqueTag>(passB), Is.True);
        Assert.That(store.GetTag<StencilRefTag>(passB)!.Value.Value, Is.EqualTo(42));
    }

    [Test]
    public void RemoveAllTags_ClearsAll()
    {
        var store = new TagStore<MaterialPass>();
        var mat = new Material { Name = "X" };
        var pass = mat.Passes[0];
        store.Register(pass);

        store.SetTag<OpaqueTag>(pass);
        store.SetTag<MaskedTag>(pass);

        store.RemoveAllTags(pass);

        Assert.That(store.HasTag<OpaqueTag>(pass), Is.False);
        Assert.That(store.HasTag<MaskedTag>(pass), Is.False);
    }

    [Test]
    public void CopyAllTags_TargetNotRegistered_Throws()
    {
        var store = new TagStore<MaterialPass>();
        var matA = new Material { Name = "A" };
        var matB = new Material { Name = "B" };
        store.Register(matA.Passes[0]);
        // passB not registered

        Assert.Throws<InvalidOperationException>(() =>
            store.CopyAllTags(matA.Passes[0], matB.Passes[0]));
    }
}

[TestFixture]
public class ShaderParamBagScalarTests
{
    [Test]
    public void SetScalar_Float_GetScalar_Roundtrip()
    {
        var bag = new ShaderParamBag();
        bag.SetScalar("roughness", 0.7f);
        Assert.That(bag.GetScalar("roughness"), Is.EqualTo(0.7f));
    }

    [Test]
    public void SetScalar_Int_GetScalar_Roundtrip()
    {
        var bag = new ShaderParamBag();
        bag.SetScalar("mode", 3);
        Assert.That(bag.GetScalar("mode"), Is.EqualTo(3));
    }

    [Test]
    public void SetScalar_Vector4_GetScalar_Roundtrip()
    {
        var bag = new ShaderParamBag();
        var v = new System.Numerics.Vector4(1, 2, 3, 4);
        bag.SetScalar("tiling", v);
        Assert.That(bag.GetScalar("tiling"), Is.EqualTo(v));
    }

    [Test]
    public void GetScalar_NonExistent_ReturnsNull()
    {
        var bag = new ShaderParamBag();
        Assert.That(bag.GetScalar("missing"), Is.Null);
    }

    [Test]
    public void SetScalar_IncludesInSignature()
    {
        var bagA = new ShaderParamBag();
        var bagB = new ShaderParamBag();
        bagA.SetScalar("r", 0.5f);
        bagB.SetScalar("r", 0.8f);
        Assert.That(bagA.GetSignatureHash(), Is.Not.EqualTo(bagB.GetSignatureHash()));
    }
}
