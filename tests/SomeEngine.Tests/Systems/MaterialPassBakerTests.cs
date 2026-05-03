using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.Systems;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Systems;

public class MaterialPassBakerTests
{
    [Fact]
    public void Rebuild_ExpandsOneRuntimeEntityPerMaterialPass()
    {
        EntityStore authoringStore = new();
        EntityStore runtimeStore = new();
        EntityStore materialStore = new();
        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterial(materialStore, materialGuid);
        WorldBaker baker = new(new MaterialPassBaker(guid => guid == materialGuid ? material : null));

        Entity source = authoringStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 17 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        baker.Rebuild(authoringStore, runtimeStore);

        List<Entity> runtimeEntities = CollectEntities(runtimeStore);
        Assert.Equal(2, runtimeEntities.Count);
        Assert.All(runtimeEntities, entity => Assert.Equal(source.Id, entity.GetComponent<RenderSourceEntity>().SourceEntityId));
        Assert.All(runtimeEntities, entity => Assert.Equal((uint)17, entity.GetComponent<RenderMaterialSlotBinding>().BVHRootIndex));
        Assert.All(runtimeEntities, entity => Assert.Equal(0, entity.GetComponent<RenderMaterialSlotBinding>().LocalMaterialSlot));
        Assert.All(runtimeEntities, entity => Assert.Equal(materialGuid, entity.GetComponent<RenderMaterialSlotBinding>().MaterialAssetGuid));
        Assert.All(runtimeEntities, entity => Assert.True(entity.TryGetComponent<MaterialRef>(out var materialRef) && ReferenceEquals(materialRef.Owner, material)));
        Assert.Contains(runtimeEntities, entity => entity.Tags.Has<Opaque>());
        Assert.Contains(runtimeEntities, entity => entity.TryGetComponent<OverlayShade>(out OverlayShade overlay) && overlay.Layer == 3);
    }

    [Fact]
    public void Rebuild_ClearsRuntimeStoreBeforeBakingAgain()
    {
        EntityStore authoringStore = new();
        EntityStore runtimeStore = new();
        EntityStore materialStore = new();
        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterial(materialStore, materialGuid);
        WorldBaker baker = new(new MaterialPassBaker(guid => guid == materialGuid ? material : null));

        Entity source = authoringStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 7 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        baker.Rebuild(authoringStore, runtimeStore);
        Assert.Equal(2, CountEntities(runtimeStore));

        source.DeleteEntity();

        baker.Rebuild(authoringStore, runtimeStore);
        Assert.Equal(0, CountEntities(runtimeStore));
    }

    private static Material CreateMaterial(EntityStore materialStore, AssetGuid materialGuid)
    {
        Material material = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, "MaterialPassBakeMaterial", materialGuid);
        Entity overlayPass = MaterialAssetPipelineTestHelpers.CreatePass(material);
        material.PassEntities[0].AddTag<Opaque>();
        overlayPass.AddComponent(new OverlayShade { Layer = 3 });
        MaterialAssetPipelineTestHelpers.SetPassEntities(material, material.PassEntities[0], overlayPass);

        return material;
    }

    private static List<Entity> CollectEntities(EntityStore store)
    {
        List<Entity> entities = [];
        foreach (Entity entity in store.Entities)
        {
            entities.Add(entity);
        }

        return entities;
    }

    private static int CountEntities(EntityStore store)
    {
        int count = 0;
        foreach (Entity _ in store.Entities)
        {
            count++;
        }

        return count;
    }
}
