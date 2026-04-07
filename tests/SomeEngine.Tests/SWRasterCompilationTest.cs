using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class SWRasterCompilationTest
{
    [Fact]
    public void SWRaster_CompilesSuccessfully()
    {
        // Inline Slang source that includes sw_raster.slang — just the entry point.
        // sw_raster.slang itself includes wave_queue.slang and cluster_structures.slang.
        string source = """
            #include "sw_raster.slang"
        """;

        // TestDirectory = tests/SomeEngine.Tests/bin/x64/Debug/net10.0/
        // Need to go up 6 levels to reach project root, then into assets/Shaders
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, "_test_sw_raster.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            // At least SPIR-V should compile. DXIL requires DXC which may not be available.
            var csSpirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == "CSSWRaster" && v.Backend == "spirv");
            Assert.NotNull(csSpirv);
            Assert.True(csSpirv!.Data.HasValue && csSpirv.Data.Value.Length > 0, "SPIR-V bytecode should be non-empty");

            var csDxil = asset.Variants.FirstOrDefault(v => v.EntryPoint == "CSSWRaster" && v.Backend == "dxil");
            if (csDxil != null)
            {
                Assert.True(csDxil.Data.HasValue && csDxil.Data.Value.Length > 0, "DXIL bytecode should be non-empty");
            }
            else
            {
                Console.WriteLine("WARNING: DXIL variant not produced (DXC not found). SPIR-V OK.");
            }

            Console.WriteLine($"SWRaster compilation OK: {asset.Variants.Count} variants");
            foreach (var v in asset.Variants)
            {
                Console.WriteLine($"  {v.Backend} / {v.Stage} / {v.EntryPoint}: {v.Data?.Length ?? 0} bytes");
            }
        }
        finally
        {
            if (File.Exists(slangFile)) File.Delete(slangFile);
            string metaFile = slangFile + ".meta";
            if (File.Exists(metaFile)) File.Delete(metaFile);
            string assetFile = Path.ChangeExtension(slangFile, ".shader.asset");
            if (File.Exists(assetFile)) File.Delete(assetFile);
            string assetMetaFile = assetFile + ".meta";
            if (File.Exists(assetMetaFile)) File.Delete(assetMetaFile);
        }
    }
}
