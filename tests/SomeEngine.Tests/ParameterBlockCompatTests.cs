using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SlangShaderSharp;

namespace SomeEngine.Tests;

/// <summary>
/// 验证 Slang ParameterBlock 反射能力。
/// 使用 SPIR-V target（Slang 内置后端，无需 dxcompiler.dll）。
/// </summary>
[TestFixture]
public class ParameterBlockCompatTests
{
    private IGlobalSession _gs = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        Slang.CreateGlobalSession(Slang.ApiVersion, out _gs);
    }

    [Test]
    public void ParameterBlock_ModuleReflection_ExtractsMaterialBindings()
    {
        var source = """
            [__AttributeUsage(_AttributeTargets.Struct)]
            struct PipelineTagAttribute { string tag; };

            [PipelineTag("ClusterShader")]
            struct PBRMaterialParams
            {
                Texture2D AlbedoMap;
                Texture2D NormalMap;
                Texture2D ARMMap;
                SamplerState MaterialSampler;
            };

            ConstantBuffer<float4> Uniforms;
            StructuredBuffer<uint> SomeBuffer;
            ParameterBlock<PBRMaterialParams> materialParams;

            [shader("compute")]
            [numthreads(64, 1, 1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                float4 u = Uniforms;
                uint s = SomeBuffer[tid.x];
                float4 albedo = materialParams.AlbedoMap.Load(int3(0,0,0));
            }
            """u8;

        var sessionDesc = new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = _gs.FindProfile("glsl_460") }],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.ColumnMajor,
            CompilerOptionEntries = [
                new CompilerOptionEntry(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                new CompilerOptionEntry(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
            ],
        };

        _gs.CreateSession(sessionDesc, out var session);
        var blob = Slang.CreateBlob(source);
        var module = session.LoadModuleFromSource("test_pb", "test_pb.slang", blob, out var diag);
        Assert.That(module, Is.Not.Null, $"Module load failed: {diag?.AsString}");

        // ── Module Reflection ──
        var moduleRefl = module.GetModuleReflection();
        Assert.That(moduleRefl, Is.Not.EqualTo(DeclReflection.Null));

        TestContext.Out.WriteLine($"Top-level declarations: {moduleRefl.Count}");

        bool foundPB = false;
        var matBindings = new System.Collections.Generic.List<string>();
        string? pbStructName = null;
        TypeReflection? pbType = null;

        for (uint i = 0; i < moduleRefl.Count; i++)
        {
            var decl = moduleRefl[(int)i];
            TestContext.Out.WriteLine($"  [{i}] {decl.Name} ({decl.Kind})");

            if (decl.Kind != DeclReflectionKind.Variable) continue;

            var v = decl.AsVariable();
            if (v == VariableReflection.Null) continue;

            var vt = v.Type;
            TestContext.Out.WriteLine($"       Type.Kind={vt.Kind}  Name={vt.Name}");

            if (vt.Kind == SlangTypeKind.ParameterBlock)
            {
                foundPB = true;
                var elem = vt.ElementType;
                pbStructName = elem.Name;
                pbType = elem;
                TestContext.Out.WriteLine($"       → ParameterBlock<{elem.Name}>");
                TestContext.Out.WriteLine($"         FieldCount={elem.FieldCount}  AttrCount={elem.AttributeCount}");

                for (uint f = 0; f < elem.FieldCount; f++)
                {
                    var field = elem.GetFieldByIndex(f);
                    matBindings.Add(field.Name);
                    TestContext.Out.WriteLine($"         Field[{f}]: {field.Name} (Kind={field.Type.Kind})");
                }
            }
        }

        // ── Assertions ──
        Assert.That(foundPB, Is.True, "Should detect ParameterBlock<T>");
        Assert.That(pbStructName, Is.EqualTo("PBRMaterialParams"));
        Assert.That(matBindings, Is.EqualTo(new[] { "AlbedoMap", "NormalMap", "ARMMap", "MaterialSampler" }));

        // Assert attribute is present
        Assert.That(pbType, Is.Not.Null);
        var pbValue = pbType!.Value;
        Assert.That(pbValue.AttributeCount, Is.GreaterThan(0u), "Should expose user attributes");
        var attr = pbValue.GetAttribute(0);
        Assert.That(attr.Name, Is.EqualTo("PipelineTag"));
        Assert.That(attr.ArgumentCount, Is.EqualTo(1));
        Assert.That(attr.GetArgumentValueString(0), Is.EqualTo("ClusterShader"));

        // Global resources must NOT leak into ParameterBlock
        Assert.That(matBindings, Does.Not.Contain("Uniforms"));
        Assert.That(matBindings, Does.Not.Contain("SomeBuffer"));

        TestContext.Out.WriteLine("\n=== ParameterBlock reflection PASSED ===");

        // ── Compile & verify SPIR-V bytecode ──
        module.FindEntryPointByName("CSMain", out var ep);
        Assert.That(ep, Is.Not.Null);

        session.CreateCompositeComponentType([module, ep], out var composed, out _);
        composed!.Link(out var linked, out _);
        Assert.That(linked, Is.Not.Null);

        linked!.GetEntryPointCode(0, 0, out var codeBlob, out _);
        Assert.That(codeBlob, Is.Not.Null, "Should produce SPIR-V code");
        var codeSize = (int)codeBlob!.GetBufferSize();
        Assert.That(codeSize, Is.GreaterThan(0), "SPIR-V should not be empty");
        TestContext.Out.WriteLine($"SPIR-V size: {codeSize} bytes");

        // ── Linked layout — check binding spaces ──
        var layout = linked.GetLayout(0, out _);
        Assert.That(layout, Is.Not.EqualTo(ShaderReflection.Null));

        TestContext.Out.WriteLine($"\nLinked layout params: {layout.ParameterCount}");
        for (uint i = 0; i < layout.ParameterCount; i++)
        {
            var p = layout.GetParameterByIndex(i);
            TestContext.Out.WriteLine($"  Param[{i}]: {p.Name}  space={p.BindingSpace}  binding={p.BindingIndex}  type.kind={p.TypeLayout.Type.Kind}");
        }

        TestContext.Out.WriteLine("\n=== ParameterBlock compilation + binding layout PASSED ===");
    }
}
