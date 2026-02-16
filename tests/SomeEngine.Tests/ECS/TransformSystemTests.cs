using System;
using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using NUnit.Framework;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.Core.Math;

namespace SomeEngine.Tests.ECS;

[TestFixture]
public class TransformSystemTests
{
    private EntityStore _store;
    private HierarchySystem _hierarchySystem;
    private TransformSystem _transformSystem;
    private SystemRoot _systemRoot;
    private SystemContext _context;

    [SetUp]
    public void Setup()
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

    [Test]
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
        _systemRoot.Update(default);

        // 3. Verify Depths
        Assert.That(root.GetComponent<TransformDepth>().Value, Is.EqualTo(0));
        Assert.That(child.GetComponent<TransformDepth>().Value, Is.EqualTo(1));
        Assert.That(grandChild.GetComponent<TransformDepth>().Value, Is.EqualTo(2));

        // 4. Verify World Transforms
        var rootWorld = root.GetComponent<WorldTransform>().Qvvs;
        var childWorld = child.GetComponent<WorldTransform>().Qvvs;
        var grandChildWorld = grandChild.GetComponent<WorldTransform>().Qvvs;

        Assert.That(rootWorld.Position, Is.EqualTo(new Vector3(0, 0, 0)));
        Assert.That(childWorld.Position, Is.EqualTo(new Vector3(10, 0, 0)));
        Assert.That(grandChildWorld.Position, Is.EqualTo(new Vector3(10, 5, 0)));
    }

    [Test]
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

        _systemRoot.Update(default);

        var childWorld = child.GetComponent<WorldTransform>().Qvvs;
        
        var expected = Vector3.Transform(new Vector3(10, 0, 0), rotation);
        
        Assert.That(childWorld.Position.X, Is.EqualTo(expected.X).Within(0.001f));
        Assert.That(childWorld.Position.Y, Is.EqualTo(expected.Y).Within(0.001f));
        Assert.That(childWorld.Position.Z, Is.EqualTo(expected.Z).Within(0.001f));
    }
}
