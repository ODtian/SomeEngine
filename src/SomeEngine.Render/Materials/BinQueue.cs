using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace SomeEngine.Render.Materials;

/// <summary>
/// Bin 级渲染队列。按 orderKey 动态生成有序区间，并在区间内按签名去重分 bin。
/// </summary>
public sealed class BinQueue
{
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
        public required Func<Entity[]> Query { get; init; }
        public required Func<Entity, int> OrderKey { get; init; }
        public required Func<Entity, ulong> SignatureFunc { get; init; }
    }

    private readonly List<BinGroup> _groups = new();
    private Entity[] _entities = [];
    private BinRange[] _ranges = [];
    private ushort[] _argsBinMap = [];
    private readonly List<EntityBinEntry> _entityBins = [];

    public int TotalBinCount => _entities.Length;

    public void RegisterGroup(BinGroup group)
    {
        _groups.Add(group);
    }

    public void Rebuild()
    {
        _entityBins.Clear();
        var pendingEntries = new List<EntitySignatureEntry>();

        foreach (var group in _groups)
        {
            var entities = group.Query() ?? [];
            foreach (var entity in entities)
            {
                pendingEntries.Add(new EntitySignatureEntry(entity, group.OrderKey(entity), group.SignatureFunc(entity)));
            }
        }

        pendingEntries.Sort(static (a, b) =>
        {
            int orderCompare = a.OrderKey.CompareTo(b.OrderKey);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            int signatureCompare = a.Signature.CompareTo(b.Signature);
            return signatureCompare != 0 ? signatureCompare : a.Entity.Id.CompareTo(b.Entity.Id);
        });

        var allEntities = new List<Entity>();
        var ranges = new List<BinRange>();
        var primaryBinsBySignature = new List<SignatureBinEntry>();
        var argsMap = new List<ushort>();
        ushort currentBin = 0;

        int entryIndex = 0;
        while (entryIndex < pendingEntries.Count)
        {
            int orderKey = pendingEntries[entryIndex].OrderKey;
            var signatureBins = new List<SignatureBinEntry>();
            ushort regionStartBin = currentBin;

            while (entryIndex < pendingEntries.Count && pendingEntries[entryIndex].OrderKey == orderKey)
            {
                var entry = pendingEntries[entryIndex++];
                int signatureIndex = FindSignature(signatureBins, entry.Signature);
                ushort binIndex;

                if (signatureIndex >= 0)
                {
                    binIndex = signatureBins[signatureIndex].Bin;
                }
                else
                {
                    binIndex = currentBin++;
                    signatureBins.Add(new SignatureBinEntry(entry.Signature, binIndex));
                    allEntities.Add(entry.Entity);

                    ushort argsBin = binIndex;
                    if (orderKey == 0)
                    {
                        primaryBinsBySignature.Add(new SignatureBinEntry(entry.Signature, binIndex));
                    }
                    else
                    {
                        int primaryIndex = FindSignature(primaryBinsBySignature, entry.Signature);
                        if (primaryIndex >= 0)
                        {
                            argsBin = primaryBinsBySignature[primaryIndex].Bin;
                        }
                    }

                    argsMap.Add(argsBin);
                }

                AddOrReplaceEntityBin(entry.Entity.Id, binIndex);
            }

            ushort regionCount = (ushort)(currentBin - regionStartBin);
            if (regionCount > 0)
            {
                ranges.Add(new BinRange(regionStartBin, regionCount, orderKey));
            }
        }

        _entities = allEntities.ToArray();
        _ranges = ranges.ToArray();
        _argsBinMap = argsMap.ToArray();
    }

    public BinRange[] GetRanges()
    {
        return _ranges;
    }

    public Entity GetEntity(int binIndex)
    {
        if (binIndex < 0 || binIndex >= _entities.Length)
            throw new ArgumentOutOfRangeException(nameof(binIndex));
        return _entities[binIndex];
    }

    public ushort GetBinForEntity(Entity entity)
    {
        for (int i = 0; i < _entityBins.Count; i++)
        {
            if (_entityBins[i].EntityId == entity.Id)
            {
                return _entityBins[i].Bin;
            }
        }

        throw new KeyNotFoundException("Entity not found in BinQueue.");
    }

    public int GetArgsBin(int binIndex)
    {
        if (binIndex < 0 || binIndex >= _argsBinMap.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(binIndex));
        }

        return _argsBinMap[binIndex];
    }

    private void AddOrReplaceEntityBin(int entityId, ushort bin)
    {
        for (int i = 0; i < _entityBins.Count; i++)
        {
            if (_entityBins[i].EntityId == entityId)
            {
                _entityBins[i] = new EntityBinEntry(entityId, bin);
                return;
            }
        }

        _entityBins.Add(new EntityBinEntry(entityId, bin));
    }

    private static int FindSignature(List<SignatureBinEntry> entries, ulong signature)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Signature == signature)
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct EntitySignatureEntry(Entity Entity, int OrderKey, ulong Signature);
    private readonly record struct SignatureBinEntry(ulong Signature, ushort Bin);
    private readonly record struct EntityBinEntry(int EntityId, ushort Bin);
}
