using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Tests.Assets;

public class GeneratedAssetPipelineCatalogTests
{
    [Fact]
    public void CreateProvidersAndImporters_ExposeBuiltInAssetPipelineTypes()
    {
        IReadOnlyList<IAssetProvider> providers = GeneratedAssetPipelineCatalog.CreateProviders();
        IReadOnlyList<IAssetImporter> importers = GeneratedAssetPipelineCatalog.CreateImporters();

        Assert.Contains(providers, provider => provider.AssetType == nameof(ShaderAsset));
        Assert.Contains(providers, provider => provider.AssetType == nameof(MaterialAsset));
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
            MaterialAssetSerializer.Save(new MaterialAsset
            {
                AssetGuid = materialGuid.ToFlatString(),
                Name = "GeneratedMaterial",
                Passes = [],
                Textures = [],
                Scalars = [],
            }, materialPath);

            AssetDatabase db = GeneratedAssetPipelineCatalog.CreateDatabase(dir);
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

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
