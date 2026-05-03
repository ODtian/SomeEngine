using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace SomeEngine.Render.Materials;

/// <summary>
/// Bin 级渲染队列。按 orderKey 动态生成有序区间，并在区间内按签名去重分 bin。
/// </summary>
public sealed class BinQueue
{
    public delegate ReadOnlySpan<Entity> QueryEntities();

    public readonly struct BinRange
    {
        public readonly ushort Start;
        public readonly ushort Count;
        public readonly int OrderKey;

        public BinRange(ushort start, ushort count, int orderKey)
        {
            Start = start;
            Count = count;
            OrderKey = orderKey;
        }
    }

    public readonly struct BinGroup
    {
        public required QueryEntities Query { get; init; }
        public required Func<Entity, int> OrderKey { get; init; }
        public required Func<Entity, ulong> SignatureFunc { get; init; }
    }

    private readonly List<BinGroup> _groups = new();
    private Entity[] _entities = [];
    private int _entityCount;
    private BinRange[] _ranges = [];
    private int _rangeCount;
    private ushort[] _argsBinMap = [];
    private int _argsBinCount;
    private readonly Dictionary<int, ushort> _entityBins = [];
    private readonly List<EntitySignatureEntry> _pendingEntries = [];
    private readonly List<Entity> _allEntitiesScratch = [];
    private readonly List<BinRange> _rangesScratch = [];
    private readonly List<ushort> _argsMapScratch = [];
    private readonly Dictionary<ulong, ushort> _primaryBinsBySignature = [];
    private readonly Dictionary<ulong, ushort> _signatureBins = [];

    public int TotalBinCount => _entityCount;

    public void RegisterGroup(BinGroup group)
    {
        _groups.Add(group);
    }

    public void Rebuild()
    {
        _entityBins.Clear();
        _pendingEntries.Clear();

        foreach (var group in _groups)
        {
            ReadOnlySpan<Entity> entities = group.Query();
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                _pendingEntries.Add(new EntitySignatureEntry(entity, group.OrderKey(entity), group.SignatureFunc(entity)));
            }
        }

        _pendingEntries.Sort(static (a, b) =>
        {
            int orderCompare = a.OrderKey.CompareTo(b.OrderKey);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            int signatureCompare = a.Signature.CompareTo(b.Signature);
            return signatureCompare != 0 ? signatureCompare : a.Entity.Id.CompareTo(b.Entity.Id);
        });

        _allEntitiesScratch.Clear();
        _rangesScratch.Clear();
        _primaryBinsBySignature.Clear();
        _argsMapScratch.Clear();
        ushort currentBin = 0;

        int entryIndex = 0;
        while (entryIndex < _pendingEntries.Count)
        {
            int orderKey = _pendingEntries[entryIndex].OrderKey;
            _signatureBins.Clear();
            ushort regionStartBin = currentBin;

            while (entryIndex < _pendingEntries.Count && _pendingEntries[entryIndex].OrderKey == orderKey)
            {
                var entry = _pendingEntries[entryIndex++];
                if (_signatureBins.TryGetValue(entry.Signature, out ushort binIndex))
                {
                    AddOrReplaceEntityBin(entry.Entity.Id, binIndex);
                    continue;
                }

                binIndex = currentBin++;
                _signatureBins[entry.Signature] = binIndex;
                _allEntitiesScratch.Add(entry.Entity);

                ushort argsBin = binIndex;
                if (orderKey == 0)
                {
                    _primaryBinsBySignature[entry.Signature] = binIndex;
                }
                else if (_primaryBinsBySignature.TryGetValue(entry.Signature, out ushort primaryBin))
                {
                    argsBin = primaryBin;
                }

                _argsMapScratch.Add(argsBin);
                AddOrReplaceEntityBin(entry.Entity.Id, binIndex);
            }

            ushort regionCount = (ushort)(currentBin - regionStartBin);
            if (regionCount > 0)
            {
                _rangesScratch.Add(new BinRange(regionStartBin, regionCount, orderKey));
            }
        }

        EnsureCapacity(ref _entities, _allEntitiesScratch.Count);
        _allEntitiesScratch.CopyTo(_entities);
        _entityCount = _allEntitiesScratch.Count;

        EnsureCapacity(ref _ranges, _rangesScratch.Count);
        _rangesScratch.CopyTo(_ranges);
        _rangeCount = _rangesScratch.Count;

        EnsureCapacity(ref _argsBinMap, _argsMapScratch.Count);
        for (int i = 0; i < _argsMapScratch.Count; i++)
        {
            _argsBinMap[i] = _argsMapScratch[i];
        }
        _argsBinCount = _argsMapScratch.Count;
    }

    public ReadOnlySpan<BinRange> GetRanges()
    {
        return _ranges.AsSpan(0, _rangeCount);
    }

    public Entity GetEntity(int binIndex)
    {
        if (binIndex < 0 || binIndex >= _entityCount)
            throw new ArgumentOutOfRangeException(nameof(binIndex));
        return _entities[binIndex];
    }

    public ushort GetBinForEntity(Entity entity)
    {
        if (TryGetBinForEntity(entity, out ushort bin))
        {
            return bin;
        }

        throw new KeyNotFoundException("Entity not found in BinQueue.");
    }

    public bool TryGetBinForEntity(Entity entity, out ushort bin)
    {
        return _entityBins.TryGetValue(entity.Id, out bin);
    }

    public int GetArgsBin(int binIndex)
    {
        if (binIndex < 0 || binIndex >= _argsBinCount)
        {
            throw new ArgumentOutOfRangeException(nameof(binIndex));
        }

        return _argsBinMap[binIndex];
    }

    private void AddOrReplaceEntityBin(int entityId, ushort bin)
    {
        _entityBins[entityId] = bin;
    }

    private static void EnsureCapacity<T>(ref T[] array, int count)
    {
        if (array.Length < count)
        {
            Array.Resize(ref array, Math.Max(count, array.Length == 0 ? 16 : array.Length * 2));
        }
    }

    private readonly record struct EntitySignatureEntry(Entity Entity, int OrderKey, ulong Signature);
}
