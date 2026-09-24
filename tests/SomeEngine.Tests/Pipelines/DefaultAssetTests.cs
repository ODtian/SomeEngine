using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests.Pipelines;

public class DefaultAssetTests
{
    [Fact]
    public void LoadsDefaultAsset()
    {
        using AssetDatabase db =
            AssetCatalog.CreateDatabase(TestProjectPaths.ProjectRoot());

        ClusterRenderAsset renderAsset = ClusterRenderAssets.LoadDefault(db);

        Assert.Equal(
            ClusterRenderAssets.DefaultGuid,
            db.Resolve(ClusterRenderAssets.DefaultPath));
        Assert.Equal("post_tonemap",
            HostShaders.PostTonemap(db).Name);
        Assert.Equal("temporal_resolve",
            renderAsset.RequireShader(
                db,
                renderAsset.TemporalResolve,
                nameof(renderAsset.TemporalResolve)).Name);
        Assert.Equal("imgui",
            HostShaders.ImGui(db).Name);
        Assert.Equal("cluster_motion_vectors",
            renderAsset.RequireShader(
                db,
                renderAsset.ClusterMotionVectors,
                nameof(renderAsset.ClusterMotionVectors)).Name);
    }

    [Fact]
    public void DeclaresShaderDeps()
    {
        using AssetDatabase db =
            AssetCatalog.CreateDatabase(TestProjectPaths.ProjectRoot());

        ClusterRenderAsset renderAsset = ClusterRenderAssets.LoadDefault(db);

        Assert.Equal(
            ClusterRenderAssets.DefaultGuid.ToFlatString(),
            renderAsset.AssetGuid);

        HashSet<AssetGuid> expectedDependencies = [];
        foreach ((string FieldName, string? ShaderGuid) entry in ShaderFields(renderAsset))
        {
            Assert.True(
                AssetGuid.TryParse(entry.ShaderGuid, out AssetGuid shaderGuid)
                && !shaderGuid.IsEmpty,
                $"Field '{entry.FieldName}' must reference a valid ShaderAsset GUID.");

            expectedDependencies.Add(shaderGuid);
            Assert.NotNull(db.Load<ShaderAsset>(shaderGuid));
        }

        Assert.True(expectedDependencies.SetEquals(db.GetDependencies(ClusterRenderAssets.DefaultGuid)));
    }

    private static IEnumerable<(string FieldName, string? ShaderGuid)> ShaderFields(
        ClusterRenderAsset renderAsset)
    {
        yield return (nameof(renderAsset.TemporalResolve), renderAsset.TemporalResolve?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterBinning), renderAsset.ClusterBinning?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterBvhTraverse), renderAsset.ClusterBvhTraverse?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterCull), renderAsset.ClusterCull?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterDeformBinning), renderAsset.ClusterDeformBinning?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterDeform), renderAsset.ClusterDeform?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterDraw), renderAsset.ClusterDraw?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterMotionVectors), renderAsset.ClusterMotionVectors?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterResolve), renderAsset.ClusterResolve?.ShaderGuid);
        yield return (nameof(renderAsset.ClusterShadeBinning), renderAsset.ClusterShadeBinning?.ShaderGuid);
        yield return (nameof(renderAsset.DepthMerge), renderAsset.DepthMerge?.ShaderGuid);
        yield return (nameof(renderAsset.HizBuild), renderAsset.HizBuild?.ShaderGuid);
        yield return (nameof(renderAsset.BvhPatch), renderAsset.BvhPatch?.ShaderGuid);
    }
}
