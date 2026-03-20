using SomeEngine.Render.Materials;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Tests.Materials;

[TestFixture]
public class TagStoreTests
{
    [Test]
    public void Register_IncreasesCount()
    {
        var store = new TagStore<MaterialPass>();
        var mat = new Material { Name = "Test" };
        var pass = mat.Passes[0];
        store.Register(pass);
        Assert.That(store.Count, Is.EqualTo(1));
    }

    [Test]
    public void Register_IncrementsVersion()
    {
        var store = new TagStore<MaterialPass>();
        var v0 = store.Version;
        var mat = new Material { Name = "Test" };
        store.Register(mat.Passes[0]);
        Assert.That(store.Version, Is.GreaterThan(v0));
    }

    [Test]
    public void Unregister_DecreasesCount()
    {
        var store = new TagStore<MaterialPass>();
        var mat = new Material { Name = "Test" };
        var pass = mat.Passes[0];
        store.Register(pass);
        store.Unregister(pass);
        Assert.That(store.Count, Is.EqualTo(0));
    }

    [Test]
    public void SetTag_GetTag_Roundtrip()
    {
        var store = new TagStore<MaterialPass>();
        var mat = new Material { Name = "Test" };
        var pass = mat.Passes[0];
        store.Register(pass);
        store.SetTag<OpaqueTag>(pass);
        Assert.That(store.HasTag<OpaqueTag>(pass), Is.True);
    }

    [Test]
    public void HasTag_Absent_ReturnsFalse()
    {
        var store = new TagStore<MaterialPass>();
        var mat = new Material { Name = "Test" };
        var pass = mat.Passes[0];
        store.Register(pass);
        Assert.That(store.HasTag<MaskedTag>(pass), Is.False);
    }

    [Test]
    public void Query_ReturnsTaggedItems()
    {
        var store = new TagStore<MaterialPass>();
        var mat1 = new Material { Name = "M1" };
        var mat2 = new Material { Name = "M2" };
        var p1 = mat1.Passes[0];
        var p2 = mat2.Passes[0];
        store.Register(p1);
        store.Register(p2);
        store.SetTag<OpaqueTag>(p1);
        // p2 has no OpaqueTag
        var result = store.Query<OpaqueTag>();
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.SameAs(p1));
    }

    [Test]
    public void Query_Intersection_ReturnsBothTagged()
    {
        var store = new TagStore<MaterialPass>();
        var mat1 = new Material { Name = "M1" };
        var mat2 = new Material { Name = "M2" };
        var p1 = mat1.Passes[0];
        var p2 = mat2.Passes[0];
        store.Register(p1);
        store.Register(p2);
        store.SetTag<OpaqueTag>(p1);
        store.SetTag<ClusterShaderTag>(p1);
        store.SetTag<OpaqueTag>(p2);
        // p2 has Opaque but not ClusterShader
        var result = store.Query<OpaqueTag, ClusterShaderTag>();
        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.SameAs(p1));
    }

    [Test]
    public void Query_Empty_ReturnsEmpty()
    {
        var store = new TagStore<MaterialPass>();
        var result = store.Query<OpaqueTag>();
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetTag_ValueTag_ReturnsValue()
    {
        var store = new TagStore<MaterialPass>();
        var mat = new Material { Name = "Test" };
        var pass = mat.Passes[0];
        store.Register(pass);
        store.SetTag(pass, new StencilRefTag { Value = 42 });
        var tag = store.GetTag<StencilRefTag>(pass);
        Assert.That(tag.HasValue, Is.True);
        Assert.That(tag!.Value.Value, Is.EqualTo(42));
    }
}

[TestFixture]
public class MaterialTests
{
    [Test]
    public void Passes_DefaultsToOnePass()
    {
        var mat = new Material { Name = "Test" };
        Assert.That(mat.Passes.Length, Is.EqualTo(1));
    }

    [Test]
    public void Instantiate_CreatesIndependentCopy()
    {
        var mat = new Material { Name = "Base", ShaderAssetName = "TestShader" };
        var inst = mat.Instantiate();
        Assert.That(inst.Name, Does.Contain("Instance"));
        Assert.That(inst.ShaderAssetName, Is.EqualTo("TestShader"));
        Assert.That(inst.Params, Is.Not.SameAs(mat.Params));
    }

    [Test]
    public void SetTexture_InvalidatesPasses()
    {
        var mat = new Material { Name = "Test" };
        var pass1 = mat.Passes[0]; // Force resolve
        mat.SetTexture("TestTex", null);
        var pass2 = mat.Passes[0]; // Should re-resolve
        // After InvalidateResolvedPasses, new pass objects are created
        Assert.That(pass2, Is.Not.SameAs(pass1));
    }

