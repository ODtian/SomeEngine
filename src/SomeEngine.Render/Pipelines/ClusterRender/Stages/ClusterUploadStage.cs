using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: 负责创建全局 GPU 资源 + Upload Uniform/Instance/BVH 数据。
/// 所有 View 共享此 Stage 的产物。
/// </summary>
public class ClusterUploadStage(
    RenderContext context,
    ClusterResourceManager clusterMgr,
    InstanceDataManager instanceMgr
)
{
    private ClusterBVHPatchPass? _bvhPatchPass;
    private bool _initialized;

    public void Init()
    {
        if (_initialized) return;
        _bvhPatchPass = new ClusterBVHPatchPass(context);
        _bvhPatchPass.Init();
        _initialized = true;
    }

    /// <summary>
    /// 向 RenderGraph 添加 Upload 相关的所有 pass，返回全局资源 Handle。
    /// </summary>
    public ClusterGlobalResources AddPasses(RenderGraph graph, in ClusterUploadConfig config)
    {
        if (!_initialized) Init();

        // ─── Create Uniform Buffers ───
        var hCullingUB = graph.CreateBuffer(
            "CullingUniforms",
            new BufferDesc
            {
                Size = (ulong)Marshal.SizeOf<CullingUniforms>(),
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );
        var hDrawUB = graph.CreateBuffer(
            "DrawUniforms",
            new BufferDesc
            {
                Size = 256,
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );
        var hCopyUB = graph.CreateBuffer(
            "CopyUniforms",
            new BufferDesc
            {
                Size = 16,
                Usage = Usage.Dynamic,
                BindFlags = BindFlags.UniformBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );
        var hBinningUB = graph.CreateBuffer(
            "BinningUniforms",
            new BufferDesc
            {
                Size = (ulong)Marshal.SizeOf<BinningUniforms>(),
                BindFlags = BindFlags.UniformBuffer,
                Usage = Usage.Dynamic,
                CPUAccessFlags = CpuAccessFlags.Write,
            }
        );

        // ─── Assemble uniform data ───
        var cullViewProjT = Matrix4x4.Transpose(config.View * config.Proj);
        var viewProjT = Matrix4x4.Transpose(config.View * config.Proj);
        var viewT = Matrix4x4.Transpose(config.View);

        var cullingData = new CullingUniforms
        {
            ViewProj = cullViewProjT,
            CameraPos = config.CameraPos,
            LodThreshold = config.LodThreshold,
            LodScale = config.LodScale,
            MaxQueueNodes = 4 * 1024 * 1024u,
            MaxCandidates = ClusterRenderFeature.MaxDraws,
            ForcedLODLevel = config.ForcedLODLevel,
            InstanceCount = (uint)instanceMgr.Count,
            DebugMode = config.BypassCulling ? 1u : 0u,
            DumpHiZData = config.DumpNextFrame ? 1u : 0u,
            CurrentDepth = 0,
            Pad5 = config.DebugShowHiZAABBs ? 1u : 0u,
            PrevViewProj = Matrix4x4.Transpose(config.PrevViewProj),
            HasPrevHistory = config.HasPrevHistory ? 1u : 0u,
            HiZMipCount = config.HiZMipCount,
            HiZInvSize = config.HiZInvSize,
            View = Matrix4x4.Transpose(config.View),
            P00 = config.Proj.M11,
            P11 = config.Proj.M22,
            Pad7 = default,
            QuantOrigin = clusterMgr.QuantOrigin,
            QuantStep = clusterMgr.QuantStep,
            PrevView = Matrix4x4.Transpose(config.PrevView),
            PrevP00 = config.PrevProj.M11,
            PrevP11 = config.PrevProj.M22,
            Pad8 = default,
        };
        var drawData = new DrawUniforms
        {
            ViewProj = viewProjT,
            View = viewT,
            PageTableSize = clusterMgr.PageCount,
            DebugMode = config.DebugMode,
            ScreenWidth = config.ScreenWidth,
            ScreenHeight = config.ScreenHeight,
            QuantOrigin = clusterMgr.QuantOrigin,
            QuantStep = clusterMgr.QuantStep,
        };
        var copyData = new CopyUniforms { SphereVertexCount = 1536 };
        var binningData = new BinningUniforms
        {
            MaxBins = ClusterRenderFeature.MaxBins,
            MaxClustersPerBin = ClusterRenderFeature.MaxClustersPerBin,
        };

        // ─── Upload Uniforms pass ───
        var uploadData = new UploadUniformsData
        {
            HCullingUB = hCullingUB,
            HDrawUB = hDrawUB,
            HCopyUB = hCopyUB,
            HBinningUB = hBinningUB,
            CullingData = cullingData,
            DrawData = drawData,
            CopyData = copyData,
            BinningData = binningData,
        };
        graph.AddPass<UploadUniformsData>(
            "UploadUniforms",
            (builder, data) =>
            {
                data.HCullingUB = uploadData.HCullingUB;
                data.HDrawUB = uploadData.HDrawUB;
                data.HCopyUB = uploadData.HCopyUB;
                data.HBinningUB = uploadData.HBinningUB;
                data.CullingData = uploadData.CullingData;
                data.DrawData = uploadData.DrawData;
                data.CopyData = uploadData.CopyData;
                data.BinningData = uploadData.BinningData;

                builder.Write(hCullingUB, ResourceState.ConstantBuffer);
                builder.Write(hDrawUB, ResourceState.ConstantBuffer);
                builder.Write(hCopyUB, ResourceState.ConstantBuffer);
                builder.Write(hBinningUB, ResourceState.ConstantBuffer);
            },
            (rgCtx, data) =>
            {
                var ctx2 = rgCtx.RenderContext.ImmediateContext;
                if (ctx2 == null) return;

                var cBuf = rgCtx.GetBuffer(data.HCullingUB);
                var dBuf = rgCtx.GetBuffer(data.HDrawUB);
                var cpBuf = rgCtx.GetBuffer(data.HCopyUB);

                if (cBuf != null)
                {
                    var cSpan = ctx2.MapBuffer<CullingUniforms>(cBuf, MapType.Write, MapFlags.Discard);
                    cSpan[0] = data.CullingData;
                    ctx2.UnmapBuffer(cBuf, MapType.Write);
                }
                if (dBuf != null)
                {
                    var dSpan = ctx2.MapBuffer<DrawUniforms>(dBuf, MapType.Write, MapFlags.Discard);
                    dSpan[0] = data.DrawData;
                    ctx2.UnmapBuffer(dBuf, MapType.Write);
                }
                if (cpBuf != null)
                {
                    var cpSpan = ctx2.MapBuffer<CopyUniforms>(cpBuf, MapType.Write, MapFlags.Discard);
                    cpSpan[0] = data.CopyData;
                    ctx2.UnmapBuffer(cpBuf, MapType.Write);
                }
                var bBuf = rgCtx.GetBuffer(data.HBinningUB);
                if (bBuf != null)
                {
                    var bSpan = ctx2.MapBuffer<BinningUniforms>(bBuf, MapType.Write, MapFlags.Discard);
                    bSpan[0] = data.BinningData;
                    ctx2.UnmapBuffer(bBuf, MapType.Write);
                }
            }
        );

        // ─── Create global data buffers ───
        int maxInstances = Math.Max(instanceMgr.Count, 1);
        var hGlobalTransform = graph.CreateBuffer(
            "GlobalTransform",
            new BufferDesc
            {
                Size = (ulong)(maxInstances * GpuTransform.SizeInBytes),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuTransform.SizeInBytes,
            }
        );
        var hGlobalInstanceHeader = graph.CreateBuffer(
            "GlobalInstanceHeader",
            new BufferDesc
            {
                Size = (ulong)(maxInstances * GpuInstanceHeader.SizeInBytes),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Structured,
                ElementByteStride = GpuInstanceHeader.SizeInBytes,
            }
        );
        var hInstanceDataHeap = graph.CreateBuffer(
            "InstanceDataHeap",
            new BufferDesc
            {
                Size = Math.Max((ulong)instanceMgr.MetadataByteCount, 16ul),
                BindFlags = BindFlags.ShaderResource,
                Mode = BufferMode.Raw,
            }
        );
        var hGlobalBVH = graph.CreateBuffer("GlobalBVH", clusterMgr.GlobalBVHDesc);
        var hPageHeap = graph.CreateBuffer("PageHeap", clusterMgr.PageHeapDesc);

        // ─── Resource Upload pass ───
        graph.AddPass(new ClusterResourceUploadPass(clusterMgr, hGlobalBVH, hPageHeap));

        // ─── BVH Patch pass ───
        var patches = clusterMgr.ExtractPendingPatches();
        if (patches.Count > 0)
        {
            var hPatchBuffer = graph.CreateBuffer(
                "PatchNodeIndices",
                new BufferDesc
                {
                    Size = (ulong)patches.Count * 8,
                    Usage = Usage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                    Mode = BufferMode.Structured,
                    ElementByteStride = 8,
                }
            );
            var hPatchUniforms = graph.CreateBuffer(
                "PatchUniforms",
                new BufferDesc
                {
                    Size = 16,
                    Usage = Usage.Dynamic,
                    BindFlags = BindFlags.UniformBuffer,
                    CPUAccessFlags = CpuAccessFlags.Write,
                }
            );

            _bvhPatchPass!.Patches = patches;
            _bvhPatchPass.HGlobalBVH = hGlobalBVH;
            _bvhPatchPass.HPatchBuffer = hPatchBuffer;
            _bvhPatchPass.HPatchUniforms = hPatchUniforms;
            graph.AddPass(_bvhPatchPass);
        }

        // ─── Instance Data Upload pass ───
        if (instanceMgr.Count > 0)
        {
            graph.AddPass(
                new ClusterUploadInstanceDataPass(
                    instanceMgr,
                    hGlobalTransform,
                    hGlobalInstanceHeader,
                    hInstanceDataHeap
                )
            );
        }

        return new ClusterGlobalResources(
            hGlobalBVH, hPageHeap, hGlobalTransform, hGlobalInstanceHeader,
            hInstanceDataHeap, hCullingUB, hDrawUB, hBinningUB, hCopyUB
        );
    }

    public void Dispose()
    {
        _bvhPatchPass?.Dispose();
    }

    // Internal data class for the Upload Uniforms lambda pass
    private class UploadUniformsData
    {
        public RenderGraphHandle HCullingUB;
        public RenderGraphHandle HDrawUB;
        public RenderGraphHandle HCopyUB;
        public RenderGraphHandle HBinningUB;
        public CullingUniforms CullingData;
        public DrawUniforms DrawData;
        public CopyUniforms CopyData;
        public BinningUniforms BinningData;
    }
}
