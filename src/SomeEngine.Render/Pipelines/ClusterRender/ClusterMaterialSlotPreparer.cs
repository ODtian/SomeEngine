using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

internal sealed class ClusterMaterialSlotPreparer(BinSpace binSpace, EntityStore renderWorldStore, InstanceDataManager instanceDataManager)
{
    private static readonly IComparer<Entity> s_renderEntityComparer =
        Comparer<Entity>.Create(CompareRenderEntities);

    private readonly BinSpace _binSpace = binSpace;
    private readonly EntityStore _renderWorldStore = renderWorldStore;
    private readonly InstanceDataManager _instanceDataManager = instanceDataManager;

    private Entity[] _renderEntities = [];
    private int _renderEntityCount;

    private MaterialSlotBinding[] _slotBindings = [];
    private Entity[][] _slotFieldEntities = [];
    private Entity[] _slotRepresentatives = [];

    private int[] _activeInstanceIndices = [];
    private int[] _activeInstanceOffsets = [];
    private ulong[] _activeInstanceHashes = [];
    private float[] _activeInstanceBoundsExpansions = [];
    private bool[] _activeInstanceMatched = [];
    private int _activeInstanceCount;

    private int[] _nextInstanceIndices = [];
    private int[] _nextInstanceOffsets = [];
    private ulong[] _nextInstanceHashes = [];
    private float[] _nextInstanceBoundsExpansions = [];

    private uint _lastRenderWorldVersion = uint.MaxValue;

    public bool Prepare(
        int rasterFieldIndex,
        int shadingFieldIndex,
        int vertexEvalFieldIndex,
        uint renderWorldVersion)
    {
        bool renderWorldChanged = _lastRenderWorldVersion != renderWorldVersion;
        if (!renderWorldChanged)
        {
            WriteCachedInstanceHeaders();
            return false;
        }

        GatherRenderEntities();
        Array.Clear(_activeInstanceMatched, 0, _activeInstanceCount);

        bool slotsChanged = true;
        int renderIndex = 0;
        int nextCount = 0;

        while (renderIndex < _renderEntityCount)
        {
            Entity firstEntity = _renderEntities[renderIndex];
            if (!TryGetMaterialSlot(firstEntity, out RenderMaterialSlotBinding firstBinding))
            {
                renderIndex++;
                continue;
            }

            int instanceIndex = firstBinding.InstanceIndex;
            float authoredBoundsExpansion = 0f;
            float deformBoundsExpansion = 0f;
            int slotCount = 0;

            int groupStart = renderIndex;
            int groupEnd = renderIndex;
            while (groupEnd < _renderEntityCount
                && TryGetMaterialSlot(_renderEntities[groupEnd], out RenderMaterialSlotBinding binding)
                && binding.InstanceIndex == instanceIndex)
            {
                if (binding.LocalMaterialSlot >= 0)
                {
                    slotCount = Math.Max(slotCount, binding.LocalMaterialSlot + 1);
                    authoredBoundsExpansion = MathF.Max(authoredBoundsExpansion, MathF.Max(0f, binding.BoundsExpansion));
                    deformBoundsExpansion = MathF.Max(deformBoundsExpansion, GetDeformBoundsExpansion(_renderEntities[groupEnd]));
                }

                groupEnd++;
            }

            if (slotCount == 0)
            {
                renderIndex = groupEnd;
                continue;
            }

            EnsureSlotBindingCapacity(slotCount, _binSpace.Stride);
            for (int slot = 0; slot < slotCount; slot++)
            {
                Array.Clear(_slotFieldEntities[slot], 0, _slotFieldEntities[slot].Length);
                _slotRepresentatives[slot] = default;
            }

            for (int i = groupStart; i < groupEnd; i++)
            {
                Entity renderEntity = _renderEntities[i];
                if (!TryGetMaterialSlot(renderEntity, out RenderMaterialSlotBinding binding)
                    || binding.LocalMaterialSlot < 0)
                {
                    continue;
                }

                Entity[] fieldEntities = _slotFieldEntities[binding.LocalMaterialSlot];
                if (_slotRepresentatives[binding.LocalMaterialSlot].IsNull)
                {
                    _slotRepresentatives[binding.LocalMaterialSlot] = renderEntity;
                }

                AssignFieldEntity(
                    renderEntity,
                    fieldEntities,
                    rasterFieldIndex,
                    shadingFieldIndex,
                    vertexEvalFieldIndex);
            }

            for (int slot = 0; slot < slotCount; slot++)
            {
                Entity[] fieldEntities = _slotFieldEntities[slot];
                Entity representative = SelectRepresentative(fieldEntities, _slotRepresentatives[slot], shadingFieldIndex);
                _slotBindings[slot] = new MaterialSlotBinding(representative, fieldEntities);
            }

            renderIndex = groupEnd;

            ReadOnlySpan<MaterialSlotBinding> slotBindings = _slotBindings.AsSpan(0, slotCount);
            ulong hash = MaterialSlotCache.ComputeHash(slotBindings);
            int activeIndex = FindActiveInstance(instanceIndex);

            int offset;
            if (activeIndex >= 0)
            {
                _activeInstanceMatched[activeIndex] = true;
                if (_activeInstanceHashes[activeIndex] == hash)
                {
                    offset = _activeInstanceOffsets[activeIndex];
                }
                else
                {
                    _binSpace.ReleaseSlots(_activeInstanceOffsets[activeIndex]);
                    offset = _binSpace.AllocateSlots(slotBindings);
                    slotsChanged = true;
                }
            }
            else
            {
                offset = _binSpace.AllocateSlots(slotBindings);
                slotsChanged = true;
            }

            float finalBoundsExpansion = MathF.Max(authoredBoundsExpansion, deformBoundsExpansion);
            WritePreparedInstanceHeader(instanceIndex, (uint)offset, finalBoundsExpansion);

            EnsureNextInstanceCapacity(nextCount + 1);
            _nextInstanceIndices[nextCount] = instanceIndex;
            _nextInstanceOffsets[nextCount] = offset;
            _nextInstanceHashes[nextCount] = hash;
            _nextInstanceBoundsExpansions[nextCount] = finalBoundsExpansion;
            nextCount++;
        }

        slotsChanged |= ReleaseUnmatchedInstances();
        SwapInstanceCaches(nextCount);

        _lastRenderWorldVersion = renderWorldVersion;
        return slotsChanged;
    }

