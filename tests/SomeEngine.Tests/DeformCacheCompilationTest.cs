using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class DeformCacheCompilationTest
{
    private string GetShaderDir() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

    private void CompileAndAssert(string filename, string source, params string[] expectedEntryPoints)
    {
        string shaderDir = GetShaderDir();
        string slangFile = Path.Combine(shaderDir, filename);
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);
            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            foreach (var ep in expectedEntryPoints)
            {
                var variant = asset.Variants!.FirstOrDefault(v => v.EntryPoint == ep && v.Backend == "spirv");
                Assert.NotNull(variant);
                Assert.True(variant!.Data.HasValue && variant.Data.Value.Length > 0, $"SPIR-V bytecode should be non-empty for {ep}");
            }

            Console.WriteLine($"DeformCache compilation OK: {asset.Variants!.Count} variants");
            foreach (var v in asset.Variants)
                Console.WriteLine($"  {v.Backend} / {v.Stage} / {v.EntryPoint}: {v.Data?.Length ?? 0} bytes");
        }
        finally
        {
            if (File.Exists(slangFile)) File.Delete(slangFile);
            foreach (var ext in new[] { ".meta", ".shader.asset", ".shader.asset.meta" })
            {
                string f = ext == ".meta" ? slangFile + ext : Path.ChangeExtension(slangFile, ext);
                if (File.Exists(f)) File.Delete(f);
            }
        }
    }

    [Fact]
    public void CSSWRasterCached_CompilesSuccessfully()
    {
        CompileAndAssert("_test_sw_raster_cached.slang",
            """#include "sw_raster.slang" """,
            "CSSWRasterCached");
    }

    [Fact]
    public void CSDeform_EntryPoints_CompileSuccessfully()
    {
        CompileAndAssert("_test_cluster_deform.slang",
            """#include "cluster_deform.slang" """,
            "CSDeformPrepareVisibleArgs", "CSDeformInitVisible");
    }

    [Fact]
    public void VSVisBufferCached_CompilesSuccessfully()
    {
        CompileAndAssert("_test_visbuffer_cached.slang",
            """#include "cluster_draw.slang" """,
            "VSVisBufferCached");
    }
}
