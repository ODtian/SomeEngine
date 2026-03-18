using System;
using System.Collections.Generic;

namespace SomeEngine.Render.Materials;

/// <summary>
/// Overlay dispatch 条目。记录 primary bin 与 overlay pass 的映射关系。
/// Feature dispatch 时按此列表顺序遍历，换 PSO/SRB 重新发射。
/// </summary>
public struct OverlayEntry
{
    /// <summary>复用的 primary bin index。</summary>
    public ushort PrimaryBin;

    /// <summary>overlay pass（含独立的 shader/绑定参数）。</summary>
    public MaterialPass OverlayPass;

    /// <summary>在 overlay 序列中的索引（排序用）。</summary>
    public byte LayerIndex;
}

/// <summary>
/// Overlay mapping 构建工具。从 MaterialRegistry 查询 OverlayTag，
/// 结合 BinQueue 映射出 (PrimaryBin, OverlayPass, LayerIndex) 列表。
/// <para>
/// 供 Feature 在 RebuildPSOsAndSRBs() 中调用，结果存在 Feature 上。
/// </para>
/// </summary>
public static class OverlayMapping
{
    /// <summary>
    /// 构建 overlay dispatch 列表。按 (PrimaryBin, LayerIndex) 排序。
    /// </summary>
    public static List<OverlayEntry> Build(MaterialRegistry registry, BinQueue binQueue)
    {
        var entries = new List<OverlayEntry>();

        foreach (var overlay in registry.Query<OverlayTag>())
        {
            var tag = registry.GetTag<OverlayTag>(overlay);
            if (tag is not { } t || t.PrimaryPass == null) continue;

            ushort primaryBin;
            try
            {
                primaryBin = binQueue.GetBinForPass(t.PrimaryPass);
            }
            catch (KeyNotFoundException)
            {
                // primary pass 不在此 BinQueue 中（可能属于不同 stage），跳过
                continue;
            }

            entries.Add(new OverlayEntry
            {
                PrimaryBin = primaryBin,
                OverlayPass = overlay,
                LayerIndex = t.LayerIndex,
            });
        }

        entries.Sort((a, b) =>
            a.PrimaryBin != b.PrimaryBin
                ? a.PrimaryBin.CompareTo(b.PrimaryBin)
                : a.LayerIndex.CompareTo(b.LayerIndex));

        return entries;
    }
}
