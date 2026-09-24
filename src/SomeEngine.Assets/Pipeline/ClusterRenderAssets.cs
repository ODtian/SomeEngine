using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Pipeline;

public static class ClusterRenderAssets
{
    public static readonly AssetGuid DefaultGuid =
        new(new Guid("32600000-0000-4000-8000-000000000001"));

    public const string DefaultPath = "assets/Pipelines/default_cluster.clusterrender.asset";

    public static ClusterRenderAsset LoadDefault(AssetDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        return database.Load<ClusterRenderAsset>(DefaultGuid)
            ?? database.Load<ClusterRenderAsset>(DefaultPath)
            ?? throw new InvalidOperationException(
                $"Default cluster render asset '{DefaultPath}' is not indexed. Run the default asset generator before starting rendering.");
    }
}
