using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using SlangShaderSharp;

namespace SomeEngine.Tests;

[TestFixture]
public class SlangVersionTest
{
    [Test]
    public void PrintVersion()
    {
        TestContext.Out.WriteLine($"Slang API Version: {Slang.ApiVersion}");
        
        // Ensure Slang is loaded
        Slang.CreateGlobalSession(Slang.ApiVersion, out var _gs);

        var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            if (module.ModuleName.Contains("slang", StringComparison.OrdinalIgnoreCase))
            {
                TestContext.Out.WriteLine($"Loaded Slang DLL: {module.FileName}");
                TestContext.Out.WriteLine($"Version Info: {module.FileVersionInfo.FileVersion}");
            }
        }
    }
}
