using System.IO;
using System.Linq;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class WaveQueueCompilationTest
{
    [Fact]
    public void WaveQueue_CompilesWithTrivialTask()
    {
        // Inline Slang source that includes wave_queue.slang and implements a trivial IWaveTask.
        // Each lane "produces" 1 task that writes lane index to a UAV buffer.
        string source = """
            #include "wave_queue.slang"

            RWStructuredBuffer<uint> OutputBuffer;

            struct TrivialTask : IWaveTask
            {
                uint value;

                uint GetTaskCount() { return 1; }

                [mutating]
                void ExecuteTask(uint srcLane, uint localIdx, bool bActive)
                {
                    if (!bActive) return;
                    uint v = WaveReadLaneAt(value, srcLane);
                    OutputBuffer[WaveGetLaneIndex()] = v;
                }
            };

            [shader("compute")]
            [numthreads(32, 1, 1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                TrivialTask task;
                task.value = tid.x;
                WaveQueue::Distribute(task);
            }
        """;

        // Write to the same directory as wave_queue.slang so #include resolves.
        // TestDirectory = tests/SomeEngine.Tests/bin/x64/Debug/net10.0/
        // Need to go up 6 levels to reach project root, then into assets/Shaders
        string shaderDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "assets", "Shaders"));

        string slangFile = Path.Combine(shaderDir, "_test_wave_queue_trivial.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.NotEmpty(asset.Variants!);

            // At least SPIR-V should compile. DXIL requires DXC which may not be available.
            var csSpirv = asset.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == "spirv");
            Assert.NotNull(csSpirv);
            Assert.True(csSpirv!.Data.HasValue && csSpirv.Data.Value.Length > 0, "SPIR-V bytecode should be non-empty");

            var csDxil = asset.Variants.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == "dxil");
            if (csDxil != null)
            {
                Assert.True(csDxil.Data.HasValue && csDxil.Data.Value.Length > 0, "DXIL bytecode should be non-empty");
            }
            else
            {
                Console.WriteLine("WARNING: DXIL variant not produced (DXC not found). SPIR-V OK.");
            }

            Console.WriteLine($"WaveQueue compilation OK: {asset.Variants.Count} variants");
            foreach (var v in asset.Variants)
            {
                Console.WriteLine($"  {v.Backend} / {v.Stage} / {v.EntryPoint}: {v.Data?.Length ?? 0} bytes");
            }
        }
        finally
        {
            if (File.Exists(slangFile)) File.Delete(slangFile);
            // Also clean up generated .asset and meta files
            string assetFile = Path.ChangeExtension(slangFile, ".shader.asset");
            if (File.Exists(assetFile)) File.Delete(assetFile);
            string assetMetaFile = assetFile + ".meta";
            if (File.Exists(assetMetaFile)) File.Delete(assetMetaFile);
        }
    }
}
