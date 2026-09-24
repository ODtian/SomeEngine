using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeECS.Core.Entities;
using SomeECS.Core.Hierarchy;
using SomeECS.Core.Queries;
using SomeEcsDepth = SomeECS.Core.Hierarchy.Depth;

namespace SomeEngine.Tests.ECS;

public class TransformSystemTests
{
    private readonly GameWorld _world = new();

    private void RunSystems()
        => _world.Update(0);

    [Fact]
    public void TestHierarchyAndTransform()
    {
        EntityId root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(new Vector3(0, 0, 0), Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        EntityId child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        OrderedHierarchy.Attach(_world.World, child, root);

        EntityId grandChild = _world.World.CreateEntity();
        _world.World.Add(grandChild, new LocalTransform { Value = new TransformQvvs(new Vector3(0, 5, 0), Quaternion.Identity) });
        _world.World.Add(grandChild, new WorldTransform());
        OrderedHierarchy.Attach(_world.World, grandChild, child);

        RunSystems();

        Assert.Equal(0, _world.World.Read<SomeEcsDepth>(root).Value);
        Assert.Equal(1, _world.World.Read<SomeEcsDepth>(child).Value);
        Assert.Equal(2, _world.World.Read<SomeEcsDepth>(grandChild).Value);

        var rootWorld = _world.World.Read<WorldTransform>(root).Qvvs;
        var childWorld = _world.World.Read<WorldTransform>(child).Qvvs;
        var grandChildWorld = _world.World.Read<WorldTransform>(grandChild).Qvvs;

        Assert.Equal(new Vector3(0, 0, 0), rootWorld.Position);
        Assert.Equal(new Vector3(10, 0, 0), childWorld.Position);
        Assert.Equal(new Vector3(10, 5, 0), grandChildWorld.Position);
    }

    [Fact]
    public void TestRotation()
    {
        EntityId root = _world.World.CreateEntity();
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, rotation) });
        _world.World.Add(root, new WorldTransform());

        EntityId child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        OrderedHierarchy.Attach(_world.World, child, root);

        RunSystems();

        var childWorld = _world.World.Read<WorldTransform>(child).Qvvs;
        var expected = Vector3.Transform(new Vector3(10, 0, 0), rotation);

        Assert.InRange(childWorld.Position.X, expected.X - 0.001f, expected.X + 0.001f);
        Assert.InRange(childWorld.Position.Y, expected.Y - 0.001f, expected.Y + 0.001f);
        Assert.InRange(childWorld.Position.Z, expected.Z - 0.001f, expected.Z + 0.001f);
    }

    [Fact]
    public void ParentMove_UpdatesDescendantWorldTransformAfterSteadyFrame()
    {
        EntityId root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        EntityId child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(1, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        OrderedHierarchy.Attach(_world.World, child, root);

        RunSystems();
        RunSystems();

        ref LocalTransform rootLocal = ref _world.World.Get<LocalTransform>(root);
        rootLocal.Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity);

        RunSystems();

        Assert.Equal(new Vector3(11, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void RemoveParent_UpdatesChildWorldTransform()
    {
        EntityId root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        EntityId child = _world.World.CreateEntity();
        _world.World.Add(child, new LocalTransform { Value = new TransformQvvs(new Vector3(1, 0, 0), Quaternion.Identity) });
        _world.World.Add(child, new WorldTransform());
        OrderedHierarchy.Attach(_world.World, child, root);

        RunSystems();
        Assert.Equal(new Vector3(11, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);

        OrderedHierarchy.Detach(_world.World, child);
        RunSystems();

        Assert.Equal(new Vector3(1, 0, 0), _world.World.Read<WorldTransform>(child).Qvvs.Position);
    }

    [Fact]
    public void TestRepeatedUpdatesDoNotExhaustJobCounters()
    {
        EntityId root = _world.World.CreateEntity();
        _world.World.Add(root, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(root, new WorldTransform());

        for (int i = 0; i < 200; i++)
            RunSystems();

        var world = _world.World.Read<WorldTransform>(root).Qvvs;
        Assert.Equal(Vector3.Zero, world.Position);
    }

    [Fact]
    public void FlatLeafUpdate_OnlyMarksChangedWorldTransformRows()
    {
        EntityId first = _world.World.CreateEntity();
        _world.World.Add(first, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(first, new WorldTransform());

        EntityId second = _world.World.CreateEntity();
        _world.World.Add(second, new LocalTransform { Value = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
        _world.World.Add(second, new WorldTransform());

        RunSystems();
        RunSystems();

        uint beforeLocalChange = _world.World.CurrentTick;
        _world.World.Replace(first, new LocalTransform { Value = new TransformQvvs(new Vector3(5, 0, 0), Quaternion.Identity) });

        RunSystems();

        QueryHandle changedWorldTransforms = _world.World.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Changed<WorldTransform>()
                .None<Parent>()
                .None<ChildBuffer>());

        List<EntityId> changed = [];
        foreach (QueryChunkView chunk in _world.World.RunQuery(
            changedWorldTransforms,
            beforeLocalChange,
            _world.World.CurrentTick).Chunks)
        {
            foreach (int row in chunk.RowIndices)
                changed.Add(chunk.GetEntity(row));
        }

        EntityId changedEntity = Assert.Single(changed);
        Assert.Equal(first, changedEntity);
        Assert.DoesNotContain(second, changed);
    }
}