    private void WriteCachedInstanceHeaders()
    {
        for (int i = 0; i < _activeInstanceCount; i++)
        {
            WritePreparedInstanceHeader(
                _activeInstanceIndices[i],
                (uint)_activeInstanceOffsets[i],
                _activeInstanceBoundsExpansions[i]);
        }
    }

    private void WritePreparedInstanceHeader(int instanceIndex, uint materialSlotOffset, float boundsExpansion)
    {
        if (instanceIndex < 0 || instanceIndex >= _instanceDataManager.Count)
        {
            return;
        }

        var writer = _instanceDataManager.GetHeaderWriter(instanceIndex);
        writer.SetMaterialSlotOffset(materialSlotOffset);
        writer.SetBoundsExpansionWorld(MathF.Max(0f, boundsExpansion));
    }

    private void GatherRenderEntities()
    {
        int count = 0;

    restart:
        foreach (Entity entity in _renderWorldStore.Entities)
        {
            if (count == _renderEntities.Length)
            {
                Array.Resize(ref _renderEntities, Math.Max(16, _renderEntities.Length * 2));
                count = 0;
                goto restart;
            }

            _renderEntities[count++] = entity;
        }

        _renderEntityCount = count;
        Array.Sort(_renderEntities, 0, _renderEntityCount, s_renderEntityComparer);
    }

    private static int CompareRenderEntities(Entity left, Entity right)
    {
        bool leftHasSlot = TryGetMaterialSlot(left, out RenderMaterialSlotBinding leftBinding);
        bool rightHasSlot = TryGetMaterialSlot(right, out RenderMaterialSlotBinding rightBinding);
        if (leftHasSlot != rightHasSlot)
        {
            return leftHasSlot ? -1 : 1;
        }

        if (!leftHasSlot)
        {
            return left.Id.CompareTo(right.Id);
        }

        int instanceCompare = leftBinding.InstanceIndex.CompareTo(rightBinding.InstanceIndex);
        if (instanceCompare != 0)
        {
            return instanceCompare;
        }

        int slotCompare = leftBinding.LocalMaterialSlot.CompareTo(rightBinding.LocalMaterialSlot);
        if (slotCompare != 0)
        {
            return slotCompare;
        }

        int passCompare = leftBinding.PassIndex.CompareTo(rightBinding.PassIndex);
        return passCompare != 0 ? passCompare : left.Id.CompareTo(right.Id);
    }

