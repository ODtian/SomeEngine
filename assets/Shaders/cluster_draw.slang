#include "vertex_evaluate.slang"

struct DrawUniforms
{
    float4x4 ViewProj;
    float4x4 View;
    uint PageTableSize;
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
};

struct DrawDispatchUniforms
{
    uint DrawArgsByteOffset;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<DrawUniforms> Uniforms;
ConstantBuffer<DrawDispatchUniforms> DispatchUniforms;

ByteAddressBuffer PageHeap;
StructuredBuffer<uint2> BinnedClusterIndexBuffer;
StructuredBuffer<uint4> VisibleClusters;
ByteAddressBuffer VisibleClusterMeta;

ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;

static const uint DRAW_CACHE_OFFSET_INVALID = 0xFFFFFFFDu;

struct PSInput
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
    nointerpolation uint ClusterID : CLUSTER_ID;
};

struct PSVisBufferInput
{
    float4 Pos : SV_POSITION;
    nointerpolation uint VisibleClusterIndex : VIS_CLUSTER_IDX;
    nointerpolation uint TriangleID : TRIANGLE_ID;
};

uint GetBinnedClusterIndex(uint instanceID)
{
    DrawInstancedArgs args = LoadDrawInstancedArgs(VisibleClusterMeta, DispatchUniforms.DrawArgsByteOffset);
    return args.StartInstance + instanceID;
}

uint LoadLocalVertexIndex(uint pageOffset, uint idxOffset, uint tStart, uint triIdx, uint corner)
{
    uint indexByteAddr = pageOffset + idxOffset + tStart + triIdx * 3u + corner;
    uint alignedAddr = indexByteAddr & ~3u;
    uint shift = (indexByteAddr & 3u) * 8u;
    uint packedIndex = PageHeap.Load(alignedAddr);
    return (packedIndex >> shift) & 0xFFu;
}

bool DecodeDrawVertex(
    uint vertexID,
    uint instanceID,
    out uint visibleClusterIndex,
    out uint clusterID,
    out uint realInstanceID,
    out uint lodLevel,
    out uint triangleID,
    out uint localVertIdx,
    out uint pageOffset,
    out uint posOffset,
    out uint attrOffset,
    out uint idxOffset,
    out uint totalVertexCount,
    out uint vStart,
    out uint vertexCount,
    out int3 intBase)
{
    uint binnedIndex = GetBinnedClusterIndex(instanceID);
    uint2 binEntry = BinnedClusterIndexBuffer[binnedIndex];
    visibleClusterIndex = binEntry.x;
    uint packedRange = binEntry.y;

    uint4 req = VisibleClusters[visibleClusterIndex];
    pageOffset = req.x;
    clusterID = req.y;
    realInstanceID = req.z;
    totalVertexCount = PageHeap.Load(pageOffset + 4u);

    uint4 header1 = PageHeap.Load4(pageOffset + 16u);
    posOffset = header1.y;
    attrOffset = header1.z;
    idxOffset = header1.w;

    uint tStart;
    uint packedCounts;
    LoadClusterInfo(PageHeap, pageOffset, clusterID, intBase, vStart, tStart, packedCounts);
    vertexCount = packedCounts & 0xFFu;
    lodLevel = (packedCounts >> 16u) & 0xFFu;

    uint rangeStart = (packedRange >> 16u) & 0xFFFFu;
    uint rangeEnd = packedRange & 0xFFFFu;
    uint batchTriCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0u;

    triangleID = rangeStart + vertexID / 3u;
    uint corner = vertexID % 3u;
    localVertIdx = 0u;

    if (vertexID >= batchTriCount * 3u)
        return false;

    localVertIdx = LoadLocalVertexIndex(pageOffset, idxOffset, tStart, triangleID, corner);
    return true;
}

VertexEvalContext MakeDrawVertexEvalContext(
    uint pageOffset,
    uint posOffset,
    uint attrOffset,
    uint idxOffset,
    uint totalVertexCount,
    uint vStart,
    int3 intBase,
    uint localVertIdx,
    uint instanceID,
    GpuTransform transform)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    ctx.pageOffset = pageOffset;
    ctx.posOffset = posOffset;
    ctx.attrOffset = attrOffset;
    ctx.idxOffset = idxOffset;
    ctx.vStart = vStart;
    ctx.totalVertexCount = totalVertexCount;
    ctx.localVertIdx = localVertIdx;
    ctx.intBase = intBase;
    ctx.instanceID = instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = 0u;
    return ctx;
}

void LoadDrawDeformedVertex<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    bool useDeformCache,
    uint visibleClusterIndex,
    uint vertexCount,
    VertexEvalContext ctx,
    out TVE.DeformedVertex vertex)
{
    if (useDeformCache)
    {
        uint cacheBaseByte = CacheOffsets[visibleClusterIndex];
        if (cacheBaseByte >= DRAW_CACHE_OFFSET_INVALID)
            vertex = evaluator.evaluate(ctx);
        else
            vertex = evaluator.readCache(DeformCache, cacheBaseByte, vertexCount, ctx.localVertIdx);
        return;
    }

    vertex = evaluator.evaluate(ctx);
}

