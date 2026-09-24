using System.Collections.Generic;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Components;
using SomeECS.Core;
using SomeECS.Core.Entities;

namespace SomeEngine.Render.Systems;

public sealed class RenderWorld
{
    private readonly List<EntityId> _resetScratch = [];
    private EntityId[] _instanceUpdateEntities = [];
    private int[] _instanceUpdateIndices = [];
    private InstanceDirtyFlags[] _instanceUpdateFlags = [];
    private int _instanceUpdateCount;
    private InstanceDirtyFlags _instanceUpdateFlagsUnion;
    private bool _instanceUpdateHasData;
    private bool _instanceUpdatesCoverPrefix = true;
    private bool _instanceUpdateDataHasOverride = true;
    private bool _instanceUpdateUniform;
    private InstanceDirtyFlags _instanceUpdateUniformFlags;
    private EntityId[] _instanceSlotEntities = [];
    private RenderInstance[] _instanceSlots = [];
    private MaterialOverride[] _instanceSlotOverrides = [];
    private bool[] _instanceSlotHasOverride = [];
    private bool[] _instanceSlotActive = [];
    private int _instanceSlotCount;
    private int _instanceSlotOverrideCount;

    public World World { get; } = new();
    public SceneLights SceneLights { get; internal set; }
    public uint Version { get; internal set; }
    public uint ShapeVersion { get; internal set; }
    public uint InstanceShapeVersion { get; internal set; }
    public uint MaterialVersion { get; internal set; }
    public uint LightVersion { get; internal set; }
    internal int InstanceCount { get; set; }
    internal int InstanceUpdateCount => _instanceUpdateCount;
    internal ReadOnlySpan<EntityId> InstanceUpdateEntities
    {
        get
        {
            ExpandUniformInstanceUpdates();
            return _instanceUpdateEntities.AsSpan(0, _instanceUpdateCount);
        }
    }
    internal ReadOnlySpan<int> InstanceUpdateIndices
    {
        get
        {
            ExpandUniformInstanceUpdates();
            return _instanceUpdateIndices.AsSpan(0, _instanceUpdateCount);
        }
    }
    internal ReadOnlySpan<InstanceDirtyFlags> InstanceUpdateFlags
    {
        get
        {
            ExpandUniformInstanceUpdates();
            return _instanceUpdateFlags.AsSpan(0, _instanceUpdateCount);
        }
    }
    internal InstanceDirtyFlags InstanceUpdateFlagsUnion => _instanceUpdateFlagsUnion;
    internal bool InstanceUpdateHasData => _instanceUpdateHasData;
    internal bool InstanceUpdateUniform => _instanceUpdateUniform;
    internal InstanceDirtyFlags InstanceUpdateUniformFlags => _instanceUpdateUniformFlags;
    internal bool InstanceUpdatesCoverAllSlots
        => _instanceUpdatesCoverPrefix && _instanceUpdateCount == InstanceCount && InstanceCount > 0;

    internal bool InstanceUpdatesAllDataHaveOverride
        => _instanceUpdateDataHasOverride;

    internal int InstanceSlotCount => _instanceSlotCount;
    internal ReadOnlySpan<EntityId> InstanceSlotEntities => _instanceSlotEntities.AsSpan(0, _instanceSlotCount);
    internal ReadOnlySpan<RenderInstance> InstanceSlots => _instanceSlots.AsSpan(0, _instanceSlotCount);
    internal ReadOnlySpan<MaterialOverride> InstanceSlotOverrides => _instanceSlotOverrides.AsSpan(0, _instanceSlotCount);
    internal ReadOnlySpan<bool> InstanceSlotHasOverride => _instanceSlotHasOverride.AsSpan(0, _instanceSlotCount);
    internal ReadOnlySpan<bool> InstanceSlotActive => _instanceSlotActive.AsSpan(0, _instanceSlotCount);
    internal bool AllActiveSlotsHaveOverride => InstanceCount > 0 && _instanceSlotOverrideCount == InstanceCount;

    public int CountInstances() => InstanceCount;

