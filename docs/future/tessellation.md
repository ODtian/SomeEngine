# Nanite Tessellation 设计

## 1. 为什么 Tess 必须走软光栅

1. **数量爆炸**：128 tri × TF² 可达数千微三角形，无法预分配 DrawIndirect
2. **极小三角形**：微三角形通常 < 1 pixel，HW quad overshading 浪费 75%+
3. **可编程 Domain**：per-material domain evaluation 必须在 CS 中调用用户 shader
4. **拓扑不固定**：微三角形由查表决定，不是标准 index buffer

## 2. 整体管线

```
Binned Visible Clusters (RasterType == Tess)
    ↓
Split (全局队列，多轮 dispatch)
    ↓ VisiblePatches buffer
Dice (1 group = 1 patch, 64 threads)
    ↓ 直接调用 SW Raster 核心 → VisBuffer
```

进入 Tess 管线的 Cluster **至少 dice 一次**——这是 Bin 级决策（材质标记 `HasDisplacement`），不是 per-triangle 分支。TF=1 时 dice table 输出 1 micro-triangle = 原始三角形，代码路径统一无分支。

## 3. Split 阶段：全局队列

### 为什么用队列而非栈

- 队列无递归深度限制 → **无最高精度限制**
- 全局 work 自动均衡（persistent threads）
- 内存用量有界

### 两级架构

**Level 1（per-group 初筛）**：1 group = 1 cluster，128 threads 处理 128 triangles。估算 TF，TF ≤ table_max 的直接 emit，TF > table_max 的 push to global queue。

**Level 2（persistent global）**：独立 dispatch，worker threads 循环 dequeue → split → sub-patch TF ≤ max → emit 或 re-enqueue。

```
Global Queue: ping-pong double buffer
每轮 dispatch: 读 QueueA → 处理 → 写 QueueB → swap
终止: writeCount == 0
```

### SplitWorkItem 数据

```slang
struct SplitWorkItem
{
    uint VisibleClusterIndex;
    uint TriangleID;
    float2 BaryUV0, BaryUV1, BaryUV2;  // sub-patch 在原始三角形中的重心坐标
    uint PackedTessFactors;              // edge0:8 | edge1:8 | edge2:8 | level:8
};
// 32 bytes
```

### TessFactor 估算

```slang
float ComputeEdgeTessFactor(float3 wp0, float3 wp1)
{
    float4 cp0 = mul(float4(wp0, 1), ViewProj);
    float4 cp1 = mul(float4(wp1, 1), ViewProj);
    float2 sp0 = (cp0.xy / cp0.w) * float2(ScreenW, ScreenH) * 0.5;
    float2 sp1 = (cp1.xy / cp1.w) * float2(ScreenW, ScreenH) * 0.5;
    float edgePixels = length(sp1 - sp0);
    return max(1.0, ceil(edgePixels / PIXELS_PER_EDGE));  // PIXELS_PER_EDGE 可调
}
```

### 保守 Displacement Bounds 剔除

Split 阶段可剔除被 HiZ 遮挡的 patch：
- 计算 undisplaced patch AABB
- 扩展 `max_displacement * normal_range` → 保守 displaced AABB
- 与 HiZ 比较 → 剔除

## 4. Dice 阶段

**1 group = 1 patch, 64 threads。**

```slang
groupshared float4 gs_MicroVerts[MAX_MICRO_VERTS];  // e.g., 81 for TF=8

[numthreads(64, 1, 1)]
void CSDice(uint3 gid : SV_GroupID, uint tid : SV_GroupThreadIndex)
{
    VisiblePatch patch = PatchBuffer[gid.x];
    uint3 tfs = UnpackTessFactors(patch.PackedTessFactors);
    uint microVertCount = DiceTable_VertCount(tfs);
    uint microTriCount  = DiceTable_TriCount(tfs);
    
    // Step 1: Evaluate micro-vertices
    for (uint vi = tid; vi < microVertCount; vi += 64)
    {
        float3 localBary = DiceTable_VertexBary(tfs, vi);
        float2 origBary = localBary.x * patch.BaryUV0 
                        + localBary.y * patch.BaryUV1 
                        + localBary.z * patch.BaryUV2;
        
        float3 basePos    = InterpolateFromCluster(patch, origBary, ATTR_POSITION);
        float3 baseNormal = InterpolateFromCluster(patch, origBary, ATTR_NORMAL);
        float2 uv         = InterpolateFromCluster(patch, origBary, ATTR_UV);
        
        // ★ 用户可编程: IDomainEvaluate
        float3 displacedPos = IDomainEvaluate::EvaluateDomain(basePos, baseNormal, uv);
        gs_MicroVerts[vi] = mul(float4(displacedPos, 1), ViewProj);
    }
    GroupMemoryBarrierWithGroupSync();
    
    // Step 2: Rasterize micro-triangles (via WaveQueue distribute)
    // 每线程取若干 micro-tri → SetupTriangle → RasterizeTriangle
}
```

### LDS 预算

| TF max | 微顶点 | 微三角形 | LDS (float4) |
|--------|--------|---------|--------------|
| 8 | ~81 | ~128 | 1.3 KB |
| 16 | ~289 | ~512 | 4.6 KB |

TF > 16 由 Split 递归拆分到 ≤ 16。

## 5. Watertight 查表

### 核心要求

相邻三角形的共享边必须产生完全相同的顶点序列，即使两边 TF 不同。否则出现 T-junction 裂缝。

### Edge-driven tessellation tables

- 每条边独立细分，由该边的 TF 决定
- 内部拓扑由三条边 TF 组合查表
- 边 TF 向上取到 2 的幂（1, 2, 4, 8）→ 组合数 4³ = 64 种