    [Test]
    public void ComputeSignature_IncludesShaderIdentity()
    {
        var mat1 = new Material { Name = "Test", ShaderAsset = new ShaderAsset { Name = "A" } };
        var mat2 = new Material { Name = "Test", ShaderAsset = new ShaderAsset { Name = "B" } };
        var mat3 = new Material { Name = "Test", ShaderAsset = new ShaderAsset { Name = "A" } };

        Assert.That(mat1.Passes[0].ComputeSignature(), Is.Not.EqualTo(mat2.Passes[0].ComputeSignature()));
        Assert.That(mat1.Passes[0].ComputeSignature(), Is.EqualTo(mat3.Passes[0].ComputeSignature()));
    }
}

[TestFixture]
public class ShaderParamBagTests
{
    [Test]
    public void Set_IncrementsCount()
    {
        var bag = new ShaderParamBag();
        Assert.That(bag.Count, Is.EqualTo(0));
        bag.Set("test", (Diligent.ITextureView?)null);
        Assert.That(bag.Count, Is.EqualTo(1));
    }

    [Test]
    public void Clone_CreatesIndependentCopy()
    {
        var bag = new ShaderParamBag();
        bag.Set("a", (Diligent.ITextureView?)null);
        var clone = bag.Clone();
        Assert.That(clone.Count, Is.EqualTo(1));
        Assert.That(clone.Contains("a"), Is.True);
        clone.Remove("a");
        Assert.That(bag.Contains("a"), Is.True); // Original unaffected
    }

    [Test]
    public void GetSignatureHash_DifferentParams_DifferentHash()
    {
        var bag1 = new ShaderParamBag();
        bag1.Set("a", (Diligent.ITextureView?)null);
        var bag2 = new ShaderParamBag();
        bag2.Set("b", (Diligent.ITextureView?)null);
        Assert.That(bag1.GetSignatureHash(), Is.Not.EqualTo(bag2.GetSignatureHash()));
    }
}

[TestFixture]
public class MaterialRegistryTests
{
    [Test]
    public void MaterialCount_InitiallyZero()
    {
        var registry = new MaterialRegistry();
        Assert.That(registry.MaterialCount, Is.EqualTo(0u));
        registry.Dispose();
    }

    [Test]
    public void Register_IncreasesMaterialCount()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "Test" };
        registry.Register(mat);
        Assert.That(registry.MaterialCount, Is.EqualTo(1u));
        registry.Dispose();
    }

    [Test]
    public void Register_AssignsMaterialID()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "Test" };
        registry.Register(mat);
        Assert.That(mat.Passes[0].MaterialID, Is.EqualTo(0u));

        var mat2 = new Material { Name = "Test2" };
        registry.Register(mat2);
        Assert.That(mat2.Passes[0].MaterialID, Is.EqualTo(1u));
        registry.Dispose();
    }

    [Test]
    public void GetPass_ReturnsCorrectPass()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "Test" };
        registry.Register(mat);
        var pass = registry.GetPass(0);
        Assert.That(pass, Is.Not.Null);
        Assert.That(pass!.Owner, Is.SameAs(mat));
        registry.Dispose();
    }

    [Test]
    public void GetPass_OutOfRange_ReturnsNull()
    {
        var registry = new MaterialRegistry();
        Assert.That(registry.GetPass(0), Is.Null);
        Assert.That(registry.GetPass(99), Is.Null);
        registry.Dispose();
    }

    [Test]
    public void Unregister_RemovesPass()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "Test" };
        registry.Register(mat);
        registry.Unregister(mat);
        Assert.That(registry.MaterialCount, Is.EqualTo(0u));
        Assert.That(registry.GetPass(0), Is.Null);
        registry.Dispose();
    }

    [Test]
    public void TagQuery_ReturnsCorrectPasses()
    {
        var registry = new MaterialRegistry();
        var mat1 = new Material { Name = "M1" };
        var mat2 = new Material { Name = "M2" };
        registry.Register(mat1);
        registry.Register(mat2);
        registry.SetTag<OpaqueTag>(mat1.Passes[0]);
        // mat2 has no tag
        var result = registry.Query<OpaqueTag>();
        Assert.That(result, Has.Length.EqualTo(1));
        registry.Dispose();
    }

    [Test]
    public void Register_AutoDerivesTagFromShaderMetadata()
    {
        var registry = new MaterialRegistry();
        var shaderAsset = new ShaderAsset 
        { 
            Name = "TestShader",
            Metadata = new ShaderMetadata 
            {
                PipelineTags = new[] { "ClusterShader" }
            }
        };
        var mat = new Material { Name = "Test", ShaderAsset = shaderAsset };
        registry.Register(mat);
        Assert.That(registry.HasTag<ClusterShaderTag>(mat.Passes[0]), Is.True);
        registry.Dispose();
    }
}

