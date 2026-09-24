using System.IO;
using System.Text.Json;
using SomeEngine.Assets;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class SourceMetaFilesTests
{
    [Fact]
    public void SaveLoad_Roundtrip_PreservesImporterSettings()
    {
        string dir = CreateTempDir();
        string sourcePath = Path.Combine(dir, "assets", "Models", "character.gltf");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, "{}");

        var settings = JsonDocument.Parse(
            """
            {
              "lit_material_template": "assets/Materials/Templates/PbrTemplate.material.asset",
              "unlit_material_template": "assets/Materials/Templates/UnlitTemplate.material.asset"
            }
            """).RootElement.Clone();

        SourceMetaFiles.Save(sourcePath, new SourceMeta
        {
            SourceGuid = SourceGuid.New(),
            Importer = "GltfSourceImporter",
            ImporterSettings = settings,
        });

        SourceMeta loaded = SourceMetaFiles.Load(sourcePath);

        Assert.True(loaded.ImporterSettings.HasValue);
        Assert.Equal(
            "assets/Materials/Templates/PbrTemplate.material.asset",
            loaded.ImporterSettings.Value.GetProperty("lit_material_template").GetString());
        Assert.Equal(
            "assets/Materials/Templates/UnlitTemplate.material.asset",
            loaded.ImporterSettings.Value.GetProperty("unlit_material_template").GetString());

        Directory.Delete(dir, true);
    }

}
