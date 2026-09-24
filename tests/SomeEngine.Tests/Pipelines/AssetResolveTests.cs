using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Pipelines;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Pipelines;

public class AssetResolveTests
{
    [Fact]
    public void SameFieldResolves()
    {
        string dir = CreateTempDir();
        try
        {
            using AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            AssetGuid defaultShaderGuid = AssetGuid.New();
            AssetGuid userShaderGuid = AssetGuid.New();
            db.CreateAsset("assets/Shaders/default_post.shader.asset", CreateShader(defaultShaderGuid, "DefaultPost"));
            db.CreateAsset("assets/Shaders/user_post.shader.asset", CreateShader(userShaderGuid, "UserPost"));

            ClusterRenderAsset defaultRender = CreateRender(AssetGuid.New(), "DefaultCluster", defaultShaderGuid);
            ClusterRenderAsset userRender = CreateRender(AssetGuid.New(), "UserCluster", userShaderGuid);

            ShaderAsset defaultShader = defaultRender.RequireShader(
                db,
                defaultRender.ClusterCull,
                nameof(defaultRender.ClusterCull));
            ShaderAsset userShader = userRender.RequireShader(
                db,
                userRender.ClusterCull,
                nameof(userRender.ClusterCull));

            Assert.Equal("DefaultPost", defaultShader.Name);
            Assert.Equal("UserPost", userShader.Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShaderFieldWins()
    {
        string dir = CreateTempDir();
        try
        {
            using AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            AssetGuid shaderGuid = AssetGuid.New();
            db.CreateAsset(
                "assets/Shaders/multi_entry_cluster_cull.shader.asset",
                CreateShader(shaderGuid, "MultiEntryClusterCull"));

            ClusterRenderAsset renderAsset = CreateRender(AssetGuid.New(), "Cluster", shaderGuid);

            ShaderAsset loaded = renderAsset.RequireShader(
                db,
                renderAsset.ClusterCull,
                nameof(renderAsset.ClusterCull));

            Assert.Equal("MultiEntryClusterCull", loaded.Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingShaderRejects()
    {
        string dir = CreateTempDir();
        try
        {
            using AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            ClusterRenderAsset renderAsset = CreateRender(AssetGuid.New(), "Cluster", AssetGuid.New());

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => renderAsset.RequireShader(db, renderAsset.ClusterCull, nameof(renderAsset.ClusterCull)));

            Assert.Contains("missing ShaderAsset", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ListsShaderDeps()
    {
        string dir = CreateTempDir();
        try
        {
            AssetGuid shaderA = AssetGuid.New();
            AssetGuid shaderB = AssetGuid.New();
            string path = Path.Combine(dir, "cluster.clusterrender.asset");
            ClusterRenderAsset renderAsset = CreateRender(AssetGuid.New(), "Cluster", shaderA);
            renderAsset.TemporalResolve = ShaderRef(shaderB);
            ClusterRenderCodec.Save(renderAsset, path);

            var provider = new ClusterRenderProvider();
            IReadOnlyList<AssetGuid> dependencies = provider.GetDependencies(path);

            Assert.Equal(2, dependencies.Count);
            Assert.Contains(shaderA, dependencies);
            Assert.Contains(shaderB, dependencies);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadsRenderAsset()
    {
        string dir = CreateTempDir();
        try
        {
            using AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            AssetGuid shaderGuid = AssetGuid.New();
            db.CreateAsset("assets/Shaders/custom.shader.asset", CreateShader(shaderGuid, "CustomFullscreen"));

            AssetGuid renderGuid = AssetGuid.New();
            db.CreateAsset(
                "assets/Pipelines/custom.clusterrender.asset",
                CreateRender(renderGuid, "CustomCluster", shaderGuid));

            ClusterRenderAsset? loaded = db.Load<ClusterRenderAsset>(renderGuid);

            Assert.NotNull(loaded);
            Assert.Equal(renderGuid.ToFlatString(), loaded.AssetGuid);
            Assert.Equal(new[] { shaderGuid }, db.GetDependencies(renderGuid));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static ClusterRenderAsset CreateRender(AssetGuid renderGuid, string name, AssetGuid shaderGuid)
        => new()
        {
            AssetGuid = renderGuid.ToFlatString(),
            Name = name,
            TemporalResolve = ShaderRef(shaderGuid),
            ClusterBinning = ShaderRef(shaderGuid),
            ClusterBvhTraverse = ShaderRef(shaderGuid),
            ClusterCull = ShaderRef(shaderGuid),
            ClusterDeformBinning = ShaderRef(shaderGuid),
            ClusterDeform = ShaderRef(shaderGuid),
            ClusterDraw = ShaderRef(shaderGuid),
            ClusterMotionVectors = ShaderRef(shaderGuid),
            ClusterResolve = ShaderRef(shaderGuid),
            ClusterShadeBinning = ShaderRef(shaderGuid),
            DepthMerge = ShaderRef(shaderGuid),
            HizBuild = ShaderRef(shaderGuid),
            BvhPatch = ShaderRef(shaderGuid),
        };

    private static ShaderAssetRef ShaderRef(AssetGuid shaderGuid)
        => new() { ShaderGuid = shaderGuid.ToFlatString() };

    private static ShaderAsset CreateShader(AssetGuid guid, string name)
        => new()
        {
            AssetGuid = guid.ToFlatString(),
            Name = name,
            ImportTrace = new ImportTrace
            {
                SourceGuid = SourceGuid.New().ToFlatString(),
                SourcePath = $"{name}.slang",
                SubAssetKey = "shader:main",
                ContentFingerprint = "fp",
                Dependencies = [],
                ImporterVersion = 1,
            },
            Variants =
            [
                new ShaderBytecode
                {
                    Backend = "dxil",
                    Stage = ShaderStage.Vertex,
                    EntryPoint = "VSMain",
                    Data = Array.Empty<byte>(),
                    ContentHash = $"{name}-vs",
                },
                new ShaderBytecode
                {
                    Backend = "dxil",
                    Stage = ShaderStage.Pixel,
                    EntryPoint = "PSMain",
                    Data = Array.Empty<byte>(),
                    ContentHash = $"{name}-ps",
                },
            ],
            EntryPointAttributes = [],
            Reflections = [],
            EntryPointReflections =
            [
                new ShaderEntryPointReflection
                {
                    Backend = "dxil",
                    EntryPoint = "VSMain",
                    Stage = ShaderStage.Vertex,
                    Reflection = new ShaderReflectionData { Resources = [] },
                },
                new ShaderEntryPointReflection
                {
                    Backend = "dxil",
                    EntryPoint = "PSMain",
                    Stage = ShaderStage.Pixel,
                    Reflection = new ShaderReflectionData { Resources = [] },
                },
            ],
            Metadata = new ShaderMetadata
            {
                Tags = [],
                MaterialBindings = [],
                MaterialScalarLayouts = [],
            },
        };

}
