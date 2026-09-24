using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public sealed class ClusterRenderProvider : AssetProvider<ClusterRenderAsset>
{
    public override string AssetType => nameof(ClusterRenderAsset);

    public override bool Matches(string assetPath)
        => assetPath.EndsWith(".clusterrender.asset", StringComparison.OrdinalIgnoreCase);

    public override ClusterRenderAsset Create(AssetGuid guid, string filePath)
        => ClusterRenderCodec.Load(filePath);

    public override IReadOnlyList<AssetGuid> GetDependencies(string filePath)
    {
        ClusterRenderAsset asset = ClusterRenderCodec.Load(filePath);
        var deps = new HashSet<AssetGuid>();

        Add(deps, asset.TemporalResolve?.ShaderGuid, filePath, nameof(asset.TemporalResolve));
        Add(deps, asset.ClusterBinning?.ShaderGuid, filePath, nameof(asset.ClusterBinning));
        Add(deps, asset.ClusterBvhTraverse?.ShaderGuid, filePath, nameof(asset.ClusterBvhTraverse));
        Add(deps, asset.ClusterCull?.ShaderGuid, filePath, nameof(asset.ClusterCull));
        Add(deps, asset.ClusterDeformBinning?.ShaderGuid, filePath, nameof(asset.ClusterDeformBinning));
        Add(deps, asset.ClusterDeform?.ShaderGuid, filePath, nameof(asset.ClusterDeform));
        Add(deps, asset.ClusterDraw?.ShaderGuid, filePath, nameof(asset.ClusterDraw));
        Add(deps, asset.ClusterMotionVectors?.ShaderGuid, filePath, nameof(asset.ClusterMotionVectors));
        Add(deps, asset.ClusterResolve?.ShaderGuid, filePath, nameof(asset.ClusterResolve));
        Add(deps, asset.ClusterShadeBinning?.ShaderGuid, filePath, nameof(asset.ClusterShadeBinning));
        Add(deps, asset.DepthMerge?.ShaderGuid, filePath, nameof(asset.DepthMerge));
        Add(deps, asset.HizBuild?.ShaderGuid, filePath, nameof(asset.HizBuild));
        Add(deps, asset.BvhPatch?.ShaderGuid, filePath, nameof(asset.BvhPatch));

        return deps.ToArray();
    }

    private static void Add(
        HashSet<AssetGuid> deps,
        string? shaderGuidValue,
        string filePath,
        string fieldName)
    {
        if (!AssetGuid.TryParse(shaderGuidValue, out AssetGuid guid) || guid.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Cluster render asset '{filePath}' field '{fieldName}' references invalid ShaderAsset GUID '{shaderGuidValue}'.");
        }

        deps.Add(guid);
    }
}
