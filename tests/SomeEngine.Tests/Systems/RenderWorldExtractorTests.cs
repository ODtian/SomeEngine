using System;
using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.Systems;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Systems;

public class RenderWorldExtractorTests
{
    [Fact]
    public void Rebuild_ExpandsMaterialBinding_AndMaterialPasses()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialAGuid = AssetGuid.New();
        Material materialA = CreateMaterialWithPasses(materialStore, materialAGuid, "MatA", 2, includeOverlayOnLastPass: true);

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 7 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialAGuid] });

        extractor.Rebuild(sourceStore, guid =>
            guid == materialAGuid ? materialA : null);

        EntityList entities = renderWorld.Store.Query<RenderMaterialSlotBinding, RenderSourceEntity>().ToEntityList();
        Assert.Equal(2, entities.Count);

        foreach (Entity entity in entities)
        {
            RenderMaterialSlotBinding binding = entity.GetComponent<RenderMaterialSlotBinding>();
            RenderSourceEntity renderSource = entity.GetComponent<RenderSourceEntity>();

            Assert.Equal(source.Id, renderSource.SourceEntityId);
            Assert.Equal(0, binding.LocalMaterialSlot);
            Assert.Equal((uint)7, binding.BVHRootIndex);
            Assert.True(entity.TryGetComponent<MaterialRef>(out _));
        }

        Assert.Contains(entities, entity => entity.TryGetComponent<OverlayShade>(out OverlayShade overlay) && overlay.Layer == 7);
    }

    [Fact]
    public void Rebuild_RemovesStaleRenderEntities_WhenSourceEntityDeleted()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterialWithPasses(materialStore, materialGuid, "Mat", 1, includeOverlayOnLastPass: false);

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 3 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        extractor.Rebuild(sourceStore, guid => guid == materialGuid ? material : null);
        Assert.Single(renderWorld.Store.Query<RenderMaterialSlotBinding>().ToEntityList());

        source.DeleteEntity();
        extractor.Rebuild(sourceStore, guid => guid == materialGuid ? material : null);

        Assert.Empty(renderWorld.Store.Query<RenderMaterialSlotBinding>().ToEntityList());
    }

    [Fact]
    public void Rebuild_UpdatesRenderEntities_WhenBindingsChange()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialAGuid = AssetGuid.New();
        AssetGuid materialBGuid = AssetGuid.New();
        Material materialA = CreateMaterialWithPasses(materialStore, materialAGuid, "MatA", 1, includeOverlayOnLastPass: false);
        Material materialB = CreateMaterialWithPasses(materialStore, materialBGuid, "MatB", 2, includeOverlayOnLastPass: true);

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 11 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialAGuid] });

        extractor.Rebuild(sourceStore, guid =>
            guid == materialAGuid ? materialA :
            guid == materialBGuid ? materialB :
            null);
        Assert.Single(renderWorld.Store.Query<RenderMaterialSlotBinding>().ToEntityList());

        ref MeshMaterialBindings binding = ref source.GetComponent<MeshMaterialBindings>();
        binding.MaterialAssetGuids = [materialBGuid];

        extractor.Rebuild(sourceStore, guid =>
            guid == materialAGuid ? materialA :
            guid == materialBGuid ? materialB :
            null);

        EntityList entities = renderWorld.Store.Query<RenderMaterialSlotBinding>().ToEntityList();
        Assert.Equal(2, entities.Count);
    }

    [Fact]
    public void Rebuild_RunsAgain_WhenMaterialVersionChanges()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterialWithPasses(materialStore, materialGuid, "Mat", 1, includeOverlayOnLastPass: false);
        Func<AssetGuid, Material?> resolver = guid => guid == materialGuid ? material : null;

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 5 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        extractor.Rebuild(sourceStore, resolver);
        uint version = extractor.Version;

        material.Touch();
        extractor.Rebuild(sourceStore, resolver);

        Assert.True(extractor.Version > version);
    }

    [Fact]
    public void Rebuild_SteadyState_DoesNotAllocateManagedMemory()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterialWithPasses(materialStore, materialGuid, "Mat", 2, includeOverlayOnLastPass: true);
        Func<AssetGuid, Material?> resolver = guid => guid == materialGuid ? material : null;

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 13 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        extractor.Rebuild(sourceStore, resolver);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        extractor.Rebuild(sourceStore, resolver);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    private static Material CreateMaterialWithPasses(
        EntityStore materialStore,
        AssetGuid materialGuid,
        string name,
        int passCount,
        bool includeOverlayOnLastPass)
    {
        Material material = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, name, materialGuid);
        List<Entity> passEntities = [material.PassEntities[0]];
        for (int i = 1; i < passCount; i++)
        {
            Entity pass = MaterialAssetPipelineTestHelpers.CreatePass(material);
            if (includeOverlayOnLastPass && i == passCount - 1)
            {
                pass.AddComponent(new OverlayShade { Layer = 7 });
            }

            passEntities.Add(pass);
        }

        MaterialAssetPipelineTestHelpers.SetPassEntities(material, [.. passEntities]);

        return material;
    }
}