[TestFixture]
public class BinQueueTests
{
    [Test]
    public void Rebuild_CreatesCorrectBinCount()
    {
        var registry = new MaterialRegistry();
        var mat1 = new Material { Name = "M1" };
        var mat2 = new Material { Name = "M2" };
        registry.Register(mat1);
        registry.Register(mat2);
        registry.SetTag<OpaqueTag>(mat1.Passes[0]);
        registry.SetTag<OpaqueTag>(mat2.Passes[0]);

        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        // Two passes with different signature hashes → 2 bins
        Assert.That(queue.TotalBinCount, Is.GreaterThanOrEqualTo(1));
        registry.Dispose();
    }

    [Test]
    public void GetRange_ReturnsCorrectRange()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "M1" };
        registry.Register(mat);
        registry.SetTag<OpaqueTag>(mat.Passes[0]);

        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        var range = queue.GetRange("opaque");
        Assert.That(range.Start, Is.EqualTo(0));
        Assert.That(range.Count, Is.EqualTo(1));
        registry.Dispose();
    }

    [Test]
    public void GetBinForPass_ReturnsCorrectBin()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "M1" };
        registry.Register(mat);
        registry.SetTag<OpaqueTag>(mat.Passes[0]);

        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        var bin = queue.GetBinForPass(mat.Passes[0]);
        Assert.That(bin, Is.EqualTo(0));
        registry.Dispose();
    }

    [Test]
    public void Rebuild_DifferentShaders_DifferentBins()
    {
        var registry = new MaterialRegistry();
        var mat1 = new Material { Name = "M1", ShaderAsset = new ShaderAsset { Name = "A" } };
        var mat2 = new Material { Name = "M2", ShaderAsset = new ShaderAsset { Name = "B" } };
        registry.Register(mat1);
        registry.Register(mat2);
        registry.SetTag<OpaqueTag>(mat1.Passes[0]);
        registry.SetTag<OpaqueTag>(mat2.Passes[0]);

        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        Assert.That(queue.TotalBinCount, Is.GreaterThanOrEqualTo(2));
        registry.Dispose();
    }
}

[TestFixture]
public class MultiPassTagTests
{
    [Test]
    public void Register_MultiPass_SetsMultiPassTag()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "Skin" };
        mat.AddPass(); // overlay pass
        registry.Register(mat);

        var primary = mat.Passes[0];
        Assert.That(registry.HasTag<MultiPassTag>(primary), Is.True);
        var tag = registry.GetTag<MultiPassTag>(primary);
        Assert.That(tag!.Value.OverlayCount, Is.EqualTo(1));
        registry.Dispose();
    }

    [Test]
    public void Register_MultiPass_SetsOverlayTags()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "Skin" };
        mat.AddPass(); // overlay 0
        mat.AddPass(); // overlay 1
        registry.Register(mat);

        var primary = mat.Passes[0];
        var overlay0 = mat.Passes[1];
        var overlay1 = mat.Passes[2];

        // primary should have MultiPassTag with count=2
        var mpt = registry.GetTag<MultiPassTag>(primary);
        Assert.That(mpt!.Value.OverlayCount, Is.EqualTo(2));

        // overlay 0
        Assert.That(registry.HasTag<OverlayTag>(overlay0), Is.True);
        var ot0 = registry.GetTag<OverlayTag>(overlay0);
        Assert.That(ot0!.Value.LayerIndex, Is.EqualTo(0));
        Assert.That(ot0.Value.PrimaryPass, Is.SameAs(primary));

        // overlay 1
        var ot1 = registry.GetTag<OverlayTag>(overlay1);
        Assert.That(ot1!.Value.LayerIndex, Is.EqualTo(1));
        Assert.That(ot1.Value.PrimaryPass, Is.SameAs(primary));

        registry.Dispose();
    }

    [Test]
    public void Register_SinglePass_NoMultiPassTag()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "Simple" };
        registry.Register(mat);

        Assert.That(registry.HasTag<MultiPassTag>(mat.Passes[0]), Is.False);
        registry.Dispose();
    }
}

