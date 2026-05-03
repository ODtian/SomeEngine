#ifndef VERTEX_EVALUATE_SLANG
#define VERTEX_EVALUATE_SLANG

#include "common.slang"
#include "cluster_common.slang"

// ═══════════════════════════════════════════════════════════════
//  IVertexEvaluate — 可编程顶点变形接口
//
//  用户继承此接口实现自定义顶点变形（蒙皮、WPO、程序化动画等）。
//  引擎在运行时选择 inline（光栅内联求值）或 cached（PreDeform CS）路径，
//  用户代码无需感知。
// ═══════════════════════════════════════════════════════════════

// ── 顶点求值上下文 ──
// 提供几何基础数据和 PageHeap 访问，用户可通过 StreamCursor
// 读取法线/UV/骨骼权重等任意 SoA 属性流。
struct VertexEvalContext
{
    // 基础几何
    float3       objectPos;       // 反量化后物体空间位置
    float3       worldPos;        // ApplyTransform 后世界位置
    uint         localVertIdx;    // cluster 内局部顶点索引
    uint         instanceID;      // 实例 ID
    GpuTransform transform;       // 当前实例的 transform

    // PageHeap 访问 — 用户可自由读取法线/UV/骨骼权重等
    ByteAddressBuffer pageHeap;
    uint         pageOffset;      // 当前 page 在 PageHeap 中的字节偏移
    uint         vStart;          // 顶点起始索引
    uint         totalVertexCount;// page 内总顶点数
    uint         attrOffset;      // PageHeader.AttributesOffset

    // 构建 StreamCursor，用户按流顺序 advance 读取各属性
    StreamCursor getStreamCursor()
    {
        StreamCursor c;
        c.base = pageOffset + attrOffset;
        c.totalVerts = totalVertexCount;
        return c;
    }
};

// ── 核心接口 ──
interface IVertexEvaluate
{
    associatedtype DeformedVertex;     // 用户定义的输出结构

    // 核心变形 — 用户只需实现这个方法
    DeformedVertex evaluate(VertexEvalContext ctx);

    // 从用户自定义输出中提取世界空间位置（光栅必需）
    float3 getPosition(DeformedVertex v);

    // ── Cache 序列化（压缩格式存储） ──
    // 返回每顶点在 cache 中的字节数（编译期常量）
    uint getCacheStride();
    // 将 DeformedVertex 写入 cache（压缩）
    void writeCache(RWByteAddressBuffer buf, uint byteAddr, DeformedVertex v);
    // 从 cache 读取 DeformedVertex（解压）
    DeformedVertex readCache(ByteAddressBuffer buf, uint byteAddr);
};

// ── 默认实现：静态网格（无变形）──
struct StaticDeformedVertex
{
    float3 position;
};

struct StaticVertexEval : IVertexEvaluate
{
    typedef StaticDeformedVertex DeformedVertex;

    DeformedVertex evaluate(VertexEvalContext ctx)
    {
        DeformedVertex v;
        v.position = ctx.worldPos;   // identity — 无变形
        return v;
    }

    float3 getPosition(DeformedVertex v)
    {
        return v.position;
    }

    // Cache: float3, preserving world-space precision for cached raster/shade paths.
    uint getCacheStride() { return 12; }

    void writeCache(RWByteAddressBuffer buf, uint byteAddr, DeformedVertex v)
    {
        buf.Store(byteAddr,     asuint(v.position.x));
        buf.Store(byteAddr + 4, asuint(v.position.y));
        buf.Store(byteAddr + 8, asuint(v.position.z));
    }

    DeformedVertex readCache(ByteAddressBuffer buf, uint byteAddr)
    {
        DeformedVertex v;
        v.position = float3(
            asfloat(buf.Load(byteAddr)),
            asfloat(buf.Load(byteAddr + 4)),
            asfloat(buf.Load(byteAddr + 8)));
        return v;
    }
};

// ── Demo: 正弦波变形 ──
struct WaveDeformedVertex
{
    float3 position;
};

struct WaveVertexEval : IVertexEvaluate
{
    typedef WaveDeformedVertex DeformedVertex;

    DeformedVertex evaluate(VertexEvalContext ctx)
    {
        DeformedVertex v;
        float freq = 3.0;
        float amplitude = 0.3;
        float offset = sin(ctx.worldPos.x * freq) * cos(ctx.worldPos.z * freq) * amplitude;
        v.position = ctx.worldPos + float3(0, offset, 0);
        return v;
    }

    float3 getPosition(DeformedVertex v)
    {
        return v.position;
    }

    // Cache: float3, preserving world-space precision for cached raster/shade paths.
    uint getCacheStride() { return 12; }

