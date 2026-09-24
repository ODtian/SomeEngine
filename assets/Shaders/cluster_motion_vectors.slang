#include "vertex_evaluate.slang"

struct MotionVectorUniforms
{
    float4x4 ViewProj;
    float4x4 PrevViewProj;
    uint ScreenWidth;
    uint ScreenHeight;
    uint HasPreviousFrame;
    uint Pad0;
};

ConstantBuffer<MotionVectorUniforms> Uniforms;

Texture2D<uint> VisBuffer;
StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;
StructuredBuffer<GpuTransform> PreviousInstances;

RWTexture2D<float2> OutputMotionVectors;

static const uint MOTION_VECTOR_THREAD_GROUP_SIZE_X = 8;
static const uint MOTION_VECTOR_THREAD_GROUP_SIZE_Y = 8;

struct MotionTriangleInfo
{
    uint visibleClusterIndex;
    uint triangleID;
    uint instanceID;
    uint vi0;
    uint vi1;
    uint vi2;
    uint pageOffset;
    int3 intBase;
    uint vStart;
    uint vertexCount;
    uint totalVertexCount;
    uint posOffset;
    uint attrOffset;
    uint idxOffset;
};

bool DecodeMotionTriangle(uint2 pixelCoord, out MotionTriangleInfo tri)
{
    tri = {};
    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return false;

    uint visData = VisBuffer[pixelCoord];
    if (IsVisBufferEmpty(visData))
        return false;

    tri.visibleClusterIndex = DecodeVisBufferVisibleClusterIndex(visData);
    tri.triangleID = DecodeVisBufferTriangleID(visData);

    DrawRequest req = VisibleClusters[tri.visibleClusterIndex];
    tri.pageOffset = req.PageOffset;
    tri.instanceID = req.InstanceID;

    uint tStart;
    uint packedCounts;
    LoadClusterInfo(PageHeap, tri.pageOffset, req.ClusterID, tri.intBase, tri.vStart, tStart, packedCounts);
    tri.vertexCount = packedCounts & 0xFFu;

    tri.totalVertexCount = PageHeap.Load(tri.pageOffset + 4);
    uint4 header1 = PageHeap.Load4(tri.pageOffset + 16);
    tri.posOffset = header1.y;
    tri.attrOffset = header1.z;
    tri.idxOffset = header1.w;

    tri.vi0 = FetchVertexIndex(PageHeap, tri.pageOffset, tri.idxOffset, tStart, tri.triangleID, 0);
    tri.vi1 = FetchVertexIndex(PageHeap, tri.pageOffset, tri.idxOffset, tStart, tri.triangleID, 1);
    tri.vi2 = FetchVertexIndex(PageHeap, tri.pageOffset, tri.idxOffset, tStart, tri.triangleID, 2);
    return true;
}

VertexEvalContext MakeMotionVertexEvalContext(
    MotionTriangleInfo tri,
    GpuTransform transform,
    uint isPreviousFrame,
    uint localVertIdx)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    ctx.pageOffset = tri.pageOffset;
    ctx.posOffset = tri.posOffset;
    ctx.attrOffset = tri.attrOffset;
    ctx.idxOffset = tri.idxOffset;
    ctx.vStart = tri.vStart;
    ctx.totalVertexCount = tri.totalVertexCount;
    ctx.localVertIdx = localVertIdx;
    ctx.intBase = tri.intBase;
    ctx.instanceID = tri.instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = isPreviousFrame;
    return ctx;
}

void EvaluateMotionTriangle<TVE : IVertexEvaluate>(
    uniform TVE eval,
    MotionTriangleInfo tri,
    GpuTransform transform,
    uint isPreviousFrame,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    v0 = eval.evaluate(MakeMotionVertexEvalContext(tri, transform, isPreviousFrame, tri.vi0));
    v1 = eval.evaluate(MakeMotionVertexEvalContext(tri, transform, isPreviousFrame, tri.vi1));
    v2 = eval.evaluate(MakeMotionVertexEvalContext(tri, transform, isPreviousFrame, tri.vi2));
}

float2 ClipToUv(float4 clip)
{
    float2 ndc = clip.xy / clip.w;
    return float2(ndc.x * 0.5 + 0.5, 1.0 - (ndc.y * 0.5 + 0.5));
}

float2 ComputeMotionVector(float3 currentPosition, float3 previousPosition)
{
    if (Uniforms.HasPreviousFrame == 0)
        return float2(0.0);

    float4 currentClip = mul(float4(currentPosition, 1.0), Uniforms.ViewProj);
    float4 previousClip = mul(float4(previousPosition, 1.0), Uniforms.PrevViewProj);
    if (abs(currentClip.w) < 1e-6 || abs(previousClip.w) < 1e-6)
        return float2(0.0);

    // Sign convention: normalized current-frame UV minus previous-frame UV.
    return ClipToUv(currentClip) - ClipToUv(previousClip);
}

void WriteMotionVector<TVE : IVertexEvaluate>(
    uniform TVE eval,
    uint2 pixelCoord,
    TVE.DeformedVertex v0,
    TVE.DeformedVertex v1,
    TVE.DeformedVertex v2,
    TVE.DeformedVertex previousV0,
    TVE.DeformedVertex previousV1,
    TVE.DeformedVertex previousV2)
{
    float3 p0 = eval.getPosition(v0);
    float3 p1 = eval.getPosition(v1);
    float3 p2 = eval.getPosition(v2);
    ScreenProjection sp0 = ProjectToScreenProjection(p0, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp1 = ProjectToScreenProjection(p1, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp2 = ProjectToScreenProjection(p2, Uniforms.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float3 bary = ComputePerspectiveBarycentric(float2(pixelCoord) + 0.5, sp0, sp1, sp2);

    float3 currentPosition = bary.x * p0 + bary.y * p1 + bary.z * p2;
    float3 previousPosition =
        bary.x * eval.getPosition(previousV0) +
        bary.y * eval.getPosition(previousV1) +
        bary.z * eval.getPosition(previousV2);
    OutputMotionVectors[pixelCoord] = ComputeMotionVector(currentPosition, previousPosition);
}

[shader("compute")]
[numthreads(MOTION_VECTOR_THREAD_GROUP_SIZE_X, MOTION_VECTOR_THREAD_GROUP_SIZE_Y, 1)]
void CSMotionVectors(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pixelCoord = dispatchThreadID.xy;
    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return;

    MotionTriangleInfo tri;
    if (!DecodeMotionTriangle(pixelCoord, tri))
    {
        OutputMotionVectors[pixelCoord] = float2(0.0);
        return;
    }

    if (Uniforms.HasPreviousFrame == 0)
    {
        OutputMotionVectors[pixelCoord] = float2(0.0);
        return;
    }

    StaticVertexEval eval;
    StaticVertexEval.DeformedVertex v0;
    StaticVertexEval.DeformedVertex v1;
    StaticVertexEval.DeformedVertex v2;
    EvaluateMotionTriangle(eval, tri, Instances[tri.instanceID], 0u, v0, v1, v2);

    StaticVertexEval.DeformedVertex previousV0;
    StaticVertexEval.DeformedVertex previousV1;
    StaticVertexEval.DeformedVertex previousV2;
    EvaluateMotionTriangle(eval, tri, PreviousInstances[tri.instanceID], 1u, previousV0, previousV1, previousV2);

    WriteMotionVector(eval, pixelCoord, v0, v1, v2, previousV0, previousV1, previousV2);
}
