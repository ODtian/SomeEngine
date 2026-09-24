using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class VertexEvaluateCompilationTest
{
    [Fact]
    public void SWRaster_WithInlineVertexEval_CompilesSuccessfully()
    {
        var asset = SlangShaderImporter.Import(TestProjectPaths.ShaderPath("sw_raster.slang"));

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

    [Fact]
    public void CustomVertexEvaluate_CompilesSuccessfully()
    {
        // Test a custom IVertexEvaluate with a StructuredBuffer field.
        string swRasterPath = TestProjectPaths.ShaderPath("sw_raster.slang").Replace('\\', '/');
        string source = $$"""
            #include "{{swRasterPath}}"

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
                    v.position = EvalWorldPosition(ctx) + offset;
                    v.normal = float3(0, 1, 0);
                    return v;
                }

                float3 getPosition(DeformedVertex v)
                {
                    return v.position;
                }

                uint getCacheByteSize(uint vertexCount) { return vertexCount * 16; }
                void writeCache(
                    RWByteAddressBuffer buf,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx,
                    DeformedVertex current,
                    DeformedVertex previous)
                {
                    uint addr = cacheBaseByte + localVertIdx * 16;
                    buf.Store(addr,      f32tof16(current.position.x) | (f32tof16(current.position.y) << 16));
                    buf.Store(addr + 4,  f32tof16(current.position.z));
                    buf.Store(addr + 8,  f32tof16(previous.position.x) | (f32tof16(previous.position.y) << 16));
                    buf.Store(addr + 12, f32tof16(previous.position.z));
                }
                DeformedVertex readCache(
                    ByteAddressBuffer buf,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx)
                {
                    uint addr = cacheBaseByte + localVertIdx * 16;
                    uint xy = buf.Load(addr);
                    uint z_ = buf.Load(addr + 4);
                    DeformedVertex v;
                    v.position = float3(f16tof32(xy), f16tof32(xy >> 16), f16tof32(z_));
                    v.normal = float3(0, 1, 0);
                    return v;
                }
                DeformedVertex readPreviousCache(
                    ByteAddressBuffer buf,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx)
                {
                    uint addr = cacheBaseByte + localVertIdx * 16;
                    uint xy = buf.Load(addr + 8);
                    uint z_ = buf.Load(addr + 12);
                    DeformedVertex v;
                    v.position = float3(f16tof32(xy), f16tof32(xy >> 16), f16tof32(z_));
                    v.normal = float3(0, 1, 0);
                    return v;
                }
            };

            [shader("compute")]
            [numthreads(32, 1, 1)]
            void CSCustomRaster(
                uniform WPOVertexEval eval,
                uint3 groupID : SV_GroupID,
                uint groupThreadIndex : SV_GroupThreadID)
            {
                SWRasterKernel(eval, false, groupID, groupThreadIndex);
            }
        """;

        string tempDir = Path.Combine(Path.GetTempPath(), "SomeEngine.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string slangFile = Path.Combine(tempDir, "custom_vertex_eval.slang");
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
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
