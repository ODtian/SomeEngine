using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class SWRasterCompilationTest
{
    [Fact]
    public void SWRaster_CompilesSuccessfully()
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath("sw_raster.slang"));

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
}
