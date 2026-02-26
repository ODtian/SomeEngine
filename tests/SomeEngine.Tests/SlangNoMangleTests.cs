using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using SlangShaderSharp;

namespace SomeEngine.Tests;

public class SlangNoMangleTests
{
    private IGlobalSession? _globalSession;

    [OneTimeSetUp]
    public void Setup()
    {
        Slang.CreateGlobalSession(Slang.ApiVersion, out _globalSession!);
    }

    [Test]
    public unsafe void TestSlangNoMangleHlslExport()
    {
        string slangFilePath = @"d:\SomeEngine\assets\Shaders\cluster_draw.slang";

        Assert.That(
            File.Exists(slangFilePath),
            Is.True,
            $"Slang file not found at {slangFilePath}"
        );

        // 1. Setup TargetDesc with NoMangle
        // var hlslProfile = _globalSession!.FindProfile("sm_6_5");
        var profile = _globalSession.FindProfile("sm_6_5");

        var options = new[]
        {
            new CompilerOptionEntry(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1)),
            new CompilerOptionEntry(
                CompilerOptionName.VulkanEmitReflection,
                CompilerOptionValue.FromInt(1)
            ),
        };

        var target = new TargetDesc
        {
            Format = SlangCompileTarget.DxilAsm,
            Profile = profile,
            CompilerOptionEntries = options,
        };

        // 2. Create Session
        var sessionDesc = new SessionDesc
        {
            Targets = new[] { target },
            SearchPaths = new[] { Path.GetDirectoryName(slangFilePath)! },
        };

        var res = _globalSession.CreateSession(sessionDesc, out var session);

        Assert.That(session, Is.Not.Null);

        // 3. Load Module
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
            Assert.Fail($"Failed to load module: {diagStr}");
        }

        // 4. Link for VSMain and PSMain
        string[] entryPoints = { "VSMain", "PSMain" };
        foreach (var epName in entryPoints)
        {
            module!.FindEntryPointByName(epName, out var entryPoint);
            Assert.That(entryPoint, Is.Not.Null, $"Entry point {epName} not found");

            session.CreateCompositeComponentType(
                new[] { (IComponentType)module, entryPoint },
                out var composedLine,
                out var diag1
            );
            Assert.That(
                composedLine,
                Is.Not.Null,
                $"Failed to compose {epName}: {GetString(diag1)}"
            );

            composedLine.Link(out var linkedProgram, out var diag2);
            Assert.That(linkedProgram, Is.Not.Null, $"Failed to link {epName}: {GetString(diag2)}");

            // 5. Get compiled asm
            linkedProgram.GetEntryPointCode(0, 0, out var codeBlob, out var diag3);
            Assert.That(
                codeBlob,
                Is.Not.Null,
                $"Failed to get code for {epName}: {GetString(diag3)}"
            );

            string hlslCode = GetString(codeBlob)!;

            TestContext.Out.WriteLine($"--- Decompiled ASM Code for {epName} ---");
            TestContext.Out.WriteLine(hlslCode);
            TestContext.Out.WriteLine("------------------------------------------");

            Assert.That(hlslCode.Contains(epName) || hlslCode.Contains("OpFunction"), Is.True);
        }
    }

    private static string? GetString(ISlangBlob? blob)
    {
        if (blob == null)
            return null;

        return blob.AsString; // 使用了Span Api/安全的扩展方法，不再使用unsafe
    }
}
