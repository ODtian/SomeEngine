using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class ClusterBVHTraverseCompilationTest
{
    [Fact]
    public void ClusterBVHTraverse_CompilesSuccessfully()
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath("cluster_bvh_traverse.slang"));

        Assert.NotNull(asset);
        Assert.NotNull(asset.Variants);
        Assert.NotEmpty(asset.Variants!);

        string[] entryPoints = ["clear_args", "main"];
        foreach (string entryPoint in entryPoints)
        {
            var spirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == entryPoint && v.Backend == "spirv");
            Assert.NotNull(spirv);
            Assert.True(spirv!.Data.HasValue && spirv.Data.Value.Length > 0, $"SPIR-V bytecode should be non-empty for {entryPoint}");
        }
    }

    [Fact]
    public void ClusterBVHTraverse_ExpandsFrustumAndLodBoundsFromInstanceHeader()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_bvh_traverse.slang"));

        Assert.Contains("ByteAddressBuffer InstanceHeaders", source);
        Assert.Contains("LoadInstanceBoundsExpansionWorld(InstanceHeaders, instanceID)", source);
        Assert.Contains("LocalExpansionForWorldRadius", source);
        Assert.Contains("IsNodeOutsideFrustum(node, t, boundsExpansion)", source);
        Assert.Contains("float worldRadius = node.LODSphere.w * maxScale;", source);
        Assert.Contains("static const uint MaxTraverseStack", source);
        Assert.Contains("uint bvhRootIndex = LoadInstanceBVHRootIndex(InstanceHeaders, instanceID);", source);
        Assert.DoesNotContain("Queue_Current", source);
        Assert.DoesNotContain("NextDispatchArgs", source);
    }
}
