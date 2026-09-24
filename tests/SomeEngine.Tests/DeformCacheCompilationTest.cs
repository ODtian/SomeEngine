using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;
namespace SomeEngine.Tests;

public class DeformCompileTests
{
    private void CompileOk(string shaderFile, params string[] expectedEntryPoints)
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath(shaderFile));
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

    [Fact]
    public void SwRasterCompiles()
    {
        CompileOk("sw_raster.slang",
            "CSSWRasterCached");
    }

    [Fact]
    public void DeformCompiles()
    {
        CompileOk("cluster_deform.slang",
            "CSDeformStatic",
            "CSDeformWave");
    }

    [Fact]
    public void DeformTailGuard()
    {
        string shaderPath = TestProjectPaths.ShaderPath("cluster_deform.slang");
        string source = File.ReadAllText(shaderPath);

        Assert.Contains("if (flatIndex >= binMeta.BinCount)", source);
    }

    [Fact]
    public void UsesDirectAllocation()
    {
        string shaderPath = TestProjectPaths.ShaderPath("cluster_deform.slang");
        string source = File.ReadAllText(shaderPath);

        Assert.Contains("uint requestBytesRaw = evaluator.getCacheByteSize(vertexCount);", source);
        Assert.Contains("InterlockedAdd(CacheAllocationCounter[0], requestBytes", source);
        Assert.DoesNotContain("CacheRequestBytes", source);
        Assert.DoesNotContain("CacheBlockSums", source);
        Assert.DoesNotContain("CacheBlockOffsets", source);
        Assert.DoesNotContain("vertexCount * Uniforms.CacheStrideBytes", source);
        Assert.DoesNotContain("CacheStrideBytes", source);
        Assert.DoesNotContain("getCacheStride", source);
    }

    [Fact]
    public void CacheOffsetsCommit()
    {
        string shaderPath = TestProjectPaths.ShaderPath("cluster_deform.slang");
        string source = File.ReadAllText(shaderPath);

        Assert.Contains("CacheOffsetsWrite[visibleIndex] = cacheBaseByte;", source);
        Assert.Contains("[numthreads(64, 1, 1)]", source);
    }

    [Fact]
    public void BinningEmitsOnce()
    {
        string shaderPath = TestProjectPaths.ShaderPath("cluster_deform_binning.slang");
        string source = File.ReadAllText(shaderPath);
        string io = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_bin_io.slang"));

        Assert.Contains("#include \"cluster_bin_io.slang\"", source);
        Assert.Contains("ScatterDeform(", source);
        Assert.Contains("indices[offset + local] = uint2(visible, 0u);", io);
        Assert.Contains("offsets[visible] = sentinel;", io);
        Assert.DoesNotContain("triStart << 16", source);
    }

    [Fact]
    public void VisBufferCompiles()
    {
        CompileOk("cluster_draw.slang",
            "VSVisBufferCached");
    }

    [Fact]
    public void StreamCursorTotal()
    {
        string shaderPath = TestProjectPaths.ShaderPath("cluster_draw.slang");
        string source = File.ReadAllText(shaderPath);

        Assert.Contains("totalVertexCount = PageHeap.Load(pageOffset + 4u);", source);
        Assert.Contains("ctx.totalVertexCount = totalVertexCount;", source);
        Assert.DoesNotContain("ctx.totalVertexCount = vertexCount;", source);
        Assert.DoesNotContain("VertexFetchArgs", source);
    }

}