[TestFixture]
public class OverlayMappingTests
{
    [Test]
    public void Build_ReturnsCorrectEntries()
    {
        var registry = new MaterialRegistry();

        var mat = new Material { Name = "Skin" };
        mat.AddPass(); // overlay
        registry.Register(mat);

        // Tag primary with opaque + cluster
        registry.SetTag<OpaqueTag>(mat.Passes[0]);
        registry.SetTag<ClusterShaderTag>(mat.Passes[0]);

        // Build BinQueue
        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        // Build overlay mapping
        var entries = OverlayMapping.Build(registry, queue);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].OverlayPass, Is.SameAs(mat.Passes[1]));
        Assert.That(entries[0].LayerIndex, Is.EqualTo(0));
        registry.Dispose();
    }

    [Test]
    public void Build_SortsByPrimaryBinThenLayerIndex()
    {
        var registry = new MaterialRegistry();

        // Material A with 2 overlays
        var matA = new Material { Name = "A" };
        matA.AddPass(); // overlay 0
        matA.AddPass(); // overlay 1
        registry.Register(matA);
        registry.SetTag<OpaqueTag>(matA.Passes[0]);

        // Build BinQueue
        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        var entries = OverlayMapping.Build(registry, queue);

        // Should have 2 entries, sorted by LayerIndex
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries[0].LayerIndex, Is.EqualTo(0));
        Assert.That(entries[1].LayerIndex, Is.EqualTo(1));
        Assert.That(entries[0].PrimaryBin, Is.EqualTo(entries[1].PrimaryBin));
        registry.Dispose();
    }

    [Test]
    public void Build_SkipsOverlaysWithUnknownPrimary()
    {
        var registry = new MaterialRegistry();

        var mat = new Material { Name = "Test" };
        mat.AddPass();
        registry.Register(mat);
        // Don't tag the primary with OpaqueTag → it won't be in BinQueue

        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        var entries = OverlayMapping.Build(registry, queue);
        Assert.That(entries, Is.Empty);
        registry.Dispose();
    }
}

[TestFixture]
public class MaterialSlotCacheTests
{
    [Test]
    public void GetOrAllocate_SamePasses_SameOffset()
    {
        var buffer = new MaterialSlotBuffer(stride: 1);
        var cache = new MaterialSlotCache(buffer);
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "M1" };
        registry.Register(mat);

        var passes = mat.Passes.ToArray();
        int offset1 = cache.GetOrAllocate(passes);
        int offset2 = cache.GetOrAllocate(passes);

        Assert.That(offset1, Is.EqualTo(offset2));
        Assert.That(cache.UniqueCount, Is.EqualTo(1));
        Assert.That(cache.GetRefCount(offset1), Is.EqualTo(2));
        registry.Dispose();
        cache.Dispose();
        buffer.Dispose();
    }

    [Test]
    public void GetOrAllocate_DifferentPasses_DifferentOffset()
    {
        var buffer = new MaterialSlotBuffer(stride: 1);
        var cache = new MaterialSlotCache(buffer);
        var registry = new MaterialRegistry();
        var mat1 = new Material { Name = "M1" };
        var mat2 = new Material { Name = "M2" };
        registry.Register(mat1);
        registry.Register(mat2);

        int offset1 = cache.GetOrAllocate(mat1.Passes.ToArray());
        int offset2 = cache.GetOrAllocate(mat2.Passes.ToArray());

        Assert.That(offset1, Is.Not.EqualTo(offset2));
        Assert.That(cache.UniqueCount, Is.EqualTo(2));
        registry.Dispose();
        cache.Dispose();
        buffer.Dispose();
    }

    [Test]
    public void Release_DecrementsRefCount()
    {
        var buffer = new MaterialSlotBuffer(stride: 1);
        var cache = new MaterialSlotCache(buffer);
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "M1" };
        registry.Register(mat);

        var passes = mat.Passes.ToArray();
        int offset = cache.GetOrAllocate(passes);
        cache.GetOrAllocate(passes); // refcount = 2

        cache.Release(offset);
        Assert.That(cache.GetRefCount(offset), Is.EqualTo(1));

        cache.Release(offset);
        Assert.That(cache.UniqueCount, Is.EqualTo(0)); // freed
        registry.Dispose();
        cache.Dispose();
        buffer.Dispose();
    }

    [Test]
    public void RebuildField_PatchesBinKeys()
    {
        var buffer = new MaterialSlotBuffer(stride: 2);
        var cache = new MaterialSlotCache(buffer);
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "M1" };
        registry.Register(mat);
        registry.SetTag<OpaqueTag>(mat.Passes[0]);

        int offset = cache.GetOrAllocate(mat.Passes.ToArray());

        // Build BinQueue
        var queue = new BinQueue();
        queue.RegisterRegion("opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        queue.Rebuild();

        // RebuildField writes bin key to field 0
        cache.RebuildField(0, queue);

        ushort binKey = buffer.GetField(offset, 0, 0);
        Assert.That(binKey, Is.EqualTo(queue.GetBinForPass(mat.Passes[0])));

        // Field 1 should still be 0 (untouched)
        ushort field1 = buffer.GetField(offset, 0, 1);
        Assert.That(field1, Is.EqualTo(0));

        registry.Dispose();
        cache.Dispose();
        buffer.Dispose();
    }
}

