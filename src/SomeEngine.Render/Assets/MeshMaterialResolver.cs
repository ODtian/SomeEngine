using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Assets;

/// <summary>
/// 从 MeshAsset.default_material_guids 解析出 Material 列表。
/// 按 localMaterialIndex 索引。
/// </summary>
public static class MeshMaterialResolver
{
    public delegate Material? MaterialLoadByGuidFunc(AssetGuid guid);

    public static Material?[] Resolve(MeshAsset meshAsset, MaterialLoadByGuidFunc materialLoader)
    {
        if (meshAsset.DefaultMaterialGuids == null || meshAsset.DefaultMaterialGuids.Count == 0)
        {
            return [];
        }

        Material?[] result = new Material?[meshAsset.DefaultMaterialGuids.Count];
        for (int i = 0; i < meshAsset.DefaultMaterialGuids.Count; i++)
        {
            string? guidText = meshAsset.DefaultMaterialGuids[i];
            if (!AssetGuid.TryParse(guidText, out AssetGuid guid) || guid.IsEmpty)
            {
                continue;
            }

            result[i] = materialLoader(guid);
        }

        return result;
    }
}
