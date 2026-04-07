using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class TestDisassemble
{
    [Fact]
    public void Run()
    {
        var globalSession = SlangShaderImporter.GlobalSession;
            var target = new TargetDesc { Format = SlangCompileTarget.Spirv };
            var sd = new SessionDesc { Targets = [target] };
            globalSession.CreateSession(sd, out var s);
            var src = "[shader(\"vertex\")] float4 VSMain() : SV_Position { return float4(0,0,0,1); }";
            var blob = Slang.CreateBlob(Encoding.UTF8.GetBytes(src));
            var m = s.LoadModuleFromSource("test", "test.slang", blob, out var diag);
            Assert.NotNull(m);
            m.FindEntryPointByName("VSMain", out var ep);
            Assert.NotNull(ep);
            s.CreateCompositeComponentType([(IComponentType)m, ep], out var comp, out _);
            Assert.NotNull(comp);
            comp.Link(out var lnk, out _);
            Assert.NotNull(lnk);

            lnk.GetEntryPointCode(0, 0, out var code, out _);
            Assert.NotNull(code);
            Assert.True((long)code.GetBufferSize() > 0, "SPIR-V bytecode should be non-empty");
            Console.WriteLine($"SPIR-V bytecode size: {code.GetBufferSize()} bytes");
    }
}
