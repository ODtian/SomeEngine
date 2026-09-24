using System.IO;
using System.Linq;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
namespace SomeEngine.Tests;

public class SlangIntegrationTests
{
    [Fact]
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

            Assert.NotNull(asset);
            Assert.Equal(Path.GetFileNameWithoutExtension(slangFile), asset.Name);

            // Check variants
            // We expect 2 entry points * 2 targets = 4 variants
            Assert.Equal(4, asset.Variants!.Count);

            var vsDxil = asset.Variants.FirstOrDefault(v => v.EntryPoint == "vertexMain" && v.Backend == "dxil");
            Assert.NotNull(vsDxil);
            Assert.Equal(ShaderStage.Vertex, vsDxil.Stage);
            Assert.True(vsDxil.Data.HasValue && vsDxil.Data.Value.Length > 0);

            var psSpirv = asset.Variants.FirstOrDefault(v => v.EntryPoint == "pixelMain" && v.Backend == "spirv");
            Assert.NotNull(psSpirv);
            Assert.Equal(ShaderStage.Pixel, psSpirv.Stage);
            Assert.True(psSpirv.Data.HasValue && psSpirv.Data.Value.Length > 0);
        }
        finally
        {
            if (File.Exists(slangFile)) File.Delete(slangFile);
        }
    }
    [Fact]
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
            Assert.NotNull(reflection);
            Assert.NotNull(reflection!.Resources);

            // Check gParams
            var gParams = reflection.Resources!.FirstOrDefault(r => r.Name == "gParams");
            Assert.NotNull(gParams);
            Assert.True((gParams.Stages & 0x02) != 0, "Should be visible in Pixel stage (0x02)");

            // Check gTexture
            var gTexture = reflection.Resources.FirstOrDefault(r => r.Name == "gTexture");
            Assert.NotNull(gTexture);
            Assert.True((gTexture!.Stages & 0x02) != 0, "Should be visible in Pixel stage (0x02)");

            // Print all resources for manual verification
            Console.WriteLine($"--- Layout for {asset.Name} ---");
            foreach (var r in reflection.Resources)
            {
                Console.WriteLine($"  Name: {r.Name}, Stages: 0x{r.Stages:X}");
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void TestSlangImport_GeneratesStableAssetGuidAndMeta()
    {
        string source = @"
            [shader(""compute"")]
            [numthreads(1,1,1)]
            void main() {}
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string slangFile = Path.Combine(dir, "stable_guid.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset1 = SlangShaderImporter.Import(slangFile);
            var asset2 = SlangShaderImporter.Import(slangFile);

            Assert.True(AssetGuid.TryParse(asset1.AssetGuid, out var guid1));
            Assert.True(AssetGuid.TryParse(asset2.AssetGuid, out var guid2));
            Assert.Equal(guid2, guid1);
            Assert.True(File.Exists(SourceMetaFiles.GetMetaPath(slangFile)));
            string assetPath = Path.ChangeExtension(Path.GetFullPath(slangFile), ".shader.asset");
            Assert.True(File.Exists(AssetMetaFiles.GetMetaPath(assetPath)));
            Assert.NotNull(asset1.ImportTrace);
            Assert.NotEmpty(asset1.ImportTrace!.SourceGuid);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void TestSlangImport_TracksIncludeDependencies_AndKeepsAssetGuidStable()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        string includeFile = Path.Combine(dir, "common_inc.slang");
        string sourceFile = Path.Combine(dir, "with_include.slang");

        File.WriteAllText(includeFile, "float4 GetColor() { return float4(1, 0, 0, 1); }");
        File.WriteAllText(sourceFile, "#include \"common_inc.slang\"\n[shader(\"pixel\")] float4 main() : SV_Target { return GetColor(); }");

        try
        {
            var asset1 = SlangShaderImporter.Import(sourceFile);
            Assert.True(AssetGuid.TryParse(asset1.AssetGuid, out var guid1));
            Assert.NotNull(asset1.ImportTrace);
            Assert.NotNull(asset1.ImportTrace!.Dependencies);
            Assert.True(asset1.ImportTrace.Dependencies!.Count >= 2);
            Assert.True(asset1.ImportTrace.Dependencies.Any(d => d.Path == "common_inc.slang" || d.Path!.EndsWith("/common_inc.slang")));

            string fingerprint1 = asset1.ImportTrace.ContentFingerprint!;

            File.WriteAllText(includeFile, "float4 GetColor() { return float4(0, 1, 0, 1); }");

            var asset2 = SlangShaderImporter.Import(sourceFile);
            Assert.True(AssetGuid.TryParse(asset2.AssetGuid, out var guid2));
            Assert.Equal(guid1, guid2);
            Assert.NotNull(asset2.ImportTrace);
            Assert.NotEqual(fingerprint1, asset2.ImportTrace!.ContentFingerprint);
            Assert.True(asset2.ImportTrace.Dependencies.Any(d => d.Path == "common_inc.slang" || d.Path!.EndsWith("/common_inc.slang")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void ContentHash_IdenticalLogicInDifferentFiles_ProducesSameHash()
    {
        // 两个不同文件，相同的 compute shader 逻辑 → content hash 应该一致
        string source = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "shader_a.slang");
        string fileB = Path.Combine(dir, "shader_b.slang");
        File.WriteAllText(fileA, source);
        File.WriteAllText(fileB, source);

        try
        {
            var assetA = SlangShaderImporter.Import(fileA);
            var assetB = SlangShaderImporter.Import(fileB);

            Assert.NotNull(assetA.Variants);
            Assert.NotNull(assetB.Variants);

            // 验证每个 backend 的 content hash 都一致
            var hashesA = assetA.Variants!
                .Where(v => v.EntryPoint == "CSMain")
                .ToDictionary(v => v.Backend!, v => v.ContentHash);
            var hashesB = assetB.Variants!
                .Where(v => v.EntryPoint == "CSMain")
                .ToDictionary(v => v.Backend!, v => v.ContentHash);

            Assert.True(hashesA.Count > 0, "Should have at least one CSMain variant");

            foreach (var kvp in hashesA)
            {
                Assert.True(hashesB.ContainsKey(kvp.Key), $"Backend {kvp.Key} missing from shader_b");
                Assert.Equal(kvp.Value, hashesB[kvp.Key]);
                Assert.NotNull(kvp.Value);
                Assert.NotEmpty(kvp.Value!);

                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ContentHash_DifferentLogic_ProducesDifferentHash()
    {
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 3.0 + 1.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "shader_same.slang");
        string fileB = Path.Combine(dir, "shader_diff.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            var assetA = SlangShaderImporter.Import(fileA);
            var assetB = SlangShaderImporter.Import(fileB);

            var spirvA = assetA.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == "spirv");
            var spirvB = assetB.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == "spirv");

            Assert.NotNull(spirvA);
            Assert.NotNull(spirvB);
            Assert.NotEqual(spirvB!.ContentHash, spirvA!.ContentHash);

            Console.WriteLine($"  A: {spirvA.ContentHash}");
            Console.WriteLine($"  B: {spirvB.ContentHash}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ContentHash_DifferentLocalVarNames_SameLogic_SameHash()
    {
        // 局部变量名不同但逻辑相同 → 编译后应产生相同字节码
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                float value = float(tid.x) * 2.0;
                outputBuffer[tid.x] = value;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                float result = float(tid.x) * 2.0;
                outputBuffer[tid.x] = result;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "varname_a.slang");
        string fileB = Path.Combine(dir, "varname_b.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            var assetA = SlangShaderImporter.Import(fileA);
            var assetB = SlangShaderImporter.Import(fileB);

            foreach (var backend in new[] { "spirv", "dxil" })
            {
                var varA = assetA.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == backend);
                var varB = assetB.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == backend);
                if (varA == null || varB == null) continue;

                Console.WriteLine($"  [{backend}] A: {varA.ContentHash}");
                Console.WriteLine($"  [{backend}] B: {varB.ContentHash}");
                Assert.Equal(varB.ContentHash, varA.ContentHash);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ContentHash_UnusedExtraBuffer_SameHash()
    {
        // 一个文件多了一个未使用的 buffer 声明 → DCE 应剥离 → hash 相同
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;
            RWStructuredBuffer<float> unusedBuffer;  // 额外声明，但未使用

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "no_extra.slang");
        string fileB = Path.Combine(dir, "with_extra.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            var assetA = SlangShaderImporter.Import(fileA);
            var assetB = SlangShaderImporter.Import(fileB);

            foreach (var backend in new[] { "spirv", "dxil" })
            {
                var varA = assetA.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == backend);
                var varB = assetB.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == backend);
                if (varA == null || varB == null) continue;

                Console.WriteLine($"  [{backend}] A: {varA.ContentHash}");
                Console.WriteLine($"  [{backend}] B: {varB.ContentHash}");
                Assert.Equal(varB.ContentHash, varA.ContentHash);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ContentHash_DifferentUnusedBufferLayout_SameHash()
    {
        // 两个文件都有一个未使用的 buffer，但布局不同 → DCE 剥离后 hash 应相同
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            struct ExtraA { float x; float y; };
            RWStructuredBuffer<ExtraA> unusedBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;

            struct ExtraB { int a; int b; int c; };
            RWStructuredBuffer<ExtraB> unusedBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "layout_a.slang");
        string fileB = Path.Combine(dir, "layout_b.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            var assetA = SlangShaderImporter.Import(fileA);
            var assetB = SlangShaderImporter.Import(fileB);

            foreach (var backend in new[] { "spirv", "dxil" })
            {
                var varA = assetA.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == backend);
                var varB = assetB.Variants!.FirstOrDefault(v => v.EntryPoint == "CSMain" && v.Backend == backend);
                if (varA == null || varB == null) continue;

                Console.WriteLine($"  [{backend}] A: {varA.ContentHash}");
                Console.WriteLine($"  [{backend}] B: {varB.ContentHash}");
                Assert.Equal(varB.ContentHash, varA.ContentHash);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
