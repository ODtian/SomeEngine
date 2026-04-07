using System;
using System.Diagnostics;
using System.Linq;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class SlangVersionTest
{
    [Fact]
    public void PrintVersion()
    {
        _ = SlangShaderImporter.GlobalSession;
            Console.WriteLine($"Slang API Version: {Slang.ApiVersion}");

            // Ensure Slang is loaded
            var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (module.ModuleName.Contains("slang", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Loaded Slang DLL: {module.FileName}");
                    Console.WriteLine($"Version Info: {module.FileVersionInfo.FileVersion}");
                }
            }
    }
}
