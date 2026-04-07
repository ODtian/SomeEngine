using System.IO;
using System.Linq;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

/// <summary>
/// 验证 Slang ParameterBlock 反射能力。
/// 使用 SPIR-V target（Slang 内置后端，无需 dxcompiler.dll）。
/// </summary>
public class ParameterBlockCompatTests
{
    [Fact]
    public void ParameterBlock_ModuleReflection_ExtractsMaterialBindings()
    {
        var globalSession = SlangShaderImporter.GlobalSession;
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
                Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = globalSession.FindProfile("glsl_460") }],
                DefaultMatrixLayoutMode = SlangMatrixLayoutMode.ColumnMajor,
                CompilerOptionEntries = [
                    new CompilerOptionEntry(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                    new CompilerOptionEntry(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
                ],
            };

            globalSession.CreateSession(sessionDesc, out var session);
            var blob = Slang.CreateBlob(source);
            var module = session.LoadModuleFromSource("test_pb", "test_pb.slang", blob, out var diag);
            Assert.NotNull(module);

        // ── Module Reflection ──
        var moduleRefl = module.GetModuleReflection();
        Assert.NotEqual(DeclReflection.Null, moduleRefl);

        Console.WriteLine($"Top-level declarations: {moduleRefl.Count}");

        bool foundPB = false;
        var matBindings = new System.Collections.Generic.List<string>();
        string? pbStructName = null;
        TypeReflection? pbType = null;

        for (uint i = 0; i < moduleRefl.Count; i++)
        {
            var decl = moduleRefl[(int)i];
            Console.WriteLine($"  [{i}] {decl.Name} ({decl.Kind})");

            if (decl.Kind != DeclReflectionKind.Variable) continue;

            var v = decl.AsVariable();
            if (v == VariableReflection.Null) continue;

            var vt = v.Type;
            Console.WriteLine($"       Type.Kind={vt.Kind}  Name={vt.Name}");

            if (vt.Kind == SlangTypeKind.ParameterBlock)
            {
                foundPB = true;
                var elem = vt.ElementType;
                pbStructName = elem.Name;
                pbType = elem;
                Console.WriteLine($"       → ParameterBlock<{elem.Name}>");
                Console.WriteLine($"         FieldCount={elem.FieldCount}  AttrCount={elem.AttributeCount}");

                for (uint f = 0; f < elem.FieldCount; f++)
                {
                    var field = elem.GetFieldByIndex(f);
                    matBindings.Add(field.Name);
                    Console.WriteLine($"         Field[{f}]: {field.Name} (Kind={field.Type.Kind})");
                }
            }
        }

        // ── Assertions ──
        Assert.True(foundPB, "Should detect ParameterBlock<T>");
        Assert.Equal("PBRMaterialParams", pbStructName);
        Assert.Equal(new[] { "AlbedoMap", "NormalMap", "ARMMap", "MaterialSampler" }, matBindings);

        // Assert attribute is present
        Assert.NotNull(pbType);
        var pbValue = pbType!.Value;
        Assert.True(pbValue.AttributeCount > 0u, "Should expose user attributes");
        var attr = pbValue.GetAttribute(0);
        Assert.Equal("PipelineTag", attr.Name);
        Assert.Equal(1u, attr.ArgumentCount);
        Assert.Equal("ClusterShader", attr.GetArgumentValueString(0));

        // Global resources must NOT leak into ParameterBlock
        Assert.DoesNotContain("Uniforms", matBindings);
        Assert.DoesNotContain("SomeBuffer", matBindings);

        Console.WriteLine("\n=== ParameterBlock reflection PASSED ===");

        // ── Compile & verify SPIR-V bytecode ──
        module.FindEntryPointByName("CSMain", out var ep);
        Assert.NotNull(ep);

        session.CreateCompositeComponentType([module, ep], out var composed, out _);
        composed!.Link(out var linked, out _);
        Assert.NotNull(linked);

        linked!.GetEntryPointCode(0, 0, out var codeBlob, out _);
        Assert.NotNull(codeBlob);
        var codeSize = (int)codeBlob!.GetBufferSize();
        Assert.True(codeSize > 0, "SPIR-V should not be empty");
        Console.WriteLine($"SPIR-V size: {codeSize} bytes");

        // ── Linked layout — check binding spaces ──
        var layout = linked.GetLayout(0, out _);
        Assert.NotEqual(ShaderReflection.Null, layout);

        Console.WriteLine($"\nLinked layout params: {layout.ParameterCount}");
        for (uint i = 0; i < layout.ParameterCount; i++)
        {
            var p = layout.GetParameterByIndex(i);
            Console.WriteLine($"  Param[{i}]: {p.Name}  space={p.BindingSpace}  binding={p.BindingIndex}  type.kind={p.TypeLayout.Type.Kind}");
        }

            Console.WriteLine("\n=== ParameterBlock compilation + binding layout PASSED ===");
    }
}
