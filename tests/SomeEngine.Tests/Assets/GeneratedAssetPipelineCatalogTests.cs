using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class AssetCatalogTests
{
    [Fact]
    public void CreateProvidersAndImporters_ExposeBuiltInAssetPipelineTypes()
    {
        IReadOnlyList<IAssetProvider> providers = AssetCatalog.CreateProviders();
        IReadOnlyList<IAssetImporter> importers = AssetCatalog.CreateImporters();

        Assert.Contains(providers, provider => provider.AssetType == nameof(ShaderAsset));
        Assert.Contains(providers, provider => provider.AssetType == nameof(MaterialAsset));
        Assert.Contains(providers, provider => provider.AssetType == nameof(ClusterRenderAsset));
        Assert.Contains(providers, provider => provider.AssetType == nameof(MaterialInstanceAsset));
        Assert.Contains(providers, provider => provider.AssetType == nameof(MeshAsset));
        Assert.Contains(providers, provider => provider.AssetType == nameof(TextureAsset));
        Assert.Contains(importers, importer => importer.ImporterName == nameof(SomeEngine.Assets.Importers.SlangShaderImporter));
        Assert.Contains(importers, importer => importer.ImporterName == nameof(SomeEngine.Assets.Importers.GltfSourceImporter));
    }

    [Fact]
    public void CreateDatabase_ConstructsAssetDatabaseWithoutStaticRegistration()
    {
        string dir = CreateTempDir();

        try
        {
            AssetGuid materialGuid = AssetGuid.New();
            string materialPath = Path.Combine(dir, "assets", "Materials", "generated.material.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            MaterialAssetCodec.Save(new MaterialAsset
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "GeneratedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetDatabase db = AssetCatalog.CreateDatabase(dir);
            db.Import("assets/Materials/generated.material.asset");
            MaterialAsset? loaded = db.Load<MaterialAsset>("assets/Materials/generated.material.asset");

            Assert.NotNull(loaded);
            Assert.Equal(materialGuid, db.Resolve("assets/Materials/generated.material.asset"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

}
