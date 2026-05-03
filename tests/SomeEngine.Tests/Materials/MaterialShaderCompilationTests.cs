using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests.Materials;

public class MaterialShaderCompilationTests
{
    [Theory]
    [InlineData("cluster_shade_material.slang")]
    [InlineData("cluster_shade_unlit.slang")]
    public void MaterialShadeShader_ImportsSuccessfully(string shaderName)
    {
        string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
        string path = Path.Combine(projectRoot, "assets", "Shaders", shaderName);

        var asset = SlangShaderImporter.Import(path);

        Assert.NotNull(asset);
        Assert.NotEmpty(asset.Variants!);
        string[] expectedMaterialResources = shaderName == "cluster_shade_material.slang"
            ? ["AlbedoMap", "NormalMap", "ARMMap", "MaterialSampler"]
            : ["AlbedoMap", "MaterialSampler"];
        foreach (var backend in asset.Reflections!)
        {
            foreach (string resourceName in expectedMaterialResources)
            {
                Assert.Contains(backend.Reflection!.Resources!, r => r.Name == resourceName);
            }
        }
    }

    [Fact]
    public void MaterialShadeShader_ReflectsMaterialScalarLayout()
    {
        string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
        string path = Path.Combine(projectRoot, "assets", "Shaders", "cluster_shade_material.slang");

        var asset = SlangShaderImporter.Import(path);

        var layout = Assert.Single(asset.Metadata!.MaterialScalarLayouts!);
        Assert.Equal("StandardPBRScalars", layout.Name);
        Assert.Contains(layout.Fields!, field => field.Name == "BaseColorTint" && field.Offset == 0 && field.Size == 16);
        Assert.Contains(layout.Fields!, field => field.Name == "MetallicFactor");
        Assert.Contains(layout.Fields!, field => field.Name == "Roughness");
        Assert.Contains(layout.Fields!, field => field.Name == "EmissiveFactor");
        Assert.True(layout.Size >= layout.Fields!.Max(field => field.Offset + field.Size));
    }

    private static string ResolveProjectRoot(string startPath)
    {
        string current = Path.GetFullPath(startPath);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "SomeEngine.slnx")))
            {
                return current;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        throw new DirectoryNotFoundException($"Could not locate project root from '{startPath}'.");
    }
}
