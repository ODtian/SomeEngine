using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Assets;

/// <summary>
/// 从 MeshAsset.default_material_slots 解析出 MaterialPass 列表。
/// 将 MeshAsset 的字符串路径列表解析为运行时 MaterialPass[]，
/// 按 localMaterialIndex 索引。
/// </summary>
public static class MeshMaterialResolver
{
    /// <summary>材质加载回调。path → Material?</summary>
    public delegate Material? MaterialLoadFunc(string path);

    /// <summary>
    /// 解析 MeshAsset 的 default_material_slots 为 MaterialPass 数组。
    /// </summary>
    /// <param name="meshAsset">源 MeshAsset。</param>
    /// <param name="materialLoader">按路径加载 Material 的回调。</param>
    /// <returns>per-localMaterialIndex 的 MaterialPass 映射。null 位表示该 slot 没有可用材质。</returns>
    public static MaterialPass?[] Resolve(
        MeshAsset meshAsset,
        MaterialLoadFunc materialLoader)
    {
        var slots = meshAsset.DefaultMaterialSlots;
        if (slots == null || slots.Count == 0)
            return [];

        var result = new MaterialPass?[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            var path = slots[i];
            if (string.IsNullOrEmpty(path)) continue;

            var mat = materialLoader(path);
            if (mat != null && mat.Passes.Length > 0)
                result[i] = mat.Passes[0];
        }
        return result;
    }
}
