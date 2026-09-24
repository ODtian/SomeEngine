using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Data;
using SomeEngine.Render.Materials;
using SomeECS.Core;
using SomeECS.Core.Entities;
using SomeECS.Core.Queries;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Render.Systems;

public sealed class RenderWorldExtractor
{
    private readonly RenderWorld _renderWorld;
    private readonly Dictionary<EntityId, EntityId> _instanceEntities = [];
    private readonly Dictionary<EntityId, RenderInstance> _previous = [];
    private readonly List<int> _freeIndices = [];
    private readonly List<EntityId> _linkSources = [];
    private readonly List<EntityId> _linkTargets = [];
    private readonly List<int> _linkIndices = [];
    private readonly HashSet<EntityId> _removedSources = [];
    private readonly List<DirectionalLight> _directionalLights = [];
    private readonly List<PointLight> _pointLights = [];
    private readonly List<SpotLight> _spotLights = [];
    private World? _sourceWorld;
    private QueryHandle _sourceQuery;
    private QueryHandle _unlinkedSources;
    private QueryHandle _changedTransforms;
    private QueryHandle _changedOverrides;
    private QueryHandle _changedMesh;
    private QueryHandle _lostMesh;
    private QueryHandle _lostTransform;
    private QueryHandle _removedMesh;
    private QueryHandle _removedTransform;
    private QueryHandle _changedBindings;
    private QueryHandle _removedBindings;
    private QueryHandle _removedOverride;
    private QueryHandle _sceneLights;
    private QueryHandle _addedSceneLights;
    private QueryHandle _changedSceneLights;
    private QueryHandle _removedSceneLights;
    private QueryHandle _renderInstances;
    private uint _lastSourceVersion;
    private int _nextIndex;
    private bool _built;
    private uint _version;
    private uint _shapeVersion;
    private uint _instanceShapeVersion;
    private uint _materialVersion;
    private uint _denseSourceSlotShapeVersion;
    private bool _denseSourceSlots;

    public RenderWorldExtractor(RenderWorld renderWorld)
    {
        _renderWorld = renderWorld ?? throw new ArgumentNullException(nameof(renderWorld));
        _renderInstances = _renderWorld.World.Query(
            new QueryDefinitionBuilder()
                .Read<RenderInstance>());
    }

    public uint Version => _version;
    public uint ShapeVersion => _shapeVersion;

    public void Rebuild(World sourceWorld)
    {
        ArgumentNullException.ThrowIfNull(sourceWorld);
        uint current = sourceWorld.AcquireSystemTick();
        EnsureQueries(sourceWorld);
        _renderWorld.ClearInstanceUpdates();
        if (!_built)
        {
            using (Profiler.BeginScope("RenderWorldExtractor.Build"))
            {
                CapturePrevious();
                _renderWorld.Reset();
                Build(sourceWorld, current);
                CollectLights(sourceWorld);
                _renderWorld.LightVersion = 1;
                _materialVersion = 1;
                _renderWorld.MaterialVersion = _materialVersion;
                IndexInstances();
                _built = true;
                _version++;
                _renderWorld.Version = _version;
                _instanceShapeVersion++;
                if (_instanceShapeVersion == 0)
                    _instanceShapeVersion = 1;
                _renderWorld.InstanceShapeVersion = _instanceShapeVersion;
                TouchShape();
            }

            _lastSourceVersion = current;
            return;
        }

        bool changed;
        bool materialChanged;
        bool lightsChanged;
        bool instanceShapeChanged;
        bool lightsShapeChanged;
        using (Profiler.BeginScope("RenderWorldExtractor.Apply"))
        {
            using (Profiler.BeginScope("RenderWorldExtractor.ApplyInstances"))
            {
                changed = Apply(sourceWorld, current, out instanceShapeChanged, out materialChanged);
            }

            using (Profiler.BeginScope("RenderWorldExtractor.ApplyLights"))
            {
                lightsChanged = ApplyLights(sourceWorld, current, out lightsShapeChanged);
            }
        }

        if (changed || lightsChanged)
        {
            _version++;
            _renderWorld.Version = _version;
        }
        if (lightsChanged)
        {
            _renderWorld.LightVersion++;
            if (_renderWorld.LightVersion == 0)
                _renderWorld.LightVersion = 1;
        }
        if (materialChanged)
        {
            _materialVersion++;
            if (_materialVersion == 0)
                _materialVersion = 1;
            _renderWorld.MaterialVersion = _materialVersion;
        }
        if (instanceShapeChanged)
        {
            _instanceShapeVersion++;
            if (_instanceShapeVersion == 0)
                _instanceShapeVersion = 1;
            _renderWorld.InstanceShapeVersion = _instanceShapeVersion;
            TouchShape();
        }
        else if (lightsShapeChanged)
        {
            TouchShape();
        }

        _lastSourceVersion = current;
    }

