using System.Numerics;
using Friflo.Engine.ECS;
using NUnit.Framework;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Tests.Systems
{
    [TestFixture]
    public class InstanceSyncSystemTests
    {
        private GameWorld _world;
        private RenderContext _mockContext;
        private InstanceSyncSystem _syncSystem;

        [SetUp]
        public void Setup()
        {
            _world = new GameWorld();
            // In a real scenario we might need a mocked RenderContext that doesn't crash on buffer creation without D3D12/Vulkan.
            // For this test, since RenderContext.Device might be null if not initialized, we just pass an empty context
            // and rely on the null checks inside InstanceSyncSystem to not crash when trying to create/upload buffers.
            _mockContext = new RenderContext();
            _syncSystem = new InstanceSyncSystem(_mockContext);
            _world.SystemRoot.Add(_syncSystem);
        }

        [TearDown]
        public void TearDown()
        {
            _syncSystem?.Dispose();
            _mockContext?.Dispose();
        }

        [Test]
        public void EmptyWorld_HasZeroCount()
        {
            _world.Update(0.16f);
            Assert.That(_syncSystem.Count, Is.EqualTo(0));
        }

        [Test]
        public void EntityWithOnlyTransform_IsNotSynced()
        {
            var e = _world.EntityStore.CreateEntity();
            e.AddComponent(new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));

            _world.Update(0.16f);
            Assert.That(_syncSystem.Count, Is.EqualTo(0));
        }

        [Test]
        public void EntityWithOnlyMeshInstance_IsNotSynced()
        {
            var e = _world.EntityStore.CreateEntity();
            e.AddComponent(new MeshInstance { BVHRootIndex = 1 });

            _world.Update(0.16f);
            Assert.That(_syncSystem.Count, Is.EqualTo(0));
        }

        [Test]
        public void EntityWithBoth_IsSynced()
        {
            var e = _world.EntityStore.CreateEntity();
            e.AddComponent(new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
            e.AddComponent(new MeshInstance { BVHRootIndex = 5 });

            _world.Update(0.16f);
            Assert.That(_syncSystem.Count, Is.EqualTo(1));
        }

        [Test]
        public void MultipleEntities_AreHandledCorrectly()
        {
            var e1 = _world.EntityStore.CreateEntity();
            e1.AddComponent(new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
            e1.AddComponent(new MeshInstance { BVHRootIndex = 5 });

            var e2 = _world.EntityStore.CreateEntity(); // only transform
            e2.AddComponent(new TransformQvvs(Vector3.One, Quaternion.Identity, 1.0f));

            var e3 = _world.EntityStore.CreateEntity();
            e3.AddComponent(new TransformQvvs(new Vector3(2, 2, 2), Quaternion.Identity, 2.0f));
            e3.AddComponent(new MeshInstance { BVHRootIndex = 12 });

            _world.Update(0.16f);
            Assert.That(_syncSystem.Count, Is.EqualTo(2));
        }
    }
}
