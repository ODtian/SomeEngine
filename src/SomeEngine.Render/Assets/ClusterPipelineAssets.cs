using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Pipelines;
using SomeEngine.Render.RHI;

namespace SomeEngine.Render.Assets;

public static class ClusterPipelineAssets
{
    public static ClusterPipeline LoadOpaque(
        RenderContext context,
        AssetDatabase assetDatabase,
        ClusterRenderAsset renderAsset,
        AssetStore assets)
        => ClusterPipeline.Opaque(
            context,
            Shaders(assetDatabase, renderAsset, assets),
            assets);

    public static ClusterShaders Shaders(
        AssetDatabase assetDatabase,
        ClusterRenderAsset renderAsset,
        AssetStore assets)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(renderAsset);
        ArgumentNullException.ThrowIfNull(assets);

        return new ClusterShaders(
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.BvhPatch, nameof(renderAsset.BvhPatch)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.ClusterBvhTraverse, nameof(renderAsset.ClusterBvhTraverse)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.ClusterCull, nameof(renderAsset.ClusterCull)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.ClusterBinning, nameof(renderAsset.ClusterBinning)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.ClusterDraw, nameof(renderAsset.ClusterDraw)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.DepthMerge, nameof(renderAsset.DepthMerge)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.HizBuild, nameof(renderAsset.HizBuild)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.ClusterShadeBinning, nameof(renderAsset.ClusterShadeBinning)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.ClusterResolve, nameof(renderAsset.ClusterResolve)),
            renderAsset.LoadShader(assetDatabase, assets, renderAsset.TemporalResolve, nameof(renderAsset.TemporalResolve)));
    }

    public static Handle<Shader> LoadShader(
        this ClusterRenderAsset renderAsset,
        AssetDatabase assetDatabase,
        AssetStore assets,
        ShaderAssetRef? shaderRef,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(assets);
        AssetGuid shaderGuid = RequireShaderGuid(
            renderAsset,
            shaderRef?.ShaderGuid,
            fieldName);

        if (assets.TryFind(shaderGuid, out Handle<Shader> existing)
            && assets.TryGet(existing, out Shader? existingShader)
            && existingShader != null)
        {
            return existing;
        }

        Handle<Shader> handle = RuntimeAssetLoader
            .RequestShader(assets, assetDatabase, shaderGuid)
            .GetAwaiter()
            .GetResult();
        if (!handle.IsValid)
        {
            throw new InvalidOperationException(
                $"Cluster render '{RenderName(renderAsset)}' field '{fieldName}' references missing ShaderAsset '{shaderGuid}'.");
        }

        return handle;
    }

    public static ShaderAsset RequireShader(
        this ClusterRenderAsset renderAsset,
        AssetDatabase assetDatabase,
        ShaderAssetRef? shaderRef,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(renderAsset);
        ArgumentNullException.ThrowIfNull(assetDatabase);
        AssetGuid shaderGuid = RequireShaderGuid(
            renderAsset,
            shaderRef?.ShaderGuid,
            fieldName);

        return assetDatabase.Load<ShaderAsset>(shaderGuid)
            ?? throw new InvalidOperationException(
                $"Cluster render '{RenderName(renderAsset)}' field '{fieldName}' references missing ShaderAsset '{shaderGuid}'.");
    }

    private static AssetGuid RequireShaderGuid(
        ClusterRenderAsset renderAsset,
        string? shaderGuidValue,
        string fieldName)
    {
        if (!AssetGuid.TryParse(shaderGuidValue, out AssetGuid shaderGuid) || shaderGuid.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Cluster render '{RenderName(renderAsset)}' field '{fieldName}' does not reference a valid ShaderAsset GUID.");
        }

        return shaderGuid;
    }

    private static string RenderName(ClusterRenderAsset renderAsset)
        => string.IsNullOrWhiteSpace(renderAsset.Name)
            ? "UnnamedClusterPipeline"
            : renderAsset.Name!;
}
