using System;
using System.Collections.Generic;

namespace SomeEngine.Render.Materials;

/// <summary>
/// Bin 级渲染队列。管理多个区间（region），每个区间内按签名去重分 bin。
/// <para>
/// 流程：RegisterRegion → Rebuild → 按 bin index 遍历 dispatch。
/// </para>
/// </summary>
public sealed class BinQueue
{
    /// <summary>一个连续的 bin 范围。</summary>
    public readonly struct BinRange
    {
        public readonly ushort Start;
        public readonly ushort Count;
        public BinRange(ushort start, ushort count) { Start = start; Count = count; }
    }

    private readonly List<RegionConfig> _regions = new();
    private MaterialPass[] _passes = [];
    private readonly Dictionary<MaterialPass, ushort> _passToBin = new();
    private readonly Dictionary<string, BinRange> _regionRanges = new();
    private int _totalBinCount;

    /// <summary>总 bin 数。</summary>
    public int TotalBinCount => _totalBinCount;

    /// <summary>注册一个区间配置。</summary>
    /// <param name="name">区间名称（如 "opaque", "translucent"）。</param>
    /// <param name="queryFunc">返回该区间内的 MaterialPass 列表。</param>
    /// <param name="signatureFunc">计算 pass 签名 hash（相同签名合并为同一 bin）。</param>
    public void RegisterRegion(string name,
        Func<MaterialPass[]> queryFunc,
        Func<MaterialPass, ulong> signatureFunc)
    {
        _regions.Add(new RegionConfig(name, queryFunc, signatureFunc));
    }

    /// <summary>
    /// 重建 bin 分配。遍历所有 region，为每个 pass 分配 bin index。
    /// </summary>
    public void Rebuild()
    {
        _passToBin.Clear();
        _regionRanges.Clear();

        var allPasses = new List<MaterialPass>();
        ushort currentBin = 0;

        foreach (var region in _regions)
        {
            var passes = region.QueryFunc();
            var signatureMap = new Dictionary<ulong, ushort>();
            ushort regionStartBin = currentBin;

            foreach (var pass in passes)
            {
                ulong sig = region.SignatureFunc(pass);
                if (!signatureMap.TryGetValue(sig, out ushort binIndex))
                {
                    binIndex = currentBin++;
                    signatureMap[sig] = binIndex;
                }

                _passToBin[pass] = binIndex;

                // Ensure allPasses has enough space
                while (allPasses.Count <= binIndex)
                    allPasses.Add(null!);
                allPasses[binIndex] = pass; // last-write for duplicate signatures
            }

            ushort regionCount = (ushort)(currentBin - regionStartBin);
            _regionRanges[region.Name] = new BinRange(regionStartBin, regionCount);
        }

        _passes = allPasses.ToArray();
        _totalBinCount = currentBin;
    }

    /// <summary>获取指定区间的 bin 范围。</summary>
    public BinRange GetRange(string regionName)
    {
        return _regionRanges.TryGetValue(regionName, out var range) ? range : default;
    }

    /// <summary>获取指定 bin index 的 MaterialPass。</summary>
    public MaterialPass GetPass(int binIndex)
    {
        if (binIndex < 0 || binIndex >= _passes.Length)
            throw new ArgumentOutOfRangeException(nameof(binIndex));
        return _passes[binIndex];
    }

    /// <summary>反向查找 MaterialPass 对应的 bin index。</summary>
    public ushort GetBinForPass(MaterialPass pass)
    {
        if (_passToBin.TryGetValue(pass, out ushort bin))
            return bin;
        throw new KeyNotFoundException("MaterialPass not found in BinQueue.");
    }

    private readonly record struct RegionConfig(
        string Name,
        Func<MaterialPass[]> QueryFunc,
        Func<MaterialPass, ulong> SignatureFunc
    );
}