```slang
struct DiceTableEntry
{
    uint VertCount;
    uint TriCount;
    uint VertBarycentricsOffset;   // 微顶点重心坐标表的起始
    uint TriIndicesOffset;         // 微三角形索引表的起始
};

StructuredBuffer<DiceTableEntry> DiceTable;       // 64 entries
StructuredBuffer<float3> DiceVertBarycentrics;    // 所有组合的重心坐标
StructuredBuffer<uint3>  DiceTriIndices;          // 所有组合的索引
```

总表大小约几 KB，完全放入 VRAM cache。

## 6. VisBuffer 统一编码

### 关键设计决策

**VisBuffer 对 Tess 和 Non-Tess 像素使用完全相同的编码：**

```
(VisibleClusterIndex + 1) << 7 | TriangleID
```

Tess 像素写的是**原始 cluster 的 visibleClusterIndex + 原始 triangleID**。

### Shade 时重心坐标恢复

Shade pass 从 depth 重建 worldPos，将 worldPos 投影回原始三角形平面求重心坐标：

```slang
float3 ComputeBarycentric3D(float3 worldPos, float3 v0, float3 v1, float3 v2)
{
    float3 e0 = v1 - v0;
    float3 e1 = v2 - v0;
    float3 e2 = worldPos - v0;
    float d00 = dot(e0, e0), d01 = dot(e0, e1), d11 = dot(e1, e1);
    float d20 = dot(e2, e0), d21 = dot(e2, e1);
    float denom = d00 * d11 - d01 * d01;
    float v = (d11 * d20 - d01 * d21) / denom;
    float w = (d00 * d21 - d01 * d20) / denom;
    return float3(1 - v - w, v, w);
}
```

### 为什么精度够用

- Dice 保证微三角形在屏幕上接近像素大小
- worldPos 在 displaced surface 上，投影到原始平面的横向误差 ∝ `displacement × curvature × micro_tri_size`
- 当 micro_tri 像素级时，误差 sub-pixel，着色不可见

### 巨大好处

- VisBuffer 编码完全统一，无 bit flag / PatchIndex / MicroTriID
- Shade pass 代码完全统一——不区分 tess / non-tess 像素
- VisiblePatches buffer 只给 Dice 阶段用，Shade 不需要访问
- Shade Binning 零修改
- 所有现有 Resolve debug 模式直接工作

## 7. IDomainEvaluate 接口 & PN Patch 工具

### 接口

```slang
interface IDomainEvaluate
{
    static float3 EvaluateDomain(DomainContext ctx);
};

struct DomainContext
{
    float3 pos[3];       // 角顶点 world pos
    float3 normal[3];    // 角顶点 world normal
    float2 uv[3];        // 角顶点 UV
    float3 bary;         // 当前微顶点重心坐标
    uint   instanceID;
};
```

管线在 Dice 阶段填充 `DomainContext`，对 `EvaluateDomain` 内部完全黑盒。

### PN Triangle 工具函数

管线提供的 utility，从平面三角形 + 角法线构造三次 Bezier 曲面。八面体 + PN → 光滑球体。

```slang
struct PNPatch
{
    float3 b300, b030, b003;
    float3 b210, b120, b021, b012, b102, b201;
    float3 b111;
    
    static PNPatch Create(float3 p0, float3 p1, float3 p2,
                          float3 n0, float3 n1, float3 n2)
    {
        PNPatch pn;
        pn.b300 = p0; pn.b030 = p1; pn.b003 = p2;
        pn.b210 = (2*p0 + p1 - dot(p1-p0, n0)*n0) / 3.0;
        pn.b120 = (p0 + 2*p1 - dot(p0-p1, n1)*n1) / 3.0;
        pn.b021 = (2*p1 + p2 - dot(p2-p1, n1)*n1) / 3.0;
        pn.b012 = (p1 + 2*p2 - dot(p1-p2, n2)*n2) / 3.0;
        pn.b102 = (2*p2 + p0 - dot(p0-p2, n2)*n2) / 3.0;
        pn.b201 = (p2 + 2*p0 - dot(p2-p0, n0)*n0) / 3.0;
        float3 E = (pn.b210 + pn.b120 + pn.b021 + pn.b012 + pn.b102 + pn.b201) / 6.0;
        pn.b111 = E + (E - (p0+p1+p2)/3.0) * 0.5;
        return pn;
    }
    
    float3 Evaluate(float3 bary)
    {
        float u = bary.x, v = bary.y, w = bary.z;
        return b300*u*u*u + b030*v*v*v + b003*w*w*w
             + 3*(b210*u*u*v + b120*u*v*v + b021*v*v*w
                + b012*v*w*w + b102*w*w*u + b201*w*u*u)
             + 6*b111*u*v*w;
    }
};
```

### 用户态组合示例

```slang
struct PNDisplacement : IDomainEvaluate
{
    static float3 EvaluateDomain(DomainContext ctx)
    {
        PNPatch pn = PNPatch.Create(ctx.pos[0], ctx.pos[1], ctx.pos[2],
                                    ctx.normal[0], ctx.normal[1], ctx.normal[2]);
        float3 curvedPos = pn.Evaluate(ctx.bary);
        float3 smoothN = normalize(ctx.normal[0]*ctx.bary.x 
                                 + ctx.normal[1]*ctx.bary.y 
                                 + ctx.normal[2]*ctx.bary.z);
        float height = HeightMap.SampleLevel(Sampler, InterpolateUV(ctx), 0).r;
        return curvedPos + smoothN * height * Scale;
    }
}
```
