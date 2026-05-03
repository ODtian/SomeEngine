using System;
using System.Numerics;
using Friflo.Engine.ECS;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.Render.Data;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Tests.Systems
{
    public class InstanceSyncSystemTests : IDisposable
    {
        private GameWorld _world;
        private RenderContext _mockContext;
        private InstanceDataManager _dataManager;
        private InstanceSyncSystem _syncSystem;

        public InstanceSyncSystemTests()
        {
            _world = new GameWorld();
            // In a real scenario we might need a mocked RenderContext that doesn't crash on buffer creation without D3D12/Vulkan.
            // For this test, since RenderContext.Device might be null if not initialized, we just pass an empty context
            // and rely on the null checks inside InstanceSyncSystem to not crash when trying to create/upload buffers.
            _mockContext = new RenderContext();
            _dataManager = new InstanceDataManager();
            _syncSystem = new InstanceSyncSystem(_dataManager, _world.SystemContext);
            _world.SystemRoot.Add(_syncSystem);
        }

        public void Dispose()
        {
            _mockContext.Dispose();
        }

        [Fact]
        public void EmptyWorld_HasZeroCount()
        {
            _world.Update(0.16f);
            Assert.Equal(0, _dataManager.Count);
        }

        [Fact]
        public void EntityWithOnlyTransform_IsNotSynced()
        {
            var e = _world.EntityStore.CreateEntity();
            AddTransform(e, new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));

            _world.Update(0.16f);
            Assert.Equal(0, _dataManager.Count);
        }

        [Fact]
        public void EntityWithOnlyMeshInstance_IsNotSynced()
        {
            var e = _world.EntityStore.CreateEntity();
            e.AddComponent(new MeshInstance { BVHRootIndex = 1 });

            _world.Update(0.16f);
            Assert.Equal(0, _dataManager.Count);
        }

        [Fact]
        public void EntityWithBoth_IsSynced()
        {
            var e = _world.EntityStore.CreateEntity();
            AddTransform(e, new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
            e.AddComponent(new MeshInstance { BVHRootIndex = 5 });

            _world.Update(0.16f);
            Assert.Equal(1, _dataManager.Count);
        }

        [Fact]
        public void MeshInstanceAuthoredBoundsExpansion_IsUploadedToInstanceHeader()
        {
            var e = _world.EntityStore.CreateEntity();
            AddTransform(e, new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
            e.AddComponent(new MeshInstance
            {
                BVHRootIndex = 5,
                BoundsExpansion = 0.25f,
            });

            _world.Update(0.16f);

            Assert.Equal(1, _dataManager.Count);
            Assert.Equal(
                0.25f,
                InstanceHeaderLayout.ReadFloat32(_dataManager.GetHeader(0), InstanceHeaderLayout.BoundsExpansionWorld));
        }

        [Fact]
        public void MaterialOverride_IsUploadedThroughInstanceDataFlagsAndHeapOffset()
        {
            var e = _world.EntityStore.CreateEntity();
            AddTransform(e, new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
            e.AddComponent(new MeshInstance { BVHRootIndex = 5 });
            e.AddComponent(new MaterialOverride { BaseColorTint = new Vector4(0.2f, 0.3f, 0.4f, 1.0f) });

            _world.Update(0.16f);

            ReadOnlySpan<byte> header = _dataManager.GetHeader(0);
            Assert.Equal(
                (uint)GpuInstanceDataFlags.MaterialOverride,
                InstanceHeaderLayout.ReadUInt32(header, InstanceHeaderLayout.InstanceDataFlags));
            Assert.Equal(0u, InstanceHeaderLayout.ReadUInt32(header, InstanceHeaderLayout.InstanceDataOffset));
            Assert.True(_dataManager.MetadataByteCount >= sizeof(float) * 4);
        }

        [Fact]
        public void MultipleEntities_AreHandledCorrectly()
        {
            var e1 = _world.EntityStore.CreateEntity();
            AddTransform(e1, new TransformQvvs(Vector3.Zero, Quaternion.Identity, 1.0f));
            e1.AddComponent(new MeshInstance { BVHRootIndex = 5 });

            var e2 = _world.EntityStore.CreateEntity(); // only transform
            AddTransform(e2, new TransformQvvs(Vector3.One, Quaternion.Identity, 1.0f));

            var e3 = _world.EntityStore.CreateEntity();
            AddTransform(e3, new TransformQvvs(new Vector3(2, 2, 2), Quaternion.Identity, 2.0f));
            e3.AddComponent(new MeshInstance { BVHRootIndex = 12 });

            _world.Update(0.16f);
            Assert.Equal(2, _dataManager.Count);
        }

        private static void AddTransform(Entity entity, TransformQvvs value)
        {
            entity.AddComponent(new LocalTransform { Value = value });
            entity.AddComponent(new WorldTransform());
        }
    }
}
