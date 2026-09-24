using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeECS.Core;
using SomeECS.Core.Entities;
using SomeECS.Core.Hierarchy;
using SomeECS.Core.Queries;
using SomeECS.Systems;

namespace SomeEngine.Core.ECS.Systems;

public sealed class TransformSystem : ISystem<EngineSystemContext>
{
    private readonly List<EntityId> _rootsScratch = [];
    private readonly List<EntityId> _stackScratch = [];
    private readonly List<EntityId> _dirtyRootsScratch = [];
    private readonly List<EntityId> _staleParentScratch = [];
    private readonly HashSet<EntityId> _dirtySet = [];
    private readonly HashSet<EntityId> _seenParentSet = [];
    private readonly HashSet<EntityId> _updatedSet = [];
    private readonly Dictionary<EntityId, EntityId> _knownParents = [];
    private QueryHandle _transformQuery;
    private QueryHandle _parentedTransformQuery;
    private QueryHandle _changedFlatLeafLocalQuery;
    private QueryHandle _changedLocalQuery;
    private QueryHandle _changedParentQuery;
    private bool _forceFullUpdate = true;

    public void OnCreate(ref EngineSystemContext context)
    {
        World world = context.World;
        _transformQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .Read<WorldTransform>());
        _parentedTransformQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .Read<WorldTransform>()
                .Read<Parent>());
        _changedFlatLeafLocalQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .ReadWrite<WorldTransform>()
                .ChunkChanged<LocalTransform>()
                .None<Parent>()
                .None<ChildBuffer>());
        _changedLocalQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .Read<WorldTransform>()
                .Changed<LocalTransform>()
                .Any<Parent>()
                .Any<ChildBuffer>());
        _changedParentQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .Read<WorldTransform>()
                .Read<Parent>()
                .Changed<Parent>());
    }

    public void OnUpdate(ref EngineSystemContext context)
    {
        using var scope = Profiler.BeginScope("TransformSystem.OnUpdate");
        World world = context.World;
        OrderedHierarchy.Update(world);

        using (Profiler.BeginScope("TransformSystem.UpdateHierarchy"))
        {
            bool forceChildren = _forceFullUpdate;
            if (!forceChildren)
            {
                using (Profiler.BeginScope("TransformSystem.UpdateFlatLeafTransforms"))
                {
                    UpdateFlatLeafTransforms(world, context.LastSystemVersion, context.CurrentSystemVersion);
                }
            }

            using (Profiler.BeginScope("TransformSystem.CollectDirtyRoots"))
            {
                if (!CollectDirtyRoots(world, context.LastSystemVersion, context.CurrentSystemVersion))
                    return;
            }

            using (Profiler.BeginScope("TransformSystem.UpdateDirtyRoots"))
            {
                _updatedSet.Clear();
                for (int i = 0; i < _dirtyRootsScratch.Count; i++)
                {
                    EntityId root = _dirtyRootsScratch[i];
                    if (HasDirtyAncestor(world, root))
                        continue;

                    UpdateTransformSubtree(world, root, forceChildren);
                }
            }

            _forceFullUpdate = false;
        }
    }

    private void UpdateFlatLeafTransforms(World world, uint lastSystemVersion, uint currentSystemVersion)
    {
        foreach (QueryChunkView chunk in world.RunQuery(_changedFlatLeafLocalQuery, lastSystemVersion, currentSystemVersion).Chunks)
        {
            ReadOnlySpan<LocalTransform> locals = chunk.Read<LocalTransform>();
            ReadOnlySpan<uint> localVersions = chunk.ReadWriteVersions<LocalTransform>();

            int firstUnchanged = -1;
            for (int i = 0; i < locals.Length; i++)
            {
                if ((int)(localVersions[i] - lastSystemVersion) <= 0)
                {
                    firstUnchanged = i;
                    break;
                }
            }

            if (firstUnchanged < 0)
            {
                Span<WorldTransform> worlds = chunk.ReadWrite<WorldTransform>();
                for (int i = 0; i < locals.Length; i++)
                {
                    TransformQvvs local = locals[i].Value;
                    worlds[i].Qvvs = local;
                }

                continue;
            }

            for (int i = 0; i < locals.Length; i++)
            {
                if ((int)(localVersions[i] - lastSystemVersion) <= 0)
                    continue;

                TransformQvvs local = locals[i].Value;
                ref WorldTransform worldTransform = ref chunk.ReadWrite<WorldTransform>(i);
                worldTransform.Qvvs = local;
            }
        }
    }

    private bool CollectDirtyRoots(World world, uint lastSystemVersion, uint currentSystemVersion)
    {
        _dirtyRootsScratch.Clear();
        _dirtySet.Clear();

        if (_forceFullUpdate)
        {
            CollectTraversalRoots(world);
            for (int i = 0; i < _rootsScratch.Count; i++)
                AddDirtyRoot(_rootsScratch[i]);
            RefreshKnownParents(world, markChangesDirty: false);
            return _dirtyRootsScratch.Count != 0;
        }

        CollectChanged(world, lastSystemVersion, currentSystemVersion);
        CollectChangedParents(world, lastSystemVersion, currentSystemVersion);
        RefreshKnownParents(world, markChangesDirty: true);
        return _dirtyRootsScratch.Count != 0;
    }

    private void CollectTraversalRoots(World world)
    {
        _rootsScratch.Clear();
        foreach (QueryChunkView chunk in world.RunQuery(_transformQuery).Chunks)
        {
            ReadOnlySpan<EntityId> entities = chunk.Entities;

            for (int i = 0; i < entities.Length; i++)
            {
                EntityId entity = entities[i];
                if (IsTraversalRoot(world, entity))
                    _rootsScratch.Add(entity);
            }
        }
    }

    private void UpdateTransformChildren(World world, EntityId root)
    {
        _stackScratch.Clear();
        PushTransformChildren(world, root);

        while (_stackScratch.Count != 0)
        {
            int index = _stackScratch.Count - 1;
            EntityId entity = _stackScratch[index];
            _stackScratch.RemoveAt(index);

            if (!TryUpdateEntity(world, entity))
                continue;

            PushTransformChildren(world, entity);
        }
    }

    private void UpdateTransformSubtree(World world, EntityId root, bool forceChildren)
    {
        _stackScratch.Clear();
        _stackScratch.Add(root);

        while (_stackScratch.Count != 0)
        {
            int index = _stackScratch.Count - 1;
            EntityId entity = _stackScratch[index];
            _stackScratch.RemoveAt(index);

            if (!_updatedSet.Add(entity))
                continue;

            bool explicitlyDirty = _dirtySet.Remove(entity);
            if (!TryUpdateEntity(world, entity, out bool worldChanged))
                continue;

            if (forceChildren || explicitlyDirty || worldChanged)
                PushTransformChildren(world, entity);
        }
    }

    private void CollectChanged(World world, uint lastSystemVersion, uint currentSystemVersion)
    {
        foreach (QueryChunkView chunk in world.RunQuery(_changedLocalQuery, lastSystemVersion, currentSystemVersion).Chunks)
        {
            foreach (int row in chunk.RowIndices)
                AddDirtyRoot(chunk.GetEntity(row));
        }
    }

    private void CollectChangedParents(World world, uint lastSystemVersion, uint currentSystemVersion)
    {
        foreach (QueryChunkView chunk in world.RunQuery(_changedParentQuery, lastSystemVersion, currentSystemVersion).Chunks)
        {
            foreach (int row in chunk.RowIndices)
                AddDirtyRoot(chunk.GetEntity(row));
        }
    }

    private void RefreshKnownParents(World world, bool markChangesDirty)
    {
        _seenParentSet.Clear();
        foreach (QueryChunkView chunk in world.RunQuery(_parentedTransformQuery).Chunks)
        {
            ReadOnlySpan<EntityId> entities = chunk.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                EntityId entity = entities[i];
                EntityId parent = OrderedHierarchy.GetParent(world, entity);
                _seenParentSet.Add(entity);
                if (_knownParents.TryGetValue(entity, out EntityId previousParent))
                {
                    if (markChangesDirty && previousParent != parent)
                        AddDirtyRoot(entity);
                    if (parent == EntityId.Null)
                        _knownParents.Remove(entity);
                    else
                        _knownParents[entity] = parent;
                }
                else
                {
                    if (parent != EntityId.Null)
                        _knownParents.Add(entity, parent);
                    if (markChangesDirty)
                        AddDirtyRoot(entity);
                }
            }
        }

        _staleParentScratch.Clear();
        foreach (EntityId entity in _knownParents.Keys)
        {
            if (!_seenParentSet.Contains(entity))
                _staleParentScratch.Add(entity);
        }

        for (int i = 0; i < _staleParentScratch.Count; i++)
        {
            EntityId entity = _staleParentScratch[i];
            if (markChangesDirty)
                AddDirtyRoot(entity);
            _knownParents.Remove(entity);
        }
    }

    private void AddDirtyRoot(EntityId entity)
    {
        if (_dirtySet.Add(entity))
            _dirtyRootsScratch.Add(entity);
    }

    private bool HasDirtyAncestor(World world, EntityId entity)
    {
        EntityId parent = OrderedHierarchy.GetParent(world, entity);
        while (parent != EntityId.Null && IsTransformNode(world, parent))
        {
            if (_dirtySet.Contains(parent))
                return true;
            parent = OrderedHierarchy.GetParent(world, parent);
        }

        return false;
    }

    private void PushTransformChildren(World world, EntityId parent)
    {
        ReadOnlySpan<EntityId> children = OrderedHierarchy.GetChildren(world, parent);
        for (int i = children.Length - 1; i >= 0; i--)
        {
            EntityId child = children[i];
            if (IsTransformNode(world, child))
                _stackScratch.Add(child);
        }
    }

    private static bool IsTraversalRoot(World world, EntityId entity)
    {
        EntityId parent = OrderedHierarchy.GetParent(world, entity);
        return parent == EntityId.Null || !world.IsAlive(parent) || !IsTransformNode(world, parent);
    }

    private static bool IsTransformNode(World world, EntityId entity)
        => world.IsAlive(entity) && world.Has<LocalTransform>(entity) && world.Has<WorldTransform>(entity);

    private static bool TryUpdateEntity(World world, EntityId entity)
        => TryUpdateEntity(world, entity, out _);

    private static bool TryUpdateEntity(World world, EntityId entity, out bool changed)
    {
        changed = false;
        if (!IsTransformNode(world, entity))
            return false;

        TransformQvvs local = world.Read<LocalTransform>(entity).Value;
        TransformQvvs worldValue = local;

        EntityId parent = OrderedHierarchy.GetParent(world, entity);
        if (parent != EntityId.Null && world.IsAlive(parent) && world.Has<WorldTransform>(parent))
        {
            WorldTransform parentWorld = world.Read<WorldTransform>(parent);
            worldValue = TransformQvvs.Combine(parentWorld.Qvvs, local);
        }

        WorldTransform current = world.Read<WorldTransform>(entity);
        if (TransformEquals(current.Qvvs, worldValue))
            return true;

        ref WorldTransform worldTransform = ref world.Get<WorldTransform>(entity);
        worldTransform.Qvvs = worldValue;
        changed = true;
        return true;
    }

    private static bool TransformEquals(in TransformQvvs left, in TransformQvvs right)
        => left.Position == right.Position
            && left.Rotation == right.Rotation
            && left.Stretch == right.Stretch
            && left.Scale == right.Scale;
}
