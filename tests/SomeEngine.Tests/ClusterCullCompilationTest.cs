using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using SomeEngine.Render.Pipelines;

namespace SomeEngine.Tests;

public class ClusterCullCompilationTest
{
    [Fact]
    public void ClusterCull_CompilesSuccessfully()
    {
        string source = """
            #include "cluster_cull.slang"
        """;

        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, "_test_cluster_cull.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            string[] entryPoints = ["UpdateIndirectArgs", "main_phase1", "main_phase2"];
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

            string metaFile = slangFile + ".meta";
            if (File.Exists(metaFile)) File.Delete(metaFile);

            string assetFile = Path.ChangeExtension(slangFile, ".shader.asset");
            if (File.Exists(assetFile)) File.Delete(assetFile);

            string assetMetaFile = assetFile + ".meta";
            if (File.Exists(assetMetaFile)) File.Delete(assetMetaFile);
        }
    }

    [Fact]
    public void ClusterCull_UsesSingleAabbPathWithOptionalBoundsExpansion()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string source = File.ReadAllText(Path.Combine(shaderDir, "cluster_cull.slang"));

        Assert.Contains("BuildClusterAabbScreenBoundsAndNearDepth", source);
        Assert.Contains("float4x4 viewProjMat = usePrev ? Uniforms.PrevViewProj : Uniforms.ViewProj;", source);
        Assert.Contains("worldMin -= expansion.xxx;", source);
        Assert.Contains("worldMax += expansion.xxx;", source);
        Assert.DoesNotContain("BuildClusterVertexScreenBoundsAndNearDepth", source);
        Assert.DoesNotContain("BuildScreenBoundsAndNearDepth", source);
    }

    [Fact]
    public void ClusterCull_LODFilterDoesNotUseBoundsExpansion()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string source = File.ReadAllText(Path.Combine(shaderDir, "cluster_cull.slang"));

        Assert.Contains("float worldRadius = cluster.LODRadius * maxScale;", source);
        Assert.DoesNotContain("cluster.LODRadius * maxScale + boundsExpansion", source);
    }

    [Fact]
    public void ClusterCull_HiZBoundsAreBuiltFromClusterAabb()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string source = File.ReadAllText(Path.Combine(shaderDir, "cluster_cull.slang"));

        Assert.Contains("BuildClusterAabbScreenBoundsAndNearDepth", source);
        Assert.Contains("float3 localMin = cluster.BoundMin;", source);
        Assert.Contains("float3 localMax = cluster.BoundMax;", source);
        Assert.DoesNotContain("FetchClusterVertexPosition", source);
        Assert.DoesNotContain("localMin = min(localMin, localPos);", source);
        Assert.DoesNotContain("localMax = max(localMax, localPos);", source);
    }

    [Fact]
    public void GPUClusterLayout_MatchesShaderStrideAndBoundsOffsets()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string structures = File.ReadAllText(Path.Combine(shaderDir, "cluster_structures.slang"));

        Assert.Equal(88, GPUCluster.SizeInBytes);
        Assert.Equal(GPUCluster.SizeInBytes, Marshal.SizeOf<GPUCluster>());
        Assert.Equal(64, Marshal.OffsetOf<GPUCluster>(nameof(GPUCluster.BoundMin)).ToInt32());
        Assert.Equal(76, Marshal.OffsetOf<GPUCluster>(nameof(GPUCluster.BoundMax)).ToInt32());
        Assert.Contains("static const uint GPU_CLUSTER_STRIDE_BYTES = 88;", structures);
        Assert.Contains("float3 BoundMin;", structures);
        Assert.Contains("float3 BoundMax;", structures);
    }

    [Fact]
    public void ClusterDebugSphere_CompilesAndUsesCurrentGPUClusterStride()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string source = File.ReadAllText(Path.Combine(shaderDir, "debug_sphere.hlsl"));
        string common = File.ReadAllText(Path.Combine(shaderDir, "common.hlsl"));

        Assert.Contains("#define GPU_CLUSTER_STRIDE_BYTES 88", common);
        Assert.Contains("uint clusterStride = GPU_CLUSTER_STRIDE_BYTES;", source);

        string hlslFile = Path.Combine(shaderDir, "_test_debug_sphere.hlsl");
        File.WriteAllText(hlslFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(hlslFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            Assert.Contains(asset.Variants!, v => v.EntryPoint == "VSMain" && v.Backend == "spirv" && v.Data.HasValue && v.Data.Value.Length > 0);
            Assert.Contains(asset.Variants!, v => v.EntryPoint == "PSMain" && v.Backend == "spirv" && v.Data.HasValue && v.Data.Value.Length > 0);
        }
        finally
        {
            if (File.Exists(hlslFile)) File.Delete(hlslFile);

            string metaFile = hlslFile + ".meta";
            if (File.Exists(metaFile)) File.Delete(metaFile);

            string assetFile = Path.ChangeExtension(hlslFile, ".shader.asset");
            if (File.Exists(assetFile)) File.Delete(assetFile);

            string assetMetaFile = assetFile + ".meta";
            if (File.Exists(assetMetaFile)) File.Delete(assetMetaFile);
        }
    }

    [Fact]
    public void ClusterCull_DeformBoundsExpansionUsesInstanceHeaderAndSameAabbPath()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string source = File.ReadAllText(Path.Combine(shaderDir, "cluster_cull.slang"));

        Assert.Contains("ByteAddressBuffer InstanceHeaders", source);
        Assert.Contains("LoadInstanceBoundsExpansionWorld(InstanceHeaders, instanceID)", source);
        Assert.Contains("BuildClusterAabbScreenBoundsAndNearDepth", source);
        Assert.Contains("BuildWorldAabbScreenBoundsAndNearDepth", source);
        Assert.DoesNotContain("DecodeClusterRadius(cluster, Uniforms.QuantStep) * maxScale + boundsExpansion", source);
        Assert.DoesNotContain("camForward", source);
    }

    [Fact]
    public void ClusterCull_HiZDebugDumpUsesSharedHeaderAndDetailedSamples()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string source = File.ReadAllText(Path.Combine(shaderDir, "cluster_cull.slang"));
        string aabbSource = File.ReadAllText(Path.Combine(shaderDir, "debug_aabb.slang"));

        Assert.Equal(128u, ClusterHiZDebugLayout.HeaderBytes);
        Assert.Equal(80u, ClusterHiZDebugLayout.SampleStrideBytes);
        Assert.Equal(128u + 4096u * 80u, ClusterHiZDebugLayout.BufferBytes);
        Assert.Contains("static const uint DEBUG_HIZ_HEADER_BYTES = 128", source);
        Assert.Contains("static const uint DEBUG_HIZ_SAMPLE_STRIDE_BYTES = 80", source);
        Assert.Contains("DEBUG_WORD_PHASE1_NO_HISTORY", source);
        Assert.Contains("DEBUG_WORD_PHASE2_CULLED", source);
        Assert.Contains("DebugStoreHiZSample(", source);
        Assert.Contains("uint base = 128 + entryIdx * 80", aabbSource);
    }
}
