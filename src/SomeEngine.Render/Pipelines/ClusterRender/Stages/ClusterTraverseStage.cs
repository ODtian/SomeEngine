using Diligent;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: BVH 遍历 — 从全局 BVH 中选出候选 Cluster。
/// </summary>
public class ClusterTraverseStage : IDisposable
{
    private readonly RenderContext _context;
    private readonly ClusterResourceManager _clusterMgr;
    private readonly InstanceDataManager _instanceMgr;
    private ClusterBVHTraversePass? _bvhTraversePass;
    private ClusterCullUpdateArgsPass? _cullUpdateArgsPass;
    private bool _initialized;

    public ClusterTraverseStage(
        RenderContext context,
        ClusterResourceManager clusterMgr,
        InstanceDataManager instanceMgr
    )
    {
        _context = context;
        _clusterMgr = clusterMgr;
        _instanceMgr = instanceMgr;
    }

    public void Init()
    {
        if (_initialized) return;
        _bvhTraversePass = new ClusterBVHTraversePass(_context, _clusterMgr, _instanceMgr);
        _bvhTraversePass.Init();
        _cullUpdateArgsPass = new ClusterCullUpdateArgsPass(_context);
        _cullUpdateArgsPass.Init();
        _initialized = true;
    }

    /// <summary>
    /// 向 RenderGraph 添加 BVH 遍历 pass，返回候选 Cluster 列表。
    /// </summary>
    public ClusterTraverseOutput AddPasses(
        RenderGraph graph,
        in ClusterGlobalResources globals,
        in ClusterTraverseConfig config,
        in ClusterUploadConfig frameData
    )
    {
        if (!_initialized) Init();

        // Forward frame data to internal BVH pass
        _bvhTraversePass!.SetFrameData(
            frameData.View, frameData.Proj, frameData.CameraPos,
            frameData.LodThreshold, frameData.LodScale, frameData.ForcedLODLevel,
            frameData.BypassCulling,
            System.Numerics.Matrix4x4.Transpose(frameData.PrevViewProj), // UploadConfig stores non-transposed
            frameData.HasPrevHistory, frameData.HiZMipCount, frameData.HiZInvSize
        );

        // ─── Create candidate/queue buffers ───
        uint maxDraws = ClusterLimits.MaxDraws;

        var hCandidateClusters = config.OutputCandidateClusters.IsValid
            ? config.OutputCandidateClusters
            : graph.CreateBuffer("CandidateClusters", new BufferDesc
            {
                Size = (ulong)(maxDraws * 12),
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = 12,
            });

        var hCandidateArgs = config.OutputCandidateArgs.IsValid
            ? config.OutputCandidateArgs
            : graph.CreateBuffer("CandidateArgs", new BufferDesc
            {
                Size = 16,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.IndirectDrawArgs | BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
                ElementByteStride = 4,
            });

        var hCandidateCount = config.OutputCandidateCount.IsValid
            ? config.OutputCandidateCount
            : graph.CreateBuffer("CandidateCount", new BufferDesc
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

        var hPageFaultBuffer = graph.CreateBuffer("PageFaultBuffer", _clusterMgr.PageFaultDesc);
        var hPageFaultReadback = graph.CreateBuffer("PageFaultReadback", _clusterMgr.PageFaultReadbackDesc);

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
        _bvhTraversePass!.HCandidateClusters = hCandidateClusters;
        _bvhTraversePass.HCandidateArgs = hCandidateArgs;
        _bvhTraversePass.HCandidateCount = hCandidateCount;
        _bvhTraversePass.HIndirectDrawArgs = hIndirectDrawArgs;
        _bvhTraversePass.HQueueA = hBvhQueueA;
        _bvhTraversePass.HQueueB = hBvhQueueB;
        _bvhTraversePass.HArgsA = hBvhArgsA;
        _bvhTraversePass.HArgsB = hBvhArgsB;
        _bvhTraversePass.HReadbackBuffer = hBvhReadback;
        _bvhTraversePass.HPageFaultBuffer = hPageFaultBuffer;
        _bvhTraversePass.HPageFaultReadbackBuffer = hPageFaultReadback;
        _bvhTraversePass.HCullingUniforms = globals.CullingUniforms;
        _bvhTraversePass.HGlobalTransformBuffer = globals.GlobalTransform;
        _bvhTraversePass.HGlobalInstanceHeaderBuffer = globals.GlobalInstanceHeader;
        _bvhTraversePass.HGlobalBVHBuffer = globals.GlobalBVH;
        _bvhTraversePass.HPageHeap = globals.PageHeap;

        graph.AddPass(new ClusterBVHClearArgsPass(_bvhTraversePass, true, "BVH Clear Args A"));
        graph.AddPass(new ClusterBVHClearArgsPass(_bvhTraversePass, false, "BVH Clear Args B"));

        if (_instanceMgr.Count > 0)
        {
            graph.AddPass(new ClusterBVHInitQueuePass(_bvhTraversePass));
            graph.AddPass(new ClusterBVHUpdateArgsPass(_bvhTraversePass, true, "BVH Update Init Args"));

            bool currentIsA = true;
            int maxDepth = config.MaxDepth;
            for (int depth = 0; depth < maxDepth; depth++)
            {
                bool nextIsA = !currentIsA;
                graph.AddPass(new ClusterBVHTraverseDepthPass(
                    _bvhTraversePass, currentIsA, depth, $"BVH Traverse D{depth}"));
                graph.AddPass(new ClusterBVHUpdateArgsPass(
                    _bvhTraversePass, nextIsA, $"BVH Update Args D{depth}"));
                graph.AddPass(new ClusterBVHClearArgsPass(
                    _bvhTraversePass, currentIsA, $"BVH Clear Recycle D{depth}"));
                currentIsA = nextIsA;
            }

            graph.AddPass(new ClusterBVHReadbackPass(_bvhTraversePass));
        }

        graph.AddPass(new ClusterBVHPageFaultCopyPass(_bvhTraversePass, hPageFaultReadback));

        // ─── CullUpdateArgs ───
        _cullUpdateArgsPass!.HCandidateCount = hCandidateCount;
        _cullUpdateArgsPass.HCandidateArgs = hCandidateArgs;
        graph.AddPass(_cullUpdateArgsPass);

        return new ClusterTraverseOutput(
            hCandidateClusters, hCandidateArgs, hCandidateCount
        );
    }

    public void Dispose()
    {
        _bvhTraversePass?.Dispose();
        _cullUpdateArgsPass?.Dispose();
    }
}