void WriteInvalidPSInput(out PSInput output)
{
    output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000));
    output.Normal = float3(0.0);
    output.UV = float2(0.0);
    output.Color = float3(0.0);
    output.ClusterID = 0u;
}

[shader("vertex")]
PSInput VSMain(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    PSInput output;

    uint visibleClusterIndex;
    uint clusterID;
    uint realInstanceID;
    uint lodLevel;
    uint triangleID;
    uint localVertIdx;
    uint pageOffset;
    uint posOffset;
    uint attrOffset;
    uint idxOffset;
    uint totalVertexCount;
    uint vStart;
    uint vertexCount;
    int3 intBase;

    if (!DecodeDrawVertex(
            vertexID,
            instanceID,
            visibleClusterIndex,
            clusterID,
            realInstanceID,
            lodLevel,
            triangleID,
            localVertIdx,
            pageOffset,
            posOffset,
            attrOffset,
            idxOffset,
            totalVertexCount,
            vStart,
            vertexCount,
            intBase))
    {
        WriteInvalidPSInput(output);
        return output;
    }

    StaticVertexEval eval;
    VertexEvalContext ctx = MakeDrawVertexEvalContext(
        pageOffset,
        posOffset,
        attrOffset,
        idxOffset,
        totalVertexCount,
        vStart,
        intBase,
        localVertIdx,
        realInstanceID,
        Instances[realInstanceID]);
    StaticVertexEval.DeformedVertex vertex;
    LoadDrawDeformedVertex(eval, false, visibleClusterIndex, vertexCount, ctx, vertex);

    output.Pos = mul(float4(eval.getPosition(vertex), 1.0), Uniforms.ViewProj);
    output.ClusterID = clusterID;
    output.Normal = vertex.worldNormal;
    output.UV = vertex.uv0;

    if (Uniforms.DebugMode == 2u)
    {
        float l = float(lodLevel) / 10.0;
        output.Color = float3(l, 1.0 - l, 0.0);
    }
    else
    {
        output.Color = vertex.worldNormal * 0.5 + 0.5;
    }

    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    if (Uniforms.DebugMode == 1u)
    {
        uint h = PCGHash(input.ClusterID);
        return float4(ColorFromHash(h), 1.0);
    }

    return float4(input.Color, 1.0);
}

[shader("pixel")]
float4 PSOverdraw(PSInput input) : SV_TARGET
{
    return float4(0.05, 0.05, 0.05, 1.0);
}

PSVisBufferInput BuildVisBufferVertex<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    bool useDeformCache,
    uint vertexID,
    uint instanceID)
{
    PSVisBufferInput output;

    uint visibleClusterIndex;
    uint clusterID;
    uint realInstanceID;
    uint lodLevel;
    uint triangleID;
    uint localVertIdx;
    uint pageOffset;
    uint posOffset;
    uint attrOffset;
    uint idxOffset;
    uint totalVertexCount;
    uint vStart;
    uint vertexCount;
    int3 intBase;

    output.VisibleClusterIndex = 0u;
    output.TriangleID = 0u;

    if (!DecodeDrawVertex(
            vertexID,
            instanceID,
            visibleClusterIndex,
            clusterID,
            realInstanceID,
            lodLevel,
            triangleID,
            localVertIdx,
            pageOffset,
            posOffset,
            attrOffset,
            idxOffset,
            totalVertexCount,
            vStart,
            vertexCount,
            intBase))
    {
        output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000));
        return output;
    }

    VertexEvalContext ctx = MakeDrawVertexEvalContext(
        pageOffset,
        posOffset,
        attrOffset,
        idxOffset,
        totalVertexCount,
        vStart,
        intBase,
        localVertIdx,
        realInstanceID,
        Instances[realInstanceID]);
    TVE.DeformedVertex vertex;
    LoadDrawDeformedVertex(evaluator, useDeformCache, visibleClusterIndex, vertexCount, ctx, vertex);

    output.Pos = mul(float4(evaluator.getPosition(vertex), 1.0), Uniforms.ViewProj);
    output.VisibleClusterIndex = visibleClusterIndex;
    output.TriangleID = triangleID;
    return output;
}

[MaterialTarget("cluster.raster.vs")]
[shader("vertex")]
PSVisBufferInput VSVisBuffer(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    StaticVertexEval eval;
    return BuildVisBufferVertex(eval, false, vertexID, instanceID);
}

[MaterialTarget("cluster.raster.vs.cached")]
[shader("vertex")]
PSVisBufferInput VSVisBufferCached(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    StaticVertexEval eval;
    return BuildVisBufferVertex(eval, true, vertexID, instanceID);
}

[MaterialTarget("cluster.raster.ps")]
[shader("pixel")]
uint PSVisBuffer(PSVisBufferInput input) : SV_TARGET
{
    return EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID);
}
