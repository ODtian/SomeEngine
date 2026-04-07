using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class VertexEvaluateCompilationTest
{
    [Fact]
    public void SWRaster_WithInlineVertexEval_CompilesSuccessfully()
    {
        // Test that CSSWRaster using InlineSource<StaticVertexEval> compiles.
        // This validates the IVertexEvaluate + IVertexSource interface chain.
        string source = """
            #include "sw_raster.slang"
        """;

        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, "_test_vertex_evaluate.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            var csSpirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == "CSSWRaster" && v.Backend == "spirv");
            Assert.NotNull(csSpirv);
            Assert.True(csSpirv!.Data.HasValue && csSpirv.Data.Value.Length > 0, "SPIR-V bytecode should be non-empty");

            Console.WriteLine($"VertexEvaluate compilation OK: {asset.Variants.Count} variants");
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

    [Fact]
    public void CustomVertexEvaluate_CompilesSuccessfully()
    {
        // Test a custom IVertexEvaluate with a StructuredBuffer field.
        string source = """
            #include "sw_raster.slang"

            struct WPODeformedVertex
            {
                float3 position;
                float3 normal;
            };

            struct WPOVertexEval : IVertexEvaluate
            {
                StructuredBuffer<float4> NoiseData;

                typedef WPODeformedVertex DeformedVertex;

                DeformedVertex evaluate(VertexEvalContext ctx)
                {
                    DeformedVertex v;
                    float3 offset = float3(0, NoiseData[ctx.instanceID].x * 0.1, 0);
                    v.position = ctx.worldPos + offset;
                    v.normal = float3(0, 1, 0);
                    return v;
                }

                float3 getPosition(DeformedVertex v)
                {
                    return v.position;
                }

                // Cache: pack pos as half3 (8B), ignore normal for cache
                uint getCacheStride() { return 8; }
                void writeCache(RWByteAddressBuffer buf, uint addr, DeformedVertex v)
                {
                    buf.Store(addr,     f32tof16(v.position.x) | (f32tof16(v.position.y) << 16));
                    buf.Store(addr + 4, f32tof16(v.position.z));
                }
                DeformedVertex readCache(ByteAddressBuffer buf, uint addr)
                {
                    uint xy = buf.Load(addr);
                    uint z_ = buf.Load(addr + 4);
                    DeformedVertex v;
                    v.position = float3(f16tof32(xy), f16tof32(xy >> 16), f16tof32(z_));
                    v.normal = float3(0, 1, 0);
                    return v;
                }
            };

            [shader("compute")]
            [numthreads(32, 1, 1)]
            void CSCustomRaster(
                uniform InlineSource<WPOVertexEval> source,
                uint3 groupID : SV_GroupID,
                uint groupThreadIndex : SV_GroupThreadID)
            {
                SWRasterKernel(source, groupID, groupThreadIndex);
            }
        """;

        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, "_test_custom_vertex_eval.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            // Check for the custom entry point
            var customSpirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == "CSCustomRaster" && v.Backend == "spirv");
            Assert.NotNull(customSpirv);
            Assert.True(customSpirv!.Data.HasValue && customSpirv.Data.Value.Length > 0, "SPIR-V bytecode should be non-empty");

            Console.WriteLine($"Custom VertexEvaluate compilation OK: {asset.Variants.Count} variants");
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
