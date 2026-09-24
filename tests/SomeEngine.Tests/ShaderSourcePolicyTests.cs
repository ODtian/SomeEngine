using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class ShaderSourcePolicyTests
{
    [Fact]
    public void ShaderSourcesUseSlangExtension()
    {
        string projectRoot = TestProjectPaths.ProjectRoot();
        string shaderDir = TestProjectPaths.ShaderDirectory();
        string[] hlslSources = Directory
            .EnumerateFiles(shaderDir, "*.hlsl", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(projectRoot, path))
            .ToArray();

        Assert.Empty(hlslSources);
    }

    [Theory]
    [InlineData("imgui.slang", "VSMain", "PSMain")]
    [InlineData("debug_args_copy.slang", "CSMain")]
    [InlineData("debug_sphere.slang", "VSMain", "PSMain")]
    [InlineData("triangle.slang", "VSMain", "PSMain")]
    [InlineData("hello_triangle.slang", "VSMain", "PSMain")]
    public void MigratedShadersImportAsSlang(string shaderFile, params string[] entryPoints)
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath(shaderFile));

        foreach (string entryPoint in entryPoints)
        {
            Assert.Contains(asset.Variants!, v =>
                v.EntryPoint == entryPoint
                && v.Data.HasValue
                && v.Data.Value.Length > 0);
        }
    }

    [Fact]
    public void VisBufferEncodingMagicNumbersStayCentralized()
    {
        string shaderDir = TestProjectPaths.ShaderDirectory();
        string helperPath = Path.Combine(shaderDir, "visbuffer_encoding.slang");
        Assert.True(File.Exists(helperPath));

        foreach (string path in Directory.EnumerateFiles(shaderDir, "*.slang", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(path, helperPath, StringComparison.OrdinalIgnoreCase))
                continue;

            string source = File.ReadAllText(path);
            Assert.DoesNotContain("0x7FFFFFFFu", source);
            Assert.DoesNotContain("0x80000000u", source);
            Assert.DoesNotContain(">> 7", source);
            Assert.DoesNotContain("& 0x7F", source);
            Assert.DoesNotContain("<< 7", source);
        }
    }
}