    private void EnsureSlotBindingCapacity(int bindingCount, int stride)
    {
        if (_slotBindings.Length < bindingCount)
        {
            Array.Resize(ref _slotBindings, Math.Max(bindingCount, _slotBindings.Length == 0 ? 8 : _slotBindings.Length * 2));
            Array.Resize(ref _slotFieldEntities, _slotBindings.Length);
            Array.Resize(ref _slotRepresentatives, _slotBindings.Length);
        }

        for (int i = 0; i < _slotFieldEntities.Length; i++)
        {
            if (_slotFieldEntities[i] == null || _slotFieldEntities[i].Length != stride)
            {
                _slotFieldEntities[i] = new Entity[stride];
            }
        }
    }

    private static bool TryGetMaterialSlot(Entity entity, out RenderMaterialSlotBinding binding)
    {
        return entity.TryGetComponent(out binding);
    }

    private static void AssignFieldEntity(
        Entity passEntity,
        Entity[] fieldEntities,
        int rasterFieldIndex,
        int shadingFieldIndex,
        int vertexEvalFieldIndex)
    {
        if (rasterFieldIndex >= 0
            && rasterFieldIndex < fieldEntities.Length
            && fieldEntities[rasterFieldIndex].IsNull
            && passEntity.TryGetComponent<ClusterRaster>(out _))
        {
            fieldEntities[rasterFieldIndex] = passEntity;
        }

        if (shadingFieldIndex >= 0
            && shadingFieldIndex < fieldEntities.Length
            && fieldEntities[shadingFieldIndex].IsNull
            && passEntity.TryGetComponent<ClusterShadeComponent>(out _))
        {
            fieldEntities[shadingFieldIndex] = passEntity;
        }

        if (vertexEvalFieldIndex >= 0
            && vertexEvalFieldIndex < fieldEntities.Length
            && fieldEntities[vertexEvalFieldIndex].IsNull
            && passEntity.TryGetComponent<ClusterDeform>(out _))
        {
            fieldEntities[vertexEvalFieldIndex] = passEntity;
        }
    }

    private static float GetDeformBoundsExpansion(Entity passEntity)
    {
        return passEntity.TryGetComponent<ClusterDeform>(out ClusterDeform deform)
            ? MathF.Max(0f, deform.BoundsExpansion)
            : 0f;
    }

    private static Entity SelectRepresentative(Entity[] fieldEntities, Entity fallback, int shadingFieldIndex)
    {
        if (shadingFieldIndex >= 0
            && shadingFieldIndex < fieldEntities.Length
            && !fieldEntities[shadingFieldIndex].IsNull)
        {
            return fieldEntities[shadingFieldIndex];
        }

        for (int i = 0; i < fieldEntities.Length; i++)
        {
            if (!fieldEntities[i].IsNull)
            {
                return fieldEntities[i];
            }
        }

        return fallback;
    }

    private int FindActiveInstance(int instanceIndex)
    {
        for (int i = 0; i < _activeInstanceCount; i++)
        {
            if (_activeInstanceIndices[i] == instanceIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private bool ReleaseUnmatchedInstances()
    {
        bool released = false;
        for (int i = 0; i < _activeInstanceCount; i++)
        {
            if (!_activeInstanceMatched[i])
            {
                _binSpace.ReleaseSlots(_activeInstanceOffsets[i]);
                released = true;
            }
        }

        return released;
    }

    private void EnsureNextInstanceCapacity(int count)
    {
        if (_nextInstanceIndices.Length >= count)
        {
            return;
        }

        int newCapacity = Math.Max(count, _nextInstanceIndices.Length == 0 ? 8 : _nextInstanceIndices.Length * 2);
        Array.Resize(ref _nextInstanceIndices, newCapacity);
        Array.Resize(ref _nextInstanceOffsets, newCapacity);
        Array.Resize(ref _nextInstanceHashes, newCapacity);
        Array.Resize(ref _nextInstanceBoundsExpansions, newCapacity);
    }

    private void SwapInstanceCaches(int nextCount)
    {
        (_activeInstanceIndices, _nextInstanceIndices) = (_nextInstanceIndices, _activeInstanceIndices);
        (_activeInstanceOffsets, _nextInstanceOffsets) = (_nextInstanceOffsets, _activeInstanceOffsets);
        (_activeInstanceHashes, _nextInstanceHashes) = (_nextInstanceHashes, _activeInstanceHashes);
        (_activeInstanceBoundsExpansions, _nextInstanceBoundsExpansions) = (
            _nextInstanceBoundsExpansions,
            _activeInstanceBoundsExpansions);

        if (_activeInstanceMatched.Length < _activeInstanceIndices.Length)
        {
            Array.Resize(ref _activeInstanceMatched, _activeInstanceIndices.Length);
        }

        _activeInstanceCount = nextCount;
    }
}