    private void Build(World sourceWorld, uint current)
    {
        EnsureQueries(sourceWorld);
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        int instanceIndex = 0;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_sourceQuery, 0, current).Chunks)
        {
            ReadOnlySpan<EntityId> entities = chunk.Entities;
            ReadOnlySpan<WorldTransform> transforms = chunk.Read<WorldTransform>();
            ReadOnlySpan<MeshInstance> meshes = chunk.Read<MeshInstance>();
            bool hasBindings = chunk.TryRead<MeshMaterialBindings>(out ReadOnlySpan<MeshMaterialBindings> bindings);
            bool hasOverrides = chunk.TryRead<MaterialOverride>(out ReadOnlySpan<MaterialOverride> overrides);

            for (int i = 0; i < entities.Length; i++)
            {
                EntityId source = entities[i];
                GpuTransform transform = GpuTransform.FromQvvs(transforms[i].Qvvs);
                RenderInstance old = _previous.TryGetValue(source, out RenderInstance previous)
                    ? previous
                    : default;
                Handle<Material>[] materials = hasBindings
                    ? bindings[i].Materials.ToArray()
                    : [];
                var instance = new RenderInstance
                {
                    SourceEntity = source,
                    InstanceIndex = instanceIndex++,
                    Transform = transform,
                    PrevTransform = old.SourceEntity == source ? old.Transform : transform,
                    Mesh = meshes[i].Mesh,
                    DataOffset = old.SourceEntity == source ? old.DataOffset : 0,
                    DataFlags = hasOverrides ? InstanceFlags.MaterialOverride : InstanceFlags.None,
                    BoundsExpansion = MathF.Max(0f, meshes[i].BoundsExpansion),
                };

                EntityId renderEntity = _renderWorld.World.CreateEntity();
                _renderWorld.World.Add(renderEntity, new RenderSourceEntity { SourceEntity = source });
                _renderWorld.World.Add(renderEntity, new RenderMaterials { Materials = materials });
                InstanceMarks.Write(_renderWorld.World, renderEntity, instance, InstanceDirtyFlags.All);
                MaterialOverride materialOverride = hasOverrides ? overrides[i] : default;
                if (hasOverrides)
                    _renderWorld.World.Add(renderEntity, materialOverride);
                _renderWorld.StoreInstance(renderEntity, in instance, hasOverrides, in materialOverride);
                _linkSources.Add(source);
                _linkTargets.Add(renderEntity);
                _linkIndices.Add(instance.InstanceIndex);
            }
        }

        LinkSources(sourceWorld);
        _nextIndex = instanceIndex;
        _renderWorld.InstanceCount = instanceIndex;
    }

    private bool Apply(World sourceWorld, uint current, out bool shapeChanged, out bool materialChanged)
    {
        bool changed = false;
        shapeChanged = false;
        materialChanged = false;
        _removedSources.Clear();
        bool removedLost;
        using (Profiler.BeginScope("RenderWorldExtractor.RemoveLost"))
        {
            removedLost = RemoveLost(sourceWorld, current);
        }

        bool removedSources;
        using (Profiler.BeginScope("RenderWorldExtractor.RemoveSources"))
        {
            removedSources = RemoveSources(sourceWorld, current);
        }

        bool addedSources;
        using (Profiler.BeginScope("RenderWorldExtractor.AddSources"))
        {
            addedSources = AddSources(sourceWorld, current);
        }

        changed |= removedLost;
        changed |= removedSources;
        changed |= addedSources;
        shapeChanged |= removedLost;
        shapeChanged |= removedSources;
        shapeChanged |= addedSources;
        materialChanged |= removedLost;
        materialChanged |= removedSources;
        materialChanged |= addedSources;
        bool instanceDataChanged;
        bool instanceDataShapeChanged;
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateChangedInstances"))
        {
            instanceDataChanged = UpdateChangedInstances(sourceWorld, current, out instanceDataShapeChanged);
        }
        changed |= instanceDataChanged;
        shapeChanged |= instanceDataShapeChanged;

        bool meshesChanged;
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateMeshes"))
        {
            meshesChanged = UpdateMeshes(sourceWorld, current);
        }

        bool bindingsChanged;
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateBindings"))
        {
            bindingsChanged = UpdateBindings(sourceWorld, current);
        }

        bool bindingsRemoved;
        using (Profiler.BeginScope("RenderWorldExtractor.RemoveBindings"))
        {
            bindingsRemoved = RemoveBindings(sourceWorld, current);
        }

        changed |= meshesChanged;
        changed |= bindingsChanged;
        changed |= bindingsRemoved;
        shapeChanged |= meshesChanged;
        shapeChanged |= bindingsChanged;
        shapeChanged |= bindingsRemoved;
        materialChanged |= meshesChanged;
        materialChanged |= bindingsChanged;
        materialChanged |= bindingsRemoved;
        bool overridesRemoved;
        bool overridesRemovedShapeChanged;
        using (Profiler.BeginScope("RenderWorldExtractor.RemoveOverrides"))
        {
            overridesRemoved = RemoveOverrides(sourceWorld, current, out overridesRemovedShapeChanged);
        }

        changed |= overridesRemoved;
        shapeChanged |= overridesRemovedShapeChanged;
        return changed;
    }

    private bool RemoveSources(World sourceWorld, uint current)
    {
        bool changed = false;
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_removedMesh, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= chunk.TryRead(row, out RenderSourceLink link)
                    ? RemoveSource(source, link.RenderEntity)
                    : RemoveSource(source);
            }
        }

        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_removedTransform, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= chunk.TryRead(row, out RenderSourceLink link)
                    ? RemoveSource(source, link.RenderEntity)
                    : RemoveSource(source);
            }
        }

        ClearLinks(sourceWorld);
        sourceWorld.ClearRemoved<MeshInstance>(current);
        sourceWorld.ClearRemoved<WorldTransform>(current);
        return changed;
    }

    private bool RemoveLost(World sourceWorld, uint current)
    {
        bool changed = false;
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_lostMesh, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= RemoveSource(source, chunk.Read<RenderSourceLink>(row).RenderEntity);
            }
        }

        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_lostTransform, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (!_removedSources.Add(source))
                    continue;

                _linkSources.Add(source);
                changed |= RemoveSource(source, chunk.Read<RenderSourceLink>(row).RenderEntity);
            }
        }

        ClearLinks(sourceWorld);
        return changed;
    }

    private bool RemoveSource(EntityId source)
    {
        if (!_instanceEntities.TryGetValue(source, out EntityId renderEntity))
            return false;

        return RemoveSource(source, renderEntity);
    }

    private bool RemoveSource(EntityId source, EntityId renderEntity)
    {
        _instanceEntities.Remove(source);
        if (renderEntity == EntityId.Null || !_renderWorld.World.IsAlive(renderEntity))
            return false;

        if (_renderWorld.World.TryRead(renderEntity, out RenderInstance instance))
        {
            _freeIndices.Add(instance.InstanceIndex);
            _renderWorld.ClearInstanceSlot(instance.InstanceIndex);
            _renderWorld.World.Remove<RenderInstance>(renderEntity);
            _denseSourceSlotShapeVersion = 0;
            _denseSourceSlots = false;
        }

        if (_renderWorld.World.IsAlive(renderEntity))
            _renderWorld.World.DestroyEntity(renderEntity);

        if (_renderWorld.InstanceCount > 0)
            _renderWorld.InstanceCount--;

        return true;
    }

    private bool AddSources(World sourceWorld, uint current)
    {
        bool changed = false;
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        changed |= AddSources(sourceWorld, _unlinkedSources, current);
        LinkSources(sourceWorld);
        return changed;
    }

    private bool AddSources(World sourceWorld, QueryHandle query, uint current)
    {
        bool changed = false;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(query, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId source = chunk.GetEntity(row);
                if (_instanceEntities.ContainsKey(source))
                    continue;

                WorldTransform transform = chunk.Read<WorldTransform>(row);
                MeshInstance mesh = chunk.Read<MeshInstance>(row);
                bool hasBindings = chunk.TryRead(row, out MeshMaterialBindings bindings);
                bool hasOverrides = chunk.TryRead(row, out MaterialOverride materialOverride);
                Handle<Material>[] materials = hasBindings
                    ? bindings.Materials.ToArray()
                    : [];
                RenderInstance instance = new()
                {
                    SourceEntity = source,
                    InstanceIndex = AllocateIndex(),
                    Transform = GpuTransform.FromQvvs(transform.Qvvs),
                    PrevTransform = GpuTransform.FromQvvs(transform.Qvvs),
                    Mesh = mesh.Mesh,
                    DataFlags = hasOverrides ? InstanceFlags.MaterialOverride : InstanceFlags.None,
                    BoundsExpansion = MathF.Max(0f, mesh.BoundsExpansion),
                };

                EntityId renderEntity = _renderWorld.World.CreateEntity();
                _renderWorld.World.Add(renderEntity, new RenderSourceEntity { SourceEntity = source });
                _renderWorld.World.Add(renderEntity, new RenderMaterials { Materials = materials });
                InstanceMarks.Write(_renderWorld.World, renderEntity, instance, InstanceDirtyFlags.All);
                if (hasOverrides)
                    _renderWorld.World.Add(renderEntity, materialOverride);
                _renderWorld.StoreInstance(renderEntity, in instance, hasOverrides, in materialOverride);
                _instanceEntities[source] = renderEntity;
                _linkSources.Add(source);
                _linkTargets.Add(renderEntity);
                _linkIndices.Add(instance.InstanceIndex);
                _renderWorld.InstanceCount++;
                _denseSourceSlotShapeVersion = 0;
                _denseSourceSlots = false;
                changed = true;
            }
        }

        return changed;
    }

    private bool UpdateChangedInstances(World sourceWorld, uint current, out bool shapeChanged)
    {
        bool changed = false;
        shapeChanged = false;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_changedTransforms, _lastSourceVersion, current).Chunks)
        {
            bool overrideChunkChanged = chunk.Has<MaterialOverride>()
                && (int)(chunk.GetChangeVersion<MaterialOverride>() - _lastSourceVersion) > 0;
            changed |= UpdateInstanceChunk(
                chunk,
                transformChunkChanged: true,
                overrideChunkChanged,
                out bool chunkShapeChanged);
            shapeChanged |= chunkShapeChanged;
        }

        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_changedOverrides, _lastSourceVersion, current).Chunks)
        {
            if ((int)(chunk.GetChangeVersion<WorldTransform>() - _lastSourceVersion) > 0)
                continue;

            changed |= UpdateInstanceChunk(
                chunk,
                transformChunkChanged: false,
                overrideChunkChanged: true,
                out bool chunkShapeChanged);
            shapeChanged |= chunkShapeChanged;
        }

        return changed;
    }

    private bool UpdateInstanceChunk(
        QueryChunkView chunk,
        bool transformChunkChanged,
        bool overrideChunkChanged,
        out bool shapeChanged)
    {
        bool changed = false;
        shapeChanged = false;
        World renderWorld = _renderWorld.World;
        ReadOnlySpan<RenderSourceLink> links = chunk.Read<RenderSourceLink>();
        ReadOnlySpan<WorldTransform> transforms = transformChunkChanged
            ? chunk.Read<WorldTransform>()
            : default;
        ReadOnlySpan<MaterialOverride> overrides = overrideChunkChanged
            ? chunk.Read<MaterialOverride>()
            : default;
        ReadOnlySpan<uint> transformVersions = transformChunkChanged
            ? chunk.ReadWriteVersions<WorldTransform>()
            : default;
        ReadOnlySpan<uint> overrideVersions = overrideChunkChanged
            ? chunk.ReadWriteVersions<MaterialOverride>()
            : default;
        ReadOnlySpan<EntityId> slotEntities = _renderWorld.InstanceSlotEntities;
        ReadOnlySpan<RenderInstance> slotInstances = _renderWorld.InstanceSlots;
        ReadOnlySpan<MaterialOverride> slotOverrides = overrideChunkChanged
            ? _renderWorld.InstanceSlotOverrides
            : default;
        ReadOnlySpan<bool> slotHasOverrides = overrideChunkChanged
            ? _renderWorld.InstanceSlotHasOverride
            : default;
        ReadOnlySpan<bool> slotActive = _renderWorld.InstanceSlotActive;
        bool canUseFullRange = links.Length == _renderWorld.InstanceCount
            && links.Length > 0
            && slotEntities.Length >= links.Length
            && slotInstances.Length >= links.Length
            && (!overrideChunkChanged || slotOverrides.Length >= links.Length)
            && (!overrideChunkChanged || slotHasOverrides.Length >= links.Length)
            && slotActive.Length >= links.Length;
        InstanceDirtyFlags fullRangeDirty = InstanceDirtyFlags.None;
        if (transformChunkChanged)
            fullRangeDirty |= InstanceDirtyFlags.Transform;
        if (overrideChunkChanged)
            fullRangeDirty |= InstanceDirtyFlags.Data;
        using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.FullRangeCheck"))
        {
            if (canUseFullRange)
            {
                bool denseSourceSlots = _denseSourceSlotShapeVersion == _instanceShapeVersion
                    && _denseSourceSlots;
                if (_denseSourceSlotShapeVersion != _instanceShapeVersion)
                {
                    denseSourceSlots = true;
                    for (int i = 0; i < links.Length; i++)
                    {
                        RenderSourceLink link = links[i];
                        if (link.InstanceIndex != i
                            || link.RenderEntity == EntityId.Null
                            || !slotActive[i]
                            || slotEntities[i] != link.RenderEntity
                            || slotInstances[i].InstanceIndex != i)
                        {
                            denseSourceSlots = false;
                            break;
                        }
                    }

                    _denseSourceSlots = denseSourceSlots;
                    _denseSourceSlotShapeVersion = _instanceShapeVersion;
                }

                if (denseSourceSlots && overrideChunkChanged)
                    denseSourceSlots = _renderWorld.AllActiveSlotsHaveOverride;

                canUseFullRange = denseSourceSlots;
            }
        }

        if (canUseFullRange)
        {
            int changedRowCount = 0;
            using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.FullRangeApply"))
            {
                for (int i = 0; i < links.Length; i++)
                {
                    RenderSourceLink link = links[i];
                    EntityId renderEntity = link.RenderEntity;
                    RenderInstance instance = slotInstances[i];
                    bool transformChanged = transformChunkChanged
                        && (int)(transformVersions[i] - _lastSourceVersion) > 0;
                    bool overrideChanged = overrideChunkChanged
                        && (int)(overrideVersions[i] - _lastSourceVersion) > 0;
                    if (!transformChanged && !overrideChanged)
                        continue;

                    changedRowCount++;
                    InstanceDirtyFlags dirty = InstanceDirtyFlags.None;
                    if (transformChanged)
                    {
                        GpuTransform transform = GpuTransform.FromQvvs(transforms[i].Qvvs);
                        instance.PrevTransform = instance.Transform;
                        instance.Transform = transform;
                        dirty |= InstanceDirtyFlags.Transform;
                        changed = true;
                    }

                    bool hasOverrideAfter = false;
                    MaterialOverride updateOverride = default;
                    if (overrideChanged)
                    {
                        hasOverrideAfter = slotHasOverrides[i];
                        updateOverride = slotOverrides[i];
                        MaterialOverride next = overrides[i];
                        bool hadOverride = hasOverrideAfter;
                        dirty |= InstanceDirtyFlags.Data;
                        if ((instance.DataFlags & InstanceFlags.MaterialOverride) == 0)
                        {
                            instance.DataFlags |= InstanceFlags.MaterialOverride;
                        }

                        if (!hadOverride && !renderWorld.Has<MaterialOverride>(renderEntity))
                            renderWorld.Add(renderEntity, next);
                        updateOverride = next;
                        hasOverrideAfter = true;
                        changed = true;
                        shapeChanged |= !hadOverride;
                    }

                    _renderWorld.UpdateInstanceSlot(i, in instance, hasOverrideAfter, in updateOverride, dirty);
                }
            }

            using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.FullRangeUpdates"))
            {
                if (changedRowCount == links.Length)
                {
                    _renderWorld.AddInstanceUpdatesForAllSlots(
                        fullRangeDirty,
                        allDataHaveOverride: true);
                }
                else
                {
                    for (int i = 0; i < links.Length; i++)
                    {
                        InstanceDirtyFlags dirty = InstanceDirtyFlags.None;
                        if (transformChunkChanged
                            && (int)(transformVersions[i] - _lastSourceVersion) > 0)
                        {
                            dirty |= InstanceDirtyFlags.Transform;
                        }

                        if (overrideChunkChanged
                            && (int)(overrideVersions[i] - _lastSourceVersion) > 0)
                        {
                            dirty |= InstanceDirtyFlags.Data;
                        }

                        if (dirty == InstanceDirtyFlags.None)
                            continue;

                        _renderWorld.AddInstanceUpdate(
                            links[i].RenderEntity,
                            i,
                            dirty);
                    }
                }
            }
            return changed;
        }

        using (Profiler.BeginScope("RenderWorldExtractor.UpdateInstanceChunk.SparseApply"))
        {
            for (int i = 0; i < links.Length; i++)
            {
                bool transformChanged = transformChunkChanged
                    && (int)(transformVersions[i] - _lastSourceVersion) > 0;
                bool overrideChanged = overrideChunkChanged
                    && (int)(overrideVersions[i] - _lastSourceVersion) > 0;
                if (!transformChanged && !overrideChanged)
                    continue;

                RenderSourceLink link = links[i];
                EntityId renderEntity = link.RenderEntity;
                int instanceIndex = link.InstanceIndex;
                if (renderEntity == EntityId.Null)
                {
                    continue;
                }

                InstanceDirtyFlags dirty = InstanceDirtyFlags.None;
                if (!_renderWorld.TryGetInstance(
                    instanceIndex,
                    out EntityId fullSlotEntity,
                    out RenderInstance fullInstance,
                    out bool hasOverrideAfter,
                    out MaterialOverride updateOverride)
                    || fullSlotEntity != renderEntity)
                {
                    continue;
                }

                if (transformChanged)
                {
                    GpuTransform transform = GpuTransform.FromQvvs(transforms[i].Qvvs);
                    fullInstance.PrevTransform = fullInstance.Transform;
                    fullInstance.Transform = transform;
                    dirty |= InstanceDirtyFlags.Transform;
                    changed = true;
                }

                if (overrideChanged)
                {
                    MaterialOverride next = overrides[i];
                    bool hadOverride = hasOverrideAfter;
                    dirty |= hadOverride
                        ? InstanceDirtyFlags.Data
                        : InstanceDirtyFlags.Header | InstanceDirtyFlags.Data;
                    if ((fullInstance.DataFlags & InstanceFlags.MaterialOverride) == 0)
                    {
                        fullInstance.DataFlags |= InstanceFlags.MaterialOverride;
                        dirty |= InstanceDirtyFlags.Header;
                    }

                    if (!hadOverride && !renderWorld.Has<MaterialOverride>(renderEntity))
                        renderWorld.Add(renderEntity, next);
                    updateOverride = next;
                    hasOverrideAfter = true;
                    changed = true;
                    shapeChanged |= !hadOverride;
                }

                if (dirty != InstanceDirtyFlags.None)
                {
                    _renderWorld.UpdateInstanceSlot(
                        fullInstance.InstanceIndex,
                        in fullInstance,
                        hasOverrideAfter,
                        in updateOverride,
                        dirty);
                    _renderWorld.AddInstanceUpdate(
                        renderEntity,
                        fullInstance.InstanceIndex,
                        dirty);
                }
            }
        }

        return changed;
    }

    private bool UpdateMeshes(World sourceWorld, uint current)
    {
        bool changed = false;
        World renderWorld = _renderWorld.World;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_changedMesh, _lastSourceVersion, current).Chunks)
        {
            ReadOnlySpan<uint> meshVersions = chunk.ReadWriteVersions<MeshInstance>();
            ReadOnlySpan<MeshInstance> meshes = chunk.Read<MeshInstance>();
            ReadOnlySpan<RenderSourceLink> links = chunk.Read<RenderSourceLink>();
            for (int i = 0; i < links.Length; i++)
            {
                if ((int)(meshVersions[i] - _lastSourceVersion) <= 0)
                    continue;

                RenderSourceLink link = links[i];
                EntityId renderEntity = link.RenderEntity;
                if (renderEntity == EntityId.Null
                    || !renderWorld.IsAlive(renderEntity)
                    || !_renderWorld.TryGetInstance(
                        link.InstanceIndex,
                        out EntityId slotEntity,
                        out RenderInstance currentInstance,
                        out bool hasOverride,
                        out MaterialOverride materialOverride)
                    || slotEntity != renderEntity)
                {
                    continue;
                }

                MeshInstance mesh = meshes[i];
                InstanceDirtyFlags dirty = InstanceDirtyFlags.None;
                bool meshChanged = currentInstance.Mesh != mesh.Mesh;
                if (meshChanged)
                {
                    dirty |= InstanceDirtyFlags.Header | InstanceDirtyFlags.MaterialHeader;
                }

                float bounds = MathF.Max(0f, mesh.BoundsExpansion);
                bool boundsChanged = currentInstance.BoundsExpansion != bounds;
                if (boundsChanged)
                {
                    dirty |= InstanceDirtyFlags.Header | InstanceDirtyFlags.MaterialHeader;
                }

                if (dirty != InstanceDirtyFlags.None)
                {
                    ref RenderInstance instance = ref renderWorld.Get<RenderInstance>(renderEntity);
                    if (meshChanged)
                        instance.Mesh = mesh.Mesh;
                    if (boundsChanged)
                        instance.BoundsExpansion = bounds;
                    _renderWorld.StoreInstance(renderEntity, in instance, hasOverride, in materialOverride);
                    InstanceMarks.Mark(renderWorld, renderEntity, dirty);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool UpdateBindings(World sourceWorld, uint current)
    {
        bool changed = false;
        World renderWorld = _renderWorld.World;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_changedBindings, _lastSourceVersion, current).Chunks)
        {
            ReadOnlySpan<uint> bindingVersions = chunk.ReadWriteVersions<MeshMaterialBindings>();
            ReadOnlySpan<MeshMaterialBindings> bindings = chunk.Read<MeshMaterialBindings>();
            ReadOnlySpan<RenderSourceLink> links = chunk.Read<RenderSourceLink>();
            for (int i = 0; i < links.Length; i++)
            {
                if ((int)(bindingVersions[i] - _lastSourceVersion) <= 0)
                    continue;

                EntityId renderEntity = links[i].RenderEntity;
                if (renderEntity == EntityId.Null
                    || !renderWorld.IsAlive(renderEntity)
                    || !renderWorld.Has<RenderInstance>(renderEntity))
                {
                    continue;
                }

                ReadOnlySpan<Handle<Material>> incoming = bindings[i].Materials.Span;
                bool hasMaterials = renderWorld.Has<RenderMaterials>(renderEntity);
                if (hasMaterials
                    && renderWorld.ReadRef<RenderMaterials>(renderEntity).Materials.Span.SequenceEqual(incoming))
                {
                    continue;
                }

                Handle<Material>[] materials = incoming.ToArray();
                if (hasMaterials)
                    renderWorld.Get<RenderMaterials>(renderEntity).Materials = materials;
                else
                    renderWorld.Add(renderEntity, new RenderMaterials { Materials = materials });
                InstanceMarks.Mark(renderWorld, renderEntity, InstanceDirtyFlags.MaterialHeader);
                changed = true;
            }
        }

        return changed;
    }

    private bool RemoveBindings(World sourceWorld, uint current)
    {
        bool changed = false;
        World renderWorld = _renderWorld.World;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_removedBindings, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId renderEntity;
                EntityId source = chunk.GetEntity(row);
                if (chunk.TryRead(row, out RenderSourceLink link))
                {
                    renderEntity = link.RenderEntity;
                }
                else if (!_instanceEntities.TryGetValue(source, out renderEntity))
                {
                    continue;
                }

                if (renderEntity == EntityId.Null
                    || !renderWorld.IsAlive(renderEntity)
                    || !renderWorld.Has<RenderInstance>(renderEntity))
                {
                    continue;
                }

                if (renderWorld.Has<RenderMaterials>(renderEntity))
                    renderWorld.Get<RenderMaterials>(renderEntity).Materials = ReadOnlyMemory<Handle<Material>>.Empty;
                else
                    renderWorld.Add(renderEntity, new RenderMaterials { Materials = ReadOnlyMemory<Handle<Material>>.Empty });
                InstanceMarks.Mark(renderWorld, renderEntity, InstanceDirtyFlags.MaterialHeader);
                changed = true;
            }
        }

        sourceWorld.ClearRemoved<MeshMaterialBindings>(current);
        return changed;
    }

    private bool RemoveOverrides(World sourceWorld, uint current, out bool shapeChanged)
    {
        bool changed = false;
        shapeChanged = false;
        World renderWorld = _renderWorld.World;
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_removedOverride, _lastSourceVersion, current).Chunks)
        {
            foreach (int row in chunk.RowIndices)
            {
                EntityId renderEntity;
                int instanceIndex;
                EntityId source = chunk.GetEntity(row);
                if (chunk.TryRead(row, out RenderSourceLink link))
                {
                    renderEntity = link.RenderEntity;
                    instanceIndex = link.InstanceIndex;
                }
                else if (!_instanceEntities.TryGetValue(source, out renderEntity))
                {
                    continue;
                }
                else if (!_renderWorld.World.TryRead(renderEntity, out RenderInstance staleInstance))
                {
                    continue;
                }
                else
                {
                    instanceIndex = staleInstance.InstanceIndex;
                }

                if (renderEntity == EntityId.Null
                    || !renderWorld.IsAlive(renderEntity)
                    || !_renderWorld.TryGetInstance(
                        instanceIndex,
                        out EntityId slotEntity,
                        out RenderInstance currentInstance,
                        out bool hasOverride,
                        out _)
                    || slotEntity != renderEntity)
                {
                    continue;
                }

                bool hasOverrideComponent = renderWorld.Has<MaterialOverride>(renderEntity);
                bool hadOverride = hasOverride
                    || hasOverrideComponent
                    || (currentInstance.DataFlags & InstanceFlags.MaterialOverride) != 0;
                if (!hadOverride && currentInstance.DataOffset == 0)
                    continue;

                if (hasOverrideComponent)
                    renderWorld.Remove<MaterialOverride>(renderEntity);
                ref RenderInstance instance = ref renderWorld.Get<RenderInstance>(renderEntity);
                instance.DataFlags &= ~InstanceFlags.MaterialOverride;
                instance.DataOffset = 0;
                currentInstance.DataFlags &= ~InstanceFlags.MaterialOverride;
                currentInstance.DataOffset = 0;
                _renderWorld.StoreInstance(renderEntity, in currentInstance, hasOverride: false, materialOverride: default);
                InstanceMarks.Mark(renderWorld, renderEntity, InstanceDirtyFlags.Header | InstanceDirtyFlags.Data);
                changed = true;
                shapeChanged |= hadOverride;
            }
        }

        sourceWorld.ClearRemoved<MaterialOverride>(current);
        return changed;
    }

    private void CapturePrevious()
    {
        _previous.Clear();
        ReadOnlySpan<RenderInstance> instances = _renderWorld.InstanceSlots;
        ReadOnlySpan<bool> active = _renderWorld.InstanceSlotActive;
        for (int i = 0; i < instances.Length; i++)
        {
            if (active[i])
                _previous[instances[i].SourceEntity] = instances[i];
        }
    }

    private void IndexInstances()
    {
        _instanceEntities.Clear();
        ReadOnlySpan<EntityId> entities = _renderWorld.InstanceSlotEntities;
        ReadOnlySpan<RenderInstance> instances = _renderWorld.InstanceSlots;
        ReadOnlySpan<bool> active = _renderWorld.InstanceSlotActive;
        for (int i = 0; i < instances.Length; i++)
        {
            if (active[i])
                _instanceEntities[instances[i].SourceEntity] = entities[i];
        }
    }

    private void EnsureQueries(World sourceWorld)
    {
        if (ReferenceEquals(_sourceWorld, sourceWorld))
            return;

        _sourceWorld = sourceWorld;
        _sourceQuery = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<MeshInstance>()
                .Optional<MeshMaterialBindings>(QueryAccess.Read)
                .Optional<MaterialOverride>(QueryAccess.Read));
        _unlinkedSources = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<MeshInstance>()
                .None<RenderSourceLink>()
                .Optional<MeshMaterialBindings>(QueryAccess.Read)
                .Optional<MaterialOverride>(QueryAccess.Read));
        _changedTransforms = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<RenderSourceLink>()
                .Optional<MaterialOverride>(QueryAccess.Read)
                .ChunkChanged<WorldTransform>());
        _changedOverrides = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<WorldTransform>()
                .Read<RenderSourceLink>()
                .Read<MaterialOverride>()
                .ChunkChanged<MaterialOverride>());
        _changedMesh = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<MeshInstance>()
                .Read<RenderSourceLink>()
                .ChunkChanged<MeshInstance>());
        _lostMesh = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<RenderSourceLink>()
                .None<MeshInstance>());
        _lostTransform = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<RenderSourceLink>()
                .None<WorldTransform>());
        _removedMesh = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<MeshInstance>());
        _removedTransform = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<WorldTransform>());
        _changedBindings = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<MeshMaterialBindings>()
                .Read<RenderSourceLink>()
                .ChunkChanged<MeshMaterialBindings>());
        _removedBindings = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<MeshMaterialBindings>());
        _removedOverride = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Optional<RenderSourceLink>(QueryAccess.Read)
                .Removed<MaterialOverride>());
        _sceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<SceneLights>());
        _addedSceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<SceneLights>()
                .Added<SceneLights>());
        _changedSceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Read<SceneLights>()
                .Changed<SceneLights>());
        _removedSceneLights = sourceWorld.Query(
            new QueryDefinitionBuilder()
                .Removed<SceneLights>());
        _lastSourceVersion = 0;
        _built = false;
        _nextIndex = 0;
        _denseSourceSlotShapeVersion = 0;
        _denseSourceSlots = false;
        _freeIndices.Clear();
        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
        _removedSources.Clear();
    }

    private int AllocateIndex()
    {
        int last = _freeIndices.Count - 1;
        if (last >= 0)
        {
            int index = _freeIndices[last];
            _freeIndices.RemoveAt(last);
            return index;
        }

        return _nextIndex++;
    }

    private bool CollectLights(World sourceWorld)
        => CollectLights(sourceWorld, out _);

    private bool ApplyLights(World sourceWorld, uint current, out bool shapeChanged)
    {
        if (!HasRows(sourceWorld, _addedSceneLights, current)
            && !HasRows(sourceWorld, _changedSceneLights, current)
            && !HasRows(sourceWorld, _removedSceneLights, current))
        {
            shapeChanged = false;
            return false;
        }

        bool changed = CollectLights(sourceWorld, out shapeChanged);
        sourceWorld.ClearRemoved<SceneLights>(current);
        return changed;
    }

    private bool HasRows(World sourceWorld, QueryHandle query, uint current)
    {
        foreach (QueryChunkView chunk in sourceWorld.RunQuery(query, _lastSourceVersion, current).Chunks)
        {
            var rows = chunk.RowIndices;
            if (rows.MoveNext(out _))
                return true;
        }

        return false;
    }

    private bool CollectLights(World sourceWorld, out bool shapeChanged)
    {
        _directionalLights.Clear();
        _pointLights.Clear();
        _spotLights.Clear();
        Handle<Texture> lightCookieAtlas = default;
        bool lightCookieAtlasSet = false;

        foreach (QueryChunkView chunk in sourceWorld.RunQuery(_sceneLights).Chunks)
        {
            ReadOnlySpan<SceneLights> lights = chunk.Read<SceneLights>();
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].LightCookieAtlas.IsValid)
                {
                    if (lightCookieAtlasSet && lights[i].LightCookieAtlas != lightCookieAtlas)
                    {
                        throw new InvalidOperationException(
                            "RenderWorld extraction supports one light cookie atlas per scene.");
                    }

                    lightCookieAtlas = lights[i].LightCookieAtlas;
                    lightCookieAtlasSet = true;
                }

                ReadOnlySpan<DirectionalLight> directionalLights = lights[i].DirectionalLights.Span;
                for (int lightIndex = 0; lightIndex < directionalLights.Length; lightIndex++)
                    _directionalLights.Add(directionalLights[lightIndex]);

                ReadOnlySpan<PointLight> pointLights = lights[i].PointLights.Span;
                for (int lightIndex = 0; lightIndex < pointLights.Length; lightIndex++)
                    _pointLights.Add(pointLights[lightIndex]);

                ReadOnlySpan<SpotLight> spotLights = lights[i].SpotLights.Span;
                for (int lightIndex = 0; lightIndex < spotLights.Length; lightIndex++)
                    _spotLights.Add(spotLights[lightIndex]);
            }
        }

        shapeChanged =
            _renderWorld.SceneLights.DirectionalLights.Length != _directionalLights.Count
            || _renderWorld.SceneLights.PointLights.Length != _pointLights.Count
            || _renderWorld.SceneLights.SpotLights.Length != _spotLights.Count
            || _renderWorld.SceneLights.LightCookieAtlas != lightCookieAtlas;

        if (AreLightsEqual(_renderWorld.SceneLights, _directionalLights, _pointLights, _spotLights, lightCookieAtlas))
        {
            return false;
        }

        _renderWorld.SceneLights = _directionalLights.Count == 0
            && _pointLights.Count == 0
            && _spotLights.Count == 0
            && !lightCookieAtlas.IsValid
                ? default
                : new SceneLights(
                    _directionalLights.ToArray(),
                    _pointLights.ToArray(),
                    _spotLights.ToArray(),
                    lightCookieAtlas);
        return true;
    }

    private void TouchShape()
    {
        _shapeVersion++;
        if (_shapeVersion == 0)
            _shapeVersion = 1;
        _renderWorld.ShapeVersion = _shapeVersion;
    }

    private static bool AreLightsEqual(
        in SceneLights current,
        List<DirectionalLight> directionalLights,
        List<PointLight> pointLights,
        List<SpotLight> spotLights,
        Handle<Texture> lightCookieAtlas)
        => AreLightsEqual(current.DirectionalLights.Span, directionalLights)
            && AreLightsEqual(current.PointLights.Span, pointLights)
            && AreLightsEqual(current.SpotLights.Span, spotLights)
            && current.LightCookieAtlas == lightCookieAtlas;

    private static bool AreLightsEqual(ReadOnlySpan<DirectionalLight> current, List<DirectionalLight> next)
    {
        if (current.Length != next.Count)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            DirectionalLight left = current[i];
            DirectionalLight right = next[i];
            if (left.Direction != right.Direction
                || left.Color != right.Color
                || left.Intensity != right.Intensity
                || left.LayerMask != right.LayerMask
                || left.CookieIndex != right.CookieIndex
                || left.CookieStrength != right.CookieStrength
                || left.CookieScaleOffset != right.CookieScaleOffset
                || left.WorldToLightCookie != right.WorldToLightCookie)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreLightsEqual(ReadOnlySpan<PointLight> current, List<PointLight> next)
    {
        if (current.Length != next.Count)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            PointLight left = current[i];
            PointLight right = next[i];
            if (left.Position != right.Position
                || left.Range != right.Range
                || left.Color != right.Color
                || left.Intensity != right.Intensity
                || left.LayerMask != right.LayerMask
                || left.CookieIndex != right.CookieIndex
                || left.CookieStrength != right.CookieStrength
                || left.CookieScaleOffset != right.CookieScaleOffset
                || left.WorldToLightCookie != right.WorldToLightCookie)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreLightsEqual(ReadOnlySpan<SpotLight> current, List<SpotLight> next)
    {
        if (current.Length != next.Count)
            return false;

        for (int i = 0; i < current.Length; i++)
        {
            SpotLight left = current[i];
            SpotLight right = next[i];
            if (left.Position != right.Position
                || left.Range != right.Range
                || left.Direction != right.Direction
                || left.InnerConeCos != right.InnerConeCos
                || left.Color != right.Color
                || left.Intensity != right.Intensity
                || left.OuterConeCos != right.OuterConeCos
                || left.LayerMask != right.LayerMask
                || left.CookieIndex != right.CookieIndex
                || left.CookieStrength != right.CookieStrength
                || left.CookieScaleOffset != right.CookieScaleOffset
                || left.WorldToLightCookie != right.WorldToLightCookie)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameTransform(in GpuTransform left, in GpuTransform right)
        => left.Rotation == right.Rotation
            && left.Position == right.Position
            && left.Scale == right.Scale
            && left.Stretch == right.Stretch
            && left.Padding == right.Padding;

    private void LinkSources(World sourceWorld)
    {
        for (int i = 0; i < _linkSources.Count; i++)
        {
            EntityId source = _linkSources[i];
            EntityId renderEntity = _linkTargets[i];
            int instanceIndex = _linkIndices[i];
            if (sourceWorld.IsAlive(source))
                sourceWorld.AddOrSet(
                    source,
                    new RenderSourceLink
                    {
                        RenderEntity = renderEntity,
                        InstanceIndex = instanceIndex,
                    });
        }

        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
    }

    private void ClearLinks(World sourceWorld)
    {
        for (int i = 0; i < _linkSources.Count; i++)
        {
            EntityId source = _linkSources[i];
            if (sourceWorld.IsAlive(source) && sourceWorld.Has<RenderSourceLink>(source))
                sourceWorld.Remove<RenderSourceLink>(source);
        }

        _linkSources.Clear();
        _linkTargets.Clear();
        _linkIndices.Clear();
    }
}
