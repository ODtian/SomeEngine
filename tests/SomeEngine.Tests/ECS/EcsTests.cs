using NUnit.Framework;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.Core.Math;
using System.Numerics;
using Friflo.Engine.ECS;

namespace SomeEngine.Tests.ECS;

[TestFixture]
public class EcsTests
{
    [Test]
    public void TestTransformHierarchy()
    {
        var world = new GameWorld();
        // Systems are initialized in GameWorld constructor

        // Root
        var root = world.EntityStore.CreateEntity();
        root.AddComponent(new LocalTransform 
        { 
            Value = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity) 
        });
        root.AddComponent(new WorldTransform());

        // Child
        var child = world.EntityStore.CreateEntity();
        child.AddComponent(new LocalTransform 
        { 
            Value = new TransformQvvs(new Vector3(0, 5, 0), Quaternion.Identity) 
        });
        child.AddComponent(new WorldTransform());
        
        // Add child to root
        root.AddChild(child);
        // child.AddComponent(new Parent { Value = root });

        // Run systems via World
        world.Update(0);

        // Check Root
        var rootWorld = root.GetComponent<WorldTransform>();
        Assert.That(rootWorld.Qvvs.Position, Is.EqualTo(new Vector3(10, 0, 0)));

        // Check Child
        var childWorld = child.GetComponent<WorldTransform>();
        // Child local (0,5,0) + Parent (10,0,0) = (10,5,0) (No rotation)
        Assert.That(childWorld.Qvvs.Position, Is.EqualTo(new Vector3(10, 5, 0)));
    }

    [Test]
    public void TestRotationHierarchy()
    {
        var world = new GameWorld();
        // Systems are initialized in GameWorld constructor

        // Root at (0,0,0), Rotated 90 deg around Y
        var rotation90Y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);
        var root = world.EntityStore.CreateEntity();
        root.AddComponent(new LocalTransform 
        { 
            Value = new TransformQvvs(Vector3.Zero, rotation90Y) 
        });
        root.AddComponent(new WorldTransform());

        // Child at (1,0,0) local. 
        var child = world.EntityStore.CreateEntity();
        child.AddComponent(new LocalTransform 
        { 
            Value = new TransformQvvs(new Vector3(1, 0, 0), Quaternion.Identity) 
        });
        child.AddComponent(new WorldTransform());
        
        root.AddChild(child);
        // child.AddComponent(new Parent { Value = root });

        world.Update(0);

        var childWorld = child.GetComponent<WorldTransform>();
        
        // Allow some float error
        Assert.That(childWorld.Qvvs.Position.X, Is.EqualTo(0).Within(1e-5));
        Assert.That(childWorld.Qvvs.Position.Y, Is.EqualTo(0).Within(1e-5));
        Assert.That(childWorld.Qvvs.Position.Z, Is.EqualTo(-1).Within(1e-5));
    }
}