    internal void ClearInstanceUpdates()
    {
        _instanceUpdateCount = 0;
        _instanceUpdateFlagsUnion = InstanceDirtyFlags.None;
        _instanceUpdateHasData = false;
        _instanceUpdatesCoverPrefix = true;
        _instanceUpdateDataHasOverride = true;
        _instanceUpdateUniform = false;
        _instanceUpdateUniformFlags = InstanceDirtyFlags.None;
    }

    internal void AddInstanceUpdate(
        EntityId entity,
        int index,
        InstanceDirtyFlags flags)
    {
        if (flags == InstanceDirtyFlags.None)
            return;
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), "instance update index must be non-negative.");

        ExpandUniformInstanceUpdates();
        EnsureInstanceUpdates(_instanceUpdateCount + 1);
        bool slotMatches = (uint)index < (uint)_instanceSlotCount
            && _instanceSlotActive[index]
            && _instanceSlotEntities[index] == entity
            && _instanceSlots[index].InstanceIndex == index;
        _instanceUpdatesCoverPrefix &= index == _instanceUpdateCount && slotMatches;
        if ((flags & InstanceDirtyFlags.Data) != 0)
        {
            _instanceUpdateDataHasOverride &= (uint)index < (uint)_instanceSlotHasOverride.Length
                && _instanceSlotHasOverride[index];
        }

        _instanceUpdateEntities[_instanceUpdateCount] = entity;
        _instanceUpdateIndices[_instanceUpdateCount] = index;
        _instanceUpdateFlags[_instanceUpdateCount] = flags;
        _instanceUpdateFlagsUnion |= flags;
        if ((flags & InstanceDirtyFlags.Data) != 0
            && (uint)index < (uint)_instanceSlotHasOverride.Length
            && _instanceSlotHasOverride[index])
        {
            _instanceUpdateHasData = true;
        }

        _instanceUpdateCount++;
    }

    internal void AddInstanceUpdatesForAllSlots(
        InstanceDirtyFlags flags,
        bool allDataHaveOverride)
    {
        if (flags == InstanceDirtyFlags.None || InstanceCount == 0)
            return;

        ExpandUniformInstanceUpdates();
        if (_instanceUpdateCount != 0)
        {
            for (int index = 0; index < InstanceCount; index++)
            {
                if ((uint)index >= (uint)_instanceSlotCount || !_instanceSlotActive[index])
                    continue;

                AddInstanceUpdate(
                    _instanceSlotEntities[index],
                    index,
                    flags);
            }
            return;
        }

        _instanceUpdateUniform = true;
        _instanceUpdateUniformFlags = flags;
        _instanceUpdateCount = InstanceCount;
        _instanceUpdateFlagsUnion = flags;
        _instanceUpdatesCoverPrefix = true;
        _instanceUpdateDataHasOverride = (flags & InstanceDirtyFlags.Data) == 0 || allDataHaveOverride;
        _instanceUpdateHasData = (flags & InstanceDirtyFlags.Data) != 0 && allDataHaveOverride;
    }

    internal void StoreInstance(
        EntityId entity,
        in RenderInstance instance,
        bool hasOverride,
        in MaterialOverride materialOverride)
    {
        int index = instance.InstanceIndex;
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(instance), "instance slot index must be non-negative.");

        EnsureInstanceSlot(index + 1);
        bool wasActive = _instanceSlotActive[index];
        bool hadOverride = wasActive && _instanceSlotHasOverride[index];
        if (hadOverride)
            _instanceSlotOverrideCount--;
        _instanceSlotEntities[index] = entity;
        _instanceSlots[index] = instance;
        _instanceSlotHasOverride[index] = hasOverride;
        _instanceSlotOverrides[index] = hasOverride ? materialOverride : default;
        _instanceSlotActive[index] = true;
        if (hasOverride)
            _instanceSlotOverrideCount++;
        _instanceSlotCount = Math.Max(_instanceSlotCount, index + 1);
    }

    internal void UpdateInstanceSlot(
        int index,
        in RenderInstance instance,
        bool hasOverride,
        in MaterialOverride materialOverride,
        InstanceDirtyFlags dirty = InstanceDirtyFlags.All)
    {
        if ((uint)index >= (uint)_instanceSlotCount || !_instanceSlotActive[index])
            throw new ArgumentOutOfRangeException(nameof(index), "instance slot index must refer to an active slot.");

        _instanceSlots[index] = instance;
        if ((dirty & (InstanceDirtyFlags.Header | InstanceDirtyFlags.Data)) == 0)
            return;

        if (_instanceSlotHasOverride[index])
            _instanceSlotOverrideCount--;
        _instanceSlotHasOverride[index] = hasOverride;
        _instanceSlotOverrides[index] = hasOverride ? materialOverride : default;
        if (hasOverride)
            _instanceSlotOverrideCount++;
    }

    internal bool TryGetInstance(
        int index,
        out EntityId entity,
        out RenderInstance instance,
        out bool hasOverride,
        out MaterialOverride materialOverride)
    {
        if ((uint)index >= (uint)_instanceSlotCount || !_instanceSlotActive[index])
        {
            entity = default;
            instance = default;
            hasOverride = false;
            materialOverride = default;
            return false;
        }

        entity = _instanceSlotEntities[index];
        instance = _instanceSlots[index];
        hasOverride = _instanceSlotHasOverride[index];
        materialOverride = _instanceSlotOverrides[index];
        return true;
    }

    internal void ClearInstanceSlot(int index)
    {
        if ((uint)index >= (uint)_instanceSlotCount)
            return;

        if (_instanceSlotActive[index] && _instanceSlotHasOverride[index])
            _instanceSlotOverrideCount--;
        _instanceSlotEntities[index] = default;
        _instanceSlots[index] = default;
        _instanceSlotOverrides[index] = default;
        _instanceSlotHasOverride[index] = false;
        _instanceSlotActive[index] = false;
    }

    internal void Reset()
    {
        SceneLights = default;
        Version = 0;
        ShapeVersion = 0;
        InstanceShapeVersion = 0;
        MaterialVersion = 0;
        LightVersion = 0;
        InstanceCount = 0;
        ClearInstanceUpdates();
        ClearInstanceSlots();
        var allEntities = World.AllEntities();
        World.CollectEntities(allEntities, _resetScratch);
        foreach (EntityId entity in _resetScratch)
            World.DestroyEntity(entity);
    }

    private void EnsureInstanceUpdates(int count)
    {
        if (_instanceUpdateIndices.Length >= count)
            return;

        int capacity = Math.Max(count, _instanceUpdateIndices.Length == 0 ? 64 : _instanceUpdateIndices.Length * 2);
        Array.Resize(ref _instanceUpdateEntities, capacity);
        Array.Resize(ref _instanceUpdateIndices, capacity);
        Array.Resize(ref _instanceUpdateFlags, capacity);
    }

    private void ExpandUniformInstanceUpdates()
    {
        if (!_instanceUpdateUniform)
            return;

        InstanceDirtyFlags flags = _instanceUpdateUniformFlags;
        int count = _instanceUpdateCount;
        EnsureInstanceUpdates(count);
        for (int index = 0; index < count; index++)
        {
            _instanceUpdateEntities[index] = index < _instanceSlotEntities.Length ? _instanceSlotEntities[index] : default;
            _instanceUpdateIndices[index] = index;
            _instanceUpdateFlags[index] = flags;
        }

        _instanceUpdateUniform = false;
        _instanceUpdateUniformFlags = InstanceDirtyFlags.None;
    }

    private void ClearInstanceSlots()
    {
        Array.Clear(_instanceSlotEntities, 0, _instanceSlotCount);
        Array.Clear(_instanceSlots, 0, _instanceSlotCount);
        Array.Clear(_instanceSlotOverrides, 0, _instanceSlotCount);
        Array.Clear(_instanceSlotHasOverride, 0, _instanceSlotCount);
        Array.Clear(_instanceSlotActive, 0, _instanceSlotCount);
        _instanceSlotCount = 0;
        _instanceSlotOverrideCount = 0;
    }

    private void EnsureInstanceSlot(int count)
    {
        if (_instanceSlots.Length >= count)
            return;

        int capacity = Math.Max(count, _instanceSlots.Length == 0 ? 64 : _instanceSlots.Length * 2);
        Array.Resize(ref _instanceSlotEntities, capacity);
        Array.Resize(ref _instanceSlots, capacity);
        Array.Resize(ref _instanceSlotOverrides, capacity);
        Array.Resize(ref _instanceSlotHasOverride, capacity);
        Array.Resize(ref _instanceSlotActive, capacity);
    }
}
