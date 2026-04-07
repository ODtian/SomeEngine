using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Pipelines;

public class ShaderGroupTests
{
    [Fact]
    public void EmptyBinSpace_ReturnsEmptyList()
    {
        using var binSpace = SetupBinSpace([]);

        var groups = ShadePSOGroup.ComputeShaderGroups(binSpace, 0, static entity => entity.GetComponent<ClusterShadeComponent>().Default);

        Assert.Empty(groups);
    }

    [Fact]
    public void SingleBin_ReturnsSingleGroup()
    {
        var materialSystem = new MaterialSystem();
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "A", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        var entity = CreateShadeEntity(materialSystem, shader, "M1");
        using var binSpace = SetupBinSpace([entity]);

        var groups = ShadePSOGroup.ComputeShaderGroups(binSpace, 0, static e => e.GetComponent<ClusterShadeComponent>().Default);

        Assert.Single(groups);
        Assert.Equal(0, groups[0].BinStart);
        Assert.Equal(1, groups[0].BinCount);
        Assert.Same(shader, groups[0].VariantRef.Shader);
        Assert.Single(groups[0].Entities);
        Assert.Equal(entity.Id, groups[0].Entities[0].Id);
    }

    [Fact]
    public void SameShader_ConsecutiveBins_MergedIntoOneGroup()
    {
        var materialSystem = new MaterialSystem();
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "A", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        using var binSpace = SetupBinSpace(
        [
            CreateShadeEntity(materialSystem, shader, "M1"),
            CreateShadeEntity(materialSystem, shader, "M2"),
            CreateShadeEntity(materialSystem, shader, "M3"),
        ]);

        var groups = ShadePSOGroup.ComputeShaderGroups(binSpace, 0, static e => e.GetComponent<ClusterShadeComponent>().Default);

        Assert.Single(groups);
        Assert.Same(shader, groups[0].VariantRef.Shader);
    }

    [Fact]
    public void DifferentEntryPoints_BreakIntoSeparateGroups()
    {
        var materialSystem = new MaterialSystem();
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(
            AssetGuid.New(),
            "A",
            ("ClusterShade", Array.Empty<string>(), "CSMain"),
            ("ClusterShade", Array.Empty<string>(), "CSOverlay"));

        using var binSpace = SetupBinSpace(
        [
            CreateShadeEntity(materialSystem, shader, "Primary", "CSMain"),
            CreateShadeEntity(materialSystem, shader, "Overlay", "CSOverlay"),
        ]);

        var groups = ShadePSOGroup.ComputeShaderGroups(binSpace, 0, static e => e.GetComponent<ClusterShadeComponent>().Default);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, group => group.VariantRef.EntryPoint == "CSMain");
        Assert.Contains(groups, group => group.VariantRef.EntryPoint == "CSOverlay");
    }

    [Fact]
    public void GroupEntities_MatchBinSpaceOrdering()
    {
        var materialSystem = new MaterialSystem();
        var shader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(AssetGuid.New(), "A", ("ClusterShade", Array.Empty<string>(), "CSMain"));
        using var binSpace = SetupBinSpace(
        [
            CreateShadeEntity(materialSystem, shader, "M1"),
            CreateShadeEntity(materialSystem, shader, "M2"),
        ]);

        var groups = ShadePSOGroup.ComputeShaderGroups(binSpace, 0, static e => e.GetComponent<ClusterShadeComponent>().Default);

        Assert.Single(groups);
        for (int i = 0; i < groups[0].BinCount; i++)
        {
            var expected = binSpace.GetEntity(0, groups[0].BinStart + i);
            Assert.Equal(expected.Id, groups[0].Entities[i].Id);
        }
    }

    private static Entity CreateShadeEntity(MaterialSystem materialSystem, ShaderAsset shader, string name, string entryPoint = "CSMain")
    {
        var material = MaterialAssetPipelineTestHelpers.CreateMaterial(materialSystem, name, AssetGuid.New());
        material.Entity.AddComponent(new ClusterShadeComponent { Default = new ShaderVariantRef(shader, entryPoint) });
        material.Entity.AddTag<Opaque>();
        return material.Entity;
    }

    private static BinSpace SetupBinSpace(Entity[] entities)
    {
        var binSpace = new BinSpace();
        int field = binSpace.RegisterField("ShadingBin");
        binSpace.RegisterGroup(
            field,
            new BinQueue.BinGroup
            {
                Query = () => entities,
                OrderKey = static _ => 0,
                SignatureFunc = entity => MaterialEntityUtility.ComputeMaterialSignature(entity, entity.GetComponent<ClusterShadeComponent>().Default),
            });
        binSpace.FreezeLayout();

        for (int i = 0; i < entities.Length; i++)
        {
            binSpace.AllocateSlots([entities[i]]);
        }

        binSpace.RebuildIfDirty();
        return binSpace;
    }
}
