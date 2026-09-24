using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class VertexSurfaceContractCompilationTests
{
    [Fact]
    public void SurfaceEvaluate_BindsVertexEvaluateAssociatedType()
    {
        string source = """
            struct VertexEvalContext
            {
                uint localVertIdx;
                uint instanceID;
            };

            interface IVertexEvaluate
            {
                associatedtype DeformedVertex;

                DeformedVertex evaluate(VertexEvalContext ctx);
                float3 getPosition(DeformedVertex v);

                uint getCacheByteSize(uint vertexCount);
                void writeCache(
                    RWByteAddressBuffer cache,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx,
                    DeformedVertex current,
                    DeformedVertex previous);
                DeformedVertex readCache(
                    ByteAddressBuffer cache,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx);
                DeformedVertex readPreviousCache(
                    ByteAddressBuffer cache,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx);
            };

            interface ISurfaceEvaluate<TVE : IVertexEvaluate>
            {
                void evaluateSurface(
                    uniform TVE eval,
                    uint2 pixelCoord,
                    uint instanceID,
                    float3 bary,
                    TVE.DeformedVertex v0,
                    TVE.DeformedVertex v1,
                    TVE.DeformedVertex v2,
                    RWTexture2D<float4> outputColor);

                void evaluateDebug(
                    uniform TVE eval,
                    uint debugMode,
                    uint2 pixelCoord,
                    uint instanceID,
                    float3 bary,
                    TVE.DeformedVertex v0,
                    TVE.DeformedVertex v1,
                    TVE.DeformedVertex v2,
                    RWTexture2D<float4> outputColor);
            };

            struct TestDeformedVertex
            {
                float3 position;
                float3 normal;
            };

            struct TestVertexEval : IVertexEvaluate
            {
                typedef TestDeformedVertex DeformedVertex;

                DeformedVertex evaluate(VertexEvalContext ctx)
                {
                    DeformedVertex v;
                    v.position = float3(ctx.localVertIdx, ctx.instanceID, 1.0);
                    v.normal = float3(0.0, 1.0, 0.0);
                    return v;
                }

                float3 getPosition(DeformedVertex v)
                {
                    return v.position;
                }

                uint getCacheByteSize(uint vertexCount)
                {
                    return vertexCount * 32;
                }

                void writeCache(
                    RWByteAddressBuffer cache,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx,
                    DeformedVertex current,
                    DeformedVertex previous)
                {
                    uint byteAddr = cacheBaseByte + localVertIdx * 32;
                    cache.Store3(byteAddr, asuint(current.position));
                    cache.Store3(byteAddr + 16, asuint(previous.position));
                }

                DeformedVertex readCache(
                    ByteAddressBuffer cache,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx)
                {
                    uint byteAddr = cacheBaseByte + localVertIdx * 32;
                    DeformedVertex v;
                    v.position = asfloat(cache.Load3(byteAddr));
                    v.normal = float3(0.0, 1.0, 0.0);
                    return v;
                }

                DeformedVertex readPreviousCache(
                    ByteAddressBuffer cache,
                    uint cacheBaseByte,
                    uint vertexCount,
                    uint localVertIdx)
                {
                    uint byteAddr = cacheBaseByte + localVertIdx * 32;
                    DeformedVertex v;
                    v.position = asfloat(cache.Load3(byteAddr + 16));
                    v.normal = float3(0.0, 1.0, 0.0);
                    return v;
                }
            };

            RWTexture2D<float4> OutputColor;

            struct TestSurface : ISurfaceEvaluate<TestVertexEval>
            {
                void evaluateSurface(
                    uniform TestVertexEval eval,
                    uint2 pixelCoord,
                    uint instanceID,
                    float3 bary,
                    TestVertexEval.DeformedVertex v0,
                    TestVertexEval.DeformedVertex v1,
                    TestVertexEval.DeformedVertex v2,
                    RWTexture2D<float4> outputColor)
                {
                    float3 p = bary.x * v0.position + bary.y * v1.position + bary.z * v2.position;
                    outputColor[pixelCoord] = float4(p, 1.0);
                }

                void evaluateDebug(
                    uniform TestVertexEval eval,
                    uint debugMode,
                    uint2 pixelCoord,
                    uint instanceID,
                    float3 bary,
                    TestVertexEval.DeformedVertex v0,
                    TestVertexEval.DeformedVertex v1,
                    TestVertexEval.DeformedVertex v2,
                    RWTexture2D<float4> outputColor)
                {
                    outputColor[pixelCoord] = float4(v0.normal * 0.5 + 0.5, 1.0);
                }
            };

            void Shade<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate<TVE>>(
                uniform TVE eval,
                uniform TMaterial material,
                uint3 tid)
            {
                VertexEvalContext ctx0;
                ctx0.localVertIdx = 0;
                ctx0.instanceID = tid.z;

                VertexEvalContext ctx1 = ctx0;
                ctx1.localVertIdx = 1;

                VertexEvalContext ctx2 = ctx0;
                ctx2.localVertIdx = 2;

                TVE.DeformedVertex v0 = eval.evaluate(ctx0);
                TVE.DeformedVertex v1 = eval.evaluate(ctx1);
                TVE.DeformedVertex v2 = eval.evaluate(ctx2);
                material.evaluateSurface(
                    eval,
                    tid.xy,
                    tid.z,
                    float3(1.0, 0.0, 0.0),
                    v0,
                    v1,
                    v2,
                    OutputColor);
                material.evaluateDebug(
                    eval,
                    1,
                    tid.xy,
                    tid.z,
                    float3(1.0, 0.0, 0.0),
                    v0,
                    v1,
                    v2,
                    OutputColor);
            }

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void CSTest(uint3 tid : SV_DispatchThreadID)
            {
                TestVertexEval eval;
                TestSurface material;
                Shade(eval, material, tid);
            }
            """;

        string tempDir = Path.Combine(Path.GetTempPath(), "SomeEngine.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string slangFile = Path.Combine(tempDir, "vertex_surface_contract.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            var asset = SlangShaderImporter.Import(slangFile, source);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Variants);
            Assert.Contains(asset.Variants!, v => v.EntryPoint == "CSTest" && v.Backend == "spirv" && v.Data?.Length > 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
