using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class ClusterBinningCompilationTest
{
    [Fact]
    public void ClusterBinning_CompilesSuccessfully()
    {
        CompileIncludeAndAssert(
            "cluster_binning.slang",
            "_test_cluster_binning.slang",
            ["CSBinningClear", "CSBinningPrepare", "CSBinningCount", "CSBinningReserve", "CSBinningScatter"]);
    }

    [Fact]
    public void ClusterShadeBinning_CompilesSuccessfully()
    {
        CompileIncludeAndAssert(
            "cluster_shade_binning.slang",
            "_test_cluster_shade_binning.slang",
            ["CSBinCount", "CSBinReserve", "CSBinScatter"]);
    }

    [Fact]
    public void ClusterDeformBinning_CompilesSuccessfully()
    {
        CompileIncludeAndAssert(
            "cluster_deform_binning.slang",
            "_test_cluster_deform_binning.slang",
            [
                "CSDeformBinClear",
                "CSDeformBinPrepare",
                "CSDeformBinCount",
                "CSDeformBinReserve",
                "CSDeformBinReserveDispatchWrite",
                "CSDeformBinScatter",
            ]);
    }

    private static void CompileIncludeAndAssert(string includeFile, string tempFileName, string[] entryPoints)
    {
        string source = $"""
            #include "{includeFile}"
        """;

        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, tempFileName);
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            foreach (string entryPoint in entryPoints)
            {
                var spirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == entryPoint && v.Backend == "spirv");
                Assert.NotNull(spirv);
                Assert.True(spirv!.Data.HasValue && spirv.Data.Value.Length > 0, $"SPIR-V bytecode should be non-empty for {entryPoint}");
            }
        }
        finally
        {
            if (File.Exists(slangFile)) File.Delete(slangFile);

            string assetFile = Path.ChangeExtension(slangFile, ".shader.asset");
            if (File.Exists(assetFile)) File.Delete(assetFile);

            string metaFile = slangFile + ".meta";
            if (File.Exists(metaFile)) File.Delete(metaFile);

            string assetMetaFile = assetFile + ".meta";
            if (File.Exists(assetMetaFile)) File.Delete(assetMetaFile);
        }
    }
}
