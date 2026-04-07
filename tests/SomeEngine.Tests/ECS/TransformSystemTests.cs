using System;
using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.Core.Jobs;
using SomeEngine.Core.Math;

namespace SomeEngine.Tests.ECS;

public class TransformSystemTests
{
    private EntityStore _store;
    private HierarchySystem _hierarchySystem;
    private TransformSystem _transformSystem;
    private SystemRoot _systemRoot;
    private SystemContext _context;

    private void RunSystems()
    {
        _context.GlobalDependency = default;
        _systemRoot.Update(default);
        _context.GlobalDependency.Complete();
    }

    public TransformSystemTests()
    {
        _store = new EntityStore();
        _context = new SystemContext();
        _hierarchySystem = new HierarchySystem(_store);
        _transformSystem = new TransformSystem(_context);

        _systemRoot = new SystemRoot(_store) {
            _hierarchySystem,
            _transformSystem
        };
    }

    [Fact]
    public void TestHierarchyAndTransform()
    {
        // 1. Create Entities
        var root = _store.CreateEntity();
        root.AddComponent(new LocalTransform { Value = new TransformQvvs(new Vector3(0, 0, 0), Quaternion.Identity) });
        root.AddComponent(new WorldTransform());

        var child = _store.CreateEntity();
        child.AddComponent(new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        child.AddComponent(new WorldTransform());
        root.AddChild(child); // This adds TreeNode component automatically in Friflo ECS

        var grandChild = _store.CreateEntity();
        grandChild.AddComponent(new LocalTransform { Value = new TransformQvvs(new Vector3(0, 5, 0), Quaternion.Identity) });
        grandChild.AddComponent(new WorldTransform());
        child.AddChild(grandChild);

        // 2. Run Systems
        RunSystems();

        // 3. Verify Depths
        Assert.Equal(0, root.GetComponent<TransformDepth>().Value);
        Assert.Equal(1, child.GetComponent<TransformDepth>().Value);
        Assert.Equal(2, grandChild.GetComponent<TransformDepth>().Value);

        // 4. Verify World Transforms
        var rootWorld = root.GetComponent<WorldTransform>().Qvvs;
        var childWorld = child.GetComponent<WorldTransform>().Qvvs;
        var grandChildWorld = grandChild.GetComponent<WorldTransform>().Qvvs;

        Assert.Equal(new Vector3(0, 0, 0), rootWorld.Position);
        Assert.Equal(new Vector3(10, 0, 0), childWorld.Position);
        Assert.Equal(new Vector3(10, 5, 0), grandChildWorld.Position);
    }

    [Fact]
    public void TestRotation()
    {
        var root = _store.CreateEntity();
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f); // 90 deg Y
        root.AddComponent(new LocalTransform { Value = new TransformQvvs(Vector3.Zero, rotation) });
        root.AddComponent(new WorldTransform());

        var child = _store.CreateEntity();
        child.AddComponent(new LocalTransform { Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) });
        child.AddComponent(new WorldTransform());
        root.AddChild(child);

        RunSystems();

        var childWorld = child.GetComponent<WorldTransform>().Qvvs;

        var expected = Vector3.Transform(new Vector3(10, 0, 0), rotation);

        Assert.InRange(childWorld.Position.X, expected.X - 0.001f, expected.X + 0.001f);
        Assert.InRange(childWorld.Position.Y, expected.Y - 0.001f, expected.Y + 0.001f);
        Assert.InRange(childWorld.Position.Z, expected.Z - 0.001f, expected.Z + 0.001f);
    }
}
