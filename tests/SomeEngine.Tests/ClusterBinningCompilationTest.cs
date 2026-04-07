using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class ClusterBinningCompilationTest
{
    [Fact]
    public void ClusterBinning_CompilesSuccessfully()
    {
        string source = """
            #include "cluster_binning.slang"
        """;

        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, "_test_cluster_binning.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            string[] entryPoints = ["CSBinningClear", "CSBinningPrepare", "CSBinningCount", "CSBinningReserve", "CSBinningScatter"];
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