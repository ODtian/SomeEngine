using System.IO;
using System.Text;
using NUnit.Framework;
using SlangShaderSharp;

namespace SomeEngine.Tests;

public class TestDisassemble
{
    [Test]
    public void Run()
    {
        Slang.CreateGlobalSession(Slang.ApiVersion, out var session);
        var target1 = new TargetDesc { Format = SlangCompileTarget.Spirv };
        var target2 = new TargetDesc { Format = SlangCompileTarget.SpirvAsm };
        var sd = new SessionDesc { Targets = new[] { target1, target2 } };
        session.CreateSession(sd, out var s);
        var src = "void VSMain() {}";
        var blob = Slang.CreateBlob(Encoding.UTF8.GetBytes(src));
        var m = s.LoadModuleFromSource("test", "test.slang", blob, out var diag);
        m.FindEntryPointByName("VSMain", out var ep);
        s.CreateCompositeComponentType(new[] { (IComponentType)m, ep }, out var comp, out _);
        comp.Link(out var lnk, out _);

        lnk.GetEntryPointCode(0, 0, out var code, out _);
        Assert.That(code, Is.Not.Null);

        lnk.GetEntryPointCode(0, 1, out var asmblob, out _);
        Assert.That(asmblob, Is.Not.Null);

        unsafe
        {
            var str = Encoding.UTF8.GetString(
                (byte*)asmblob.GetBufferPointer(),
                (int)asmblob.GetBufferSize()
            );
            TestContext.Out.WriteLine("ASM:\n" + str);
        }
    }
}