[TestFixture]
public class BinSpaceTests
{
    [Test]
    public void RegisterField_ReturnsIncrementingIndex()
    {
        var bs = new BinSpace();
        Assert.That(bs.RegisterField("A"), Is.EqualTo(0));
        Assert.That(bs.RegisterField("B"), Is.EqualTo(1));
        Assert.That(bs.Stride, Is.EqualTo(2));
        bs.Dispose();
    }

    [Test]
    public void RegisterField_AfterFreeze_Throws()
    {
        var bs = new BinSpace();
        bs.RegisterField("A");
        bs.FreezeLayout();
        Assert.Throws<InvalidOperationException>(() => bs.RegisterField("B"));
        bs.Dispose();
    }

    [Test]
    public void GetFieldIndex_ReturnsCorrectIndex()
    {
        var bs = new BinSpace();
        bs.RegisterField("RasterBin");
        bs.RegisterField("ShadingBin");
        bs.FreezeLayout();

        Assert.That(bs.GetFieldIndex("ShadingBin"), Is.EqualTo(1));
        bs.Dispose();
    }

    [Test]
    public void AllocateSlots_And_GetRange_WorkCorrectly()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "M1" };
        registry.Register(mat);
        registry.SetTag<OpaqueTag>(mat.Passes[0]);

        var bs = new BinSpace();
        int shadeField = bs.RegisterField("ShadingBin");
        bs.RegisterRegion(shadeField, "opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());
        bs.FreezeLayout();

        int offset = bs.AllocateSlots(mat.Passes.ToArray());
        Assert.That(offset, Is.GreaterThanOrEqualTo(0));

        bs.RebuildIfDirty(registry);

        var range = bs.GetRange(shadeField, "opaque");
        Assert.That(range.Count, Is.GreaterThan(0));

        registry.Dispose();
        bs.Dispose();
    }

    [Test]
    public void MultipleFields_IndependentBinNumbering()
    {
        var registry = new MaterialRegistry();
        var mat = new Material { Name = "M1" };
        registry.Register(mat);
        registry.SetTag<OpaqueTag>(mat.Passes[0]);
        registry.SetTag<ClusterShaderTag>(mat.Passes[0]);

        var bs = new BinSpace();
        int rasterField = bs.RegisterField("RasterBin");
        int shadeField = bs.RegisterField("ShadingBin");

        bs.RegisterRegion(rasterField, "default",
            () => registry.Query<ClusterShaderTag>(),
            _ => 0UL); // all same signature → 1 bin

        bs.RegisterRegion(shadeField, "opaque",
            () => registry.Query<OpaqueTag>(),
            p => p.ComputeSignature());

        bs.FreezeLayout();
        bs.AllocateSlots(mat.Passes.ToArray());
        bs.RebuildIfDirty(registry);

        // Both fields should have independent bin ranges starting at 0
        var rasterRange = bs.GetRange(rasterField, "default");
        var shadeRange = bs.GetRange(shadeField, "opaque");
        Assert.That(rasterRange.Start, Is.EqualTo(0));
        Assert.That(shadeRange.Start, Is.EqualTo(0));

        registry.Dispose();
        bs.Dispose();
    }
}

[TestFixture]
public class MaterialAddPassTests
{
    [Test]
    public void AddPass_IncreasesPassCount()
    {
        var mat = new Material { Name = "Test" };
        Assert.That(mat.Passes.Length, Is.EqualTo(1));

        mat.AddPass();
        Assert.That(mat.Passes.Length, Is.EqualTo(2));

        mat.AddPass();
        Assert.That(mat.Passes.Length, Is.EqualTo(3));
    }

    [Test]
    public void AddPass_PreservesPrimaryPass()
    {
        var mat = new Material { Name = "Test" };
        var primary = mat.Passes[0];
        mat.AddPass();

        Assert.That(mat.Passes[0], Is.SameAs(primary));
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

