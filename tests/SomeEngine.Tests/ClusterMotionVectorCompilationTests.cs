using SomeEngine.Assets.Importers;

namespace SomeEngine.Tests;

public class ClusterMotionVectorCompilationTests
{
    private const string ClusterMotionVectorsShaderFile = "cluster_motion_vectors.slang";
    private const string ClusterMotionVectorsEntryPoint = "CSMotionVectors";

    [Fact]
    public void ClusterMotionVectorShader_ImportsSuccessfully()
    {
        string path = TestProjectPaths.ShaderPath(ClusterMotionVectorsShaderFile);

        var asset = SlangShaderImporter.Import(path);

        Assert.NotNull(asset);
        Assert.NotEmpty(asset.Variants!);
        Assert.Contains(asset.Variants!, v =>
            v.EntryPoint == ClusterMotionVectorsEntryPoint
            && v.Backend == "spirv"
            && v.Data.HasValue
            && v.Data.Value.Length > 0);
    }

    [Fact]
    public void ClusterMotionVectorShader_DocumentsSignConventionAndFirstFrameZero()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath(ClusterMotionVectorsShaderFile));

        Assert.Contains("current-frame UV minus previous-frame UV", source);
        Assert.Contains("Uniforms.HasPreviousFrame == 0", source);
        Assert.Contains("OutputMotionVectors[pixelCoord] = float2(0.0);", source);
        Assert.Contains("DecodeVisBufferVisibleClusterIndex", source);
        Assert.Contains("DecodeVisBufferTriangleID", source);
        Assert.Contains("static const uint MOTION_VECTOR_THREAD_GROUP_SIZE_X = 8;", source);
        Assert.Contains("static const uint MOTION_VECTOR_THREAD_GROUP_SIZE_Y = 8;", source);
    }

    [Fact]
    public void ClusterShadeMotionVectorsUseUnjitteredMotionMatrices()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_shade_pipeline.slang"));

        Assert.Contains("float4x4 MotionViewProj;", source);
        Assert.Contains("float4x4 PrevMotionViewProj;", source);
        Assert.Contains("Uniforms.MotionViewProj", source);
        Assert.Contains("Uniforms.PrevMotionViewProj", source);
        Assert.Contains("ProjectToScreenProjection(p0, Uniforms.ViewProj", source);
        Assert.Contains("ComputePerspectiveBarycentric", source);
    }

    [Fact]
    public void ClusterShadeMotionVectorsDoNotReadPreviousTransformsWithoutHistory()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_shade_pipeline.slang"));

        Assert.Contains("bool shouldWriteMotionVectors()", source);
        Assert.Contains("if (shouldWriteMotionVectors())", source);
        Assert.Contains("writeZeroMotionVector(pixelCoord);", source);
    }

    [Fact]
    public void ClusterMotionVectorPassUsesPerspectiveCorrectBarycentrics()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath(ClusterMotionVectorsShaderFile));

        Assert.Contains("ProjectToScreenProjection(p0, Uniforms.ViewProj", source);
        Assert.Contains("ComputePerspectiveBarycentric", source);
        Assert.Contains("EvaluateMotionTriangle(eval, tri, Instances[tri.instanceID]", source);
        Assert.Contains("EvaluateMotionTriangle(eval, tri, PreviousInstances[tri.instanceID]", source);
        Assert.DoesNotContain("FetchVertexPosition", source);
    }

    [Fact]
    public void ClusterShadePipeline_BindsEvalAndSurfaceWithoutSourceLayer()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("cluster_shade_pipeline.slang"));

        Assert.Contains("void CSShade<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate<TVE>>", source);
        Assert.Contains("void CSShadeFromCache<TVE : IVertexEvaluate, TMaterial : ISurfaceEvaluate<TVE>>", source);
        Assert.Contains("material.evaluateSurface(", source);
        Assert.Contains("material.evaluateDebug(", source);
        Assert.Contains("evaluator.readCache(cache, cacheBaseByte, tri.vertexCount, tri.vi0)", source);
        Assert.Contains("evaluator.readPreviousCache(cache, cacheBaseByte, tri.vertexCount, tri.vi0)", source);
        Assert.DoesNotContain("IShadeGeometrySource", source);
        Assert.DoesNotContain("CSShadeWithSource", source);
        Assert.DoesNotContain("FetchNormal", source);
        Assert.DoesNotContain("FetchUV", source);
    }

    [Fact]
    public void StaticVertexEval_OwnsStaticNormalTangentUvProtocol()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("vertex_evaluate.slang"));

        Assert.Contains("StaticVertexAttributes EvalStaticVertexAttributes(VertexEvalContext ctx)", source);
        Assert.Contains("uint normalBase = cursor.advance(3);", source);
        Assert.Contains("uint tangentBase = cursor.advance(4);", source);
        Assert.Contains("uint uvBase = cursor.advance(4);", source);
        Assert.Contains("FetchNormal3(ctx.pageHeap", source);
        Assert.Contains("FetchTangent(ctx.pageHeap", source);
        Assert.Contains("FetchUV(ctx.pageHeap", source);
    }
}
