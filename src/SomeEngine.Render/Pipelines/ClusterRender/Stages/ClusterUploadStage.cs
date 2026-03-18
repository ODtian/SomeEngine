using System.Numerics;
using System.Runtime.InteropServices;
using Diligent;
using SomeEngine.Render.Data;
using SomeEngine.Render.Graph;
using SomeEngine.Render.RHI;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Pipelines;

/// <summary>
/// Level 1 Stage: 负责创建全局 GPU 资源 + Upload Instance/BVH 数据。
/// 所有 View 共享此 Stage 的产物。Uniform buffer 由 Pipeline 自行管理。
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
    /// 向 RenderGraph 添加全局数据 Upload pass，返回全局资源 Handle。
    /// </summary>
    public ClusterGlobalResources AddPasses(RenderGraph graph)
    {
        if (!_initialized) Init();

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
            hInstanceDataHeap
        );
    }

    public void Dispose()
    {
        _bvhPatchPass?.Dispose();
    }
}
