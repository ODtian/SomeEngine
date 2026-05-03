using Friflo.Engine.ECS;
using Diligent;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// 一组共享同一 PSO 的 shade bins。Feature 持有，rebuild 时构建。
/// BinQueue 保证同 ShaderAsset 的 bin 在 region 内连续，所以用 BinStart + BinCount。
/// </summary>
public sealed class MaterialPSOGroup : IDisposable
{
    public IPipelineState? PSO;
    /// <summary>Per-layout SRB (Dynamic variables, shared across all bins in this group).</summary>
    public IShaderResourceBinding? SRB;
    public ShaderVariantRef ComputeVariant;
    public ShaderVariantRef VertexVariant;
    public ShaderVariantRef PixelVariant;
    public Entity[] Entities = [];
    public int[] ArgsBins = [];
    public int BinStart;
    public int BinCount;

    public void Dispose()
    {
        SRB?.Dispose();
        SRB = null;
        // PSO is managed by GlobalPsoCache, not disposed here
    }

    /// <summary>
    /// 按 Shader 引用相等性将连续 bin 分组。
    /// 纯 CPU 逻辑，不依赖 GPU 资源。ClusterShade 和 RasterPSOBuilder 共用。
    /// </summary>
    /// <returns>
    /// 每个 group 的 (Start, Count, Entities, VariantRef) 四元组。
    /// </returns>
    public static List<ShaderGroup> ComputeShaderGroups(
        BinSpace binSpace,
        int fieldIndex,
        Func<Entity, ShaderVariantRef> variantSelector)
    {
        int totalBins = binSpace.GetTotalBinCount(fieldIndex);
        if (totalBins == 0)
            return [];

        var groups = new List<ShaderGroup>();
        int groupStart = 0;
        var currentVariant = variantSelector(binSpace.GetEntity(fieldIndex, 0));

        for (int bin = 1; bin <= totalBins; bin++)
        {
            var nextVariant = bin < totalBins ? variantSelector(binSpace.GetEntity(fieldIndex, bin)) : default;
            bool isBreak = bin == totalBins || !SameVariant(nextVariant, currentVariant);

            if (isBreak)
            {
                int count = bin - groupStart;
                var entities = new Entity[count];
                for (int i = 0; i < count; i++)
                    entities[i] = binSpace.GetEntity(fieldIndex, groupStart + i);

                groups.Add(new ShaderGroup(groupStart, count, entities, currentVariant));

                if (bin < totalBins)
                {
                    groupStart = bin;
                    currentVariant = nextVariant;
                }
            }
        }

        return groups;
    }

    /// <summary>纯 CPU 分组结果。</summary>
    public readonly record struct ShaderGroup(
        int BinStart,
        int BinCount,
        Entity[] Entities,
        ShaderVariantRef VariantRef);

    private static bool SameVariant(ShaderVariantRef left, ShaderVariantRef right)
    {
        return ReferenceEquals(left.Shader, right.Shader)
            && string.Equals(left.EntryPoint, right.EntryPoint, StringComparison.Ordinal);
    }
}
