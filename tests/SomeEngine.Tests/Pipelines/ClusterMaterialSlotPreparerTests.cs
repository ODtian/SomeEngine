using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;
using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.Systems;
using SomeEngine.Tests.Materials;

namespace SomeEngine.Tests.Pipelines;

public class ClusterMaterialSlotPreparerTests
{
    [Fact]
    public void Prepare_AssignsMaterialSlotOffset_ForSingleMaterialBinding()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        InstanceDataManager instanceData = new();

        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterial(materialStore, materialGuid);
        Func<AssetGuid, Material?> resolver = guid => guid == materialGuid ? material : null;

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 9 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        extractor.Rebuild(sourceStore, resolver);

        using BinSpace binSpace = CreateBinSpace(renderWorld.Store);
        ClusterMaterialSlotPreparer preparer = new(binSpace, renderWorld.Store, instanceData);

        preparer.Prepare(0, 1, 2, extractor.Version);
        binSpace.RebuildIfDirty();

        Assert.Equal((ushort)0, binSpace.SlotBuffer!.GetField(0, 0, 0));
        Assert.Equal((ushort)0, binSpace.SlotBuffer!.GetField(0, 0, 1));
    }

    [Fact]
    public void Prepare_AssignsOneGpuSlotPerMeshLocalMaterialSlot()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialAGuid = AssetGuid.New();
        AssetGuid materialBGuid = AssetGuid.New();
        Material materialA = CreateMaterial(materialStore, materialAGuid);
        Material materialB = CreateMaterial(materialStore, materialBGuid);
        Func<AssetGuid, Material?> resolver = guid =>
            guid == materialAGuid ? materialA :
            guid == materialBGuid ? materialB :
            null;

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 9 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialAGuid, materialBGuid] });

        extractor.Rebuild(sourceStore, resolver);

        using BinSpace binSpace = CreateBinSpace(renderWorld.Store);
        InstanceDataManager instanceData = new();
        ClusterMaterialSlotPreparer preparer = new(binSpace, renderWorld.Store, instanceData);

        preparer.Prepare(0, 1, 2, extractor.Version);
        binSpace.RebuildIfDirty();

        Assert.Equal(2, binSpace.SlotBuffer!.SlotCount);
        Assert.NotEqual(
            binSpace.SlotBuffer.GetField(0, 0, 0),
            binSpace.SlotBuffer.GetField(0, 1, 0));
        Assert.NotEqual(
            binSpace.SlotBuffer.GetField(0, 0, 1),
            binSpace.SlotBuffer.GetField(0, 1, 1));
    }

    [Fact]
    public void Prepare_WritesNonZeroMaterialSlotOffset_BackToSourceAndInstanceHeaders()
    {
        GameWorld sourceWorld = new();
        EntityStore sourceStore = sourceWorld.EntityStore;
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterial(materialStore, materialGuid);
        Func<AssetGuid, Material?> resolver = guid => guid == materialGuid ? material : null;

        Entity first = sourceStore.CreateEntity();
        AddTransform(first, new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
        first.AddComponent(new MeshInstance { BVHRootIndex = 1 });
        first.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        Entity second = sourceStore.CreateEntity();
        AddTransform(second, new TransformQvvs(Vector3.One, Quaternion.Identity, 1.0f));
        second.AddComponent(new MeshInstance { BVHRootIndex = 2 });
        second.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        extractor.Rebuild(sourceStore, resolver);
        using BinSpace binSpace = CreateBinSpace(renderWorld.Store);
        InstanceDataManager instanceData = new();
        sourceWorld.SystemRoot.Add(new InstanceSyncSystem(instanceData, sourceWorld.SystemContext));
        sourceWorld.Update(0.0f);
        ClusterMaterialSlotPreparer preparer = new(binSpace, renderWorld.Store, instanceData);

        preparer.Prepare(0, 1, 2, extractor.Version);

        Assert.Equal(2, instanceData.Count);
        Assert.Equal(0u, InstanceHeaderLayout.ReadUInt32(instanceData.GetHeader(0), InstanceHeaderLayout.MaterialSlotOffset));
        Assert.NotEqual(0u, InstanceHeaderLayout.ReadUInt32(instanceData.GetHeader(1), InstanceHeaderLayout.MaterialSlotOffset));
    }

    [Fact]
    public void Prepare_WritesMaterialDeformBoundsExpansion_ToInstanceHeaders()
    {
        GameWorld sourceWorld = new();
        EntityStore sourceStore = sourceWorld.EntityStore;
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateDeformingMaterial(materialStore, materialGuid, 0.625f);
        Func<AssetGuid, Material?> resolver = guid => guid == materialGuid ? material : null;

        Entity source = sourceStore.CreateEntity();
        AddTransform(source, new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
        source.AddComponent(new MeshInstance
        {
            BVHRootIndex = 3,
            BoundsExpansion = 0.125f,
        });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        extractor.Rebuild(sourceStore, resolver);
        using BinSpace binSpace = CreateBinSpace(renderWorld.Store);
        InstanceDataManager instanceData = new();
        sourceWorld.SystemRoot.Add(new InstanceSyncSystem(instanceData, sourceWorld.SystemContext));
        sourceWorld.Update(0.0f);
        ClusterMaterialSlotPreparer preparer = new(binSpace, renderWorld.Store, instanceData);

        preparer.Prepare(0, 1, 2, extractor.Version);

        Assert.Equal(1, instanceData.Count);
        Assert.Equal(
            0.625f,
            InstanceHeaderLayout.ReadFloat32(instanceData.GetHeader(0), InstanceHeaderLayout.BoundsExpansionWorld));
    }

    [Fact]
    public void Prepare_SteadyState_DoesNotAllocateManagedMemory()
    {
        EntityStore sourceStore = new();
        EntityStore materialStore = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);

        AssetGuid materialGuid = AssetGuid.New();
        Material material = CreateMaterial(materialStore, materialGuid);
        Func<AssetGuid, Material?> resolver = guid => guid == materialGuid ? material : null;

        Entity source = sourceStore.CreateEntity();
        source.AddComponent(new MeshInstance { BVHRootIndex = 7 });
        source.AddComponent(new MeshMaterialBindings { MaterialAssetGuids = [materialGuid] });

        extractor.Rebuild(sourceStore, resolver);
        using BinSpace binSpace = CreateBinSpace(renderWorld.Store);
        InstanceDataManager instanceData = new();
        ClusterMaterialSlotPreparer preparer = new(binSpace, renderWorld.Store, instanceData);
        preparer.Prepare(0, 1, 2, extractor.Version);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        extractor.Rebuild(sourceStore, resolver);
        preparer.Prepare(0, 1, 2, extractor.Version);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    private static BinSpace CreateBinSpace(EntityStore renderWorldStore)
    {
        BinSpace binSpace = new();
        int rasterField = binSpace.RegisterField("RasterBin");
        int shadeField = binSpace.RegisterField("ShadingBin");
        int vertexField = binSpace.RegisterField("VertexEval");

        Entity[] rasterEntities = ToArray(renderWorldStore.Query<ClusterRaster>().ToEntityList());
        Entity[] shadeEntities = ToArray(renderWorldStore.Query<ClusterShadeComponent>().ToEntityList());
        Entity[] deformEntities = ToArray(renderWorldStore.Query<ClusterDeform>().ToEntityList());

        binSpace.RegisterGroup(rasterField, new BinQueue.BinGroup
        {
            Query = () => rasterEntities,
            OrderKey = static _ => 0,
            SignatureFunc = static entity => (ulong)entity.Id,
        });
        binSpace.RegisterGroup(shadeField, new BinQueue.BinGroup
        {
            Query = () => shadeEntities,
            OrderKey = static _ => 0,
            SignatureFunc = static entity => (ulong)entity.Id,
        });
        binSpace.RegisterGroup(vertexField, new BinQueue.BinGroup
        {
            Query = () => deformEntities,
            OrderKey = static _ => 0,
            SignatureFunc = static entity => (ulong)entity.Id,
        });

        binSpace.FreezeLayout();
        return binSpace;
    }

    private static Entity[] ToArray(EntityList entityList)
    {
        Entity[] entities = new Entity[entityList.Count];
        int index = 0;
        foreach (Entity entity in entityList)
        {
            entities[index++] = entity;
        }

        return entities;
    }

    private static void AddTransform(Entity entity, TransformQvvs value)
    {
        entity.AddComponent(new LocalTransform { Value = value });
        entity.AddComponent(new WorldTransform());
    }

    private static Material CreateMaterial(EntityStore materialStore, AssetGuid materialGuid)
    {
        Material material = MaterialAssetPipelineTestHelpers.CreateMaterial(materialStore, "SlotPrepareMaterial", materialGuid);
        var rasterShader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(
            AssetGuid.New(),
            "SlotRaster",
            ("ClusterRaster", ["sw_inline"], "CSSWRaster"));
        var shadeShader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(
            AssetGuid.New(),
            "SlotShade",
            ("ClusterShade", [], "CSShade"));

        material.PassEntities[0].AddComponent(new ClusterRaster { SWInline = new ShaderVariantRef(rasterShader, "CSSWRaster") });
        Entity shadePass = MaterialAssetPipelineTestHelpers.CreatePass(material);
        shadePass.AddComponent(new ClusterShadeComponent { Default = new ShaderVariantRef(shadeShader, "CSShade") });

        MaterialAssetPipelineTestHelpers.SetPassEntities(material, material.PassEntities[0], shadePass);

        return material;
    }

    private static Material CreateDeformingMaterial(EntityStore materialStore, AssetGuid materialGuid, float boundsExpansion)
    {
        Material material = CreateMaterial(materialStore, materialGuid);
        var deformShader = MaterialAssetPipelineTestHelpers.CreateShaderAsset(
            AssetGuid.New(),
            "SlotDeform",
            ("ClusterDeform", [], "CSDeformWave"));

        Entity deformPass = MaterialAssetPipelineTestHelpers.CreatePass(material);
        deformPass.AddComponent(new ClusterDeform
        {
            Default = new ShaderVariantRef(deformShader, "CSDeformWave"),
            BoundsExpansion = boundsExpansion,
        });

        MaterialAssetPipelineTestHelpers.SetPassEntities(
            material,
            material.PassEntities[0],
            material.PassEntities[1],
            deformPass);

        return material;
    }
}
