using SomeEngine.Render.Materials;

namespace SomeEngine.Tests.Materials;

/// <summary>
/// Test material for unit testing. No Diligent dependencies.
/// </summary>
public partial class TestMaterial : MaterialBase
{
    public override string SlangStructName => "TestMaterial";

    [ShaderParam("testParam")] public TextureSlot TestTexture;
    public string? TestMetadata;
}

/// <summary>
/// Test IShaderParams for composition testing.
/// </summary>
public partial class TestParams : IShaderParams
{
    [ShaderParam("paramTexture")] public TextureSlot ParamTexture;
    public bool Applied { get; private set; }

    public void ApplyToSRB(Diligent.IShaderResourceBinding srb)
    {
        Applied = true;
    }
}

/// <summary>
/// Material that composes a TestParams block.
/// </summary>
public partial class ComposedMaterial : MaterialBase
{
    public override string SlangStructName => "ComposedMaterial";
    public TestParams Extra { get; } = new();
}

[TestFixture]
public class MaterialTagSetTests
{
    [Test]
    public void Add_Marker_Has_Returns_True()
    {
        var tags = new MaterialTagSet();
        tags.Add<OpaqueTag>();
        Assert.That(tags.Has<OpaqueTag>(), Is.True);
    }

    [Test]
    public void Has_Absent_Returns_False()
    {
        var tags = new MaterialTagSet();
        Assert.That(tags.Has<MaskedTag>(), Is.False);
    }

    [Test]
    public void Set_ValueTag_Get_Returns_Value()
    {
        var tags = new MaterialTagSet();
        tags.Set(new StencilRefTag(42));
        var stencil = tags.Get<StencilRefTag>();
        Assert.That(stencil.Value, Is.EqualTo(42));
    }

    [Test]
    public void Remove_Tag_Has_Returns_False()
    {
        var tags = new MaterialTagSet();
        tags.Add<OpaqueTag>();
        tags.Remove<OpaqueTag>();
        Assert.That(tags.Has<OpaqueTag>(), Is.False);
    }

    [Test]
    public void Count_Tracks_Tags()
    {
        var tags = new MaterialTagSet();
        Assert.That(tags.Count, Is.EqualTo(0));
        tags.Add<OpaqueTag>();
        tags.Add<TwoSidedTag>();
        Assert.That(tags.Count, Is.EqualTo(2));
    }

    [Test]
    public void Has_ByType_Works()
    {
        var tags = new MaterialTagSet();
        tags.Add<MaskedTag>();
        Assert.That(tags.Has(typeof(MaskedTag)), Is.True);
        Assert.That(tags.Has(typeof(OpaqueTag)), Is.False);
    }
}

[TestFixture]
public class MaterialBaseTests
{
    [Test]
    public void SlangStructName_Returns_Correct()
    {
        var mat = new TestMaterial();
        Assert.That(mat.SlangStructName, Is.EqualTo("TestMaterial"));
    }

    [Test]
    public void CommitBindings_DoesNotThrow()
    {
        var mat = new TestMaterial();
        Assert.DoesNotThrow(() => mat.CommitBindings());
    }

    [Test]
    public void Tags_Default_Empty()
    {
        var mat = new TestMaterial();
        Assert.That(mat.Tags.Count, Is.EqualTo(0));
    }

    [Test]
    public void MaterialID_Default_Zero()
    {
        var mat = new TestMaterial();
        Assert.That(mat.MaterialID, Is.EqualTo(0u));
    }
}

[TestFixture]
public class MaterialRegistryTests
{
    [Test]
    public void CreateMaterial_WithoutRegistration_Throws()
    {
        var registry = new MaterialRegistry();
        Assert.Throws<InvalidOperationException>(() =>
            registry.CreateMaterial<TestMaterial>());
        registry.Dispose();
    }

    [Test]
    public void MaterialCount_InitiallyZero()
    {
        var registry = new MaterialRegistry();
        Assert.That(registry.MaterialCount, Is.EqualTo(0u));
        registry.Dispose();
    }

    [Test]
    public void ShaderTypes_InitiallyEmpty()
    {
        var registry = new MaterialRegistry();
        Assert.That(registry.ShaderTypes, Is.Empty);
        registry.Dispose();
    }

    [Test]
    public void GetMaterial_OutOfRange_ReturnsNull()
    {
        var registry = new MaterialRegistry();
        Assert.That(registry.GetMaterial(0), Is.Null);
        Assert.That(registry.GetMaterial(99), Is.Null);
        registry.Dispose();
    }
}

[TestFixture]
public class IShaderParamsTests
{
    [Test]
    public void TestParams_ApplyToSRB_Tracks_Call()
    {
        var p = new TestParams();
        Assert.That(p.Applied, Is.False);
        p.ApplyToSRB(null!);
        Assert.That(p.Applied, Is.True);
    }
}
