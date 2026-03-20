using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: BVH 遍历 — 从全局 BVH 中选出候选 Cluster。
/// Static class：BVHTraversePass（跨帧 readback 状态）由 Pipeline 持有并传入，
/// CullUpdateArgsPass 每帧新建（PSO 是 static cached）。
/// </summary>
public static class ClusterTraverse
{
    /// <summary>
    /// 向 RenderGraph 添加 BVH 遍历 pass，返回候选 Cluster 列表。
    /// 内部构建并上传 CullingUniforms，输出 handle 供 CullStage 复用。
    /// </summary>
    public static ClusterTraverseOutput AddPasses(
        RenderGraph graph,
        RenderContext context,
        ClusterBVHTraversePass bvhTraversePass,
        ClusterResourceManager clusterMgr,
        InstanceDataManager instanceMgr,
        in ClusterGlobalResources globals,
        in ClusterCameraData camera,
        in ClusterTraverseConfig config
    )
    {
        // Forward frame data to internal BVH pass
        var prevViewProjT = Matrix4x4.Transpose(camera.PrevViewProj);
        bvhTraversePass.SetFrameData(
            camera.View, camera.Proj, camera.CameraPos,
            camera.LodThreshold, camera.LodScale, camera.ForcedLODLevel,
            false, // bypassCulling (default for traverse)
            prevViewProjT,
            false, 0, Vector2.Zero // HiZ defaults (set externally if needed)
        );

        // ─── Create + upload CullingUniforms ───
        var cullingData = CullingUniforms.Create(
            camera.View, camera.Proj, camera.CameraPos,
            camera.LodThreshold, camera.LodScale, camera.ForcedLODLevel,
            (uint)instanceMgr.Count, false, false, false,
            prevViewProjT, false, 0, Vector2.Zero,
            camera.PrevView, camera.PrevProj,
            clusterMgr.QuantOrigin, clusterMgr.QuantStep
        );
        var hCullingUB = CreateDynamicUniformPass(graph, "CullingUniforms", cullingData);

        // ─── Create candidate/queue buffers ───
        uint maxDraws = ClusterLimits.MaxDraws;

        var hCandidateClusters = graph.CreateBuffer("CandidateClusters", new BufferDesc
        {
            Size = (ulong)(maxDraws * 12),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Structured,
            ElementByteStride = 12,
        });

        var hCandidateArgs = graph.CreateBuffer("CandidateArgs", new BufferDesc
        {
            Size = 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        var hCandidateCount = graph.CreateBuffer("CandidateCount", new BufferDesc
        {
            Size = 4,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });

        var hIndirectDrawArgs = graph.CreateBuffer("IndirectDrawArgs", new BufferDesc
        {
            Size = 256,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
        });

        var hBvhQueueA = graph.CreateBuffer("BVHQueueA", new BufferDesc
        {
            Size = 4ul * 1024 * 1024 * 8,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            Mode = BufferMode.Structured,
            ElementByteStride = 8,
        });
        var hBvhQueueB = graph.CreateBuffer("BVHQueueB", new BufferDesc
        {
            Size = 4ul * 1024 * 1024 * 8,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            Mode = BufferMode.Structured,
            ElementByteStride = 8,
        });
        var hBvhArgsA = graph.CreateBuffer("BVHArgsA", new BufferDesc
        {
            Size = 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hBvhArgsB = graph.CreateBuffer("BVHArgsB", new BufferDesc
        {
            Size = 16,
            BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
            Mode = BufferMode.Raw,
            ElementByteStride = 4,
        });
        var hBvhReadback = graph.CreateBuffer("BVHReadback", new BufferDesc
        {
            Size = 4096,
            Usage = Usage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        graph.MarkOutput(hBvhReadback);

        var hPageFaultBuffer = graph.CreateBuffer("PageFaultBuffer", clusterMgr.PageFaultDesc);
        var hPageFaultReadback = graph.CreateBuffer("PageFaultReadback", clusterMgr.PageFaultReadbackDesc);

        // ─── Clear buffers ───
        graph.AddPass(
            new ClusterClearBuffersPass(
                hIndirectDrawArgs,
                hCandidateArgs,
                hCandidateCount,
                hPageFaultBuffer,
                RenderGraphHandle.Invalid,
                RenderGraphHandle.Invalid,
                RenderGraphHandle.Invalid,
                RenderGraphHandle.Invalid
            )
        );

        // ─── Wire BVH Traverse pass ───
        bvhTraversePass.HCandidateClusters = hCandidateClusters;
        bvhTraversePass.HCandidateArgs = hCandidateArgs;
        bvhTraversePass.HCandidateCount = hCandidateCount;
        bvhTraversePass.HIndirectDrawArgs = hIndirectDrawArgs;
        bvhTraversePass.HQueueA = hBvhQueueA;
        bvhTraversePass.HQueueB = hBvhQueueB;
        bvhTraversePass.HArgsA = hBvhArgsA;
        bvhTraversePass.HArgsB = hBvhArgsB;
        bvhTraversePass.HReadbackBuffer = hBvhReadback;
        bvhTraversePass.HPageFaultBuffer = hPageFaultBuffer;
        bvhTraversePass.HPageFaultReadbackBuffer = hPageFaultReadback;
        bvhTraversePass.HCullingUniforms = hCullingUB;
        bvhTraversePass.HGlobalTransformBuffer = globals.GlobalTransform;
        bvhTraversePass.HGlobalInstanceHeaderBuffer = globals.GlobalInstanceHeader;
        bvhTraversePass.HGlobalBVHBuffer = globals.GlobalBVH;
        bvhTraversePass.HPageHeap = globals.PageHeap;

        graph.AddPass(new ClusterBVHClearArgsPass(bvhTraversePass, true, "BVH Clear Args A"));
        graph.AddPass(new ClusterBVHClearArgsPass(bvhTraversePass, false, "BVH Clear Args B"));

        if (instanceMgr.Count > 0)
        {
            graph.AddPass(new ClusterBVHInitQueuePass(bvhTraversePass));
            graph.AddPass(new ClusterBVHUpdateArgsPass(bvhTraversePass, true, "BVH Update Init Args"));

            bool currentIsA = true;
            int maxDepth = config.MaxDepth;
            for (int depth = 0; depth < maxDepth; depth++)
            {
                bool nextIsA = !currentIsA;
                graph.AddPass(new ClusterBVHTraverseDepthPass(
                    bvhTraversePass, currentIsA, depth, $"BVH Traverse D{depth}"));
                graph.AddPass(new ClusterBVHUpdateArgsPass(
                    bvhTraversePass, nextIsA, $"BVH Update Args D{depth}"));
                graph.AddPass(new ClusterBVHClearArgsPass(
                    bvhTraversePass, currentIsA, $"BVH Clear Recycle D{depth}"));
                currentIsA = nextIsA;
            }

            graph.AddPass(new ClusterBVHReadbackPass(bvhTraversePass));
        }

        graph.AddPass(new ClusterBVHPageFaultCopyPass(bvhTraversePass, hPageFaultReadback));

        // ─── CullUpdateArgs (fresh per frame, PSO is static cached) ───
        var cullUpdateArgsPass = new ClusterCullUpdateArgsPass(context);
        cullUpdateArgsPass.HCandidateCount = hCandidateCount;
        cullUpdateArgsPass.HCandidateArgs = hCandidateArgs;
        graph.AddPass(cullUpdateArgsPass);

        return new ClusterTraverseOutput(
            hCandidateClusters, hCandidateArgs, hCandidateCount, hCullingUB
        );
    }

    /// <summary>
    /// 创建动态 Uniform Buffer 并添加上传 pass。
    /// </summary>
    private static RenderGraphHandle CreateDynamicUniformPass<T>(
        RenderGraph graph, string name, T data) where T : unmanaged
    {
        var handle = graph.CreateBuffer(name, new BufferDesc
        {
            Size = (ulong)Marshal.SizeOf<T>(),
            Usage = Usage.Dynamic,
            BindFlags = BindFlags.UniformBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        graph.AddPass(
            $"Upload{name}",
            builder => { builder.Write(handle, ResourceState.ConstantBuffer); },
            rgCtx =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                var buf = rgCtx.GetBuffer(handle);
                if (ctx2 != null && buf != null)
                {
                    var span = ctx2.MapBuffer<T>(buf, MapType.Write, MapFlags.Discard);
                    span[0] = data;
                    ctx2.UnmapBuffer(buf, MapType.Write);
                }
            }
        );
        return handle;
    }
}
