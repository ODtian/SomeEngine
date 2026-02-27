using System.IO;
using System.Linq;
using NUnit.Framework;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Tests;

public class SlangIntegrationTests
{
    [Test]
    public void TestSlangCompilation()
    {
        string source = @"
            [shader(""vertex"")]
            float4 vertexMain(float4 pos : POSITION) : SV_Position
            {
                return pos;
            }

            [shader(""pixel"")]
            float4 pixelMain() : SV_Target
            {
                return float4(1.0, 0.0, 0.0, 1.0);
            }
        ";

        string tempFile = Path.GetTempFileName();
        string slangFile = Path.ChangeExtension(tempFile, ".slang");
        // Ensure unique name to avoid module conflicts if run multiple times in same session?
        // But we create new session each time (except global session).
        // Slang module names must be unique within a session? Yes.
        // But Import creates a new session every time.

        File.WriteAllText(slangFile, source);
        // Clean up tempFile if it still exists (GetTempFileName creates it)
        if (File.Exists(tempFile)) File.Delete(tempFile);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile);

            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.Name, Is.EqualTo(Path.GetFileNameWithoutExtension(slangFile)));

            // Check variants
            // We expect 2 entry points * 2 targets = 4 variants
            Assert.That(asset.Variants!.Count, Is.EqualTo(4));

            var vsDxil = asset.Variants.FirstOrDefault(v => v.EntryPoint == "vertexMain" && v.Backend == "dxil");
            Assert.That(vsDxil, Is.Not.Null, "Vertex DXIL variant not found");
            Assert.That(vsDxil.Stage, Is.EqualTo(ShaderStage.Vertex));
            Assert.That(vsDxil.Data.HasValue && vsDxil.Data.Value.Length > 0, Is.True);

            var psSpirv = asset.Variants.FirstOrDefault(v => v.EntryPoint == "pixelMain" && v.Backend == "spirv");
            Assert.That(psSpirv, Is.Not.Null, "Pixel SPIR-V variant not found");
            Assert.That(psSpirv.Stage, Is.EqualTo(ShaderStage.Pixel));
            Assert.That(psSpirv.Data.HasValue && psSpirv.Data.Value.Length > 0, Is.True);
        }
        finally
        {
            if (File.Exists(slangFile)) File.Delete(slangFile);
        }
    }
    [Test]
    public void TestSlangReflection()
    {
        string source = @"
            struct Params {
                float4 color;
            };

            [[vk::binding(0, 0)]]
            ConstantBuffer<Params> gParams;

            [[vk::binding(1, 0)]]
            Texture2D gTexture;

            [shader(""pixel"")]
            float4 main() : SV_Target
            {
                return gTexture.Load(int3(0, 0, 0)) * gParams.color;
            }
        ";

        string tempFile = Path.Combine(Path.GetTempPath(), "test_reflection.slang");
        File.WriteAllText(tempFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(tempFile);

            var reflection = asset.Reflections?.FirstOrDefault()?.Reflection;
            Assert.That(reflection, Is.Not.Null);
            Assert.That(reflection!.Resources, Is.Not.Null);

            // Check gParams
            var gParams = reflection.Resources!.FirstOrDefault(r => r.Name == "gParams");
            Assert.That(gParams, Is.Not.Null);
            Assert.That((gParams.Stages & 0x02) != 0, Is.True, "Should be visible in Pixel stage (0x02)");

            // Check gTexture
            var gTexture = reflection.Resources.FirstOrDefault(r => r.Name == "gTexture");
            Assert.That(gTexture, Is.Not.Null);
            Assert.That((gTexture!.Stages & 0x02) != 0, Is.True, "Should be visible in Pixel stage (0x02)");

            // Print all resources for manual verification
            TestContext.Out.WriteLine($"--- Layout for {asset.Name} ---");
            foreach (var r in reflection.Resources)
            {
                TestContext.Out.WriteLine($"  Name: {r.Name}, Stages: 0x{r.Stages:X}");
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
