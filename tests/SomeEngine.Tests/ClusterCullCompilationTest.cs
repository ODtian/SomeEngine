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
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath("cluster_cull.slang"));

        Assert.NotNull(asset);
        Assert.NotNull(asset.Variants);
        Assert.NotEmpty(asset.Variants!);

        string[] entryPoints = ["clear_main_single", "main_single", "clear_main_phase1", "main_phase1", "clear_main_phase2", "main_phase2"];
        foreach (string entryPoint in entryPoints)
        {
            var spirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == entryPoint && v.Backend == "spirv");
            Assert.NotNull(spirv);
            Assert.True(spirv!.Data.HasValue && spirv.Data.Value.Length > 0, $"SPIR-V bytecode should be non-empty for {entryPoint}");
        }
    }

    [Fact]
    public void ClusterCull_UsesSingleAabbPathWithOptionalBoundsExpansion()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_cull.slang"));

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
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_cull.slang"));

        Assert.Contains("float worldRadius = cluster.LODRadius * maxScale;", source);
        Assert.DoesNotContain("cluster.LODRadius * maxScale + boundsExpansion", source);
    }

    [Fact]
    public void ClusterCull_HiZBoundsAreBuiltFromClusterAabb()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_cull.slang"));

        Assert.Contains("BuildClusterAabbScreenBoundsAndNearDepth", source);
        Assert.Contains("float3 localMin = cluster.BoundMin;", source);
        Assert.Contains("float3 localMax = cluster.BoundMax;", source);
        Assert.DoesNotContain("FetchClusterVertexPosition", source);
        Assert.DoesNotContain("localMin = min(localMin, localPos);", source);
        Assert.DoesNotContain("localMax = max(localMax, localPos);", source);
    }

    [Fact]
    public void ClusterCull_SingleMainConservativelyAppendsVisibleClusters()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_cull.slang"));
        int start = source.IndexOf("void main_single", StringComparison.Ordinal);
        int end = source.IndexOf("// HiZ follow-up cull:", StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        string singleMain = source[start..end];

        Assert.Contains("BuildClusterAabbScreenBoundsAndNearDepth", singleMain);
        Assert.Contains("false,", singleMain);
        Assert.Contains("AppendVisible(pageOffset, clusterID, instanceID, isSW);", singleMain);
        Assert.DoesNotContain("AppendPhase2Candidate", singleMain);
        Assert.DoesNotContain("IsOccludedByHiZ", singleMain);
    }

    [Fact]
    public void GPUClusterLayout_MatchesShaderStrideAndBoundsOffsets()
    {
        string structures = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_structures.slang"));

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
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("debug_sphere.slang"));
        string structures = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_structures.slang"));

        Assert.Contains("static const uint GPU_CLUSTER_STRIDE_BYTES = 88;", structures);
        Assert.Contains("#include \"cluster_structures.slang\"", source);
        Assert.Contains("uint clusterStride = GPU_CLUSTER_STRIDE_BYTES;", source);

        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath("debug_sphere.slang"));

        Assert.NotNull(asset);
        Assert.NotNull(asset.Variants);
        Assert.NotEmpty(asset.Variants!);

        Assert.Contains(asset.Variants!, v => v.EntryPoint == "VSMain" && v.Backend == "spirv" && v.Data.HasValue && v.Data.Value.Length > 0);
        Assert.Contains(asset.Variants!, v => v.EntryPoint == "PSMain" && v.Backend == "spirv" && v.Data.HasValue && v.Data.Value.Length > 0);
    }

    [Fact]
    public void ClusterCull_DeformBoundsExpansionUsesInstanceHeaderAndSameAabbPath()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_cull.slang"));

        Assert.Contains("ByteAddressBuffer InstanceHeaders", source);
        Assert.Contains("LoadInstanceBoundsExpansionWorld(InstanceHeaders, instanceID)", source);
        Assert.Contains("BuildClusterAabbScreenBoundsAndNearDepth", source);
        Assert.Contains("BuildWorldAabbScreenBoundsAndNearDepth", source);
        Assert.DoesNotContain("DecodeClusterRadius(cluster, Uniforms.QuantStep) * maxScale + boundsExpansion", source);
        Assert.DoesNotContain("camForward", source);
    }

    [Fact]
    public void ClusterCull_DoesNotOwnHiZDebugExportPath()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_cull.slang"));
        string stageSource = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "ClusterSceneStage.cs"));

        Assert.DoesNotContain("DebugHiZOutput", source);
        Assert.DoesNotContain("DebugStoreHiZSample", source);
        Assert.DoesNotContain("DebugHiZOutput", stageSource);
    }

    [Fact]
    public void ClusterCull_AtomicAppendCountersKeepTheirClearDependencies()
    {
        string source = File.ReadAllText(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "src",
            "SomeEngine.Render",
            "Pipelines",
            "ClusterPipeline",
            "ClusterSceneStage.cs"));

        Assert.Contains("builder.ReadWrite(traverse.IndirectDrawArgs, ResourceState.UnorderedAccess);", source);
        Assert.Contains("builder.Read(traverse.CandidateArgs, ResourceState.IndirectArgument);", source);
        Assert.Contains("builder.Read(traverse.CandidateCount, ResourceState.ShaderResource);", source);
        Assert.Contains("builder.ReadWrite(phase2CandidateCount, ResourceState.UnorderedAccess);", source);
        Assert.Contains("builder.ReadWrite(phase2CandidateArgs, ResourceState.UnorderedAccess);", source);
        Assert.Contains("builder.ReadWrite(cull.Phase2DrawArgs, ResourceState.UnorderedAccess);", source);
    }
}
