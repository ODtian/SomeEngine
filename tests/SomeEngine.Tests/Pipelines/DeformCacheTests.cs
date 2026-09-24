namespace SomeEngine.Tests.Pipelines;

public class DeformCacheTests
{
    [Fact]
    public void DeformUsesDirectAllocation()
    {
        string source = Shader("cluster_deform.slang");

        Assert.Contains("InterlockedAdd(CacheAllocationCounter[0], requestBytes", source);
        Assert.Contains("CacheOffsetsWrite[visibleIndex] = cacheBaseByte;", source);
        Assert.DoesNotContain("CacheRequestBytes", source);
        Assert.DoesNotContain("CacheBlockSums", source);
        Assert.DoesNotContain("CacheBlockOffsets", source);
    }

    [Fact]
    public void DeformHasOnlyMaterialEntries()
    {
        string source = Shader("cluster_deform.slang");

        Assert.Contains("void CSDeformStatic", source);
        Assert.Contains("void CSDeformWave", source);
        Assert.DoesNotContain("CSDeformPrepareVisibleArgs", source);
        Assert.DoesNotContain("CSDeformInitVisible", source);
        Assert.DoesNotContain("CSDeformCacheRequest", source);
        Assert.DoesNotContain("CSDeformCacheScan", source);
        Assert.DoesNotContain("CSDeformCacheApplyBlockOffsets", source);
        Assert.DoesNotContain("CSDeformCacheCommitAllocation", source);
    }

    [Fact]
    public void BinningInitializesCacheOffsets()
    {
        string source = Shader("cluster_binning.slang");

        Assert.Contains("static const uint CACHE_OFFSET_OVERFLOW = 0xFFFFFFFDu;", source);
        Assert.Contains("CacheOffsetsWrite[visibleIndex] = CACHE_OFFSET_OVERFLOW;", source);
        Assert.Contains("ScatterDeform(", source);
        Assert.Contains("CACHE_OFFSET_UNINITIALIZED", source);
    }

    [Fact]
    public void BinningEmitsOnce()
    {
        string source = Shader("cluster_deform_binning.slang");
        string io = Shader("cluster_bin_io.slang");

        Assert.Contains("#include \"cluster_bin_io.slang\"", source);
        Assert.Contains("ScatterDeform(", source);
        Assert.Contains("indices[offset + local] = uint2(visible, 0u);", io);
        Assert.Contains("offsets[visible] = sentinel;", io);
        Assert.DoesNotContain("triStart << 16", source);
    }

    private static string Shader(string name)
        => File.ReadAllText(TestProjectPaths.ShaderPath(name));
}
