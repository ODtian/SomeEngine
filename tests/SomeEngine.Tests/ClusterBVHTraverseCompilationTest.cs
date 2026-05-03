using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class ClusterBVHTraverseCompilationTest
{
    [Fact]
    public void ClusterBVHTraverse_CompilesSuccessfully()
    {
        string source = """
            #include "cluster_bvh_traverse.slang"
        """;

        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, "_test_cluster_bvh_traverse.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            string[] entryPoints = ["main", "UpdateArgs", "InitArgs", "InitQueue"];
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
    public void ClusterBVHTraverse_ExpandsFrustumAndLodBoundsFromInstanceHeader()
    {
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
        string source = File.ReadAllText(Path.Combine(shaderDir, "cluster_bvh_traverse.slang"));

        Assert.Contains("ByteAddressBuffer InstanceHeaders", source);
        Assert.Contains("LoadInstanceBoundsExpansionWorld(InstanceHeaders, instanceID)", source);
        Assert.Contains("LocalExpansionForWorldRadius", source);
        Assert.Contains("IsNodeOutsideFrustum(node, t, boundsExpansion)", source);
        Assert.Contains("float worldRadius = node.LODSphere.w * maxScale;", source);
    }
}
