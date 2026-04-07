using System;
using System.IO;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class SlangNoMangleTests
{
    [Fact]
    public void TestSlangNoMangleHlslExport()
    {
        var globalSession = SlangShaderImporter.GlobalSession;
            string shaderDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..", "assets", "Shaders"));
            string slangFilePath = Path.Combine(shaderDir, "simple_mesh.slang");

            Assert.True(File.Exists(slangFilePath), $"Slang file not found at {slangFilePath}");

            var profile = globalSession.FindProfile("sm_6_5");

            var options = new[]
            {
                new CompilerOptionEntry(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                new CompilerOptionEntry(
                    CompilerOptionName.VulkanEmitReflection,
                    CompilerOptionValue.FromInt(1, 0)
                ),
            };

            var target = new TargetDesc
            {
                Format = SlangCompileTarget.Hlsl,
                Profile = profile,
                CompilerOptionEntries = options,
            };

            var sessionDesc = new SessionDesc
            {
                Targets = [target],
                SearchPaths = [Path.GetDirectoryName(slangFilePath)!],
                CompilerOptionEntries = options,
            };

            var res = globalSession.CreateSession(sessionDesc, out var session);
            Assert.NotNull(session);

            var source = File.ReadAllText(slangFilePath);
            var blob = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
            var module = session.LoadModuleFromSource(
                "simple_mesh",
                slangFilePath,
                blob,
                out var diagnostics
            );

            if (module == null)
            {
                string? diagStr = GetString(diagnostics);
                throw new Xunit.Sdk.XunitException($"Failed to load module: {diagStr}");
            }

            string[] entryPoints = ["VSMain", "PSMain"];
            foreach (var epName in entryPoints)
            {
                module.FindEntryPointByName(epName, out var entryPoint);
                Assert.NotNull(entryPoint);

                session.CreateCompositeComponentType(
                    [module, entryPoint],
                    out var composedLine,
                    out var diag1
                );
                Assert.NotNull(composedLine);

                composedLine.Link(out var linkedProgram, out var diag2);
                Assert.NotNull(linkedProgram);

                linkedProgram.GetEntryPointCode(0, 0, out var codeBlob, out var diag3);
                Assert.NotNull(codeBlob);

                var hlslCode = codeBlob.AsString;

                Console.WriteLine($"--- Decompiled ASM Code for {epName} ---");
                Console.WriteLine(hlslCode);
                Console.WriteLine("------------------------------------------");

                Assert.True(hlslCode.Contains(epName) || hlslCode.Contains("OpFunction"));
            }
    }

    private static string? GetString(ISlangBlob? blob)
    {
        if (blob == null)
            return null;

        return blob.AsString; // 使用了Span Api/安全的扩展方法，不再使用unsafe
    }
}