    void writeCache(RWByteAddressBuffer buf, uint byteAddr, DeformedVertex v)
    {
        buf.Store(byteAddr,     asuint(v.position.x));
        buf.Store(byteAddr + 4, asuint(v.position.y));
        buf.Store(byteAddr + 8, asuint(v.position.z));
    }

    DeformedVertex readCache(ByteAddressBuffer buf, uint byteAddr)
    {
        DeformedVertex v;
        v.position = float3(
            asfloat(buf.Load(byteAddr)),
            asfloat(buf.Load(byteAddr + 4)),
            asfloat(buf.Load(byteAddr + 8)));
        return v;
    }
};

// ═══════════════════════════════════════════════════════════════
//  IVertexSource — 光栅 kernel 的顶点来源抽象
//
//  InlineSource<T>: 光栅时内联调用 T.evaluate()
//  CachedSource<T>: 从 PreDeform 填充的 DeformedCacheBuffer 读取
// ═══════════════════════════════════════════════════════════════

// ── 顶点 Fetch 参数包 ──
struct VertexFetchArgs
{
    ByteAddressBuffer pageHeap;
    uint     pageOffset;
    uint     posOffset;
    uint     attrOffset;
    uint     totalVertexCount;
    uint     vStart;
    int3     intBase;
    uint     localVertIdx;
    uint     instanceID;
    GpuTransform transform;
    float3   quantOrigin;
    float    quantStep;
    float4x4 viewProj;
    uint     visibleClusterIndex;  // DeformCache 寻址用
};

interface IVertexSource
{
    float4 fetchClipPosition(VertexFetchArgs args);
};

// ── 辅助：从 PageHeap 读取并反量化顶点位置 ──
float3 dequantizePosition(VertexFetchArgs args)
{
    uint posByteAddr = args.pageOffset + args.posOffset
                     + (args.vStart + args.localVertIdx) * 6;
    uint alignedAddr = posByteAddr & ~3u;
    uint offset = posByteAddr & 3u;
    uint2 raw = args.pageHeap.Load2(alignedAddr);

    uint p0, p1, p2;
    if (offset == 0)
    {
        p0 = raw.x & 0xFFFF;
        p1 = (raw.x >> 16) & 0xFFFF;
        p2 = raw.y & 0xFFFF;
    }
    else
    {
        p0 = (raw.x >> 16) & 0xFFFF;
        p1 = raw.y & 0xFFFF;
        p2 = (raw.y >> 16) & 0xFFFF;
    }

    int3 globalIntPos = int3(int(p0), int(p1), int(p2)) + args.intBase;
    return float3(globalIntPos) * args.quantStep + args.quantOrigin;
}

// ── Inline Source: 光栅时内联调用变形 ──
struct InlineSource<T : IVertexEvaluate> : IVertexSource
{
    T evaluator;    // 持有用户实例（含资源绑定）

    float4 fetchClipPosition(VertexFetchArgs args)
    {
        float3 objectPos = dequantizePosition(args);
        float3 worldPos  = ApplyTransform(objectPos, args.transform);

        VertexEvalContext ctx;
        ctx.objectPos       = objectPos;
        ctx.worldPos        = worldPos;
        ctx.localVertIdx    = args.localVertIdx;
        ctx.instanceID      = args.instanceID;
        ctx.transform       = args.transform;
        ctx.pageHeap        = args.pageHeap;
        ctx.pageOffset      = args.pageOffset;
        ctx.vStart          = args.vStart;
        ctx.totalVertexCount = args.totalVertexCount;
        ctx.attrOffset      = args.attrOffset;

        T.DeformedVertex v = evaluator.evaluate(ctx);
        float3 finalWorldPos = evaluator.getPosition(v);
        return mul(float4(finalWorldPos, 1.0), args.viewProj);
    }
};

// ── Cached Source: 从 DeformCache 读取预变形顶点 ──
struct CachedSource<TVE : IVertexEvaluate> : IVertexSource
{
    ByteAddressBuffer deformCache;
    StructuredBuffer<uint> cacheOffsets;  // 单独的寻址 buffer
    TVE evaluator;

    float4 fetchClipPosition(VertexFetchArgs args)
    {
        uint baseByte = cacheOffsets[args.visibleClusterIndex];
        if (baseByte >= 0xFFFFFFFDu)
        {
            InlineSource<TVE> fallback;
            fallback.evaluator = evaluator;
            return fallback.fetchClipPosition(args);
        }

        uint byteAddr = baseByte + args.localVertIdx * evaluator.getCacheStride();
        TVE.DeformedVertex v = evaluator.readCache(deformCache, byteAddr);
        float3 worldPos = evaluator.getPosition(v);
        return mul(float4(worldPos, 1.0), args.viewProj);
    }
};

#endif // VERTEX_EVALUATE_SLANG
