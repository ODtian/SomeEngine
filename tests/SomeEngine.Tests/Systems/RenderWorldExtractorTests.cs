using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Systems;
using SomeECS.Core;
using SomeECS.Core.Components;
using SomeECS.Core.Entities;
using SomeECS.Core.Queries;

namespace SomeEngine.Tests.Systems;

public class RenderWorldExtractorTests
{
    [Fact]
    public void Rebuild_CopiesSourceStateOnly()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        Handle<Material> material = new(7, 1);
        Handle<Mesh> mesh = new(11, 1);
        EntityId entity = AddSource(source, material, mesh, bounds: 1.25f);

        extractor.Rebuild(source);

        RenderInstance instance = Assert.Single(Instances(renderWorld.World));
        Assert.Equal(entity, instance.SourceEntity);
        Assert.Equal(0, instance.InstanceIndex);
        Assert.Equal(mesh, instance.Mesh);
        Assert.Equal(1.25f, instance.BoundsExpansion);
        Assert.Equal(material, Assert.Single(Assert.Single(Materials(renderWorld.World)).Materials.ToArray()));
        Assert.Single(Sources(renderWorld.World));
        Assert.Equal(1, renderWorld.CountInstances());
    }

    [Fact]
    public void Rebuild_UpdatesBindings_WhenHandlesChange()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        Handle<Material> first = new(1, 1);
        Handle<Material> second = new(2, 1);
        EntityId entity = AddSource(source, first, new Handle<Mesh>(3, 1), bounds: 0);

        extractor.Rebuild(source);
        uint version = extractor.Version;
        EntityId renderEntity = Assert.Single(Sources(renderWorld.World));
        ref MeshMaterialBindings bindings = ref source.Get<MeshMaterialBindings>(entity);
        bindings.Materials = new[] { second };
        extractor.Rebuild(source);

        Assert.True(extractor.Version > version);
        Assert.Equal(renderEntity, Assert.Single(Sources(renderWorld.World)));
        RenderMaterials materials = Assert.Single(Materials(renderWorld.World));
        Assert.Equal(second, Assert.Single(materials.Materials.ToArray()));
    }

    [Fact]
    public void Rebuild_UpdatesBindings_WhenBindingsAdded()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        Handle<Material> material = new(9, 1);
        EntityId entity = AddSource(
            source,
            material: default,
            mesh: new Handle<Mesh>(3, 1),
            bounds: 0,
            bindMaterial: false);

        extractor.Rebuild(source);
        EntityId renderEntity = RenderEntity(renderWorld.World, entity);
        ClearDirty(renderWorld.World, renderEntity);
        Assert.Empty(Assert.Single(Materials(renderWorld.World)).Materials.ToArray());

        source.Add(entity, new MeshMaterialBindings { Materials = new[] { material } });
        extractor.Rebuild(source);

        Assert.Single(Instances(renderWorld.World));
        RenderMaterials materials = Assert.Single(Materials(renderWorld.World));
        Assert.Equal(material, Assert.Single(materials.Materials.ToArray()));
        Assert.True((renderWorld.World.Read<InstanceDirty>(renderEntity).Flags & InstanceDirtyFlags.MaterialHeader) != 0);
    }

    [Fact]
    public void Rebuild_ClearsBindings_WhenBindingsRemoved()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(3, 1), bounds: 0);

        extractor.Rebuild(source);
        EntityId renderEntity = RenderEntity(renderWorld.World, entity);
        ClearDirty(renderWorld.World, renderEntity);
        source.Remove<MeshMaterialBindings>(entity);
        extractor.Rebuild(source);

        Assert.Single(Instances(renderWorld.World));
        RenderMaterials materials = Assert.Single(Materials(renderWorld.World));
        Assert.Empty(materials.Materials.ToArray());
        Assert.True((renderWorld.World.Read<InstanceDirty>(renderEntity).Flags & InstanceDirtyFlags.MaterialHeader) != 0);
    }

    [Fact]
    public void Rebuild_UpdatesOnlyChangedMaterialRows()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        Handle<Material> first = new(1, 1);
        Handle<Material> changed = new(2, 1);
        Handle<Material> stable = new(3, 1);
        EntityId firstEntity = AddSource(source, first, new Handle<Mesh>(3, 1), bounds: 0);
        EntityId stableEntity = AddSource(source, stable, new Handle<Mesh>(4, 1), bounds: 0);

        extractor.Rebuild(source);
        EntityId firstRender = RenderEntity(renderWorld.World, firstEntity);
        EntityId stableRender = RenderEntity(renderWorld.World, stableEntity);
        ClearDirty(renderWorld.World, firstRender);
        ClearDirty(renderWorld.World, stableRender);

        ref MeshMaterialBindings bindings = ref source.Get<MeshMaterialBindings>(firstEntity);
        bindings.Materials = new[] { changed };
        extractor.Rebuild(source);

        Assert.Equal(changed, Assert.Single(renderWorld.World.Read<RenderMaterials>(firstRender).Materials.ToArray()));
        Assert.Equal(stable, Assert.Single(renderWorld.World.Read<RenderMaterials>(stableRender).Materials.ToArray()));
        Assert.True((renderWorld.World.Read<InstanceDirty>(firstRender).Flags & InstanceDirtyFlags.MaterialHeader) != 0);
        Assert.False(renderWorld.World.Has<InstanceDirty>(stableRender));
    }

    [Fact]
    public void Rebuild_TracksTransformDirty()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);
        MaterialOverride materialOverride = new() { BaseColorTint = Vector4.One };
        source.Add(entity, materialOverride);

        extractor.Rebuild(source);
        EntityId renderEntity = Assert.Single(Sources(renderWorld.World));
        renderWorld.World.Remove<InstanceDirty>(renderEntity);
        var moved = new TransformQvvs(new Vector3(4, 5, 6), Quaternion.Identity, 1);
        ref WorldTransform transform = ref source.Get<WorldTransform>(entity);
        transform.Qvvs = moved;
        extractor.Rebuild(source);

        Assert.Equal(1, renderWorld.InstanceUpdateCount);
        RenderInstance storedInstance = Assert.Single(Instances(renderWorld.World));
        Assert.True(renderWorld.TryGetInstance(
            storedInstance.InstanceIndex,
            out _,
            out RenderInstance slotInstance,
            out bool hasOverride,
            out MaterialOverride storedOverride));
        Assert.Equal(new Vector3(4, 5, 6), slotInstance.Transform.Position);
        Assert.True(hasOverride);
        Assert.Equal(materialOverride.BaseColorTint, storedOverride.BaseColorTint);
        Assert.Equal(renderEntity, renderWorld.InstanceUpdateEntities[0]);
        Assert.Equal(storedInstance.InstanceIndex, renderWorld.InstanceUpdateIndices[0]);
        Assert.True((renderWorld.InstanceUpdateFlags[0] & InstanceDirtyFlags.Transform) != 0);
        Assert.False(renderWorld.World.Has<InstanceDirty>(renderEntity));
    }

    [Fact]
    public void Rebuild_TracksFullRangeTransformUpdatesAsUniform()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId first = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);
        EntityId second = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        Move(source, first, new Vector3(1, 0, 0));
        Move(source, second, new Vector3(2, 0, 0));
        extractor.Rebuild(source);

        Assert.True(renderWorld.InstanceUpdateUniform);
        Assert.True(renderWorld.InstanceUpdatesCoverAllSlots);
        Assert.Equal(2, renderWorld.InstanceUpdateCount);
        Assert.True((renderWorld.InstanceUpdateFlagsUnion & InstanceDirtyFlags.Transform) != 0);
    }

    [Fact]
    public void Rebuild_KeepsSparseTransformUpdatesNonUniform()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId first = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);
        EntityId second = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        EntityId firstRender = RenderEntity(renderWorld.World, first);
        EntityId secondRender = RenderEntity(renderWorld.World, second);
        Move(source, second, new Vector3(2, 0, 0));
        extractor.Rebuild(source);

        Assert.False(renderWorld.InstanceUpdateUniform);
        Assert.Equal(1, renderWorld.InstanceUpdateCount);
        Assert.Equal(secondRender, renderWorld.InstanceUpdateEntities[0]);
        Assert.NotEqual(firstRender, renderWorld.InstanceUpdateEntities[0]);
        Assert.True((renderWorld.InstanceUpdateFlags[0] & InstanceDirtyFlags.Transform) != 0);
    }

    [Fact]
    public void Rebuild_TracksSparseTransformUpdatePastFixedBitsetWidth()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        const int movedIndex = 129;
        EntityId movedSource = default;
        for (int i = 0; i <= movedIndex; i++)
        {
            EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);
            if (i == movedIndex)
                movedSource = entity;
        }

        extractor.Rebuild(source);
        EntityId movedRender = RenderEntity(renderWorld.World, movedSource);
        Move(source, movedSource, new Vector3(129, 2, 3));
        extractor.Rebuild(source);

        Assert.False(renderWorld.InstanceUpdateUniform);
        Assert.Equal(1, renderWorld.InstanceUpdateCount);
        Assert.Equal(movedRender, renderWorld.InstanceUpdateEntities[0]);
        Assert.Equal(movedIndex, renderWorld.InstanceUpdateIndices[0]);
        Assert.True((renderWorld.InstanceUpdateFlags[0] & InstanceDirtyFlags.Transform) != 0);
        Assert.True(renderWorld.TryGetInstance(
            movedIndex,
            out _,
            out RenderInstance slotInstance,
            out _,
            out _));
        Assert.Equal(new Vector3(129, 2, 3), slotInstance.Transform.Position);
    }

    [Fact]
    public void Rebuild_DoesNotChangeShapeVersion_WhenTransformChanges()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        uint version = extractor.Version;
        uint shapeVersion = extractor.ShapeVersion;

        var moved = new TransformQvvs(new Vector3(4, 5, 6), Quaternion.Identity, 1);
        ref WorldTransform transform = ref source.Get<WorldTransform>(entity);
        transform.Qvvs = moved;
        extractor.Rebuild(source);

        Assert.True(extractor.Version > version);
        Assert.Equal(shapeVersion, extractor.ShapeVersion);
    }

    [Fact]
    public void Rebuild_ChangesShapeVersion_WhenBindingsChange()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        Handle<Material> first = new(1, 1);
        Handle<Material> second = new(2, 1);
        EntityId entity = AddSource(source, first, new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        uint shapeVersion = extractor.ShapeVersion;

        ref MeshMaterialBindings bindings = ref source.Get<MeshMaterialBindings>(entity);
        bindings.Materials = new[] { second };
        extractor.Rebuild(source);

        Assert.True(extractor.ShapeVersion > shapeVersion);
    }

    [Fact]
    public void Rebuild_ChangesShapeVersion_WhenMaterialOverridePresenceChanges()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        uint shapeVersion = extractor.ShapeVersion;

        source.Add(entity, new MaterialOverride { BaseColorTint = Vector4.One });
        extractor.Rebuild(source);

        Assert.True(extractor.ShapeVersion > shapeVersion);
        shapeVersion = extractor.ShapeVersion;

        source.Remove<MaterialOverride>(entity);
        extractor.Rebuild(source);

        Assert.True(extractor.ShapeVersion > shapeVersion);
    }

    [Fact]
    public void Rebuild_DoesNotChangeShapeVersion_WhenMaterialOverrideValueChanges()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);
        source.Add(entity, new MaterialOverride { BaseColorTint = Vector4.One });

        extractor.Rebuild(source);
        uint version = extractor.Version;
        uint shapeVersion = extractor.ShapeVersion;

        source.AddOrSet(entity, new MaterialOverride { BaseColorTint = new Vector4(0.5f, 1, 1, 1) });
        extractor.Rebuild(source);

        Assert.True(extractor.Version > version);
        Assert.Equal(shapeVersion, extractor.ShapeVersion);
    }

    [Fact]
    public void Rebuild_DoesNotVersion_WhenSourceUnchanged()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        uint version = extractor.Version;
        extractor.Rebuild(source);

        Assert.Equal(version, extractor.Version);
        Assert.Single(Instances(renderWorld.World));
    }

    [Fact]
    public void Rebuild_DoesNotChangeShapeVersion_WhenLightValuesChangeWithoutCountChange()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId lightEntity = source.CreateEntity();
        Handle<Texture> atlas = new(31, 1);
        DirectionalLight directional = new(
            new Vector3(0, -1, 0),
            new Vector3(1, 0.5f, 0.25f),
            2f,
            0x00000002u,
            1,
            0.5f,
            new Vector4(0.5f, 0.5f, 0, 0),
            Matrix4x4.Identity);
        source.Add(lightEntity, new SceneLights(new[] { directional }, ReadOnlyMemory<PointLight>.Empty, ReadOnlyMemory<SpotLight>.Empty, atlas));

        extractor.Rebuild(source);
        uint version = extractor.Version;
        uint shapeVersion = extractor.ShapeVersion;

        DirectionalLight updatedDirectional = new(
            new Vector3(1, -1, 0),
            new Vector3(0.75f, 1, 0.5f),
            5f,
            0x00000004u,
            2,
            0.75f,
            new Vector4(0.25f, 0.25f, 0.5f, 0),
            Matrix4x4.CreateTranslation(1, 2, 3));
        source.AddOrSet(
            lightEntity,
            new SceneLights(
                new[] { updatedDirectional },
                ReadOnlyMemory<PointLight>.Empty,
                ReadOnlyMemory<SpotLight>.Empty,
                atlas));
        extractor.Rebuild(source);

        Assert.True(extractor.Version > version);
        Assert.Equal(shapeVersion, extractor.ShapeVersion);
        Assert.Equal(new[] { updatedDirectional }, renderWorld.SceneLights.DirectionalLights.ToArray());
        Assert.Equal(atlas, renderWorld.SceneLights.LightCookieAtlas);
    }

    [Fact]
    public void Rebuild_DoesNotChangeInstanceShapeVersion_WhenLightCountChanges()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId lightEntity = source.CreateEntity();
        DirectionalLight directional = new(new Vector3(0, -1, 0), Vector3.One, 2f);
        PointLight point = new(new Vector3(1, 2, 3), 8f, new Vector3(0.25f, 1, 0.5f), 3f);
        source.Add(
            lightEntity,
            new SceneLights(
                new[] { directional },
                ReadOnlyMemory<PointLight>.Empty,
                ReadOnlyMemory<SpotLight>.Empty,
                default));

        extractor.Rebuild(source);
        uint shapeVersion = extractor.ShapeVersion;
        uint instanceShapeVersion = renderWorld.InstanceShapeVersion;

        source.AddOrSet(
            lightEntity,
            new SceneLights(
                new[] { directional },
                new[] { point },
                ReadOnlyMemory<SpotLight>.Empty,
                default));
        extractor.Rebuild(source);

        Assert.True(extractor.ShapeVersion > shapeVersion);
        Assert.Equal(instanceShapeVersion, renderWorld.InstanceShapeVersion);
    }

    [Fact]
    public void Rebuild_UpdatesOnNewData()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId lightEntity = source.CreateEntity();
        Handle<Texture> initialAtlas = new(41, 1);
        Handle<Texture> updatedAtlas = new(42, 1);
        DirectionalLight directional = new(new Vector3(0, -1, 0), new Vector3(1, 0.5f, 0.25f), 2f);
        PointLight point = new(new Vector3(1, 2, 3), 8f, new Vector3(0.25f, 1, 0.5f), 3f);
        SpotLight spot = new(new Vector3(4, 5, 6), 16f, new Vector3(0, -1, 1), 0.8f, 0.4f, new Vector3(0.1f, 0.2f, 1), 4f);
        source.Add(lightEntity, new SceneLights(new[] { directional }, new[] { point }, new[] { spot }, initialAtlas));

        extractor.Rebuild(source);

        Assert.Equal(new[] { directional }, renderWorld.SceneLights.DirectionalLights.ToArray());
        Assert.Equal(new[] { point }, renderWorld.SceneLights.PointLights.ToArray());
        Assert.Equal(new[] { spot }, renderWorld.SceneLights.SpotLights.ToArray());
        Assert.Equal(initialAtlas, renderWorld.SceneLights.LightCookieAtlas);

        uint version = extractor.Version;
        uint shapeVersion = extractor.ShapeVersion;
        DirectionalLight updatedDirectional = new(new Vector3(1, -1, 0), new Vector3(0.75f, 1, 0.5f), 5f);
        SpotLight updatedSpot = new(new Vector3(6, 5, 4), 12f, new Vector3(1, -1, 0), 0.7f, 0.2f, new Vector3(1, 0.25f, 0.1f), 6f);
        source.AddOrSet(
            lightEntity,
            new SceneLights(
                new[] { updatedDirectional },
                ReadOnlyMemory<PointLight>.Empty,
                new[] { updatedSpot },
                updatedAtlas));
        extractor.Rebuild(source);

        Assert.True(extractor.Version > version);
        Assert.True(extractor.ShapeVersion > shapeVersion);
        Assert.Equal(new[] { updatedDirectional }, renderWorld.SceneLights.DirectionalLights.ToArray());
        Assert.Empty(renderWorld.SceneLights.PointLights.ToArray());
        Assert.Equal(new[] { updatedSpot }, renderWorld.SceneLights.SpotLights.ToArray());
        Assert.Equal(updatedAtlas, renderWorld.SceneLights.LightCookieAtlas);

        version = extractor.Version;
        extractor.Rebuild(source);

        Assert.Equal(version, extractor.Version);
    }

    [Fact]
    public void Rebuild_RemovesInstance_WhenMeshRemoved()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        source.Remove<MeshInstance>(entity);
        extractor.Rebuild(source);

        Assert.Empty(Instances(renderWorld.World));
        Assert.Empty(Sources(renderWorld.World));
        Removed<RenderInstance> removed = Assert.Single(RemovedInstances(renderWorld.World));
        Assert.Equal(0, removed.Value.InstanceIndex);
    }

    [Fact]
    public void Rebuild_RemovesInstance_WhenTransformRemoved()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        source.Remove<WorldTransform>(entity);
        extractor.Rebuild(source);

        Assert.Empty(Instances(renderWorld.World));
        Assert.Empty(Sources(renderWorld.World));
        Assert.False(source.Has<RenderSourceLink>(entity));
        Removed<RenderInstance> removed = Assert.Single(RemovedInstances(renderWorld.World));
        Assert.Equal(0, removed.Value.InstanceIndex);
    }

    [Fact]
    public void Rebuild_RemovesInstance_WhenSourceDestroyed()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = AddSource(source, new Handle<Material>(1, 1), new Handle<Mesh>(5, 1), bounds: 0);

        extractor.Rebuild(source);
        source.DestroyEntity(entity);
        extractor.Rebuild(source);

        Assert.Empty(Instances(renderWorld.World));
        Assert.Empty(Sources(renderWorld.World));
        Assert.False(source.IsAlive(entity));
        Removed<RenderInstance> removed = Assert.Single(RemovedInstances(renderWorld.World));
        Assert.Equal(0, removed.Value.InstanceIndex);
    }

    [Fact]
    public void Rebuild_AddsInstance_WhenTransformAdded()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = source.CreateEntity();
        var transform = new TransformQvvs(Vector3.One, Quaternion.Identity, 1);
        source.Add(entity, new MeshInstance { Mesh = new Handle<Mesh>(5, 1), BoundsExpansion = 0.5f });

        extractor.Rebuild(source);
        source.Add(entity, new WorldTransform { Qvvs = transform });
        extractor.Rebuild(source);

        RenderInstance instance = Assert.Single(Instances(renderWorld.World));
        Assert.Equal(entity, instance.SourceEntity);
        Assert.True(source.Has<RenderSourceLink>(entity));
    }

    [Fact]
    public void Rebuild_AddsInstance_WhenMeshAdded()
    {
        World source = new();
        RenderWorld renderWorld = new();
        RenderWorldExtractor extractor = new(renderWorld);
        EntityId entity = source.CreateEntity();
        var transform = new TransformQvvs(Vector3.One, Quaternion.Identity, 1);
        source.Add(entity, new WorldTransform { Qvvs = transform });

        extractor.Rebuild(source);
        source.Add(entity, new MeshInstance { Mesh = new Handle<Mesh>(5, 1), BoundsExpansion = 0.5f });
        extractor.Rebuild(source);

        RenderInstance instance = Assert.Single(Instances(renderWorld.World));
        Assert.Equal(entity, instance.SourceEntity);
        Assert.True(source.Has<RenderSourceLink>(entity));
    }

    private static EntityId AddSource(
        World world,
        Handle<Material> material,
        Handle<Mesh> mesh,
        float bounds,
        bool bindMaterial = true)
    {
        EntityId entity = world.CreateEntity();
        var transform = new TransformQvvs(Vector3.One, Quaternion.Identity, 1);
        world.Add(entity, new WorldTransform { Qvvs = transform });
        world.Add(entity, new MeshInstance { Mesh = mesh, BoundsExpansion = bounds });
        if (bindMaterial)
            world.Add(entity, new MeshMaterialBindings { Materials = new[] { material } });
        return entity;
    }

    private static void Move(World world, EntityId entity, Vector3 position)
    {
        var moved = new TransformQvvs(position, Quaternion.Identity, 1);
        ref WorldTransform transform = ref world.Get<WorldTransform>(entity);
        transform.Qvvs = moved;
    }

    private static EntityId RenderEntity(World world, EntityId source)
    {
        var query = world.Query(new QueryDefinitionBuilder().Read<RenderSourceEntity>());
        foreach (var chunk in world.RunQuery(query).Chunks)
        {
            ReadOnlySpan<EntityId> entities = chunk.Entities;
            ReadOnlySpan<RenderSourceEntity> sources = chunk.Read<RenderSourceEntity>();
            for (int i = 0; i < entities.Length; i++)
            {
                if (sources[i].SourceEntity == source)
                    return entities[i];
            }
        }

        throw new InvalidOperationException("render source entity was not found.");
    }

    private static void ClearDirty(World world, EntityId entity)
    {
        if (world.Has<InstanceDirty>(entity))
            world.Remove<InstanceDirty>(entity);
    }

    private static List<RenderInstance> Instances(World world)
    {
        var result = new List<RenderInstance>();
        var query = world.Query(new QueryDefinitionBuilder().Read<RenderInstance>());
        foreach (var chunk in world.RunQuery(query).Chunks)
        {
            foreach (var instance in chunk.Read<RenderInstance>())
                result.Add(instance);
        }
        return result;
    }

    private static List<EntityId> Sources(World world)
    {
        var result = new List<EntityId>();
        var query = world.Query(new QueryDefinitionBuilder().Read<RenderSourceEntity>());
        foreach (var chunk in world.RunQuery(query).Chunks)
        {
            foreach (var entity in chunk.Entities)
                result.Add(entity);
        }
        return result;
    }

    private static List<RenderMaterials> Materials(World world)
    {
        var result = new List<RenderMaterials>();
        var query = world.Query(new QueryDefinitionBuilder().Read<RenderMaterials>());
        foreach (var chunk in world.RunQuery(query).Chunks)
        {
            foreach (var materials in chunk.Read<RenderMaterials>())
                result.Add(materials);
        }
        return result;
    }

    private static List<Removed<RenderInstance>> RemovedInstances(World world)
    {
        var result = new List<Removed<RenderInstance>>();
        var query = world.Query(new QueryDefinitionBuilder().Removed<RenderInstance>());
        foreach (var chunk in world.RunQuery(query).Chunks)
        {
            foreach (var removed in chunk.Read<Removed<RenderInstance>>())
                result.Add(removed);
        }
        return result;
    }
}
